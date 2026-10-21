using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using PinBot.Contracts;

namespace PinBot.Agent.Sync;

/// <summary>
/// Keeps the cabinet on the build its server offers. Only installs a newer release signed with the key this build
/// carries (so the server or the network cannot push code), only when no table is running, and hands over the same
/// way a double-clicked newer pinbot.exe does: the new exe stops this watcher, installs itself and starts minimized.
/// Pairing, competitions, journals and queued results live outside the exe and are untouched.
/// </summary>
public sealed class AutoUpdate(HttpClient http)
{
    public static string? PublicKey => Meta("UpdatePublicKey");
    public static long Version => long.TryParse(Meta("UpdateVersion"), out var v) ? v : 0;
    public static bool Enabled => PublicKey != null && Version > 0;

    static string? Meta(string key) => typeof(AutoUpdate).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    /// <summary>A download cut short (slow or flaky line): tried again sooner than a release that failed its checks.</summary>
    static readonly TimeSpan RetryAfterDownloadFailure = TimeSpan.FromMinutes(15);
    (long Version, DateTime RetryAt)? _failed;

    /// <summary>Why <paramref name="m"/> must not be installed by a build with this key and version, or null.</summary>
    public static string? Refusal(UpdateManifest m, string publicKey, long currentVersion) =>
        m.Version <= currentVersion ? "not newer than this build"
        : !UpdateSigning.Verify(m, publicKey) ? "signature does not match the release key this build trusts" : null;

    /// <summary>True when the last check found a release but held it back because a table had started meanwhile.</summary>
    public bool Deferred { get; private set; }

    /// <summary>
    /// Checks the server and, if a newer signed release is there, starts handing over to it. True if it did.
    /// <paramref name="stillIdle"/> is asked again right before the hand-over (the download can take minutes): a table
    /// started meanwhile defers it, so a game is never cut short.
    /// </summary>
    public async Task<bool> MaybeUpdateAsync(CancellationToken ct, Func<bool>? stillIdle = null)
    {
        Deferred = false;
        if (!Enabled) return false;
        var manifest = await http.GetFromJsonAsync<UpdateManifest>("download/pinbot.json", ct);
        if (manifest is null || manifest.Version <= Version) return false;
        if (_failed is { } f && f.Version == manifest.Version && DateTime.UtcNow < f.RetryAt) return false;
        try
        {
            if (Refusal(manifest, PublicKey!, Version) is { } why)
                throw new InvalidDataException($"update {manifest.Version} refused: {why}");
            var dir = Path.Combine(LocalState.Dir, "update");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"pinbot-{manifest.Version}.exe");
            // downloaded before and held back (a table started): reused if it is still exactly the signed file
            if (!(File.Exists(path) && UpdateSigning.Matches(manifest, await File.ReadAllBytesAsync(path, ct))))
            {
                byte[] exe;
                try { exe = await http.GetByteArrayAsync("download/pinbot.exe", ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    _failed = (manifest.Version, DateTime.UtcNow + RetryAfterDownloadFailure);
                    Log.Warn($"[update] downloading release {manifest.Version} failed ({ex.Message}); trying again in 15 minutes");
                    return false;
                }
                if (!UpdateSigning.Matches(manifest, exe))
                    throw new InvalidDataException($"update {manifest.Version} refused: the download does not match its signed description");
                foreach (var old in Directory.GetFiles(dir, "pinbot-*.exe")) TryDelete(old);
                await File.WriteAllBytesAsync(path, exe, ct);
            }
            if (stillIdle?.Invoke() == false)
            {
                Deferred = true;
                Log.Info($"[update] release {manifest.Version} is ready; a table started, so it is installed once it is closed");
                return false;
            }
            Log.Good($"[update] release {manifest.Version} verified (this is {Version}); installing it");
            // the new exe stops this watcher (like Ctrl+C), replaces the installed copy and starts it minimized
            Process.Start(new ProcessStartInfo(path, "update") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _failed = (manifest.Version, DateTime.UtcNow + RetryAfterFailure);
            Log.Warn($"[update] {ex.Message}; trying again in an hour");
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
