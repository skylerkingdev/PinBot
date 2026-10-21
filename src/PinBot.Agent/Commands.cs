using PinBot.Agent.Discovery;
using PinBot.Agent.Scoring;
using PinBot.Agent.Sessions;

namespace PinBot.Agent;

/// <summary>The pinbot commands other than watch. Program.cs parses the command line and dispatches here.</summary>
sealed class Commands(MemoryMaps maps, IReadOnlyDictionary<string, List<string>> opts)
{
    string? Opt(string name) => opts.TryGetValue(name, out var v) ? v[^1] : null;

    public (Installations, List<TableInfo>, LocalState) Discover()
    {
        var tableDirs = (opts.GetValueOrDefault("tables") ?? []).Select(Path.GetFullPath).ToList();
        var vpxDirs = (opts.GetValueOrDefault("vpx") ?? []).Select(Path.GetFullPath).ToList();
        var state = tableDirs.Count + vpxDirs.Count == 0 ? LocalState.Load() : LocalState.Update(s =>
        {
            foreach (var d in tableDirs.Where(d => !s.TableDirs.Contains(d, StringComparer.OrdinalIgnoreCase))) s.TableDirs.Add(d);
            foreach (var d in vpxDirs.Where(d => !s.VpxDirs.Contains(d, StringComparer.OrdinalIgnoreCase))) s.VpxDirs.Add(d);
        });

        var inst = Installations.Discover(state.TableDirs, state.VpxDirs);
        Log.Info($"memory maps: {maps.RomCount} ROM names indexed ({MemoryMaps.ParserVersion})");
        foreach (var n in inst.Notes) Log.Info("discovery: " + n);
        foreach (var d in inst.VpxDirs) Log.Info($"VPX:    {d}");
        foreach (var d in inst.TableDirs) Log.Info($"tables: {d}");
        foreach (var d in inst.GlobalNvramDirs) Log.Info($"nvram:  {d}");
        var tables = Inventory.Build(inst, maps, state, PinBot.Agent.Discovery.TableScriptCache.Default);
        PinBot.Agent.Discovery.TableScriptCache.Default.Save();
        return (inst, tables, state);
    }

    public void PrintInventory(Installations inst, List<TableInfo> tables)
    {
        Log.Out();
        Log.Out($"{"TABLE",-44} {"ROM",-12} {"SOURCE",-9} {"NVRAM",-13} STATUS");
        foreach (var t in tables)
        {
            var name = Path.GetFileNameWithoutExtension(t.TablePath);
            if (name.Length > 43) name = name[..40] + "...";
            var status = t.Eligible ? (t.RomSource == RomSource.Observed ? "eligible" : "eligible (script + existing .nv)") : t.Problem;
            Log.Out($"{name,-44} {t.Rom ?? "-",-12} {t.RomSource,-9} {(t.Rom is null ? "-" : t.NvState.ToString()),-13} {status}");
        }
        int eligible = tables.Count(t => t.Eligible);
        Log.Out();
        Log.Out($"{tables.Count} tables: {eligible} eligible, {tables.Count(t => t.Rom != null && !t.Supported)} unsupported ROM, "
                + $"{tables.Count(t => t.Rom == null)} without a determinable ROM, {tables.Count(t => t.NvState == NvState.Ambiguous)} ambiguous NVRAM");
        foreach (var t in tables.Where(t => t.NvState == NvState.Ambiguous))
            Log.Warn($"ambiguous NVRAM for {Path.GetFileName(t.TablePath)}: {string.Join(" | ", t.NvCandidates)} (play it once while 'watch' runs to confirm)");
    }

    public int Read(string path, string? rom)
    {
        rom ??= HighScoreReader.RomFromFileName(path);
        var x = HighScoreReader.Extract(maps, rom, path);
        Log.Out($"{path}  rom={rom}  status={x.Status}  {x.Detail}");
        if (x.Sha256 != null) Log.Out($"sha256={x.Sha256}");
        foreach (var e in x.Entries)
        {
            // show the stored bytes when initials normalize to nothing, to tell "blank in NVRAM" from a decoding problem
            var raw = e.Initials is null && e.RawInitials is { Length: > 0 } r
                ? $"   raw initials: {string.Join(" ", r.Select(c => ((int)c).ToString("X2")))}" : "";
            Log.Out($"  {e.Section switch { "high_scores" => "HS ", "mode_champions" => "MC ", _ => "SEC" }}  {e}{raw}");
        }
        return x.Ok ? 0 : 1;
    }

