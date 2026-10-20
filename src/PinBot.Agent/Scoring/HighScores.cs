using System.Security.Cryptography;

namespace PinBot.Agent.Scoring;

/// <summary>One decoded high-score or mode-champion slot.</summary>
public sealed record ScoreEntry(
    string Section, string Label, string? ShortLabel,
    string? RawInitials, long? Score, string? ScoreText, bool DefaultInitials)
{
    public bool IsHighScore => Section == "high_scores";

    /// <summary>Uppercased, trimmed printable initials; null when blank, default/unused, or not printable.</summary>
    public string? Initials => NormalizeInitials(RawInitials, DefaultInitials);

    public static string? NormalizeInitials(string? raw, bool isDefault = false)
    {
        if (raw is null || isDefault) return null;
        var s = raw.Replace("\0", "").Trim();
        // Sega Whitestar long names (South Park: 10 characters) end at a '[' marker, and the letter before it repeats
        // the last one entered (the next position's preset): "SKYY[[[[[[" was entered as SKY, "JBJJ[[[[[[" as JBJ
        int end = raw.Length > 3 ? s.IndexOf('[') : -1;
        if (end >= 0)
        {
            s = s[..end].TrimEnd();
            if (s.Length >= 2 && s[^1] == s[^2]) s = s[..^1];
        }
        if (s.Length == 0 || s.Any(c => c < 0x20 || c > 0x7E)) return null;
        return s.ToUpperInvariant();
    }

    /// <summary>Initials and score only, for before/after columns.</summary>
    public string Brief => $"{Initials ?? "---",-4} {(Score is { } v ? v.ToString("N0") : ScoreText ?? "-"),16}";

    public override string ToString() => $"{ShortLabel ?? Label,-14} {Brief}";
}

public enum ExtractStatus
{
    Ok,
    UnsupportedRom,  // no tested memory map for this exact ROM revision
    NoFile,          // ROM has never written NVRAM at this path
    FileLocked,      // another process holds the file exclusively
    ReadError,
    Corrupt,         // file too small for the map, or a checksum over the score bytes fails
}

public sealed record Extraction(
    string Rom, string? NvPath, ExtractStatus Status, string? Detail,
    IReadOnlyList<ScoreEntry> Entries, string? Sha256)
{
    public bool Ok => Status == ExtractStatus.Ok;
}

public static class HighScoreReader
{
    /// <summary>Reads the .nv file without ever blocking PinMAME (shares read/write/delete) and decodes it.</summary>
    public static Extraction Extract(MemoryMaps maps, string rom, string nvPath)
    {
        if (maps.TryLoad(rom, out var why) is null)
            return new(rom, nvPath, ExtractStatus.UnsupportedRom, why, [], null);
        var (bytes, status, detail) = ReadShared(nvPath);
        if (bytes is null) return new(rom, nvPath, status, detail, [], null);
        return Extract(maps, rom, bytes, nvPath);
    }

    public static Extraction Extract(MemoryMaps maps, string rom, byte[] nv, string? nvPath = null)
    {
        var map = maps.TryLoad(rom, out var why);
        if (map is null) return new(rom, nvPath, ExtractStatus.UnsupportedRom, why, [], null);
        return Extract(map, nv, nvPath);
    }

    public static Extraction Extract(RomMap map, byte[] nv, string? nvPath = null)
    {
        var rom = map.Rom;
        var sha = Sha256(nv);
        try
        {
            var entries = map.Decode(nv);
            var bad = map.VerifyChecksums(nv);
            if (bad.Count > 0)
                return new(rom, nvPath, ExtractStatus.Corrupt, "checksum mismatch: " + string.Join(", ", bad), entries, sha);
            return new(rom, nvPath, ExtractStatus.Ok, $"{map.MapFile} ({map.Platform})", entries, sha);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return new(rom, nvPath, ExtractStatus.Corrupt, ex.Message, [], sha);
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Opens with FileShare.ReadWrite|Delete so PinMAME can always write; never opens for writing.
    /// A sharing violation means a writer holds the file exclusively.
    /// </summary>
    public static (byte[]? Bytes, ExtractStatus Status, string? Detail) ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[fs.Length];
            fs.ReadExactly(buf);
            return (buf, ExtractStatus.Ok, null);
        }
        catch (FileNotFoundException) { return (null, ExtractStatus.NoFile, "file does not exist"); }
        catch (DirectoryNotFoundException) { return (null, ExtractStatus.NoFile, "directory does not exist"); }
        catch (IOException ex) when (IsSharingViolation(ex)) { return (null, ExtractStatus.FileLocked, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, ExtractStatus.ReadError, ex.Message); }
    }

    static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>ROM name from an .nv filename: "afm_113b.nv" -> "afm_113b"; NVOffset file "afm_113b v2.nv" -> "afm_113b".</summary>
    public static string RomFromFileName(string path)
    {
        var name = Path.GetFileName(path);
        int dot = name.IndexOf('.');
        if (dot > 0) name = name[..dot];
        return System.Text.RegularExpressions.Regex.Replace(name, @"\s+v\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>Result of comparing two snapshots of the same ROM's NVRAM.</summary>
public sealed record ScoreDiff(
    IReadOnlyList<ScoreEntry> NewEntries,
    IReadOnlyList<(ScoreEntry? Before, ScoreEntry After)> ChangedSlots,
    bool SuspectedReset)
{
    public bool Any => NewEntries.Count > 0 || ChangedSlots.Count > 0;

    /// <summary>
    /// New qualifying entries are high-score (initials, score) pairs present more often after than before.
    /// Multiset comparison makes shifting slots (a new #2 pushes #2..#4 down) produce exactly one new entry.
    /// Mode champions are reported as changed slots only; they are not qualifying scores.
    /// </summary>
    public static ScoreDiff Compare(IReadOnlyList<ScoreEntry> before, IReadOnlyList<ScoreEntry> after)
    {
        static (string?, long) Key(ScoreEntry e) => (e.Initials, e.Score!.Value);

        var remaining = before.Where(e => e.IsHighScore && e.Score is > 0)
            .GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var newEntries = new List<ScoreEntry>();
        foreach (var e in after.Where(e => e.IsHighScore && e.Score is > 0))
        {
            var k = Key(e);
            if (remaining.TryGetValue(k, out var n) && n > 0) remaining[k] = n - 1;
            else newEntries.Add(e);
        }

        var changed = new List<(ScoreEntry?, ScoreEntry)>();
        for (int i = 0; i < after.Count; i++)
        {
            var b = i < before.Count ? before[i] : null;
            var a = after[i];
            if (b is null || b.Label != a.Label || b.Initials != a.Initials || b.Score != a.Score || b.ScoreText != a.ScoreText)
                changed.Add((b, a));
        }

        // If every previously stored high score vanished, the ROM most likely re-initialized its NVRAM
        // (factory reset / settings change). Defaults written by a reset must not be accepted as new scores.
        var beforeScored = before.Where(e => e.IsHighScore && e.Score is > 0).ToList();
        bool reset = beforeScored.Count >= 2 && remaining.Values.Sum() == beforeScored.Count;

        return new ScoreDiff(newEntries.OrderByDescending(e => e.Score).ToList(), changed, reset);
    }
}
