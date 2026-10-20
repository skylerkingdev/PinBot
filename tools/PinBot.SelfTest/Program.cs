using System.Diagnostics;
using PinBot.Agent;
using PinBot.Agent.Discovery;
using PinBot.Agent.Scoring;
using PinBot.Agent.Sessions;

// pinbot-selftest: boots each competition-eligible ROM on this cabinet in PinMAME (no VPX, no table) with three copies
// of its NVRAM, each in a scratch folder: the player's own file (control), a "naive" ladder with no checksum or backup
// fixes (does the ROM check its scores at power-on?), and PinBot's competition file (does the ROM keep it?).
// The cabinet's real NVRAM, ROMs and settings are only read.

if (args is ["run", var rom, var vpmDir, var romsDir, var secs]) return Child.Run(rom, vpmDir, romsDir, int.Parse(secs));

if (args is ["check", var checkRom, var checkNv]) // no emulator: the build can read and prepare NVRAM (trimming check)
{
    var m = MemoryMaps.Default().TryLoad(checkRom, out var mapWhy) ?? throw new InvalidOperationException(mapWhy);
    var bytes = File.ReadAllBytes(checkNv);
    var x = HighScoreReader.Extract(m, bytes);
    var img = m.BuildCompetitionImage(bytes);
    Console.WriteLine($"{checkRom}: {x.Status}, {x.Entries.Count(e => e.IsHighScore)} high scores, top {x.Entries.FirstOrDefault(e => e.IsHighScore)}");
    Console.WriteLine($"competition file: {(img.Ok ? string.Join(", ", img.Ladder.Select(l => l.ToString("N0"))) : img.Problem)}; naive: {m.BuildNaiveImage(bytes) is not null}");
    if (img.Ok) Console.WriteLine($"bytes changed: {Enumerable.Range(0, bytes.Length).Count(i => bytes[i] != img.Bytes![i])} (naive {Enumerable.Range(0, bytes.Length).Count(i => bytes[i] != m.BuildNaiveImage(bytes)![i])})");
    Console.WriteLine($"state: {LocalState.Load().TableDirs.Count} table dirs; PinMAME roms: {PinMame.PinMame.FindVpmPath(PinMame.PinMameFileType.Roms)}");
    return img.Ok && x.Ok ? 0 : 1;
}

if (args is ["show", var showRom, .. var files]) // the high-score table in each file, for reading a FAIL
{
    var m = MemoryMaps.Default().TryLoad(showRom, out var mapWhy) ?? throw new InvalidOperationException(mapWhy);
    foreach (var f in files)
    {
        var x = HighScoreReader.Extract(m, File.ReadAllBytes(f));
        Console.WriteLine($"{Path.GetFileName(f)}: {x.Status} {x.Detail}");
        foreach (var e in x.Entries.Where(e => e.IsHighScore)) Console.WriteLine($"   {e}");
    }
    return 0;
}

int seconds = 15;
var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
    else if (args[i] == "--rom" && i + 1 < args.Length) only.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    else { Console.WriteLine("usage: pinbot-selftest [--rom rom1,rom2] [--seconds 15]"); return 2; }
}
if (SessionWatcher.VpxRunning()) { Console.WriteLine("Close Visual Pinball first: the self-test needs the PC to itself."); return 1; }

