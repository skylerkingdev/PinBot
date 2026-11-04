using System.Text.Json;
using Dapper;

namespace PinBot.Server.Vps;

/// <summary>Where the VPS database comes from. Faked in tests.</summary>
public interface IVpsSource
{
    /// <summary>The database's version (a timestamp that changes whenever it does).</summary>
    Task<long> LastUpdatedAsync(CancellationToken ct);
    /// <summary>The whole database, already limited to <see cref="VpsCatalog.MaxDownloadBytes"/>.</summary>
    Task<Stream> DatabaseAsync(CancellationToken ct);
}

/// <summary>The VPS GitHub repository (or a mirror): PinBot__Vps__Url points at vpsdb.json; empty turns VPS off.</summary>
public sealed class HttpVpsSource(string databaseUrl) : IVpsSource
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/VirtualPinballSpreadsheet/vps-db/main/db/vpsdb.json";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>lastUpdated.json sits at the repository root, next to the db folder.</summary>
    string LastUpdatedUrl => new Uri(new Uri(databaseUrl), "../lastUpdated.json").AbsoluteUri;

    public async Task<long> LastUpdatedAsync(CancellationToken ct)
    {
        var text = await _http.GetStringAsync(LastUpdatedUrl, ct);
        return long.TryParse(text.Trim(), out var v) ? v : throw new InvalidDataException("lastUpdated.json is not a number");
    }

    public async Task<Stream> DatabaseAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(databaseUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > VpsCatalog.MaxDownloadBytes) throw new InvalidDataException("the VPS database is larger than allowed");
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        var copy = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (copy.Length + n > VpsCatalog.MaxDownloadBytes) throw new InvalidDataException("the VPS database is larger than allowed");
            copy.Write(buffer, 0, n);
        }
        copy.Position = 0;
        return copy;
    }
}

/// <summary>
/// The VPS catalog PinBot matches table updates against (docs/TABLE-UPDATES.md, "If VPS is unavailable"). Optional by
/// design: it only adds links to posts. This is the only code that contacts VPS; nothing waits for it. A download is
/// accepted only if it looks like the VPS database; otherwise, and whenever VPS can't be reached, the last good copy
/// (kept in the database, so restarts don't need VPS) stays in use, or none, and posts go out without links.
/// </summary>
public sealed class VpsCatalog(Db db, IEnumerable<IVpsSource> sources, Discord.DiscordOptions discord, TimeProvider clock, ILogger<VpsCatalog> log)
    : BackgroundService
{
    readonly IVpsSource? source = sources.LastOrDefault();

    public const long MaxDownloadBytes = 50L * 1024 * 1024;
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);
    /// <summary>A real VPS database has thousands of games: fewer than this is not it.</summary>
    public int MinGames { get; init; } = 1000;

    volatile VpsIndex? _current;
    bool _loaded;
    bool _failing;
    int _failures;

    /// <summary>The catalog in use, or null (posts go out without VPS links).</summary>
    public VpsIndex? Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // VPS only serves table-update posts on Discord; tests drive RefreshAsync themselves
        if (source is null || !discord.Enabled || !discord.RunBackground) return;
        while (!stop.IsCancellationRequested)
        {
            var pause = CheckEvery;
            try { await RefreshAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // never fatal: the rest of PinBot runs on whatever catalog it has, or none
                _failures++;
                pause = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, TimeSpan.FromMinutes(15).Ticks << Math.Min(_failures - 1, 10)));
                if (!_failing) log.LogWarning("VPS catalog not updated ({Message}); using {State}; trying again in {Pause}",
                    ex.Message, _current is null ? "no catalog (posts without VPS links)" : "the last good copy", pause);
                _failing = true;
            }
            try { await Task.Delay(pause, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass: loads the stored copy (first pass only), then downloads VPS if it changed. Throws when VPS can't be
    /// reached or what came back isn't usable; the catalog in use is unchanged then.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            await LoadStoredAsync();
            _loaded = true;
        }
        if (source is null) return;
        var version = await source.LastUpdatedAsync(ct);
        if (_current is { } c && c.LastUpdated == version) { Recovered(); return; }

        VpsIndex fresh;
        await using (var stream = await source.DatabaseAsync(ct))
            fresh = VpsIndex.Parse(stream, version);
        if (Problem(fresh) is { } problem) throw new InvalidDataException(problem);

        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO vps_catalog (id, last_updated, games, data, saved_at_utc) VALUES (1, @version, @games, @data, @now)
            ON CONFLICT (id) DO UPDATE SET last_updated = EXCLUDED.last_updated, games = EXCLUDED.games, data = EXCLUDED.data,
                                           saved_at_utc = EXCLUDED.saved_at_utc
            """, new { version, games = fresh.Games.Count, data = fresh.ToJson(), now = clock.GetUtcNow().UtcDateTime });
        _current = fresh;
        log.LogInformation("VPS catalog updated: {Games} games", fresh.Games.Count);
        Recovered();
    }

    /// <summary>Why a download must not replace the catalog in use, or null when it's fine.</summary>
    internal string? Problem(VpsIndex fresh) =>
        fresh.Games.Count < MinGames ? $"the VPS download has only {fresh.Games.Count} games"
        : _current is { } old && fresh.Games.Count < old.Games.Count * 0.8 ? $"the VPS download lost too many games ({old.Games.Count} -> {fresh.Games.Count})"
        : null;

    void Recovered()
    {
        if (_failing) log.LogInformation("VPS catalog reachable again");
        _failing = false;
        _failures = 0;
    }

    async Task LoadStoredAsync()
    {
        try
        {
            await using var conn = await db.OpenAsync();
            var row = await conn.QueryFirstOrDefaultAsync<(long LastUpdated, string Data)?>("SELECT last_updated, data FROM vps_catalog WHERE id = 1");
            if (row is { } r) _current = VpsIndex.FromJson(r.Data, r.LastUpdated);
        }
        catch (JsonException ex)
        {
            log.LogWarning("stored VPS catalog unreadable ({Message}); downloading it again", ex.Message);
        }
    }
}
