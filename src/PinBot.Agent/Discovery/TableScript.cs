using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace PinBot.Agent.Discovery;

/// <summary>
/// Extracts a VPX table's script and the PinMAME ROM name(s) it statically assigns.
/// Script-derived ROMs are unverified hints: scripts can choose a ROM dynamically.
/// </summary>
public static partial class TableScript
{
    /// <summary>
    /// VPX prefers a sidecar "&lt;table&gt;.vbs" next to the .vpx; otherwise the script is the CODE record
    /// of the GameStg/GameData stream inside the .vpx (an OLE compound file).
    /// </summary>
    public static (string? Script, string Source) Read(string vpxPath)
    {
        var sidecar = Path.ChangeExtension(vpxPath, ".vbs");
        if (File.Exists(sidecar))
            return (File.ReadAllText(sidecar, Encoding.Latin1), "sidecar .vbs");
        try
        {
            using var root = RootStorage.OpenRead(vpxPath);
            using var stream = root.OpenStorage("GameStg").OpenStream("GameData");
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            var script = ExtractCode(data);
            return (script, script is null ? "no CODE record in .vpx" : ".vpx embedded");
        }
        catch (Exception ex) when (ex is IOException or FileFormatException or UnauthorizedAccessException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
        {
            return (null, $".vpx unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// GameData is a sequence of BIFF records: int32 size, then a 4-char tag and payload of (size - 4) bytes.
    /// CODE is special: its size is 4 (tag only) and is followed by int32 script length + script bytes.
    /// </summary>
    internal static string? ExtractCode(byte[] data)
    {
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            int size = BitConverter.ToInt32(data, pos);
            if (size < 4 || pos + 4 + size > data.Length) break;
            var tag = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (tag == "CODE")
            {
                int lenPos = pos + 8;
                if (lenPos + 4 > data.Length) return null;
                int len = BitConverter.ToInt32(data, lenPos);
                if (len < 0 || lenPos + 4 + len > data.Length) return null;
                return Encoding.Latin1.GetString(data, lenPos + 4, len);
            }
            if (tag == "ENDB") break;
            pos += 4 + size;
        }
        return null;
    }

    // cGameName = "afm_113b"  |  Const cGameName="flash_l1",UseSolenoids=2  |  .GameName = "tz_92"
    [GeneratedRegex(@"(?<![\w])(?:c?GameName|\.GameName)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex RomAssignment();

    /// <summary>Distinct ROM names assigned as string literals in non-comment code, in order of appearance.</summary>
    public static IReadOnlyList<string> FindRoms(string script)
    {
        var roms = new List<string>();
        foreach (var rawLine in script.Split('\n'))
        {
            var code = StripComment(rawLine);
            foreach (Match m in RomAssignment().Matches(code))
            {
                var rom = m.Groups[1].Value.Trim();
                if (rom.Length > 0 && !roms.Contains(rom, StringComparer.OrdinalIgnoreCase))
                    roms.Add(rom);
            }
        }
        return roms;
    }

    // NVOffset (2)  |  Controller.NVOffset 2  -> PinMAME writes "<rom> v2.nv" so tables sharing a ROM keep separate NVRAM
    [GeneratedRegex(@"(?<![\w])NVOffset\s*\(?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NvOffsetCall();

    // Controller.Stop: without it PinMAME may never write NVRAM when the table exits
    [GeneratedRegex(@"\.Stop\b", RegexOptions.IgnoreCase)]
    private static partial Regex ControllerStop();

    /// <summary>The NVOffset set by non-comment code, if any (0 = none).</summary>
    public static int FindNvOffset(string script)
    {
        foreach (var line in script.Split('\n'))
            if (NvOffsetCall().Match(StripComment(line)) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n > 0)
                return n;
        return 0;
    }

    public static bool CallsControllerStop(string script) =>
        script.Split('\n').Any(l => ControllerStop().IsMatch(StripComment(l)));

    /// <summary>Removes a VBScript ' or REM comment, respecting string literals.</summary>
    internal static string StripComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("rem", StringComparison.OrdinalIgnoreCase))
            return "";
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (line[i] == '\'' && !inString) return line[..i];
        }
        return line;
    }
}
