using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PinBot.Agent.Discovery;
using PinBot.Contracts;

namespace PinBot.Agent.Sync;

/// <summary>The paired server. The credential is encrypted with DPAPI for the current Windows user.</summary>
public sealed record ServerLink(string Url, string CabinetId, string ProtectedCredential)
{
    public static ServerLink Create(string url, string cabinetId, string credential) =>
        new(url.TrimEnd('/'), cabinetId, Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(credential), null, DataProtectionScope.CurrentUser)));

    public string Credential() =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedCredential), null, DataProtectionScope.CurrentUser));

    public HttpClient CreateClient()
    {
        // longer than the server holds a waiting request (25 s), so that request is never cut short
        var http = new HttpClient { BaseAddress = new Uri(Url + "/"), Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential());
        return http;
    }
}

/// <summary>
/// Durable queue of result events: one JSON file per event, written atomically before any network call.
/// An event keeps its id across retries and is removed only when the server has given a final outcome.
/// </summary>
public sealed class Outbox(string dir)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Enqueue(ResultEvent ev)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ev.EventId + ".json");
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, ev, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public List<ResultEvent> Pending()
    {
        if (!Directory.Exists(dir)) return [];
        var list = new List<ResultEvent>();
        foreach (var f in Directory.GetFiles(dir, "*.json").Order())
        {
            try { if (JsonSerializer.Deserialize<ResultEvent>(File.ReadAllText(f), Json) is { } ev) list.Add(ev); }
            catch (JsonException) { File.Move(f, f + ".bad", overwrite: true); Log.Warn($"unreadable outbox entry set aside: {f}"); }
        }
        return list;
    }

    public void Remove(Guid eventId) => File.Delete(Path.Combine(dir, eventId + ".json"));

    /// <summary>
    /// Moves every queued event into a sibling folder named <paramref name="name"/> (kept, never sent), e.g. when the
    /// cabinet is paired with another server: its competition ids mean nothing there. Returns that folder, or null if
    /// nothing was queued.
    /// </summary>
    public string? SetAside(string name)
    {
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*.json");
        if (files.Length == 0) return null;
        var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir))!, $"{name}-{DateTime.UtcNow:yyyyMMddTHHmmss}");
        Directory.CreateDirectory(target);
        foreach (var f in files) File.Move(f, Path.Combine(target, Path.GetFileName(f)));
        return target;
    }
}

/// <summary>Talks to the PinBot server: inventory up, competitions down, results up. Never needed for gameplay.</summary>
public sealed class AgentSync(HttpClient http, Outbox outbox)
{
    static readonly string AgentVersion = AutoUpdate.Enabled ? AutoUpdate.Version.ToString() : typeof(AgentSync).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>Each table with a ROM; a ROM this cabinet recorded as rejecting competition files is not eligible.</summary>
    public static List<InventoryTable> Inventory(IEnumerable<TableInfo> tables, LocalState? state = null) =>
        tables.Where(t => t.Rom != null)
            .Select(t => state != null && t.Eligible && RejectedRom.Applies(state, t.Rom, AutoUpdate.Version)
                ? new InventoryTable(Path.GetFileNameWithoutExtension(t.TablePath), t.Rom!, false, RejectedRom.Reason)
                : new InventoryTable(Path.GetFileNameWithoutExtension(t.TablePath), t.Rom!, t.Eligible, t.Eligible ? null : t.Problem))
            .ToList();

