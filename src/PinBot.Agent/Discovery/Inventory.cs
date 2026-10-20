using PinBot.Agent.Scoring;

namespace PinBot.Agent.Discovery;

public enum RomSource { Observed, Script, Ambiguous, Unknown }
public enum NvState { Resolved, NotCreatedYet, Ambiguous, NoNvramFolder }

public sealed record TableInfo(
    string TablePath,
    string? Rom,
    RomSource RomSource,
    IReadOnlyList<string> ScriptRoms,
    string ScriptNote,
    string? NvPath,
    NvState NvState,
    IReadOnlyList<string> NvCandidates,
    bool Supported,
    string? SupportNote)
{
    /// <summary>
    /// Could take part in a competition: a decoder for this exact revision, and evidence the ROM really writes this file:
    /// observed in a session, or a single script ROM corroborated by exactly one existing .nv file.
    /// </summary>
    public bool Eligible => Rom != null && RomSource is RomSource.Observed or RomSource.Script
                            && NvState == NvState.Resolved && Supported;

    public string Problem =>
        Rom == null ? (RomSource == RomSource.Ambiguous ? $"ambiguous ROM ({string.Join(", ", ScriptRoms)})" : $"no ROM found ({ScriptNote})")
        : !Supported ? $"unsupported ROM: {SupportNote}"
        : NvState == NvState.Ambiguous ? $"ambiguous NVRAM: {string.Join(" | ", NvCandidates)}"
        : NvState == NvState.NoNvramFolder ? "no NVRAM folder found for this ROM"
        : NvState == NvState.NotCreatedYet ? "never played here (no .nv yet): play once to enable"
        : "";
}

public static class Inventory
{
    public static List<TableInfo> Build(Installations inst, MemoryMaps maps, LocalState state, TableScriptCache? cache = null)
    {
        var vpxFiles = inst.TableDirs
            .SelectMany(d => SafeEnumerate(d))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        return vpxFiles.Select(f => Describe(f, inst, maps, state, cache)).ToList();
    }

    /// <summary>The ROM names, NVRAM offset and script source of a table, from its script (or the cache).</summary>
    public static ScriptFacts ReadScript(string vpxPath)
    {
        var (script, source) = TableScript.Read(vpxPath);
        return new(script is null ? [] : TableScript.FindRoms(script).ToList(), script is null ? 0 : TableScript.FindNvOffset(script), source);
    }

    public static TableInfo Describe(string vpxPath, Installations inst, MemoryMaps maps, LocalState state, TableScriptCache? cache = null)
    {
        var facts = cache?.Get(vpxPath, () => ReadScript(vpxPath)) ?? ReadScript(vpxPath);
        var source = facts.Source;
        // VPMAlias.txt maps a script's ROM name to the real ROM, whose name PinMAME uses for NVRAM.
        var rawRoms = facts.Roms;
        var scriptRoms = rawRoms.Select(inst.ResolveAlias).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in rawRoms.Where(r => inst.Aliases.ContainsKey(r)))
            source += $"; alias {r} -> {inst.ResolveAlias(r)}";
        int nvOffset = facts.NvOffset;
        if (nvOffset > 0) source += $"; NVOffset {nvOffset}";
        string? rom = null;
        var romSource = RomSource.Unknown;
        string? nvPath = null;
        NvState nvState;
        IReadOnlyList<string> candidates;

        if (state.Observed.TryGetValue(vpxPath, out var seen))
        {
            rom = seen.Rom;
            romSource = RomSource.Observed;
        }
        else if (scriptRoms.Count == 1) { rom = scriptRoms[0]; romSource = RomSource.Script; }
        else if (scriptRoms.Count > 1) romSource = RomSource.Ambiguous;

        if (rom == null)
            return new(vpxPath, null, romSource, scriptRoms, source, null, NvState.NoNvramFolder, [], false, null);

        if (seen != null)
        {
            candidates = [seen.NvPath];
            nvPath = seen.NvPath;
            nvState = NvState.Resolved;
        }
        else
            (nvPath, nvState, candidates) = ResolveNv(rom, vpxPath, inst, NvFileName(rom, nvOffset));

        var map = maps.TryLoad(rom, out var why);
        // Attribution needs initials; many pre-1983 machines store only a score.
        if (map is { HasInitials: false }) why = "ROM stores no initials, so scores cannot be attributed";
        else if (map?.CompetitionBlocker is { } blocker) why = blocker;
        bool supported = map is { HasInitials: true, CompetitionBlocker: null };
        return new(vpxPath, rom, romSource, scriptRoms, source, nvPath, nvState, candidates, supported, why);
    }

    /// <summary>Folders PinMAME may write &lt;rom&gt;.nv to for this table: a per-table pinmame\nvram folder, then global ones.</summary>
    public static IReadOnlyList<string> NvramDirsFor(string vpxPath, Installations inst)
    {
        var dirs = new List<string>();
        var tableDir = Path.GetDirectoryName(vpxPath);
        if (tableDir != null)
        {
            var local = Path.Combine(tableDir, "pinmame", "nvram");
            if (Directory.Exists(local)) dirs.Add(local);
        }
        dirs.AddRange(inst.GlobalNvramDirs);
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Exactly one existing file -> Resolved. None -> NotCreatedYet (path PinMAME will create in the only folder),
    /// or Ambiguous if several folders could receive it. Several existing files -> Ambiguous until a session is observed.
    /// </summary>
    public static (string? Path, NvState State, IReadOnlyList<string> Candidates) ResolveNv(
        string rom, string vpxPath, Installations inst, string? nvFileName = null)
    {
        var dirs = NvramDirsFor(vpxPath, inst);
        if (dirs.Count == 0) return (null, NvState.NoNvramFolder, []);
        var paths = dirs.Select(d => Path.Combine(d, nvFileName ?? NvFileName(rom, 0))).ToList();
        var existing = paths.Where(File.Exists).ToList();
        return existing.Count switch
        {
            1 => (existing[0], NvState.Resolved, paths),
            > 1 => (null, NvState.Ambiguous, existing),
            _ => dirs.Count == 1 ? (paths[0], NvState.NotCreatedYet, paths) : (null, NvState.Ambiguous, paths),
        };
    }

    /// <summary>"afm_113b.nv", or "afm_113b v2.nv" when the table sets NVOffset 2.</summary>
    public static string NvFileName(string rom, int nvOffset) => nvOffset > 0 ? $"{rom} v{nvOffset}.nv" : $"{rom}.nv";

    static IEnumerable<string> SafeEnumerate(string dir) =>
        Directory.EnumerateFiles(dir, "*.vpx", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        });
}
