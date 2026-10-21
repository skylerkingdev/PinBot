using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using PinBot.Agent.Sync;
using PinBot.Contracts;

namespace PinBot.Agent;

/// <summary>
/// Everything a cabinet owner needs besides watching: pairing from the one line `/pinbot pair` gives, installing
/// pinbot.exe in %LOCALAPPDATA%\PinBot, starting it with Windows (minimized, from a shortcut in the user's Startup
/// folder, which they can delete), and stopping or removing it again. One watcher runs at a time; a newer copy asks the
/// running one to stop (it finishes like Ctrl+C) before replacing it.
/// </summary>
public static class Setup
{
    const string WatchMutex = @"Local\PinBot.watch";
    const string StopEventName = @"Local\PinBot.stop";

    public static string InstalledExe => Path.Combine(LocalState.Dir, "pinbot.exe");

    /// <summary>What to do when something needs a person: the same words wherever PinBot says it.</summary>
    public const string ReportHint = "Run 'pinbot report' and attach the zip from your desktop to a bug report: " +
                                     "https://github.com/skylerkingdev/PinBot/issues/new/choose (or give it to your server's admin).";
    public static string StartupShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "PinBot.lnk");
    /// <summary>Start menu entry: opens pinbot.exe without arguments, which shows the status.</summary>
    public static string MenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PinBot.lnk");

    public static bool IsWatching()
    {
        if (!Mutex.TryOpenExisting(WatchMutex, out var m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>
    /// Becomes the one watcher on this PC, waiting up to <paramref name="wait"/> for a previous one to exit (a restart
    /// after a crash, or an update). Null if another watcher keeps running.
    /// </summary>
    public static IDisposable? ClaimWatcher(TimeSpan wait = default)
    {
        var m = new Mutex(false, WatchMutex);
        try
        {
            if (m.WaitOne(wait)) return m;
        }
        catch (AbandonedMutexException) { return m; } // the previous watcher died holding it: ours now
        m.Dispose();
        return null;
    }

    static string CrashLog => Path.Combine(LocalState.Dir, "crashes.txt");

    /// <summary>
    /// After an unexpected failure of the watcher: starts a fresh one (it waits for this one to exit), so scores keep
    /// being read without anyone noticing. Three crashes within 10 minutes stop the restarts: something is wrong that a
    /// restart will not fix, and the logs say what.
    /// </summary>
    public static void RestartAfterCrash()
    {
        try
        {
            var now = DateTime.UtcNow;
            var recent = (File.Exists(CrashLog) ? File.ReadAllLines(CrashLog) : [])
                .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                .Where(t => now - t < TimeSpan.FromMinutes(10)).Append(now).ToList();
            File.WriteAllLines(CrashLog, recent.Select(t => t.ToString("O")));
            if (recent.Count >= 3)
            {
                Log.Error("PinBot crashed 3 times in 10 minutes and is not restarting itself; it starts again at the next Windows login. " + ReportHint);
                return;
            }
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "watch --minimized --after-crash") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            Log.Warn("PinBot hit an unexpected error and is restarting itself");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error($"PinBot could not restart itself: {ex.Message}");
        }
    }

    /// <summary>Cancels <paramref name="cts"/> when another PinBot asks this watcher to stop.</summary>
    public static IDisposable ListenForStop(CancellationTokenSource cts)
    {
        var e = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        e.Reset();
        var registration = ThreadPool.RegisterWaitForSingleObject(e, (_, _) => cts.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new Cleanup(() => { registration.Unregister(null); e.Dispose(); });
    }

    /// <summary>Asks a running watcher to stop and waits for it. True when none is running afterwards.</summary>
    public static bool StopRunning(TimeSpan wait)
    {
        if (!IsWatching()) return true;
        using var e = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        e.Set();
        var until = DateTime.UtcNow + wait;
        while (IsWatching() && DateTime.UtcNow < until) Thread.Sleep(250);
        e.Reset();
        return !IsWatching();
    }

    /// <summary>
    /// The line from /pinbot pair: "https://host/pair/CODE" (scheme optional, stray quotes or backticks ignored).
    /// </summary>
    public static (string Url, string Code)? ParsePairingLine(string? line)
    {
        var s = (line ?? "").Trim().Trim('`', '"', '\'', '<', '>').Trim();
        var i = s.LastIndexOf("/pair/", StringComparison.OrdinalIgnoreCase);
        if (i <= 0) return null;
        var url = s[..i].TrimEnd('/');
        var code = s[(i + "/pair/".Length)..].Trim('/', ' ');
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (code.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        return (url, code);
    }

    public static async Task<bool> PairAsync(string url, string code, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            Log.Error("server URL must look like https://pinbot.example.org");
            return false;
        }
        if (uri.Scheme == "http" && !uri.IsLoopback)
            Log.Warn("plain http: the VPin credential will cross the network unencrypted. Use https outside a test LAN.");
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
        HttpResponseMessage resp;
        try { resp = await http.PostAsJsonAsync("api/v1/pair/redeem", new PairRedeemRequest(code, name)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error($"cannot reach {url}: {ex.Message}");
            return false;
        }
        if (!resp.IsSuccessStatusCode)
        {
            Log.Error($"pairing refused ({(int)resp.StatusCode}): the code may be wrong, expired or already used. Get a new one with /pinbot pair.");
            return false;
        }
        PairRedeemResponse? paired;
        try { paired = await resp.Content.ReadFromJsonAsync<PairRedeemResponse>(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { paired = null; }
        if (paired is not { CabinetId.Length: > 0, Credential.Length: > 0 })
        {
            Log.Error($"{url} answered, but not like a PinBot server (a login page or proxy in between?). Check the line from /pinbot pair; the code may now be used up, so get a new one.");
            return false;
        }
        var link = ServerLink.Create(url, paired.CabinetId, paired.Credential);
        LocalState? previous = null;
        LocalState.Update(s => { previous = new LocalState { Server = s.Server }; s.Server = link; s.Competitions = []; });
        if (previous?.Server is { } old && (old.Url != link.Url || old.CabinetId != link.CabinetId)
            && new Outbox(OutboxDir).SetAside($"unsent-{old.CabinetId}") is { } aside)
            Log.Warn($"results queued for the previous server ({old.Url}) are not sent to this one; kept in {aside}");
        Log.Good($"paired with {link.Url} as '{name}' ({link.CabinetId})");
        return true;
    }

    public static string OutboxDir => Path.Combine(LocalState.Dir, "outbox");

    /// <summary>Copies this exe to the install folder (unless it runs from there) and makes it start with Windows.</summary>
    public static void Install()
    {
        Directory.CreateDirectory(LocalState.Dir);
        var self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            // a copy that was just stopped may hold its file for a moment while its process exits
            for (int attempt = 1; ; attempt++)
            {
                try { File.Copy(self, InstalledExe, overwrite: true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
            }
        }
        CreateShortcut(StartupShortcut, InstalledExe, "watch --minimized", minimized: true);
        CreateShortcut(MenuShortcut, InstalledExe, "", minimized: false);
    }

    /// <summary>Starts the installed watcher the same way Windows does at login (minimized).</summary>
    public static void Start() => Process.Start(new ProcessStartInfo(StartupShortcut) { UseShellExecute = true })?.Dispose();

    public static void RemoveShortcuts()
    {
        foreach (var s in new[] { StartupShortcut, MenuShortcut })
            if (File.Exists(s)) File.Delete(s);
    }

    static void CreateShortcut(string path, string target, string arguments, bool minimized)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.WindowStyle = minimized ? 7 : 1;
            link.Description = "PinBot: automatic pinball competitions for Discord";
            link.Save();
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Minimizes this console window (in case the console host ignored the shortcut's setting).</summary>
    public static void MinimizeConsole()
    {
        var w = GetConsoleWindow();
        if (w != IntPtr.Zero) ShowWindow(w, 6); // SW_MINIMIZE
    }

    sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