    public int Diff(string beforePath, string afterPath, string? rom)
    {
        rom ??= HighScoreReader.RomFromFileName(afterPath);
        var b = HighScoreReader.Extract(maps, rom, beforePath);
        var a = HighScoreReader.Extract(maps, rom, afterPath);
        if (!b.Ok || !a.Ok)
        {
            Log.Error($"before: {b.Status} {b.Detail}; after: {a.Status} {a.Detail}");
            return 1;
        }
        var d = ScoreDiff.Compare(b.Entries, a.Entries);
        foreach (var (be, ae) in d.ChangedSlots)
            Log.Out($"  slot {ae.ShortLabel ?? ae.Label,-14} {be?.Brief ?? "(none)"}  ->  {ae.Brief}");
        foreach (var e in d.NewEntries)
            Log.Good($"  NEW  {e.Initials ?? "(no initials)",-6} {e.Score:N0}  [{e.Label}]  rom={rom}");
        if (d.SuspectedReset) Log.Warn("  every previous high score disappeared: NVRAM looks re-initialized; nothing qualifies");
        if (!d.Any) Log.Out("  no changes");
        return 0;
    }

    PinBot.Agent.Nvram.NvramCoordinator Coordinator() =>
        new(Path.Combine(LocalState.Dir, "nvram"), maps, _ => !SessionWatcher.VpxRunning(), () => DateTime.UtcNow);

    public int ListCompetitions()
    {
        var now = DateTime.UtcNow;
        var comps = LocalState.Load().Competitions;
        var (entries, unreadable) = Coordinator().LoadAll();
        if (comps.Count == 0 && entries.Count == 0) Log.Out("no competitions");
        foreach (var c in comps.OrderBy(c => c.StartsUtc))
        {
            var e = entries.LastOrDefault(x => x.CompetitionId == c.Id);
            var timing = c.IsActive(now) ? $"running until {c.EffectiveEndUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
                       : now < c.StartsUtc ? "scheduled" : $"ended {c.EffectiveEndUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
            var nv = e is null
                ? (c.IsActive(now) ? "waiting for an idle moment (or the table has never been played: no .nv yet)" : "never prepared: nothing to restore")
                : e.State switch
                {
                    PinBot.Agent.Nvram.CompState.Ready => "competition NVRAM in place",
                    PinBot.Agent.Nvram.CompState.BackedUp => "original backed up; swap pending",
                    PinBot.Agent.Nvram.CompState.PendingRestore => "waiting for an idle moment to restore the original",
                    PinBot.Agent.Nvram.CompState.Restored => "original restored",
                    var s => s.ToString(),
                };
            Log.Out($"{c.Id,-28} {c.Table} ({c.Rom})");
            Log.Out($"{"",-28} {timing}; {nv}");
            if (e?.Problem is { } p) Log.Warn($"{"",-28} {p}");
            if (e != null) Log.Out($"{"",-28} journal: {e.Dir}");
        }
        foreach (var e in entries.Where(e => e.State == PinBot.Agent.Nvram.CompState.NeedsRecovery))
            Log.Error($"NEEDS RECOVERY: {e.CompetitionId} {e.NvPath} - {e.Problem}. Backup and journal kept in {e.Dir}");
        foreach (var u in unreadable) Log.Error($"NEEDS RECOVERY: unreadable journal {u}");
        return 0;
    }

