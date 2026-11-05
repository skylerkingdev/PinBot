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
