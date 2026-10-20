using Dapper;
using Npgsql;
using PinBot.Contracts;

namespace PinBot.Server;

public sealed class CompetitionRow
{
    public long Id { get; set; }
    public string CanonicalGameId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string? CreatedByName { get; set; }
    public string? DiscordChannelId { get; set; }
    public string? DiscordMessageId { get; set; }
    public string? PublishedHash { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public DateTime? EndedEarlyAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? PinState { get; set; }

    /// <summary>Gameplay deadline: sessions must exit strictly before this.</summary>
    public DateTime EffectiveEndUtc => EndedEarlyAtUtc is { } e && e < EndsAtUtc ? e : EndsAtUtc;

    /// <summary>Derived from the clock, so there is nothing to keep in sync and no scheduler is needed.</summary>
    public string Status(DateTime now, TimeSpan grace) =>
        CancelledAtUtc != null ? "cancelled"
        : now < StartsAtUtc ? "scheduled"
        : now < EffectiveEndUtc ? "active"
        : now < EffectiveEndUtc + grace ? "finalizing"   // gameplay closed; results of games finished in time can still arrive
        : "complete";
}

/// <param name="Where">whose cabinets have this game, "all", or null with a single cabinet (shown in the pick list)</param>
public sealed record GameInfo(string CanonicalGameId, string Title, IReadOnlyList<string> RomIds, int Cabinets, string? Where = null)
{
    /// <summary>How the game is offered in Discord: the title, plus where it is installed.</summary>
    public string Choice => Where is null ? Title : $"{Title} · {Where}";
}

public sealed record LeaderboardRow(int Rank, string DiscordUserId, string DisplayName, long Score, string Initials, string RomId, DateTime AchievedAtUtc);

public abstract record CreateResult
{
    public sealed record Created(CompetitionRow Competition) : CreateResult;
    public sealed record Conflict(CompetitionRow Existing) : CreateResult;
    public sealed record NotEligible(string Reason) : CreateResult;
}

public static class Competitions
{
    const string Columns = "id, canonical_game_id, title, kind, created_by, created_by_name, starts_at_utc, ends_at_utc, ended_early_at_utc, cancelled_at_utc, discord_channel_id, discord_message_id, published_hash, pin_state";

    /// <summary>ROM ids are case-insensitive in PinMAME; the server stores them lowercase.</summary>
    public static string NormalizeRom(string rom) => rom.Trim().ToLowerInvariant();

    public static async Task<string> CanonicalGameAsync(NpgsqlConnection conn, string rom, NpgsqlTransaction? tx = null) =>
        await conn.ExecuteScalarAsync<string?>("SELECT canonical_game_id FROM rom_alias WHERE rom_id = @rom",
            new { rom = NormalizeRom(rom) }, tx) ?? NormalizeRom(rom);

    /// <summary>Games with an eligible table on at least one active cabinet.</summary>
    /// <param name="viewer">the Discord member the list is for: in a large community the count says whether one of the cabinets is theirs</param>
    public static async Task<List<GameInfo>> EligibleGamesAsync(NpgsqlConnection conn, string? viewer = null)
    {
        var rows = (await conn.QueryAsync<(string Game, string Rom, string Table, string Cabinet, string? OwnerId)>("""
            SELECT COALESCE(a.canonical_game_id, i.rom_id), i.rom_id, i.table_name, i.cabinet_id, c.paired_by
            FROM installation i
            JOIN cabinet c ON c.id = i.cabinet_id AND c.revoked_at_utc IS NULL
            LEFT JOIN rom_alias a ON a.rom_id = i.rom_id
            WHERE i.eligible
            """)).ToList();
        var games = rows.GroupBy(r => r.Game)
            .Select(g => new GameInfo(g.Key, g.OrderBy(r => r.Table).First().Table,
                g.Select(r => r.Rom).Distinct().Order().ToList(), g.Select(r => r.Cabinet).Distinct().Count()))
            .ToList();
        // Only the same ROM revision is the same game (revisions can differ in rules and scoring). When cabinets have
        // different revisions of one machine, both are listed with the ROM in the title. Every choice says whose
        // cabinets have it, by their owners' tags ("all" when every cabinet does), so a player picks a game they and their
        // friends can play.
        var shared = games.GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        var allCabinets = rows.Select(r => r.Cabinet).Distinct().Count();
        var labels = await Players.CabinetLabelsAsync(conn);
        string? Owners(GameInfo g)
        {
            if (allCabinets <= 1) return null; // one cabinet: nothing to tell apart
            if (g.Cabinets == allCabinets) return "all";
            var owners = rows.Where(r => r.Game == g.CanonicalGameId).Select(r => labels.GetValueOrDefault(r.Cabinet, "?")).Distinct().Order().ToList();
            if (owners.Count <= 5) return string.Join(" ", owners);
            var yours = viewer != null && rows.Any(r => r.Game == g.CanonicalGameId && r.OwnerId == viewer);
            return $"{g.Cabinets} of {allCabinets} VPins{(yours ? ", yours included" : "")}";
        }
        return games.Select(g => (shared.Contains(g) ? g with { Title = $"{g.Title} · {string.Join("/", g.RomIds)}" } : g) with { Where = Owners(g) })
            .OrderBy(g => g.Title).ToList();
    }

