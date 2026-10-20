using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace PinBot.Server.Discord;

/// <summary>
/// Refreshes the initials map from Discord nicknames (needs the Server Members intent), then lets scores that arrived
/// before their player's tag existed count, if exactly one member now claims those initials and the competition is open.
/// </summary>
public sealed class MemberSync(Db db, IDiscordApi api, DiscordOptions discord, ServerOptions options, TimeProvider clock,
    LeaderboardPublisher publisher, ILogger<MemberSync> log)
{
    bool _shrinkSeenBefore;

    /// <summary>Clash notices already delivered, per (initials, members, recipient): a retry after a failure skips them.</summary>
    readonly HashSet<(string Initials, string Members, string User)> _clashSent = [];

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<DiscordMember> members;
        try { members = await api.ListMembersAsync(discord.GuildId!, ct); }
        catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            log.LogError("Discord refused the member list: enable 'Server Members Intent' for the PinBot application and check the bot is in the server ({Message})", ex.Message);
            return;
        }
        var now = clock.GetUtcNow().UtcDateTime;
        await using var conn = await db.OpenAsync();
        var list = members.Select(m => new Member(m.UserId, m.DisplayName, m.IsBot)).ToList();
        var taggedBefore = await conn.ExecuteScalarAsync<int>("SELECT count(DISTINCT discord_user_id) FROM member_initial");
        var taggedNow = list.Where(m => !m.IsBot).Count(m => Players.ParseTag(m.DisplayName).Count > 0);
        if (Players.SuspiciousShrink(taggedBefore, taggedNow) && !_shrinkSeenBefore)
        {
            // a member list that lost most tags at once is more likely a Discord hiccup than people renaming: keep the
            // current map, and accept the smaller list only if the next sync sees it too
            _shrinkSeenBefore = true;
            log.LogWarning("member list has {Now} tagged members instead of {Before}; keeping the current initials until the next sync confirms it", taggedNow, taggedBefore);
            return;
        }
        _shrinkSeenBefore = false;
        var report = await Players.ReplaceMembersAsync(conn, list, now);
        foreach (var (initials, names) in report.Ambiguous)
            log.LogWarning("initials [{Initials}] are claimed by several members ({Names}); their scores will not count until resolved", initials, string.Join(", ", names));
        await AnnounceClashesAsync(conn, now, ct);
        if (await ReresolveAsync(conn, now, options.UploadGrace) > 0) publisher.Wake();
    }

    /// <summary>
    /// Initials several members use in their tags count for none of them (a score cannot say who played). As soon as the
    /// sync sees such a clash, each of those members is told privately, once, naming the others, so one of them changes
    /// their tag before a score is lost. Uses the member list just read: no extra calls unless there is a clash.
    /// </summary>
    async Task AnnounceClashesAsync(NpgsqlConnection conn, DateTime now, CancellationToken ct)
    {
        var claims = (await conn.QueryAsync<(string Initials, string User, string Display)>(
            "SELECT DISTINCT initials, discord_user_id, display_name FROM member_initial")).ToList();
        foreach (var clash in claims.GroupBy(c => c.Initials).Where(g => g.Select(c => c.User).Distinct().Count() > 1))
        {
            var members = string.Join(",", clash.Select(c => c.User).Distinct().Order());
            if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM initials_clash WHERE initials = @Key AND members = @members)",
                    new { clash.Key, members })) continue;
            foreach (var (_, user, _) in clash.Where(c => c.User.All(char.IsAsciiDigit)).DistinctBy(c => c.User))
            {
                if (_clashSent.Contains((clash.Key, members, user))) continue; // told on an earlier, interrupted pass
                var others = clash.Where(c => c.User != user).Select(c => Players.StripTag(c.Display)).Distinct().ToList();
                try
                {
                    var dm = await api.OpenDmAsync(user, ct);
                    await api.PostMessageAsync(dm, ClashNotice(clash.Key, others), ct);
                    _clashSent.Add((clash.Key, members, user));
                }
                catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest)
                {
                    log.LogInformation("could not message a member about shared initials: {Message}", ex.Message); // DMs closed
                }
                catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
                {
                    log.LogWarning("shared-initials message not sent: {Message}", ex.Message);
                    return; // tried again at the next sync
                }
            }
            await conn.ExecuteAsync("INSERT INTO initials_clash (initials, members, notified_at_utc) VALUES (@Key, @members, @now) ON CONFLICT DO NOTHING",
                new { clash.Key, members, now });
        }
    }

    public static JsonObject ClashNotice(string initials, IReadOnlyList<string> others) => new()
    {
        ["content"] = $"⚠️ Your tag **[{initials}]** is also used by **{Interactions.Md(string.Join(", ", others))}**. A score entered as " +
                      $"{initials} can't say which of you played, so it counts for neither: one of you changing their nickname tag fixes it.",
        ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
    };

    /// <param name="initials">only results entered with these initials (a new guest), so a Discord command stays quick</param>
    internal static async Task<int> ReresolveAsync(NpgsqlConnection conn, DateTime now, TimeSpan grace, string? initials = null)
    {
        var pending = await conn.QueryAsync<(Guid EventId, string Initials, DateTime Exited)>("""
            SELECT r.event_id, r.initials, r.session_exited_at_utc FROM result r JOIN competition c ON c.id = r.competition_id
            WHERE r.status = 'unattributed' AND c.cancelled_at_utc IS NULL
              AND LEAST(c.ends_at_utc, COALESCE(c.ended_early_at_utc, c.ends_at_utc)) + @grace > @now
              AND (@initials::text IS NULL OR r.initials = @initials)
            """, new { now, grace, initials });
        int changed = 0;
        foreach (var (eventId, entered, exited) in pending)
        {
            var (userId, _) = await Players.ResolveAsync(conn, entered, DateTime.SpecifyKind(exited, DateTimeKind.Utc));
            if (userId is null) continue;
            changed += await conn.ExecuteAsync(
                "UPDATE result SET status = 'accepted', discord_user_id = @userId, reason = NULL WHERE event_id = @eventId AND status = 'unattributed'",
                new { userId, eventId });
        }
        return changed;
    }
}
