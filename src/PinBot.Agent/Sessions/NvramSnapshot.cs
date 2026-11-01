using PinBot.Agent.Scoring;

namespace PinBot.Agent.Sessions;

public sealed record FileStamp(long Length, DateTime LastWriteUtc);

/// <summary>What one .nv file did during a session, and whether its new entries can be trusted.</summary>
public sealed record RomOutcome(
    string Rom, string NvPath, Extraction? Before, Extraction? After, ScoreDiff? Diff, IReadOnlyList<string> Problems)
{
    public bool Trusted => Problems.Count == 0;
}

/// <summary>
/// Pre-session NVRAM state: size/mtime of every .nv in the watched folders plus the bytes of the files
/// expected to change. Taken in memory; nothing is written anywhere.
/// </summary>
public sealed class NvramSnapshot
{
    public required IReadOnlyList<string> Dirs { get; init; }
    public required Dictionary<string, FileStamp> Stamps { get; init; }
    public required Dictionary<string, byte[]> Bytes { get; init; }

    const long MaxBaselineBytes = 64L * 1024 * 1024;

    /// <param name="expectedRoms">ROMs to baseline. Empty = baseline every supported ROM's file (table unknown).</param>
    public static NvramSnapshot Capture(IReadOnlyList<string> dirs, IReadOnlyCollection<string> expectedRoms, MemoryMaps maps)
    {
        var stamps = Stamp(dirs);
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var wanted = stamps.Keys.Where(p =>
        {
            var rom = HighScoreReader.RomFromFileName(p);
            return expectedRoms.Count > 0 ? expectedRoms.Contains(rom, StringComparer.OrdinalIgnoreCase) : maps.HasMap(rom);
        });
        foreach (var path in wanted)
        {
            if (total + stamps[path].Length > MaxBaselineBytes) break;
            var (data, _, _) = HighScoreReader.ReadShared(path);
            if (data is null) continue;
            bytes[path] = data;
            total += data.Length;
        }
        return new() { Dirs = dirs, Stamps = stamps, Bytes = bytes };
    }

    public static Dictionary<string, FileStamp> Stamp(IEnumerable<string> dirs)
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.nv"))
                    result[f.FullName] = new FileStamp(f.Length, f.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* folder vanished: no files */ }
        }
        return result;
    }

    /// <summary>Waits until the folders stop changing (PinMAME has finished writing). Returns false on timeout.</summary>
    public static async Task<(bool Stable, Dictionary<string, FileStamp> Stamps)> WaitForStableAsync(
        IReadOnlyList<string> dirs, TimeSpan interval, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var previous = Stamp(dirs);
        int quiet = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            var current = Stamp(dirs);
            quiet = SameStamps(previous, current) ? quiet + 1 : 0;
            if (quiet >= 2) return (true, current);
            previous = current;
        }
        return (false, previous);
    }

    static bool SameStamps(Dictionary<string, FileStamp> a, Dictionary<string, FileStamp> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Files whose size/mtime changed or that appeared since the snapshot.</summary>
    public IReadOnlyList<string> ChangedFiles(Dictionary<string, FileStamp> after) =>
        after.Where(kv => !Stamps.TryGetValue(kv.Key, out var b) || b != kv.Value)
             .Select(kv => kv.Key).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Decodes before/after for one changed file and lists every reason its new entries cannot be trusted.</summary>
    public RomOutcome Analyze(string nvPath, MemoryMaps maps)
    {
        var rom = HighScoreReader.RomFromFileName(nvPath);
        var problems = new List<string>();
        if (maps.TryLoad(rom, out var why) is not { } map)
            return new(rom, nvPath, null, null, null, [$"unsupported ROM: {why}"]);
        if (map.OverlapProblem is { } overlap) problems.Add(overlap);

        Extraction? before = null;
        if (Bytes.TryGetValue(nvPath, out var b)) before = HighScoreReader.Extract(maps, rom, b, nvPath);
        else if (!Stamps.ContainsKey(nvPath)) problems.Add("NVRAM file was created during this session; its entries are ROM defaults, not new scores");
        else problems.Add("no baseline bytes captured for this file (ROM was not expected for this table)");

        var after = HighScoreReader.Extract(maps, rom, nvPath);
        if (after.Status == ExtractStatus.FileLocked)
            problems.Add("NVRAM file still locked by another process");
        else if (!after.Ok) problems.Add($"after: {after.Status} {after.Detail}");
        if (before is { Ok: false }) problems.Add($"before: {before.Status} {before.Detail}");

        ScoreDiff? diff = null;
        if (before is { Ok: true } && after.Ok)
        {
            diff = ScoreDiff.Compare(before.Entries, after.Entries);
            if (diff.SuspectedReset) problems.Add("every previous high score disappeared: NVRAM looks re-initialized (factory reset?)");
        }
        return new(rom, nvPath, before, after, diff, problems);
    }
}