    // Double-click (no arguments): pair if needed, install, start with Windows, run. Again later: status, or an update.
    public async Task<int> SetUp()
    {
        Log.Out("PinBot - automatic pinball competitions for your Discord server");
        Log.Out();
        var state = LocalState.Load();
        if (state.Server is null)
        {
            while (true)
            {
                Log.Out("In Discord, run /pinbot pair. Paste the line it gives you here and press Enter:");
                Console.Write("> ");
                var line = Console.ReadLine();
                if (line is null) return 1;
                if (Setup.ParsePairingLine(line) is not { } p)
                {
                    Log.Warn("That isn't the line from /pinbot pair (it looks like https://.../pair/ABCDE-FGHIJ). Try again.");
                    continue;
                }
                if (await Setup.PairAsync(p.Url, p.Code, Environment.MachineName)) break;
            }
            Log.Out();
        }
        else if (Setup.IsWatching() && File.Exists(Setup.InstalledExe) && SameFile(Environment.ProcessPath!, Setup.InstalledExe))
        {
            Status();
            return Pause(0);
        }

        var (_, tables, _) = Discover();
        var eligible = tables.Count(t => t.Eligible);
        if (tables.Count == 0)
            Log.Warn("No Visual Pinball tables were found. If VPX is installed somewhere unusual, run:  pinbot scan --vpx \"C:\\path\\to\\VisualPinball\"");
        else Log.Good($"{tables.Count} tables found; {eligible} can be used in competitions.");

        if (!Setup.StopRunning(TimeSpan.FromSeconds(30)))
        {
            Log.Error("PinBot is already running and did not stop. Close its window (taskbar) and run this again.");
            return Pause(1);
        }
        Setup.Install();
        Setup.Start();
        Log.Out();
        Log.Good("PinBot is running (minimized in the taskbar) and starts by itself whenever you log in to Windows.");
        Log.Out("Put your initials in your Discord server nickname, e.g.  Name | [ABC], and enter them when you get a high score.");
        Log.Out("Open 'PinBot' from the Start menu any time to see what it is doing.");
        return Pause(0);
    }

