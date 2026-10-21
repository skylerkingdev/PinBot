using System.Reflection;
using System.Text.Json;
using PinBot.Contracts;
using PinBot.Updater;

// Keeps a PinBot server on the releases its maintainer publishes, without anyone logging in to the host:
//   pinbot-updater                  check every hour (PINBOT_UPDATE_INTERVAL_MINUTES) and install what is due
//   pinbot-updater --healthcheck    healthy while the server image is present
//   pinbot-updater --recreate-self  run in a throwaway container: replaces the updater container with its new image
// Settings (env): PINBOT_UPDATE_RING early|stable (default stable), PINBOT_UPDATE_DELAY_HOURS (48), PINBOT_UPDATE_REPO
// (default: the repository this build came from), PINBOT_UPDATE_TOKEN (optional: a GitHub token raises the rate limit).
// State (installed release, an unfinished swap, refused releases) lives in /state/updater.json; the server reads it to
// announce skipped releases.
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
    if (!docker.Ready) { Log("cannot find this project's folder on the host: the updater is not replaced"); return 1; }
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
if (!docker.Ready)
    Log("cannot find this project's folder on the host (the /deploy mount): releases are checked but not installed. " +
        "Start the project from its own folder (Container Manager → Project → Build).");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var releases = new GitHubReleases(http, repo, Env("PINBOT_UPDATE_TOKEN"));
var state = UpdaterState.Load(stateDir);
Log($"watching {repo} releases, {(early ? "early ring: installs each release at once" : $"stable ring: installs a release once it has been out {delay.TotalHours:0} h")}");

try { await RecoverAsync(stop.Token); }
catch (Exception ex) when (ex is not OperationCanceledException) { Log($"recovery failed: {ex.Message}"); }

while (!stop.IsCancellationRequested)
{
    try { await PassAsync(); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (Exception ex) { Log($"check failed: {ex.Message}"); } // the network, GitHub or Docker: next hour
    try { await Task.Delay(every, stop.Token); } catch (OperationCanceledException) { break; }
}
return 0;

// At start: finish what a stopped updater left (a NAS reboot or a Build during a swap), and learn the running release
// when the state was lost, so a stable server does not jump ahead of its 48 hours.
async Task RecoverAsync(CancellationToken ct)
{
    if (state.Pending is { } p && docker.Ready)
    {
        Log($"a swap to release {p.Version} was interrupted: going back to the server that ran before");
        if (p.PreviousImage is { } previous && await docker.ImageIdAsync(previous, ct) is not null)
        {
            await docker.CheckedAsync(ct, "tag", previous, Docker.ServerCurrent);
            if (await docker.TryRecreateAsync("server", ct) is { } error) Log($"going back failed: {error}");
        }
        state.ClearPending(); // not refused: nothing says the release itself was at fault; it is tried again
    }
    if (state.Installed == 0 && await RunningReleaseAsync(ct) is { } running)
    {
        state.Adopt(running);
        Log($"the server runs release {running}: counted as installed");
    }
}

async Task PassAsync()
{
    var ct = stop.Token;
    var current = state.Installed;
    var all = await releases.ListAsync(ct);
    if (Ring.Choose(all, early, delay, DateTime.UtcNow, current, state.Refused) is not { } next) return;
    if (!docker.Ready) { Log($"release {next.Version} is due, but this project's folder on the host is unknown: not installed"); return; }

    var r = await releases.FetchAsync(next, ct);
    if (r is null || r.Version != next.Version)
    {
        state.Refuse(next.Version, "its description could not be read");
        Log($"release {next.Version} refused: server-release.json is missing, unreadable or for another release");
        return;
    }
    if (!UpdateSigning.VerifyServer(r, publicKey))
    {
        state.Refuse(r.Version, "its signature does not match this server's release key");
        Log($"release {r.Version} refused: signature does not match");
        return;
    }

    Log($"installing release {r.Version} (now {(current > 0 ? current.ToString() : "unverified")})");
    await docker.CheckedAsync(ct, "pull", r.ServerImage);
    var newId = await docker.ImageIdAsync(r.ServerImage, ct) ?? throw new InvalidOperationException($"pulled {r.ServerImage} but cannot find it");
    var oldId = await docker.ImageIdAsync(Docker.ServerCurrent, ct);
    if (oldId == newId)
    {
        // already running exactly this image (e.g. a fresh install pulled it as :latest): nothing to swap
        state.Install(r.Version);
        Log($"release {r.Version} was already running");
        await AfterInstallAsync(r, newId, oldId, ct);
        return;
    }

    // remembered before anything changes: an updater stopped half way puts this server back at its next start
    state.BeginSwap(r.Version, oldId);
    if (oldId is not null) await docker.CheckedAsync(ct, "tag", oldId, Docker.ServerPrevious);
    await docker.CheckedAsync(ct, "tag", newId, Docker.ServerCurrent);
    var (failure, releaseAtFault) = await SwapAsync(r.Version, ct);
    if (failure is null)
    {
        state.Install(r.Version);
        Log($"release {r.Version} is running");
        await AfterInstallAsync(r, newId, oldId, ct);
        return;
    }

    Log($"release {r.Version} failed: {failure}");
    if (oldId is not null)
    {
        Log($"going back to {(current > 0 ? $"release {current}" : "the previous image")}");
        await docker.CheckedAsync(ct, "tag", oldId, Docker.ServerCurrent);
        if (await docker.TryRecreateAsync("server", ct) is { } stillBroken)
            Log($"going back failed too: {stillBroken}. Start the project again in Container Manager (Action → Build).");
    }
    else Log("there is no previous server image to go back to");
    state.ClearPending();
    // Docker itself failing (a busy port, a typo in .env) is not the release's fault: tried again, up to 3 times
    if (releaseAtFault || state.CountFailure(r.Version) >= 3)
        state.Refuse(r.Version, "the server did not start with it, so this server went back to the previous release");
}

// Recreates the server from its new image and waits until it reports that release. A slow start (a long database
// migration) is given up to 30 minutes; a server that stops or restarts has failed, whatever the time.
async Task<(string? Failure, bool ReleaseAtFault)> SwapAsync(long version, CancellationToken ct)
{
    if (await docker.TryRecreateAsync("server", ct) is { } error) return (error, false);
    var until = DateTime.UtcNow + TimeSpan.FromMinutes(30);
    while (DateTime.UtcNow < until)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        if (await RunningReleaseAsync(ct) == version) return (null, false);
        if (await docker.ContainerStateAsync("server", ct) is { } s && (s.Restarts > 0 || s.Status is "exited" or "dead"))
            return ($"the server stopped ({s.Status}, restarted {s.Restarts} times) before it was ready", true);
    }
    return ("the server did not report the new release within 30 minutes", true);
}

