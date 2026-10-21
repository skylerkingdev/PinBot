using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace PinBot.Server.Discord;

/// <summary>
/// Keeps one Discord message per competition: posted when it starts, edited when the standings or status change,
/// final standings once the delivery window closes. Edits only when the rendered content actually changed.
/// </summary>
public sealed class LeaderboardPublisher(Db db, IDiscordApi api, DiscordOptions discord, ServerOptions options, TimeProvider clock, ILogger<LeaderboardPublisher> log)
{
    readonly SemaphoreSlim _wake = new(0);

    /// <summary>Ask for a publish pass soon (after a start, end or accepted result).</summary>
    public void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    public Task WaitAsync(TimeSpan max, CancellationToken ct) => _wake.WaitAsync(max, ct);

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (discord.ChannelId is null && discord.WeeklyChannelId is null) return;
        var now = clock.GetUtcNow().UtcDateTime;
        await using var conn = await db.OpenAsync();
        var comps = (await Competitions.ListAsync(conn))
            .Where(c => c.CancelledAtUtc is null && c.EffectiveEndUtc + options.UploadGrace + TimeSpan.FromDays(2) > now);
        foreach (var c in comps)
        {
            var rows = await Competitions.LeaderboardAsync(conn, c.Id);
            await PublishCardAsync(conn, c, rows, now, ct);
            await PinCardAsync(conn, c, now, ct);
            await AnnouncePassesAsync(conn, c, rows, ct);
            await AnnounceLastCallAsync(conn, c, rows, now, ct);
            await AnnounceEndAsync(conn, c, rows, now, ct);
        }
    }

    /// <summary>" · leaderboard" jump link to the competition's card, appended to the messages about it.</summary>
    string CardLink(CompetitionRow c) => c.DiscordChannelId is { } ch && c.DiscordMessageId is { } m && discord.GuildId is { } g
        ? $" · [leaderboard](<https://discord.com/channels/{g}/{ch}/{m}>)" : "";

    /// <summary>
    /// Keeps the card pinned while the competition runs (so it is easy to find once the channel fills with chat) and
    /// unpins it when the standings are final. Needs the Pin Messages permission; without it, pinning is skipped.
    /// </summary>
    async Task PinCardAsync(NpgsqlConnection conn, CompetitionRow c, DateTime now, CancellationToken ct)
    {
        if (c.DiscordChannelId is not { } channel || c.DiscordMessageId is not { } message || c.PinState == "denied") return;
        var running = c.Status(now, options.UploadGrace) is "scheduled" or "active" or "finalizing";
        var want = running ? "pinned" : "unpinned";
        if (c.PinState == want || (!running && c.PinState is null)) return;
        try
        {
            await api.SetPinnedAsync(channel, message, running, ct);
        }
        catch (DiscordRestException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            log.LogInformation("Discord: no permission to pin in channel {Channel}; leaderboards are not pinned there", channel);
            want = "denied";
        }
        catch (DiscordRestException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound && !running)
        {
            // the card is gone: nothing to unpin
        }
        catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("pinning the leaderboard for competition {Id} failed: {Message}", c.Id, ex.Message);
            return;
        }
        await conn.ExecuteAsync("UPDATE competition SET pin_state = @want WHERE id = @Id", new { want, c.Id });
        c.PinState = want;
    }

    /// <summary>How long before the deadline the last call goes out: time for one more game (it must be finished by then).</summary>
    public static readonly TimeSpan LastCall = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Once per competition, 10 minutes before the deadline: a reminder that games must be finished by then, with the
    /// leader. No mentions. Skipped for competitions shorter than 30 minutes, ones ended early, and when fewer than
    /// 2 minutes remain (e.g. the server was down through the window).
    /// </summary>
    async Task AnnounceLastCallAsync(NpgsqlConnection conn, CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, DateTime now, CancellationToken ct)
    {
        var left = c.EffectiveEndUtc - now;
        if (c.Status(now, options.UploadGrace) != "active" || c.EndedEarlyAtUtc != null || left > LastCall || left < TimeSpan.FromMinutes(2)
            || c.EffectiveEndUtc - c.StartsAtUtc < TimeSpan.FromMinutes(30)) return;
        var channel = c.DiscordChannelId ?? discord.ChannelFor(c.Kind);
        if (channel is null) return;
        if (await conn.ExecuteScalarAsync<DateTime?>("SELECT warning_announced_at_utc FROM competition WHERE id = @Id", new { c.Id }) != null) return;
        try
        {
            await api.PostMessageAsync(channel, LastCallNotice(c, rows, CardLink(c)), ct);
        }
        catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("last call for competition {Id} not sent: {Message}", c.Id, ex.Message);
            return;
        }
        await conn.ExecuteAsync("UPDATE competition SET warning_announced_at_utc = @now WHERE id = @Id", new { now, c.Id });
    }

    public static JsonObject LastCallNotice(CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, string link = "")
    {
        var leader = rows.FirstOrDefault() is { } top
            ? $"Leader: **{Interactions.Md(Players.StripTag(top.DisplayName))}** {top.Score.ToString("N0", CultureInfo.InvariantCulture)}."
            : "No scores yet.";
        return new JsonObject
        {
            ["content"] = $"⏰ **{Interactions.Md(c.Title)}** ends {Interactions.Ts(c.EffectiveEndUtc, "R")}: time for one more game. " +
                          $"A game counts if you've exited the table by {Interactions.Ts(c.EffectiveEndUtc, "t")}. {leader}{link}",
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
    }

    /// <summary>
    /// Once per competition: the final podium, mentioning the top three (who all played it). Posted when the standings
    /// are final: a few minutes after the deadline, once results of games finished in time have had time to arrive.
    /// </summary>
    async Task AnnounceEndAsync(NpgsqlConnection conn, CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, DateTime now, CancellationToken ct)
    {
        if (c.Status(now, options.UploadGrace) != "complete") return;
        var channel = c.DiscordChannelId ?? discord.ChannelFor(c.Kind);
        if (channel is null) return;
        if (await conn.ExecuteScalarAsync<DateTime?>("SELECT end_announced_at_utc FROM competition WHERE id = @Id", new { c.Id }) != null) return;
        try
        {
            await api.PostMessageAsync(channel, EndNotice(c, rows, CardLink(c)), ct);
        }
        catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("end notice for competition {Id} not sent: {Message}", c.Id, ex.Message);
            return;
        }
        await conn.ExecuteAsync("UPDATE competition SET end_announced_at_utc = @now WHERE id = @Id", new { now, c.Id });
    }

    /// <param name="link">appended to the message: a jump link to the card, or ""</param>
    public static JsonObject EndNotice(CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, string link = "")
    {
        var podium = rows.Where(r => r.Rank <= 3).ToList();
        var pings = new List<string>();
        var sb = new StringBuilder();
        if (rows.Count == 0) sb.Append($"🏁 **{Interactions.Md(c.Title)}** has ended with no scores.");
        else
        {
            sb.Append($"🏁 **{Interactions.Md(c.Title)}** has ended!\n");
            foreach (var r in podium)
            {
                var medal = r.Rank switch { 1 => "🥇", 2 => "🥈", _ => "🥉" };
                var who = Named(r, mention: true, pings);
                sb.Append($"{medal} {who} {r.Score.ToString("N0", CultureInfo.InvariantCulture)}\n");
            }
            sb.Append("These are the final standings.");
        }
        return new JsonObject
        {
            ["content"] = sb.Append(link).ToString(),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray(), ["users"] = new JsonArray(pings.Distinct().Select(id => (JsonNode?)id).ToArray()) },
        };
    }

    async Task PublishCardAsync(NpgsqlConnection conn, CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, DateTime now, CancellationToken ct)
    {
        var running = c.Status(now, options.UploadGrace) is "scheduled" or "active";
        var message = Render(c, rows, now, options.UploadGrace, running ? await ReadinessAsync(conn, c, now) : null);
        var json = message.ToJsonString();
        var hash = Security.Sha256(json);
        if (hash == c.PublishedHash) return;
        try
        {
            var channel = c.DiscordChannelId ?? discord.ChannelFor(c.Kind);
            if (channel is null) return;
            string messageId;
            var pinState = c.PinState;
            if (c.DiscordMessageId is null) messageId = await api.PostMessageAsync(channel, message, ct);
            else
            {
                try { await api.EditMessageAsync(channel, c.DiscordMessageId, message, ct); messageId = c.DiscordMessageId; }
                catch (DiscordRestException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
                {
                    messageId = await api.PostMessageAsync(channel, message, ct); // message was deleted: post a new one
                    if (pinState != "denied") pinState = null;                     // ...which is not pinned yet
                }
            }
            await conn.ExecuteAsync(
                "UPDATE competition SET discord_channel_id = @channel, discord_message_id = @messageId, published_hash = @hash, pin_state = @pinState WHERE id = @Id",
                new { channel, messageId, hash, pinState, c.Id });
            (c.DiscordChannelId, c.DiscordMessageId, c.PublishedHash, c.PinState) = (channel, messageId, hash, pinState);
        }
        catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("leaderboard for competition {Id} not published: {Message}", c.Id, ex.Message);
        }
    }

    /// <summary>
    /// Mentions members whose best score was just passed: ahead of the improving player at the last announcement, behind
    /// now. Works the same for live results, late uploads and scores that count once a nickname tag appears. Nothing is
    /// recorded until the message is sent, so a failed post is retried on the next pass.
    /// </summary>
    async Task AnnouncePassesAsync(NpgsqlConnection conn, CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, CancellationToken ct)
    {
        var current = rows.ToDictionary(r => r.DiscordUserId, r => r.Score);
        var stored = await conn.ExecuteScalarAsync<string?>("SELECT announced_bests FROM competition WHERE id = @Id", new { c.Id });
        if (stored != null)
        {
            var before = JsonNode.Parse(stored)!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<long>());
            var notices = PassNotices(c, rows, before, CardLink(c));
            var channel = c.DiscordChannelId ?? discord.ChannelFor(c.Kind);
            if (notices.Count > 0 && channel is null) return;
            try
            {
                foreach (var notice in notices) await api.PostMessageAsync(channel!, notice, ct);
            }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("pass notice for competition {Id} not sent: {Message}", c.Id, ex.Message);
                return;
            }
            if (before.Count == current.Count && before.All(kv => current.GetValueOrDefault(kv.Key) == kv.Value)) return;
        }
        var json = new JsonObject(current.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))).ToJsonString();
        await conn.ExecuteAsync("UPDATE competition SET announced_bests = @json WHERE id = @Id", new { json, c.Id });
    }

    /// <summary>
    /// One message per member whose score improved, when it changed something: a new leader (including the first score),
    /// the leader raising the top score, or a better place (also a first entry). Everyone who was ahead before and is
    /// behind now is mentioned. A better score that changes no place stays quiet.
    /// </summary>
    public static List<JsonObject> PassNotices(CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, IReadOnlyDictionary<string, long> before,
        string link = "")
    {
        var notices = new List<JsonObject>();
        var oldLeader = before.Count == 0 ? null : before.MaxBy(kv => kv.Value).Key;
        var game = $"**{Interactions.Md(c.Title)}**";
        foreach (var p in rows.Where(r => !before.TryGetValue(r.DiscordUserId, out var old) || r.Score > old))
        {
            var hadScore = before.TryGetValue(p.DiscordUserId, out var pOld);
            var oldRank = hadScore ? 1 + before.Count(kv => kv.Key != p.DiscordUserId && kv.Value > pOld) : int.MaxValue;
            var passed = rows.Where(o => o.DiscordUserId != p.DiscordUserId && o.Score < p.Score
                                         && before.TryGetValue(o.DiscordUserId, out var oOld) && (!hadScore || oOld > pOld)).ToList();
            var soleFirst = rows.All(o => o.DiscordUserId == p.DiscordUserId || o.Score < p.Score);
            // the overtaken are mentioned; the one who moved is named, except a guest, whose host hears about it
            var pings = new List<string>();
            var who = Named(p, mention: Players.GuestHost(p.DiscordUserId) != null, pings);
            var names = string.Join(", ", passed.Select(o => Named(o, mention: true, pings)));
            var passing = passed.Count > 0 ? $", passing {names}." : ".";
            var score = $"**{p.Score.ToString("N0", CultureInfo.InvariantCulture)}**";
            string? text =
                soleFirst && oldLeader != p.DiscordUserId ? $"👑 {who} takes the lead on {game} with {score}{passing}"
                : soleFirst ? $"👑 {who} raises the top score on {game} to {score}."
                : p.Rank < oldRank ? $"⚡ {who} takes **{Ordinal(p.Rank)} place** on {game} with {score}{passing}"
                : null;
            if (text is null) continue;
            notices.Add(new JsonObject
            {
                ["content"] = text + link,
                ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray(), ["users"] = new JsonArray(pings.Distinct().Select(id => (JsonNode?)id).ToArray()) },
            });
        }
        return notices;
    }

    /// <summary>
    /// A player in a message. A member: mentioned when <paramref name="mention"/> (Discord ids are numeric; anything else,
    /// e.g. a member set through the admin API, is named), else named. A guest: by initials with their host, who is mentioned
    /// when <paramref name="mention"/> ("@JB's guest **ABC**"): the host knows the guest. Mentioned ids go into <paramref name="pings"/>.
    /// </summary>
    internal static string Named(LeaderboardRow r, bool mention, ICollection<string> pings)
    {
        if (Players.GuestHost(r.DiscordUserId) is { } host)
        {
            if (!mention || !host.All(char.IsAsciiDigit)) return $"**{Interactions.Md(r.DisplayName)}**";
            pings.Add(host);
            var initials = Players.GuestInitials(r.DiscordUserId);
            return Players.GuestName(r.DisplayName) is { } name
                ? $"**{Interactions.Md(name)}** (<@{host}>'s guest {initials})" : $"<@{host}>'s guest **{initials}**";
        }
        if (!mention || !r.DiscordUserId.All(char.IsAsciiDigit)) return $"**{Interactions.Md(Players.StripTag(r.DisplayName))}**";
        pings.Add(r.DiscordUserId);
        return $"<@{r.DiscordUserId}>";
    }

    internal static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>A cabinet with the competition's game, and how far it is (null state: it has not reported yet).</summary>
    public sealed record CabinetReadiness(string Cabinet, string? Owner, string? State, string? Detail, bool Online);

    static readonly TimeSpan OnlineWithin = TimeSpan.FromMinutes(2); // cabinets check in at least every 25-60 s

    /// <summary>
    /// Cabinets with the competition's game: eligible ones with what they reported, and ones whose copy of the game
    /// reset its high scores on a competition file there (shown as unable to run it, with the reason).
    /// </summary>
    async Task<List<CabinetReadiness>> ReadinessAsync(NpgsqlConnection conn, CompetitionRow c, DateTime now)
    {
        var labels = await Players.CabinetLabelsAsync(conn); // shown by their owners' tags, e.g. [SKY]
        return
        (await conn.QueryAsync<(string Id, string Name, DateTime? Seen, string? State, string? Detail)>("""
            WITH game AS (
                SELECT i.cabinet_id, bool_or(i.eligible) AS eligible, bool_or(i.reason = @rejected) AS rejected
                FROM installation i LEFT JOIN rom_alias a ON a.rom_id = i.rom_id
                WHERE COALESCE(a.canonical_game_id, i.rom_id) = @CanonicalGameId
                GROUP BY i.cabinet_id)
            SELECT cab.id, cab.name, cab.last_seen_at_utc,
                   CASE WHEN g.eligible THEN cc.state ELSE @refused END,
                   CASE WHEN g.eligible THEN cc.detail ELSE @rejected END
            FROM cabinet cab
            JOIN game g ON g.cabinet_id = cab.id AND (g.eligible OR g.rejected)
            LEFT JOIN competition_cabinet cc ON cc.cabinet_id = cab.id AND cc.competition_id = @Id
            WHERE cab.revoked_at_utc IS NULL
            ORDER BY cab.name
            """, new { c.Id, c.CanonicalGameId, rejected = PinBot.Contracts.CompetitionStates.RomRejected, refused = PinBot.Contracts.CompetitionStates.Refused }))
        .Select(r => new CabinetReadiness(labels.GetValueOrDefault(r.Id, r.Name), null, r.State, r.Detail, r.Seen is { } s && now - DateTime.SpecifyKind(s, DateTimeKind.Utc) < OnlineWithin))
        .OrderBy(r => r.Cabinet, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    /// <summary>"🟢 Ready on …" lines for a running competition, so players know when and where they can play.</summary>
    static string ReadinessLines(IReadOnlyList<CabinetReadiness> cabinets)
    {
        string Name(CabinetReadiness r) => $"{Interactions.Md(r.Cabinet)}{(r.Owner is { } o ? $" ({Interactions.Md(Players.StripTag(o))})" : "")}";
        var sb = new StringBuilder();
        var ready = cabinets.Where(r => r.State == PinBot.Contracts.CompetitionStates.Ready).ToList();
        var refused = cabinets.Where(r => r.State == PinBot.Contracts.CompetitionStates.Refused).ToList();
        var waiting = cabinets.Except(ready).Except(refused).ToList();
        if (ready.Count > 0) sb.AppendLine($"🟢 Ready on: {string.Join(", ", ready.Select(Name))}");
        if (waiting.Count > 0)
            sb.AppendLine($"⏳ Getting ready: {string.Join(", ", waiting.Select(r => Name(r) + (!r.Online ? " (offline)" : r.Detail is { } d ? $" ({Interactions.Md(d)})" : "")))}");
        foreach (var r in refused) sb.AppendLine($"⚠️ Can't run on {Name(r)}: {Interactions.Md(r.Detail ?? "unknown reason")}");
        return sb.ToString();
    }

    public static JsonObject Render(CompetitionRow c, IReadOnlyList<LeaderboardRow> rows, DateTime now, TimeSpan grace,
        IReadOnlyList<CabinetReadiness>? cabinets = null)
    {
        var status = c.Status(now, grace);
        var sb = new StringBuilder();
        sb.AppendLine(c.Kind == "weekly"
            ? "📅 Weekly competition"
            : $"Started by **{Interactions.Md(Players.StripTag(c.CreatedByName ?? c.CreatedBy))}**");
        sb.AppendLine(status switch
        {
            "scheduled" => $"Starts {Interactions.Ts(c.StartsAtUtc, "f")}",
            "active" => $"Ends {Interactions.Ts(c.EffectiveEndUtc, "f")} ({Interactions.Ts(c.EffectiveEndUtc, "R")})",
            "finalizing" => $"Ended {Interactions.Ts(c.EffectiveEndUtc, "f")}. Final standings in a few minutes",
            _ => $"**Final standings**, ended {Interactions.Ts(c.EffectiveEndUtc, "f")}",
        });
        if (status is "scheduled" or "active" && cabinets is { Count: > 0 }) sb.Append(ReadinessLines(cabinets));
        sb.AppendLine();
        if (rows.Count == 0) sb.AppendLine("_No scores yet._");
        // every participant, as far as Discord's 4,096-character embed description allows
        const int Limit = 3900;
        var shown = 0;
        foreach (var r in rows)
        {
            var medal = r.Rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"`{r.Rank,2}.`" };
            var line = $"{medal} **{Interactions.Md(Players.StripTag(r.DisplayName))}**: {r.Score.ToString("N0", CultureInfo.InvariantCulture)}";
            if (sb.Length + line.Length > Limit) break;
            sb.AppendLine(line);
            shown++;
        }
        if (shown < rows.Count) sb.AppendLine($"_…and {rows.Count - shown} more_");
        return new JsonObject
        {
            // a headline while it runs: it is what members' Discord notifications show for the new card (nobody is pinged)
            ["content"] = status is "scheduled" or "active"
                ? (c.Kind == "weekly"
                    ? $"📣 New weekly competition: **{Interactions.Md(c.Title)}**"
                    : $"📣 New challenge from **{Interactions.Md(Players.StripTag(c.CreatedByName ?? c.CreatedBy))}**: **{Interactions.Md(c.Title)}**")
                : "",
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = $"🏆 {c.Title}",
                ["description"] = sb.ToString().TrimEnd(),
                ["color"] = status switch { "active" => 0x2ECC71, "finalizing" => 0xF1C40F, "complete" => 0x3498DB, _ => 0x95A5A6 },
                ["footer"] = new JsonObject { ["text"] = $"Competition #{c.Id} · Enter the initials from your server nickname, e.g. Name | [ABC]" },
            }),
        };
    }
}