    static bool SameFile(string a, string b)
    {
        if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) return true;
        var (fa, fb) = (new FileInfo(a), new FileInfo(b));
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
    }

    static int Pause(int code)
    {
        Log.Out();
        Console.Write("Press Enter to close this window.");
        Console.ReadLine();
        return code;
    }

    // Run by a running PinBot that downloaded and verified this newer release: take over without any window or prompt.
    // The running watcher was already told to stop (it finishes its current step first), so from here on this process is
    // responsible for a watcher running again: whatever fails, it never leaves the VPin without one.
    public int InstallUpdate()
    {
        if (!Setup.StopRunning(TimeSpan.FromMinutes(5)))
        {
            Log.Error("[update] the running PinBot did not stop within 5 minutes; it keeps running and tries again later");
            return 1;
        }
        try
        {
            Setup.Install();
            Setup.Start();
            Log.Good($"[update] now running release {PinBot.Agent.Sync.AutoUpdate.Version}");
            return 0;
        }
        catch (Exception ex)
        {
            // could not replace the installed copy: run the one already installed (or this one) rather than nothing
            Log.Error($"[update] installing release {PinBot.Agent.Sync.AutoUpdate.Version} failed: {ex.Message}");
            var exe = File.Exists(Setup.InstalledExe) ? Setup.InstalledExe : Environment.ProcessPath!;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "watch --minimized --after-crash")
                    { UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized })?.Dispose();
            }
            catch (Exception again) { Log.Error($"[update] starting PinBot again failed too: {again.Message}"); }
            return 1;
        }
    }

    public int Status()
    {
        var state = LocalState.Load();
        Log.Out(PinBot.Agent.Sync.AutoUpdate.Enabled
            ? $"version:    release {PinBot.Agent.Sync.AutoUpdate.Version} (updates itself from the server)"
            : "version:    development build (does not update itself)");
        Log.Out(state.Server is { } s ? $"server:     {s.Url} (VPin {s.CabinetId})" : "server:     not paired (run pinbot and paste the line from /pinbot pair)");
        Log.Out($"running:    {(Setup.IsWatching() ? "yes" : "no")}");
        Log.Out($"autostart:  {(File.Exists(Setup.StartupShortcut) ? "yes, minimized when you log in to Windows" : "no")}");
        var outbox = Setup.OutboxDir;
        var waiting = Directory.Exists(outbox) ? Directory.GetFiles(outbox, "*.json").Length : 0;
        Log.Out($"to upload:  {(waiting == 0 ? "nothing" : $"{waiting} result(s) waiting for the server")}");
        Log.Out($"files:      {LocalState.Dir}  (logs in {Log.LogDir})");
        foreach (var (rom, r) in state.RejectedRoms.Where(kv => RejectedRom.Applies(state, kv.Key, PinBot.Agent.Sync.AutoUpdate.Version)))
            Log.Warn($"not offered: {rom} reset its high scores on a competition file {r.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}; please send {r.Evidence}");
        Log.Out();
        return ListCompetitions();
    }

    public int Uninstall()
    {
        if (SessionWatcher.VpxRunning())
        {
            Log.Error("A table is running. Close Visual Pinball first, so PinBot can put your original high scores back.");
            return 1;
        }
        if (!Setup.StopRunning(TimeSpan.FromSeconds(30)))
        {
            Log.Error("PinBot is running and did not stop. Close its window (taskbar) and try again.");
            return 1;
        }
        // end every competition locally so the original NVRAM is restored before PinBot lets go
        var now = DateTime.UtcNow;
        var ended = LocalState.Update(s => s.Competitions = s.Competitions
            .Select(c => c.IsActive(now) ? c with { EndedEarlyUtc = now } : c).ToList()).Competitions;
        var coordinator = Coordinator();
        coordinator.Tick(ended);
        var (entries, unreadable) = coordinator.LoadAll();
        var inPlace = entries.GroupBy(e => e.NvPath, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .Where(e => e.State is PinBot.Agent.Nvram.CompState.Ready or PinBot.Agent.Nvram.CompState.PendingRestore
                or PinBot.Agent.Nvram.CompState.Restoring or PinBot.Agent.Nvram.CompState.NeedsRecovery).ToList();
        if (inPlace.Count > 0 || unreadable.Count > 0)
        {
            ListCompetitions();
            Log.Error("Some original NVRAM could not be restored (above), so PinBot stays installed. Nothing was deleted.");
            return 1;
        }
        LocalState.Update(s => { s.Server = null; s.Competitions = []; });
        Setup.RemoveShortcuts();
        Log.Good("PinBot removed: your original high scores are in place, this PC is unpaired, and it no longer starts with Windows.");
        Log.Out($"Its files (program, logs, NVRAM backups) are in {LocalState.Dir}; delete that folder to remove everything.");
        return 0;
    }

    // Everything needed to understand a problem on this PC, in one file on the desktop: recent logs, sessions, journal
    // entries, diagnostics and the state file without the server credential. High scores are the only game data in it.
    public int Report()
    {
        var zipPath = Path.Combine(Opt("out") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"PinBot-report-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Add(string path, string name)
            {
                try { System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, path, name.Replace(Path.DirectorySeparatorChar, '/')); }
                catch (IOException) { } // in use or gone: the rest still helps
            }
            string Rel(string path) => Path.GetRelativePath(LocalState.Dir, path);
            if (Directory.Exists(Log.LogDir))
                foreach (var log in Directory.GetFiles(Log.LogDir, "pinbot-*.log").OrderDescending().Take(3)) Add(log, Rel(log));
            foreach (var name in new[] { "sessions.jsonl", "crashes.txt", "tables-cache.json" })
                if (File.Exists(Path.Combine(LocalState.Dir, name))) Add(Path.Combine(LocalState.Dir, name), name);
            foreach (var dir in new[] { "nvram", "diagnostics", "outbox" }.Select(d => Path.Combine(LocalState.Dir, d)).Where(Directory.Exists))
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) Add(file, Rel(file));
            var state = LocalState.Load();
            state.Server = state.Server is { } s ? s with { ProtectedCredential = "(removed)" } : null; // never leaves this PC
            var entry = zip.CreateEntry("state.json");
            using (var w = new StreamWriter(entry.Open()))
                w.Write(System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var info = zip.CreateEntry("about.txt");
            using (var w = new StreamWriter(info.Open()))
                w.Write($"{(PinBot.Agent.Sync.AutoUpdate.Enabled ? $"release {PinBot.Agent.Sync.AutoUpdate.Version}" : "development build")}\n" +
                        $"Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} CPUs\nwatching: {Setup.IsWatching()}\n");
        }
        Log.Good($"Report saved: {zipPath}");
        Log.Out("Send this file to whoever asked for it (it holds logs and high scores, no passwords).");
        return 0;
    }
}
