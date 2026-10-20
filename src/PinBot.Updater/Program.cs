using System.Reflection;
using System.Text.Json;
using PinBot.Contracts;
using PinBot.Updater;

// Keeps a PinBot server on the releases its maintainer publishes, without anyone logging in to the host:
//   pinbot-updater                  check every hour (PINBOT_UPDATE_INTERVAL_MINUTES) and install what is due
//   pinbot-updater --healthcheck    healthy while the server image is present
//   pinbot-updater --recreate-self  run in a throwaway container: replaces the updater container with its new image
// Settings (env): PINBOT_UPDATE_RING early|stable (default stable), PINBOT_UPDATE_DELAY_HOURS (48), PINBOT_UPDATE_TOKEN
// (only while the repository is private), PINBOT_UPDATE_REPO (default: the repository this build came from).
// State (installed, previous, refused releases) lives in /state/updater.json; the server reads it to announce updates.
var composeDir = Environment.GetEnvironmentVariable("PINBOT_COMPOSE_DIR") ?? "/deploy";
var stateDir = Environment.GetEnvironmentVariable("PINBOT_UPDATE_STATE") ?? "/state";
var docker = new Docker(composeDir);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

if (args.Contains("--healthcheck"))
    return await docker.ImageExistsAsync(Docker.ServerCurrent, CancellationToken.None) ? 0 : 1;
await docker.UseHostProjectPathAsync(CancellationToken.None);
if (args.Contains("--recreate-self"))
{
    await Task.Delay(TimeSpan.FromSeconds(3)); // let the old updater finish writing its log line
    await docker.RecreateAsync("updater", CancellationToken.None);
    return 0;
}

static string? Meta(string key) => typeof(Ring).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;
static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

var publicKey = Meta("UpdatePublicKey");
var repo = Env("PINBOT_UPDATE_REPO") ?? Meta("UpdateRepo");
var early = string.Equals(Env("PINBOT_UPDATE_RING"), "early", StringComparison.OrdinalIgnoreCase);
var delay = TimeSpan.FromHours(double.TryParse(Env("PINBOT_UPDATE_DELAY_HOURS"), out var h) ? h : 48);
var every = TimeSpan.FromMinutes(double.TryParse(Env("PINBOT_UPDATE_INTERVAL_MINUTES"), out var m) && m >= 5 ? m : 60);
if (publicKey is null || repo is null)
{
    Log("this updater was built without a release key or repository: it does nothing (development build)");
    await Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => { });
    return 0;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var releases = new GitHubReleases(http, repo, Env("PINBOT_UPDATE_TOKEN"));
var state = UpdaterState.Load(stateDir);
Log($"watching {repo} releases, {(early ? "early ring: installs each release at once" : $"stable ring: installs a release once it has been out {delay.TotalHours:0} h")}");

while (!stop.IsCancellationRequested)
{
    try { await PassAsync(); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (Exception ex) { Log($"check failed: {ex.Message}"); } // the network, GitHub or Docker: next hour
    try { await Task.Delay(every, stop.Token); } catch (OperationCanceledException) { break; }
}
return 0;

async Task PassAsync()
{
    var ct = stop.Token;
    // 0 until this updater installed a verified release: what compose pulled on a fresh start is replaced at once
    var current = state.Installed;
    var all = await releases.ListAsync(ct);
    if (Ring.Choose(all, early, delay, DateTime.UtcNow, current, state.Refused) is not { } next) return;
    var r = next.Release;
    if (!UpdateSigning.VerifyServer(r, publicKey))
    {
        state.Refuse(r.Version, "its signature does not match this server's release key");
        Log($"release {r.Version} refused: signature does not match");
        return;
    }

    Log($"installing release {r.Version} (now {(current > 0 ? current.ToString() : "unverified")})");
    await docker.CheckedAsync(ct, "pull", r.ServerImage);
    var hadOne = await docker.ImageExistsAsync(Docker.ServerCurrent, ct);
    if (hadOne) await docker.CheckedAsync(ct, "tag", Docker.ServerCurrent, Docker.ServerPrevious);
    await docker.CheckedAsync(ct, "tag", r.ServerImage, Docker.ServerCurrent);
    string? failure;
    try { failure = await docker.TryRecreateAsync("server", ct) ?? (await ServerRunsAsync(r.Version, ct) ? null : "the server did not answer within 5 minutes"); }
    catch (Exception ex) when (ex is not OperationCanceledException) { failure = ex.Message; }
    if (failure is null)
    {
        state.Install(r.Version, previous: current);
        Log($"release {r.Version} is running");
        await UpdateSelfAsync(r, ct);
        return;
    }

    // the new server did not come up: back to the one that worked, and never this release again
    Log($"release {r.Version} failed: {failure}");
    state.Refuse(r.Version, "the server did not start with it, so this server went back to the previous release");
    if (!hadOne) { Log("there is no previous server image to go back to"); return; }
    Log($"going back to {(current > 0 ? $"release {current}" : "the previous image")}");
    await docker.CheckedAsync(ct, "tag", Docker.ServerPrevious, Docker.ServerCurrent);
    if (await docker.TryRecreateAsync("server", ct) is { } stillBroken)
        Log($"going back failed too: {stillBroken}. Start the project again in Container Manager (Action → Build).");
}

// The server answers /healthz with its release number once it has started (database migrated, ready for requests).
async Task<bool> ServerRunsAsync(long version, CancellationToken ct)
{
    var until = DateTime.UtcNow + TimeSpan.FromMinutes(5);
    while (DateTime.UtcNow < until)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("http://server:8080/healthz", ct));
            if (doc.RootElement.TryGetProperty("release", out var rel) && rel.TryGetInt64(out var running) && running == version) return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
    }
    return false;
}

// After a successful server update: if the release carries a different updater, switch to it. A container cannot
// replace itself (it would be stopped half way), so a throwaway container with the new image does the swap.
async Task UpdateSelfAsync(ServerRelease r, CancellationToken ct)
{
    if (r.UpdaterImage == state.Updater) return;
    var self = Environment.GetEnvironmentVariable("HOSTNAME");
    var tag = Environment.GetEnvironmentVariable("PINBOT_UPDATER_IMAGE");
    if (self is null || tag is null) return;
    await docker.CheckedAsync(ct, "pull", r.UpdaterImage);
    await docker.CheckedAsync(ct, "tag", r.UpdaterImage, tag);
    state.SetUpdater(r.UpdaterImage);
    Log("switching to the updater of this release");
    await docker.CheckedAsync(ct, "run", "-d", "--rm", "--volumes-from", self, tag, "--recreate-self");
}

static void Log(string line) => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {line}");

/// <summary>What this server runs and what it refused, in /state/updater.json (also read by the server for its notices).</summary>
public sealed class UpdaterState
{
    public long Installed { get; set; }
    public long Previous { get; set; }
    public string? Updater { get; set; }
    public List<Refused> Refused { get; set; } = [];

    string _path = "";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static UpdaterState Load(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "updater.json");
        UpdaterState s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { s = new(); }
        s._path = path;
        return s;
    }

    public void Install(long version, long previous) { Installed = version; Previous = previous; Save(); }
    public void Refuse(long version, string reason) { Refused.Add(new(version, reason, DateTime.UtcNow)); Save(); }
    public void SetUpdater(string image) { Updater = image; Save(); }

    void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