var maps = MemoryMaps.Default();
var state = LocalState.Load();
var inst = Installations.Discover(state.TableDirs, state.VpxDirs);
var all = Inventory.Build(inst, maps, state);
foreach (var wanted in only.Where(r => !all.Any(t => t.Eligible && string.Equals(t.Rom, r, StringComparison.OrdinalIgnoreCase))).Order())
{
    var named = all.Where(t => string.Equals(t.Rom, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"{wanted,-14} not tested: " + (named.Count == 0
        ? "no table on this cabinet uses this ROM (tables using a similar name: " +
          string.Join(", ", all.Where(t => t.Rom != null && t.Rom.StartsWith(wanted[..Math.Min(3, wanted.Length)], StringComparison.OrdinalIgnoreCase)).Select(t => t.Rom).Distinct().DefaultIfEmpty("none")) + ")"
        : string.Join("; ", named.Select(t => $"{Path.GetFileNameWithoutExtension(t.TablePath)}: {t.Problem}"))));
}
var tables = all
    .Where(t => t.Eligible && t.NvPath != null && (only.Count == 0 || only.Contains(t.Rom!)))
    .GroupBy(t => t.Rom!, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
    .OrderBy(t => t.Rom, StringComparer.OrdinalIgnoreCase).ToList();
var outDir = Path.Combine(LocalState.Dir, "selftest", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(outDir);
Console.WriteLine($"{tables.Count} ROMs, 3 runs each: power on, {seconds} s, coins and Start, {seconds} s (about {tables.Count * 3 * (2 * seconds + 10) / 60 + 1} min). Results: {outDir}");

var report = new List<string>();
foreach (var t in tables)
{
    var verdict = Test(t.Rom!, t.NvPath!);
    var line = $"{t.Rom,-14} {Path.GetFileNameWithoutExtension(t.TablePath),-40} {verdict}";
    Console.WriteLine(line);
    report.Add(line);
    File.WriteAllLines(Path.Combine(outDir, "report.txt"), report);
}
Console.WriteLine($"Report: {Path.Combine(outDir, "report.txt")}");
return 0;

string Test(string rom, string nvPath)
{
    var map = maps.TryLoad(rom, out var why);
    if (map is null) return $"SKIP  no map ({why})";
    var (original, status, detail) = HighScoreReader.ReadShared(nvPath);
    if (original is null) return $"SKIP  NVRAM not readable ({status} {detail})";
    var image = map.BuildCompetitionImage(original);
    if (!image.Ok) return $"SKIP  not offered for competitions: {image.Problem}";
    var naive = map.BuildNaiveImage(original);

    var romsDir = RomsDirFor(nvPath);
    if (romsDir is null) return "ERROR no roms folder found next to the NVRAM folder";
    if (!File.Exists(Path.Combine(romsDir, rom + ".zip"))) return $"ERROR {rom}.zip not in {romsDir}";

    var control = Boot(rom, "control", original, romsDir, map);
    if (control.Error is { } e) return $"ERROR {e}";
    if (!control.Kept) return Keep(rom, "INCONCLUSIVE  the ROM changed its own high scores in the emulator");
    var comp = Boot(rom, "competition", image.Bytes!, romsDir, map);
    if (comp.Error is { } e2) return $"ERROR {e2}";
    if (!comp.Kept) return Keep(rom, "FAIL  the ROM reset PinBot's competition file");
    (bool Kept, string? Error)? check = naive is null ? null : Boot(rom, "naive", naive, romsDir, map);
    if (check?.Error is { } e3) return $"ERROR {e3}";
    return check is { Kept: false }
        ? "PASS  checks its high scores (at power-on or game start), and keeps PinBot's file"
        : "OK    keeps PinBot's file, but did not check its high scores in the test (play proves it)";
}

string Keep(string rom, string verdict)
{
    var src = Path.Combine(Path.GetTempPath(), "pinbot-selftest");
    var dst = Path.Combine(outDir, rom);
    foreach (var kind in new[] { "control", "competition", "naive" })
    {
        var dir = Path.Combine(src, $"{rom}-{kind}");
        if (!Directory.Exists(dir)) continue;
        Directory.CreateDirectory(dst);
        if (File.Exists(Path.Combine(dir, "before.nv"))) File.Copy(Path.Combine(dir, "before.nv"), Path.Combine(dst, $"{kind}-before.nv"), true);
        if (File.Exists(Path.Combine(dir, "nvram", rom + ".nv"))) File.Copy(Path.Combine(dir, "nvram", rom + ".nv"), Path.Combine(dst, $"{kind}-after.nv"), true);
    }
    return verdict + $" (files in {dst})";
}

(bool Kept, string? Error) Boot(string rom, string kind, byte[] nv, string romsDir, RomMap map)
{
    var dir = Path.Combine(Path.GetTempPath(), "pinbot-selftest", $"{rom}-{kind}");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(Path.Combine(dir, "nvram"));
    File.WriteAllBytes(Path.Combine(dir, "before.nv"), nv);
    File.WriteAllBytes(Path.Combine(dir, "nvram", rom + ".nv"), nv);
    File.SetLastWriteTimeUtc(Path.Combine(dir, "nvram", rom + ".nv"), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // rewritten = PinMAME saved it

    var psi = new ProcessStartInfo(Environment.ProcessPath!, ["run", rom, dir, romsDir, seconds.ToString()])
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEndAsync();
    var errors = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(TimeSpan.FromSeconds(2 * seconds + 70))) { p.Kill(true); return (false, $"{kind} boot did not finish"); }
    if (p.ExitCode != 0) return (false, $"{kind} boot failed: {(output.Result + errors.Result).Trim().Split('\n').LastOrDefault()?.Trim()}");

    var after = Path.Combine(dir, "nvram", rom + ".nv");
    if (File.GetLastWriteTimeUtc(after).Year == 2000) return (false, $"{kind} boot: PinMAME did not save NVRAM (game not started?)");
    var a = HighScoreReader.Extract(map, File.ReadAllBytes(after));
    var b = HighScoreReader.Extract(map, nv);
    if (!a.Ok) return (false, $"{kind}: NVRAM after boot not readable ({a.Status})");
    static IEnumerable<string> Main(Extraction x) => x.Entries.Where(e => e.IsHighScore).Select(e => $"{e.RawInitials}|{e.Score}");
    return (Main(a).SequenceEqual(Main(b)), null);
}

// VPinMAME keeps roms next to nvram (<VPinMAME>\nvram, <VPinMAME>\roms; VPX 10.8: <tables>\pinmame\nvram and \roms)
static string? RomsDirFor(string nvPath)
{
    var parent = Path.GetDirectoryName(Path.GetDirectoryName(nvPath));
    var dir = parent is null ? null : Path.Combine(parent, "roms");
    if (dir != null && Directory.Exists(dir)) return dir;
    var vpm = PinMame.PinMame.FindVpmPath(PinMame.PinMameFileType.Roms);
    return string.IsNullOrEmpty(vpm) || !Directory.Exists(vpm) ? null : vpm;
}

static class Child
{
    /// <summary>
    /// One run in its own process (a ROM that hangs or crashes the emulator cannot take the test with it): power on,
    /// wait, insert coins and press Start (PinMAME's default keys 5 and 1, the same on every system), wait, power off.
    /// Some ROMs check their high scores at power-on (AC/DC), others only when a game starts (Big Bang Bar).
    /// </summary>
    // the PinMAME binding finds pinmame64.dll through NativeLibrary by reflection: keep it in the trimmed build
    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods, typeof(System.Runtime.InteropServices.NativeLibrary))]
    public static int Run(string rom, string vpmDir, string romsDir, int seconds)
    {
        var pm = PinMame.PinMame.Instance(vpmPath: Path.TrimEndingDirectorySeparator(vpmDir) + "\\");
        pm.RomPath = Path.TrimEndingDirectorySeparator(romsDir) + "\\";
        PinMame.PinMameKeycode? held = null;
        pm.SetHandleKeyboard(true);
        pm.IsKeyPressed += key => key == held ? 1 : 0;
        var gameStarted = false;
        pm.OnGameStarted += () => gameStarted = true;
        pm.StartGame(rom);
        var started = Stopwatch.StartNew();
        while (!gameStarted && started.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
        if (!gameStarted || !PinMame.PinMame.IsRunning) { Console.Error.WriteLine($"PinMAME did not start {rom}"); return 1; }
        Thread.Sleep(TimeSpan.FromSeconds(seconds)); // power-on checks
        void Press(PinMame.PinMameKeycode key) { held = key; Thread.Sleep(300); held = null; Thread.Sleep(700); }
        for (int i = 0; i < 4; i++) Press(PinMame.PinMameKeycode.Number5); // coins
        Press(PinMame.PinMameKeycode.Number1);                             // start
        Thread.Sleep(TimeSpan.FromSeconds(seconds)); // game-start checks
        pm.StopGame(); // PinMAME writes the NVRAM as the machine stops
        var stopping = Stopwatch.StartNew();
        while ((PinMame.PinMame.IsRunning || PinMame.PinMame.IsStopInProgress) && stopping.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
        return 0;
    }
}
