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
    string? SupportNote,
    TableMeta? Meta = null,
    BackglassInfo? Backglass = null,
    bool AltColor = false,
    bool AltSound = false,
    PovInfo? Pov = null,
    ScriptChange? Change = null)
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

/// <summary>A table's script changed while its version didn't: the hash it changed from, and the changed lines.</summary>
public sealed record ScriptChange(string Base, string Diff);

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

    /// <summary>The ROM names, NVRAM offset and script source of a table, its own description, and its backglass header.</summary>
    public static ScriptFacts ReadScript(string vpxPath)
    {
        var (script, source) = TableScript.Read(vpxPath);
        // kept (compressed, locally) so that when the script changes, the changed lines can be shared as a fix
        if (script is not null) ScriptStore.Save(TableMetadata.HashScript(script), script);
        return new(script is null ? [] : TableScript.FindRoms(script).ToList(), script is null ? 0 : TableScript.FindNvOffset(script), source,
            TableMetadata.Read(vpxPath, script), Discovery.Backglass.Read(Discovery.Backglass.PathFor(vpxPath)));
    }

    public static TableInfo Describe(string vpxPath, Installations inst, MemoryMaps maps, LocalState state, TableScriptCache? cache = null)
    {
        // a fix PinBot installed for this table's old script must not override a replaced table's new one
        try { if (FixFiles.MoveAsideIfStale(vpxPath) is { } note) Log.Warn($"[setups] {note}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

        // the table's view for this VPin's mode: a small .ini, read every time (not cached)
        var pov = Discovery.Pov.Read(vpxPath, inst.ViewMode);
        if (rom == null)
            return new(vpxPath, null, romSource, scriptRoms, source, null, NvState.NoNvramFolder, [], false, null, facts.Meta, facts.Backglass, Pov: pov);

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
        var (altColor, altSound) = Extras(rom, vpxPath, inst);
        return new(vpxPath, rom, romSource, scriptRoms, source, nvPath, nvState, candidates, supported, why, facts.Meta, facts.Backglass, altColor, altSound, pov);
    }

    /// <summary>
    /// Whether PinMAME has a colorization (altcolor\&lt;rom&gt;) or alternative sound (altsound\&lt;rom&gt;) for this ROM, in
    /// any PinMAME folder this table uses (the parent of each NVRAM folder). Shown on table-update posts only.
    /// </summary>
    public static (bool AltColor, bool AltSound) Extras(string rom, string vpxPath, Installations inst)
    {
        bool color = false, sound = false;
        foreach (var nv in NvramDirsFor(vpxPath, inst))
        {
            if (Path.GetDirectoryName(nv) is not { } pinmame) continue;
            color |= Directory.Exists(Path.Combine(pinmame, "altcolor", rom));
            sound |= Directory.Exists(Path.Combine(pinmame, "altsound", rom));
        }
        return (color, sound);
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
