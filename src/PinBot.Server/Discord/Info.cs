using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace PinBot.Server.Discord;

/// <summary>
/// The info channel (set with /pinbot-admin channels): one pinned guide that PinBot keeps current (edited when its text
/// changes, e.g. the weekly schedule or after an update), and a short announcement when a member connects a cabinet.
/// Until an info channel is set, announcements go to the one-off channel and there is no guide message.
/// </summary>
public sealed class InfoPublisher(Db db, IDiscordApi api, DiscordOptions discord, IConfiguration config, ILogger<InfoPublisher> log)
{
    /// <summary>This server's release (null for a development build) and the updater's state file: replaceable in tests.</summary>
    public string ReleasePath { get; set; } = ReleaseInfo.DefaultPath;
    public string? UpdaterStatePath { get; set; } = config["PinBot:UpdaterState"];

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync();
        var info = await Settings.GetAsync(conn, Settings.InfoChannel);
        if (info != null) await PublishGuideAsync(conn, info, ct);
        if (info != null) await AnnounceReleaseAsync(conn, info, ct);
        if ((info ?? discord.ChannelId) is { } channel) await AnnounceCabinetsAsync(conn, channel, ct);
    }

    /// <summary>Updates only go to the info channel: they are news about PinBot, not about a competition.</summary>
    async Task AnnounceReleaseAsync(NpgsqlConnection conn, string channel, CancellationToken ct)
    {
        foreach (var due in await ReleaseNotices.DueAsync(conn, ReleaseInfo.Load(ReleasePath), ReleaseNotices.ReadRefused(UpdaterStatePath)))
        {
            try
            {
                if (due.Message is { } message) await api.PostMessageAsync(channel, message, ct);
            }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("release notice not posted: {Message}", ex.Message);
                return; // posted, then remembered: this one and the rest are tried again on the next pass
            }
            await due.Remember(conn);
        }
    }

    async Task PublishGuideAsync(NpgsqlConnection conn, string channel, CancellationToken ct)
    {
        var guide = Guide(discord, await WeeklyScheduler.ScheduleAsync(conn) is { } w && await Settings.GetAsync(conn, Settings.WeeklyEnabled) == "true" ? w : null);
        var hash = Security.Sha256(guide.ToJsonString());
        var where = await Settings.GetAsync(conn, Settings.InfoGuideMessage);
        var stored = where?.Split('/') is [var ch, var existing] && ch == channel ? existing : null; // moved channel: post anew
        if (stored != null && hash == await Settings.GetAsync(conn, Settings.InfoGuideHash)) return;
        try
        {
            string id;
            if (stored is null) id = await PostPinnedAsync(channel, guide, ct);
            else
            {
                try { await api.EditMessageAsync(channel, stored, guide, ct); id = stored; }
                catch (DiscordRestException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
                {
                    id = await PostPinnedAsync(channel, guide, ct); // someone deleted it
                }
            }
            await Settings.SetAsync(conn, Settings.InfoGuideMessage, $"{channel}/{id}");
            await Settings.SetAsync(conn, Settings.InfoGuideHash, hash);
        }
        catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("guide not published in channel {Channel}: {Message}", channel, ex.Message);
        }
    }

    async Task<string> PostPinnedAsync(string channel, JsonObject message, CancellationToken ct)
    {
        var id = await api.PostMessageAsync(channel, message, ct);
        try { await api.SetPinnedAsync(channel, id, true, ct); }
        catch (DiscordRestException ex) { log.LogInformation("guide not pinned: {Message}", ex.Message); }
        return id;
    }

    /// <summary>Cabinets paired from Discord that have reported their tables and were not announced yet.</summary>
    async Task AnnounceCabinetsAsync(NpgsqlConnection conn, string channel, CancellationToken ct)
    {
        var pending = (await conn.QueryAsync<(string Id, string Name, string? PairedByName)>("""
            SELECT c.id, c.name, c.paired_by_name FROM cabinet c
            WHERE c.join_announced_at_utc IS NULL AND c.revoked_at_utc IS NULL
              AND EXISTS (SELECT 1 FROM installation i WHERE i.cabinet_id = c.id)
            """)).ToList();
        foreach (var (id, name, pairedBy) in pending)
        {
            var games = (await conn.QueryAsync<(string Game, bool Elsewhere)>("""
                SELECT DISTINCT COALESCE(a.canonical_game_id, i.rom_id),
                       EXISTS (SELECT 1 FROM installation o JOIN cabinet oc ON oc.id = o.cabinet_id AND oc.revoked_at_utc IS NULL
                               LEFT JOIN rom_alias oa ON oa.rom_id = o.rom_id
                               WHERE o.cabinet_id <> @id AND o.eligible
                                 AND COALESCE(oa.canonical_game_id, o.rom_id) = COALESCE(a.canonical_game_id, i.rom_id))
                FROM installation i LEFT JOIN rom_alias a ON a.rom_id = i.rom_id
                WHERE i.cabinet_id = @id AND i.eligible
                """, new { id })).ToList();
            var titles = (await Competitions.EligibleGamesAsync(conn)).ToDictionary(g => g.CanonicalGameId, g => g.Title);
            var newTitles = games.Where(g => !g.Elsewhere).Select(g => titles.GetValueOrDefault(g.Game, g.Game)).Order().ToList();
            try
            {
                if (pairedBy != null) await api.PostMessageAsync(channel, JoinNotice(pairedBy, name, games.Count, newTitles), ct);
            }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("cabinet {Id} not announced: {Message}", id, ex.Message);
                return;
            }
            await conn.ExecuteAsync("UPDATE cabinet SET join_announced_at_utc = now() WHERE id = @id", new { id });
        }
    }

    public static JsonObject JoinNotice(string member, string cabinet, int games, IReadOnlyList<string> newTitles)
    {
        var sb = new StringBuilder($"🎰 **{Interactions.Md(Players.StripTag(member))}** connected a VPin (**{Interactions.Md(cabinet)}**): **{games}** games ready for competitions");
        if (newTitles.Count > 0)
            sb.Append($", **{newTitles.Count}** of them new here: {string.Join(", ", newTitles.Take(5).Select(Interactions.Md))}{(newTitles.Count > 5 ? ", …" : "")}");
        sb.Append(". See them all with `/pinbot games`.");
        return new JsonObject
        {
            ["content"] = sb.ToString(),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
    }

    /// <summary>The player guide: posted in the info channel, and the answer to /pinbot help.</summary>
    public static JsonObject Guide(DiscordOptions discord, WeeklySchedule? weekly)
    {
        string Ch(string? id, string fallback) => id is null ? fallback : $"<#{id}>";
        var weeklyLine = weekly is null
            ? "• Weekly competitions are off (a server manager turns them on with `/pinbot-admin weekly`)."
            : $"• A new **weekly** game starts every **{weekly.Day} at {weekly.Time:hh\\:mm}** {weekly.ZoneName}, leaderboard in {Ch(discord.WeeklyChannelId ?? discord.ChannelId, "the weekly channel")}.";
        var text = $"""
            **Play**
            • Put your initials in your server nickname: `Name | [ABC]` (more than one: `[ABC,XYZ]`).
            • Play the competition's table on any connected VPin and enter those initials when you get a high score.
            • Playing with someone who isn't on Discord? `/pinbot guest ABC` lets their initials count for 24 hours.
            • A game counts if you **exit the table before the deadline** (a ⏰ reminder comes 10 minutes before). Your best score counts.

            **Compete**
            • `/pinbot start`: challenge everyone on any game, from 1 hour to 7 days. `/pinbot games` lists them all.
            • `/pinbot status`: what's running and who leads. `/pinbot end`: end a competition you started.
            • Challenge leaderboards are in {Ch(discord.ChannelId, "the challenges channel")}.
            {weeklyLine}
            • You are only pinged when someone passes your score, and when you finish on the podium.

            **Connect your own VPin** (any Windows PC with Visual Pinball X)
            • `/pinbot pair` (only you see the reply): download **pinbot.exe**, double-click it, paste the line. It runs by itself from then on.
            • Your own high scores are backed up before a competition and put back after it.
            • PinBot and every connected VPin update themselves; this channel says when, and what changed.
            """;
        return new JsonObject
        {
            ["content"] = "",
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = "🎯 How PinBot works",
                ["description"] = text.Replace("\r\n", "\n"),
                ["color"] = 0x5865F2,
            }),
        };
    }
}