// The server answers /healthz with its release number once it has started (database migrated, ready for requests).
async Task<long?> RunningReleaseAsync(CancellationToken ct)
{
    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync("http://server:8080/healthz", ct));
        return doc.RootElement.TryGetProperty("release", out var rel) && rel.TryGetInt64(out var running) ? running : null;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
}

async Task AfterInstallAsync(ServerRelease r, string newId, string? oldId, CancellationToken ct)
{
    // keep the running release and the one before it (for a rollback); older releases only take disk space
    await docker.PruneAsync(Docker.ServerCurrent.Split(':')[0], [newId, oldId], ct);
    await UpdateSelfAsync(r, ct);
}

// After a successful server update: if the release carries a different updater, switch to it. A container cannot
// replace itself (it would be stopped half way), so a throwaway container with the new image does the swap.
async Task UpdateSelfAsync(ServerRelease r, CancellationToken ct)
{
    var self = Environment.GetEnvironmentVariable("HOSTNAME");
    var tag = Environment.GetEnvironmentVariable("PINBOT_UPDATER_IMAGE");
    if (self is null || tag is null) return;
    var repository = tag.Split(':')[0];
    if (r.UpdaterImage == state.Updater)
    {
        // the switch is done: images of earlier updaters can go (the running one is in use and stays)
        await docker.PruneAsync(repository, [await docker.ImageIdAsync(tag, ct)], ct);
        return;
    }
    await docker.CheckedAsync(ct, "pull", r.UpdaterImage);
    await docker.CheckedAsync(ct, "tag", r.UpdaterImage, tag);
    Log("switching to the updater of this release");
    await docker.CheckedAsync(ct, "run", "-d", "--rm", "--volumes-from", self, tag, "--recreate-self");
    state.SetUpdater(r.UpdaterImage); // recorded once the switch has been started, so a failed start is tried again
}

static void Log(string line) => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {line}");
