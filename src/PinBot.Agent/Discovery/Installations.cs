using Microsoft.Win32;

namespace PinBot.Agent.Discovery;

/// <summary>Where VPX tables and PinMAME NVRAM may live on this machine. Every path here exists.</summary>
public sealed record Installations(
    IReadOnlyList<string> VpxDirs,
    IReadOnlyList<string> TableDirs,
    IReadOnlyList<string> GlobalNvramDirs,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, string>? RomAliases = null)
{
    /// <summary>VPMAlias.txt: script ROM name -> real ROM name (PinMAME writes NVRAM under the real name).</summary>
    public IReadOnlyDictionary<string, string> Aliases => RomAliases ?? new Dictionary<string, string>();

    public string ResolveAlias(string rom) => Aliases.TryGetValue(rom, out var real) ? real : rom;

    /// <summary>
    /// VPMAlias.txt lines are "alias,realrom" (several aliases may precede the real ROM, which is always last).
    /// </summary>
    public static Dictionary<string, string> ParseAliases(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '\'') continue;
            var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            foreach (var alias in parts[..^1]) map[alias] = parts[^1];
        }
        return map;
    }

    /// <summary>
    /// Cheap, non-recursive probing of conventional locations, the VPinMAME registry settings and
    /// VPX's own last-used table folder. Explicit overrides are used in addition, never instead.
    /// </summary>
    public static Installations Discover(IEnumerable<string>? tableOverrides = null, IEnumerable<string>? vpxOverrides = null)
    {
        var notes = new List<string>();
        var vpx = new List<string>();
        var tables = new List<string>();
        var nvram = new List<string>();

        foreach (var d in vpxOverrides ?? []) AddIfVpx(d, vpx, notes, explicitly: true);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            foreach (var rel in ConventionalVpxDirs)
                AddIfVpx(Path.Combine(drive.RootDirectory.FullName, rel), vpx, notes, explicitly: false);

        // VPinMAME location from its COM registration; NVRAM dir from its global settings.
        var vpm = VPinMameDir();
        if (vpm != null)
        {
            notes.Add($"VPinMAME registered at {vpm}");
            var parent = Path.GetDirectoryName(vpm);
            if (parent != null) AddIfVpx(parent, vpx, notes, explicitly: false);
        }
        else notes.Add("VPinMAME COM registration not found (VPinMAME not installed for this user, or 32/64-bit mismatch)");

        var regNv = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Freeware\Visual PinMame\globals", "nvram_directory", null) as string;
        if (!string.IsNullOrWhiteSpace(regNv))
        {
            var full = vpm != null && !Path.IsPathRooted(regNv) ? Path.GetFullPath(Path.Combine(vpm, regNv)) : regNv;
            AddDir(full, nvram);
            notes.Add($"VPinMAME nvram_directory setting: {regNv}{(Directory.Exists(full) ? "" : " (missing)")}");
        }
        if (vpm != null) AddDir(Path.Combine(vpm, "nvram"), nvram);
        foreach (var v in vpx)
        {
            AddDir(Path.Combine(v, "VPinMAME", "nvram"), nvram);
            AddDir(Path.Combine(v, "PinMAME", "nvram"), nvram);
            AddDir(Path.Combine(v, "Tables"), tables);
        }

        var lastDir = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Visual Pinball\VP10\RecentDir", "LoadDir", null) as string
                      ?? IniValue(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPinballX", "VPinballX.ini"), "LoadDir");
        if (!string.IsNullOrWhiteSpace(lastDir)) AddDir(lastDir.Trim(), tables);
        foreach (var t in tableOverrides ?? [])
        {
            if (!Directory.Exists(t)) notes.Add($"table folder override does not exist: {t}");
            AddDir(t, tables);
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliasFiles = (vpm is null ? Array.Empty<string>() : [Path.Combine(vpm, "VPMAlias.txt")])
            .Concat(vpx.SelectMany(v => new[] { Path.Combine(v, "VPinMAME", "VPMAlias.txt"), Path.Combine(v, "PinMAME", "VPMAlias.txt") }))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists);
        foreach (var f in aliasFiles)
        {
            var parsed = ParseAliases(File.ReadLines(f));
            foreach (var (k, v) in parsed) aliases[k] = v;
            notes.Add($"ROM aliases: {parsed.Count} from {f}");
        }

        if (vpx.Count == 0) notes.Add("no VPX installation found in conventional locations; use --vpx <dir> once");
        if (tables.Count == 0) notes.Add("no table folder found; use --tables <dir> once");
        var distinctNvram = Dedupe(nvram);
        if (distinctNvram.Count > 1) notes.Add($"{distinctNvram.Count} global NVRAM folders exist; the active one per ROM is confirmed by observing a session");
        return new(Dedupe(vpx), Dedupe(tables), Dedupe(nvram), notes, aliases);
    }

    static readonly string[] ConventionalVpxDirs =
    [
        @"vPinball\VisualPinball", @"Visual Pinball", @"VisualPinball", @"vPinball",
        @"Games\Visual Pinball", @"Games\VisualPinball", @"Pinball\Visual Pinball", @"Pinball\VisualPinball",
        @"Program Files\Visual Pinball", @"Program Files (x86)\Visual Pinball",
    ];

    static void AddIfVpx(string dir, List<string> vpx, List<string> notes, bool explicitly)
    {
        if (!Directory.Exists(dir)) { if (explicitly) notes.Add($"VPX override does not exist: {dir}"); return; }
        bool hasExe = Directory.EnumerateFiles(dir, "VPinball*.exe").Any();
        if (hasExe) AddDir(dir, vpx);
        else if (explicitly) notes.Add($"VPX override has no VPinball*.exe: {dir}");
    }

    static string? VPinMameDir()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            var clsid = hkcr.OpenSubKey(@"VPinMAME.Controller\CLSID")?.GetValue(null) as string;
            if (clsid == null) continue;
            var dll = hkcr.OpenSubKey($@"CLSID\{clsid}\InprocServer32")?.GetValue(null) as string;
            if (dll != null && File.Exists(dll)) return Path.GetDirectoryName(dll);
        }
        return null;
    }

    static string? IniValue(string path, string key)
    {
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            var i = line.IndexOf('=');
            if (i > 0 && line[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(i + 1)..].Trim();
        }
        return null;
    }

    static void AddDir(string dir, List<string> list)
    {
        if (Directory.Exists(dir)) list.Add(Path.GetFullPath(dir).TrimEnd('\\'));
    }

    static List<string> Dedupe(List<string> l) => l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