    /// <summary>
    /// Creates a competition for the game of <paramref name="gameOrRom"/>. Overlap with another competition for the
    /// same game is rejected by the database's exclusion constraint, so concurrent requests cannot both succeed.
    /// </summary>
    public static async Task<CreateResult> CreateAsync(NpgsqlConnection conn, string gameOrRom, string kind, string createdBy,
        DateTime startsUtc, DateTime endsUtc, DateTime now, string? createdByName = null)
    {
        var game = await CanonicalGameAsync(conn, gameOrRom);
        var info = (await EligibleGamesAsync(conn)).FirstOrDefault(g => g.CanonicalGameId == game);
        if (info is null) return new CreateResult.NotEligible($"no connected VPin has an eligible table for '{game}'");
        try
        {
            // Serialize creation per game: concurrent inserts into the exclusion constraint can otherwise deadlock
            // (PostgreSQL then aborts one with 40P01). The constraint remains the guarantee; this lock keeps it orderly.
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('competition:' || @game))", new { game }, tx);
            var row = await conn.QuerySingleAsync<CompetitionRow>($"""
                INSERT INTO competition (canonical_game_id, title, kind, created_by, created_by_name, starts_at_utc, ends_at_utc, created_at_utc)
                VALUES (@game, @title, @kind, @createdBy, @createdByName, @startsUtc, @endsUtc, @now)
                RETURNING {Columns}
                """, new { game, title = info.Title, kind, createdBy, createdByName, startsUtc, endsUtc, now }, tx);
            await tx.CommitAsync();
            return new CreateResult.Created(row);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            var existing = await conn.QueryFirstAsync<CompetitionRow>($"""
                SELECT {Columns} FROM competition
                WHERE canonical_game_id = @game AND cancelled_at_utc IS NULL AND span && tstzrange(@startsUtc, @endsUtc, '[)')
                ORDER BY starts_at_utc LIMIT 1
                """, new { game, startsUtc, endsUtc });
            return new CreateResult.Conflict(existing);
        }
    }

    public static Task<CompetitionRow?> GetAsync(NpgsqlConnection conn, long id, NpgsqlTransaction? tx = null) =>
        conn.QuerySingleOrDefaultAsync<CompetitionRow>($"SELECT {Columns} FROM competition WHERE id = @id", new { id }, tx);

    /// <summary>Ends a running competition now. Idempotent; results from sessions that exited before now still count.</summary>
    public static async Task<CompetitionRow?> EndEarlyAsync(NpgsqlConnection conn, long id, DateTime now)
    {
        await conn.ExecuteAsync("""
            UPDATE competition SET ended_early_at_utc = @now
            WHERE id = @id AND cancelled_at_utc IS NULL AND ended_early_at_utc IS NULL AND ends_at_utc > @now
            """, new { id, now });
        return await GetAsync(conn, id);
    }

    public static async Task<List<CompetitionRow>> ListAsync(NpgsqlConnection conn) =>
        (await conn.QueryAsync<CompetitionRow>($"SELECT {Columns} FROM competition ORDER BY starts_at_utc DESC")).ToList();