    public async Task PushInventoryAsync(IEnumerable<TableInfo> tables, CancellationToken ct = default)
    {
        var r = await http.PutAsJsonAsync($"api/v1/agent/inventory", new InventoryRequest(Inventory(tables, LocalState.Load()), AgentVersion), ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>The newest agent release the server offered at the last <see cref="PullCompetitionsAsync"/> (0: none).</summary>
    public long LatestRelease { get; private set; }

    /// <summary>Server competitions mapped onto this VPin's actual NVRAM files.</summary>
    public async Task<List<LocalCompetition>> PullCompetitionsAsync(IReadOnlyList<TableInfo> tables, CancellationToken ct = default)
    {
        var resp = await http.GetFromJsonAsync<AgentCompetitionsResponse>("api/v1/agent/competitions", ct)
                   ?? throw new InvalidDataException("empty competitions response");
        LatestRelease = resp.LatestRelease;
        var skew = DateTime.UtcNow - resp.ServerTimeUtc;
        if (Math.Abs(skew.TotalMinutes) > 2)
            Log.Warn($"this PC's clock differs from the server's by {skew.TotalMinutes:0} min; deadlines are judged by the server clock");
        return Map(resp.Competitions, tables);
    }

    /// <summary>
    /// Like <see cref="PullCompetitionsAsync"/>, but the server holds the request until competitions change from
    /// <paramref name="known"/> (or 25 s pass), so new competitions arrive within a second or two.
    /// </summary>
    public async Task<(List<LocalCompetition> Competitions, long Version, long LatestRelease)> WaitForCompetitionsAsync(
        IReadOnlyList<TableInfo> tables, long known, CancellationToken ct = default)
    {
        var resp = await http.GetFromJsonAsync<AgentCompetitionsResponse>($"api/v1/agent/competitions?known={known}&wait={LiveWaitSeconds}", ct)
                   ?? throw new InvalidDataException("empty competitions response");
        return (Map(resp.Competitions, tables), resp.Version, resp.LatestRelease);
    }

    /// <summary>How long the server holds the waiting request (its maximum): fewer wake-ups than a shorter wait.</summary>
    public const int LiveWaitSeconds = 25;

    /// <summary>Tells the server a table started or no table runs any more (busy, not offline, on cards and status).</summary>
    public async Task SetPlayingAsync(bool playing, CancellationToken ct = default)
    {
        var r = await http.PutAsJsonAsync("api/v1/agent/activity", new AgentActivity(playing), ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Reports which competitions this cabinet is ready for; shown on the Discord card.</summary>
    public async Task PushStatesAsync(IReadOnlyList<CompetitionState> states, CancellationToken ct = default)
    {
        var r = await http.PutAsJsonAsync("api/v1/agent/competition-states", new CompetitionStatesRequest(states), ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>One local competition per (competition, NVRAM file): related ROM revisions use different files.</summary>
    public static List<LocalCompetition> Map(IEnumerable<AgentCompetition> competitions, IReadOnlyList<TableInfo> tables) =>
        competitions.SelectMany(c => tables
                .Where(t => t.Eligible && t.NvPath != null && c.RomIds.Contains(t.Rom!, StringComparer.OrdinalIgnoreCase))
                .GroupBy(t => t.NvPath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new LocalCompetition(c.Id.ToString(), g.First().Rom!, g.Key, c.Title, c.StartsAtUtc, c.EndsAtUtc)))
            .ToList();

    /// <summary>Uploads queued events in batches; removes each one the server answered. Returns how many remain.</summary>
    public async Task<int> FlushOutboxAsync(CancellationToken ct = default)
    {
        var pending = outbox.Pending();
        foreach (var batch in pending.Chunk(100))
        {
            var r = await http.PostAsJsonAsync("api/v1/agent/results", new ResultsRequest(batch), ct);
            r.EnsureSuccessStatusCode();
            var resp = await r.Content.ReadFromJsonAsync<ResultsResponse>(cancellationToken: ct) ?? throw new InvalidDataException("empty results response");
            foreach (var o in resp.Outcomes)
            {
                var ev = batch.FirstOrDefault(e => e.EventId == o.EventId);
                if (ev is null) continue;
                var line = $"[sync] competition {ev.CompetitionId}: {ev.Initials} {ev.Score:N0} -> {o.Status}{(o.Reason is null ? "" : $" ({o.Reason})")}";
                if (o.Status == ResultStatus.Accepted) Log.Good(line); else Log.Warn(line);
                outbox.Remove(o.EventId);
            }
        }
        return outbox.Pending().Count;
    }
}
