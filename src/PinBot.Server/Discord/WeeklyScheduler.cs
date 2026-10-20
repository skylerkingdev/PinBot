using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace PinBot.Server.Discord;

/// <summary>The weekly slot: start day and time in a time zone, running for <see cref="Days"/> days.</summary>
public sealed record WeeklySchedule(DayOfWeek Day, TimeSpan Time, TimeZoneInfo Zone, int Days = 7)
{
    public override string ToString() => $"{Day} at {Time:hh\\:mm} ({Zone.Id})";
}

/// <summary>
/// When enabled from Discord, starts one competition per weekly slot on a random eligible game that is free for the
/// whole week, avoiding the last few weekly games. The schedule is set by a server manager with /pinbot-admin weekly; nothing
/// runs until then, and a change only applies from the next slot (never starting a weekly by surprise).
/// Idempotent per slot (database unique index), safe to run every minute.
/// </summary>
public sealed class WeeklyScheduler(Db db, DiscordOptions discord, IDiscordApi api, TimeProvider clock,
    LeaderboardPublisher publisher, CompetitionChanges changes, ILogger<WeeklyScheduler> log)
{
    const int AvoidRecent = 4;

    /// <summary>The schedule set from Discord, or null if none has been set yet.</summary>
    public static async Task<WeeklySchedule?> ScheduleAsync(NpgsqlConnection conn) =>
        await Settings.GetAsync(conn, Settings.WeeklyDay) is { } d
        && await Settings.GetAsync(conn, Settings.WeeklyTime) is { } t
        && await Settings.GetAsync(conn, Settings.WeeklyTimeZone) is { } z
            ? new WeeklySchedule(Enum.Parse<DayOfWeek>(d), TimeSpan.Parse(t, CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById(z))
            : null;

    /// <summary>The most recent slot start at or before <paramref name="nowUtc"/>, in UTC.</summary>
    public static DateTime CurrentSlot(WeeklySchedule w, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, w.Zone);
        var date = local.Date.AddDays(-(((int)local.DayOfWeek - (int)w.Day + 7) % 7));
        var slot = date + w.Time;
        if (slot > local) slot = slot.AddDays(-7);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(slot, DateTimeKind.Unspecified), w.Zone);
    }

    /// <summary>The first slot at or after <paramref name="notBeforeUtc"/> (DST-safe: computed in local time).</summary>
    public static DateTime NextSlot(WeeklySchedule w, DateTime notBeforeUtc)
    {
        var slot = CurrentSlot(w, notBeforeUtc);
        return slot >= notBeforeUtc ? slot : CurrentSlot(w, slot.AddDays(7).AddHours(12));
    }

    /// <summary>The weekly running now, if any.</summary>
    public static Task<CompetitionRow?> RunningAsync(NpgsqlConnection conn, DateTime now) =>
        conn.QuerySingleOrDefaultAsync<CompetitionRow>("""
            SELECT id, canonical_game_id, title, kind, created_by, created_by_name, starts_at_utc, ends_at_utc, ended_early_at_utc,
                   cancelled_at_utc, discord_channel_id, discord_message_id, published_hash
            FROM competition WHERE kind = 'weekly' AND cancelled_at_utc IS NULL
              AND starts_at_utc <= @now AND LEAST(ends_at_utc, COALESCE(ended_early_at_utc, ends_at_utc)) > @now
            ORDER BY starts_at_utc DESC LIMIT 1
            """, new { now });

    /// <summary>When the next weekly will start: the first slot after both "now" and the end of the running weekly.</summary>
    public static async Task<DateTime> NextStartAsync(NpgsqlConnection conn, WeeklySchedule w, DateTime now)
    {
        var running = await RunningAsync(conn, now);
        var notBefore = await Settings.GetAsync(conn, Settings.WeeklyNotBefore) is { } nb ? DateTime.Parse(nb, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : now;
        var floor = new[] { now, notBefore, running?.EffectiveEndUtc ?? now }.Max();
        return NextSlot(w, floor);
    }

    /// <summary>
    /// Turning the weekly on (first setup, or again after a pause) starts one right away, whatever the day, so people
    /// can play immediately. It runs until the next slot, or the one after if that is under 2 days away.
    /// </summary>
    public async Task<CompetitionRow?> StartNowAsync(NpgsqlConnection conn, WeeklySchedule schedule, DateTime now)
    {
        if (await RunningAsync(conn, now) is not null) return null;
        var end = NextSlot(schedule, now.AddDays(2));
        return await CreateWeeklyAsync(conn, now, end, now);
    }

    /// <summary>
    /// Picks a random eligible game that is free for the whole period, on every cabinet if possible, avoiding the last few
/// weekly games when possible.
    /// Null if no game is free (or another server instance created this slot first).
    /// </summary>
    async Task<CompetitionRow?> CreateWeeklyAsync(NpgsqlConnection conn, DateTime start, DateTime end, DateTime now)
    {
        var taken = (await conn.QueryAsync<string>("""
            SELECT canonical_game_id FROM competition
            WHERE cancelled_at_utc IS NULL AND span && tstzrange(@start, @end, '[)')
            """, new { start, end })).ToHashSet();
        var recent = (await conn.QueryAsync<string>(
            "SELECT canonical_game_id FROM competition WHERE kind = 'weekly' ORDER BY starts_at_utc DESC LIMIT @AvoidRecent", new { AvoidRecent })).ToHashSet();
        var free = (await Competitions.EligibleGamesAsync(conn)).Where(g => !taken.Contains(g.CanonicalGameId)).ToList();
        // everyone should be able to play the weekly: games every cabinet has come first
        var everywhere = free.Where(g => g.Where is null or "all").ToList();
        if (everywhere.Count > 0) free = everywhere;
        var pool = free.Where(g => !recent.Contains(g.CanonicalGameId)).ToList();
        if (pool.Count == 0) pool = free; // small libraries: allow a repeat rather than skip

        foreach (var game in pool.OrderBy(_ => Random.Shared.Next()))
        {
            try
            {
                if (await Competitions.CreateAsync(conn, game.CanonicalGameId, "weekly", "weekly", start, end, now, "Weekly") is CreateResult.Created c)
                {
                    log.LogInformation("weekly competition #{Id}: {Title}", c.Competition.Id, c.Competition.Title);
                    changes.Notify();
                    publisher.Wake();
                    return c.Competition;
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return null; // another instance created this slot's weekly first
            }
        }
        return null;
    }

    public async Task<CompetitionRow?> RunOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var conn = await db.OpenAsync();
        if (await Settings.GetAsync(conn, Settings.WeeklyEnabled) != "true") return null;
        if (await ScheduleAsync(conn) is not { } schedule) return null;
        if (await RunningAsync(conn, now) is not null) return null; // never two weeklies at once
        var slot = CurrentSlot(schedule, now);
        // a schedule set or changed after this slot began only applies from the next one
        if (await Settings.GetAsync(conn, Settings.WeeklyNotBefore) is { } nb
            && slot < DateTime.Parse(nb, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
            return null;
        if (now >= slot.AddDays(1)) return null; // server was off for the whole first day: skip this week rather than start late
        var slotKey = slot.ToString("O", CultureInfo.InvariantCulture);
        if (await Settings.GetAsync(conn, Settings.WeeklySkippedSlot) == slotKey) return null;
        if (await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM competition WHERE kind = 'weekly' AND starts_at_utc = @slot AND cancelled_at_utc IS NULL", new { slot }) > 0)
            return null;

        if (await CreateWeeklyAsync(conn, slot, slot.AddDays(schedule.Days), now) is { } created) return created;

        await Settings.SetAsync(conn, Settings.WeeklySkippedSlot, slotKey);
        log.LogWarning("no eligible game for the weekly competition starting {Slot}", slot);
        if (discord.ChannelFor("weekly") is { } channel)
        {
            try
            {
                await api.PostMessageAsync(channel, new JsonObject
                {
                    ["content"] = "📅 No weekly competition this week: no eligible game is free on a connected VPin.",
                    ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
                }, ct);
            }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException) { log.LogWarning("skip notice not posted: {Message}", ex.Message); }
        }
        return null;
    }
}