    /// <summary>
    /// What a cabinet needs: competitions not yet past their delivery window, each with the ROM revisions this
    /// cabinet has eligible for that game. Competitions this cabinet cannot take part in are left out.
    /// </summary>
    public static async Task<List<AgentCompetition>> ForCabinetAsync(NpgsqlConnection conn, string cabinetId, DateTime now, TimeSpan grace)
    {
        var comps = (await conn.QueryAsync<CompetitionRow>($"""
            SELECT {Columns} FROM competition
            WHERE cancelled_at_utc IS NULL AND LEAST(ends_at_utc, COALESCE(ended_early_at_utc, ends_at_utc)) > @since
            """, new { since = now - grace })).ToList();
        var roms = (await conn.QueryAsync<(string Rom, string Game)>("""
            SELECT DISTINCT i.rom_id, COALESCE(a.canonical_game_id, i.rom_id)
            FROM installation i LEFT JOIN rom_alias a ON a.rom_id = i.rom_id
            WHERE i.cabinet_id = @cabinetId AND i.eligible
            """, new { cabinetId })).ToList();
        return comps
            .Select(c => (c, roms: roms.Where(r => r.Game == c.CanonicalGameId).Select(r => r.Rom).Order().ToList()))
            .Where(x => x.roms.Count > 0)
            .Select(x => new AgentCompetition(x.c.Id, x.c.CanonicalGameId, x.c.Title, x.roms,
                x.c.StartsAtUtc, x.c.EffectiveEndUtc, x.c.Status(now, grace)))
            .ToList();
    }

    /// <summary>
    /// Best accepted score per Discord member, highest first. Ties share a rank (1, 1, 3). An earlier achievement
    /// is listed first among ties but ranked equally.
    /// </summary>
    public static async Task<List<LeaderboardRow>> LeaderboardAsync(NpgsqlConnection conn, long competitionId) =>
        (await conn.QueryAsync<LeaderboardRow>("""
            WITH best AS (
                SELECT DISTINCT ON (r.discord_user_id) r.discord_user_id, r.score, r.initials, r.rom_id, r.session_exited_at_utc
                FROM result r
                WHERE r.competition_id = @competitionId AND r.status = 'accepted'
                ORDER BY r.discord_user_id, r.score DESC, r.session_exited_at_utc
            )
            SELECT CAST(RANK() OVER (ORDER BY b.score DESC) AS int) AS rank, b.discord_user_id,
                   COALESCE((SELECT m.display_name FROM member_initial m WHERE m.discord_user_id = b.discord_user_id LIMIT 1),
                            (SELECT g.display_name FROM guest g WHERE g.id = b.discord_user_id), b.discord_user_id) AS display_name,
                   b.score, b.initials, b.rom_id, b.session_exited_at_utc AS achieved_at_utc
            FROM best b
            ORDER BY b.score DESC, b.session_exited_at_utc
            """, new { competitionId })).ToList();
}

/// <summary>
/// Lets cabinets hear about a new or ended competition within a second: they ask for competitions with the version
/// they know and wait=seconds, and the request returns as soon as something changes (or when the wait is over).
/// Cabinets sit behind home routers, so they cannot be called; a waiting request is the push.
/// </summary>
public sealed class CompetitionChanges
{
    long _version = DateTime.UtcNow.Ticks; // a restarted server never matches a version a cabinet knows from before
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Version => Interlocked.Read(ref _version);

    public void Notify()
    {
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _changed, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Returns when the version differs from <paramref name="known"/>, after <paramref name="max"/>, or when cancelled.</summary>
    public async Task WaitAsync(long known, TimeSpan max, CancellationToken ct)
    {
        var changed = Volatile.Read(ref _changed).Task;
        if (Version != known) return;
        try { await changed.WaitAsync(max, ct); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
    }
}

/// <summary>
/// ROMs that are the same game as another ROM except for coin play / free play (rom-variants.json, generated from the
/// memory maps by tools/PinBot.Variants: PinMAME names both identically apart from "Coin Play"/"Free Play" and both use
/// the same memory map). They are linked to their base ROM through rom_alias, so e.g. cv_20h and cv_20hc are one game.
/// Any other difference keeps ROMs apart. Manual aliases win; a variant with its own competition still running is
/// linked at a later start, never in the middle of it.
/// </summary>
public static class RomVariants
{
    public static IReadOnlyDictionary<string, string> All { get; } = Load();

    static Dictionary<string, string> Load()
    {
        using var s = typeof(RomVariants).Assembly.GetManifestResourceStream("PinBot.Server.rom-variants.json")
                      ?? throw new InvalidOperationException("rom-variants.json not embedded");
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
    }

    public static async Task ApplyAsync(NpgsqlConnection conn, DateTime now) =>
        await conn.ExecuteAsync("""
            INSERT INTO rom_alias (rom_id, canonical_game_id)
            SELECT @Key, @Value
            WHERE NOT EXISTS (SELECT 1 FROM competition WHERE canonical_game_id = @Key AND cancelled_at_utc IS NULL
                                AND LEAST(ends_at_utc, COALESCE(ended_early_at_utc, ends_at_utc)) + interval '1 day' > @now)
            ON CONFLICT (rom_id) DO NOTHING
            """, All.Select(kv => new { kv.Key, kv.Value, now }));
}
