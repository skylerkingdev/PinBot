using System.Globalization;

namespace PinBot.Agent.Discovery;

/// <summary>
/// A table's view (POV) for this VPin's view mode: "desktop", "cabinet" or "fss", and its view keys as text (null when
/// the table has no view saved: the mode is still sent, so the first view saved is noticed as a change).
/// </summary>
public sealed record PovInfo(string Mode, string? Settings);

/// <summary>
/// VPX 10.8 keeps a table's view in the table's own .ini (next to the .vpx), section [TableOverride], one set of View…
/// keys per view mode. Only view keys with numbers are read: the file can hold the member's other table options too.
/// Shared on table-update posts (docs/TABLE-UPDATES.md); never used for scoring.
/// </summary>
public static class Pov
{
    public const int MaxKeys = 64;

    static readonly Dictionary<string, string> Prefixes = new() { ["desktop"] = "ViewDT", ["cabinet"] = "ViewCab", ["fss"] = "ViewFSS" };

    /// <summary>VPX looks for a table's settings in the .ini named like the table: "Table.vpx" -> "Table.ini".</summary>
    public static string PathFor(string vpxPath) => Path.ChangeExtension(vpxPath, ".ini");

    /// <summary>The view mode this VPin plays in, from VPinballX.ini ([Player] BGSet: 0 desktop, 1 cabinet, 2 FSS); null if unknown.</summary>
    public static string? FindMode(IEnumerable<string> vpxDirs)
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPinballX", "VPinballX.ini") }
            .Concat(vpxDirs.Select(d => Path.Combine(d, "VPinballX.ini")));
        foreach (var ini in candidates.Where(File.Exists))
        {
            try { return ModeFrom(File.ReadLines(ini)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    internal static string? ModeFrom(IEnumerable<string> lines)
    {
        string? found = null;
        foreach (var (section, key, value) in Entries(lines))
            if (section.Equals("Player", StringComparison.OrdinalIgnoreCase) && key.Equals("BGSet", StringComparison.OrdinalIgnoreCase))
                found = value.Trim() switch { "0" => "desktop", "1" => "cabinet", "2" => "fss", _ => null };
        return found ?? "desktop"; // VPX's default when the setting was never changed
    }

    /// <summary>The table's view for <paramref name="mode"/> (settings null: no .ini or no view keys); null when the mode is unknown.</summary>
    public static PovInfo? Read(string vpxPath, string? mode)
    {
        if (mode is null) return null;
        var ini = PathFor(vpxPath);
        if (!File.Exists(ini)) return new(mode, null);
        try { return new(mode, Parse(File.ReadLines(ini), mode)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } // unreadable now: not "no view"
    }

    /// <summary>"Key = value" lines, sorted, for the mode's View… keys under [TableOverride] whose value is a number.</summary>
    internal static string? Parse(IEnumerable<string> lines, string mode)
    {
        if (!Prefixes.TryGetValue(mode, out var prefix)) return null;
        var keys = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, key, value) in Entries(lines))
        {
            if (!section.Equals("TableOverride", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.Length > 40 || !key.All(char.IsAsciiLetterOrDigit)) continue;
            var v = value.Trim();
            if (v.Length > 24 || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
            keys[key] = v;
        }
        return keys.Count == 0 ? null : string.Join("\n", keys.Take(MaxKeys).Select(k => $"{k.Key} = {k.Value}"));
    }

    /// <summary>
    /// The ini's lines with the mode's view replaced: every View… key of that mode under [TableOverride] removed (a
    /// shared POV holds only what differed from the table's own view, so leftovers would change it), then the new
    /// lines written right under the section header (or a new section at the end). Everything else stays as it was.
    /// </summary>
    internal static List<string> Replace(IReadOnlyList<string> lines, string mode, IReadOnlyList<string> view)
    {
        var prefix = Prefixes[mode];
        var result = new List<string>(lines.Count + view.Count + 2);
        bool inOverride = false, written = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inOverride = line[1..^1].Trim().Equals("TableOverride", StringComparison.OrdinalIgnoreCase);
                result.Add(raw);
                if (inOverride && !written) { result.AddRange(view); written = true; }
                continue;
            }
            var eq = line.IndexOf('=');
            if (inOverride && eq > 0 && line[..eq].Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(raw);
        }
        if (!written && view.Count > 0)
        {
            if (result.Count > 0 && result[^1].Trim().Length > 0) result.Add("");
            result.Add("[TableOverride]");
            result.AddRange(view);
        }
        return result;
    }

    /// <summary>Lines a VPin accepts for the mode: "View… = number" only, whatever the server sent.</summary>
    internal static List<string>? Validate(string content, string mode)
    {
        if (!Prefixes.TryGetValue(mode, out var prefix)) return null;
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaxKeys) return null;
        var view = new List<string>();
        foreach (var l in lines)
        {
            var eq = l.IndexOf('=');
            if (eq <= 0) return null;
            var key = l[..eq].Trim();
            var value = l[(eq + 1)..].Trim();
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.Length > 40 || !key.All(char.IsAsciiLetterOrDigit)) return null;
            if (value.Length > 24 || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return null;
            view.Add($"{key} = {value}");
        }
        return view;
    }

    /// <summary>
    /// Writes a shared view into the table's .ini (a copy of the old file is kept in PinBot's folder first). Returns
    /// the table's view before and after, or throws <see cref="InvalidDataException"/> with the reason it refused.
    /// </summary>
    public static (string? Previous, string? Now) Install(string vpxPath, string mode, string content, string backupDir)
    {
        var view = Validate(content, mode) ?? throw new InvalidDataException("the shared view isn't a plain list of view settings");
        var ini = PathFor(vpxPath);
        var previous = Read(vpxPath, mode)?.Settings;
        if (view.Count == 0 && !File.Exists(ini)) return (previous, previous); // the table's own view already: nothing to write
        string[] lines = [];
        var bom = false;
        var newline = "\r\n";
        if (File.Exists(ini))
        {
            var bytes = File.ReadAllBytes(ini);
            bom = bytes is [0xEF, 0xBB, 0xBF, ..];
            var text = System.Text.Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            if (!text.Contains("\r\n") && text.Contains('\n')) newline = "\n";
            lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            if (lines is [.., ""]) lines = lines[..^1]; // the final line break
            Directory.CreateDirectory(backupDir);
            File.Copy(ini, Path.Combine(backupDir, $"{Path.GetFileNameWithoutExtension(ini)}-{DateTime.UtcNow:yyyyMMddHHmmss}.ini"), overwrite: true);
        }
        var output = string.Join(newline, Replace(lines, mode, view)) + newline;
        var tmp = ini + ".pinbot-tmp";
        File.WriteAllText(tmp, output, new System.Text.UTF8Encoding(bom));
        File.Move(tmp, ini, overwrite: true);
        return (previous, Read(vpxPath, mode)?.Settings);
    }

    static IEnumerable<(string Section, string Key, string Value)> Entries(IEnumerable<string> lines)
    {
        var section = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            var i = line.IndexOf('=');
            if (i > 0) yield return (section, line[..i].Trim(), line[(i + 1)..]);
        }
    }
}
