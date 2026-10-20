using System.Text.Json;

namespace PinBot.Agent;

/// <summary>A table -> ROM/NVRAM association confirmed by watching which .nv file a real session wrote.</summary>
public sealed record ObservedAssociation(string Rom, string NvPath, DateTime ObservedAtUtc);

/// <summary>
/// A competition known to this cabinet: the server is the source of truth and this is its cached copy, so NVRAM is
/// still prepared and restored while the server is unreachable.
/// </summary>
public sealed record LocalCompetition(string Id, string Rom, string NvPath, string Table, DateTime StartsUtc, DateTime EndsUtc)
{
    public DateTime? EndedEarlyUtc { get; init; }
    public DateTime EffectiveEndUtc => EndedEarlyUtc is { } e && e < EndsUtc ? e : EndsUtc;
    public bool IsActive(DateTime nowUtc) => nowUtc >= StartsUtc && nowUtc < EffectiveEndUtc;
}

/// <summary>
/// The agent's own small state file (never NVRAM): one-time path overrides and observed associations.
/// Written atomically (temp file + replace).
/// </summary>
public sealed class LocalState
{
    public List<string> TableDirs { get; set; } = [];
    public List<string> VpxDirs { get; set; } = [];
    public Dictionary<string, ObservedAssociation> Observed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LocalCompetition> Competitions { get; set; } = [];

    /// <summary>Set by `pinbot pair`; when present, competitions come from the server and results are uploaded.</summary>
    public Sync.ServerLink? Server { get; set; }

    /// <summary>
    /// ROMs that reset their high-score table on a competition file (the ROM rejected it): not offered for competitions
    /// on this cabinet until a release lists the ROM in <see cref="RejectedRom.FixedInThisRelease"/>.
    /// </summary>
    public Dictionary<string, RejectedRom> RejectedRoms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Dir => Environment.GetEnvironmentVariable("PINBOT_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinBot");

    static string FilePath => Path.Combine(Dir, "state.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LocalState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var s = JsonSerializer.Deserialize<LocalState>(File.ReadAllText(FilePath), Json) ?? new();
            s.Observed = new(s.Observed, StringComparer.OrdinalIgnoreCase);
            return s;
        }
        catch (JsonException ex)
        {
            Log.Warn($"state.json unreadable, starting empty (file kept as state.json.bad): {ex.Message}");
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            return new();
        }
    }

    /// <summary>
    /// Load-modify-save under a per-user mutex, so the watcher and other PinBot commands (or the watcher's own threads)
    /// never overwrite each other's changes. Returns the saved state.
    /// </summary>
    public static LocalState Update(Action<LocalState> change) => Update(s => { change(s); return true; });

    /// <summary>
    /// Like <see cref="Update(Action{LocalState})"/>, but saves only when <paramref name="change"/> returns true (it
    /// decides on the freshly loaded state). Throws IOException when another PinBot holds the state for 30 s.
    /// </summary>
    public static LocalState Update(Func<LocalState, bool> change)
    {
        using var mutex = new Mutex(false, @"Local\PinBot.state");
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { owned = true; } // previous holder crashed; the file itself is written atomically
        if (!owned) throw new IOException("state.json is busy: another PinBot did not finish its change within 30 s");
        try
        {
            var s = Load();
            if (change(s)) s.Save();
            return s;
        }
        finally { mutex.ReleaseMutex(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, this, Json);
            fs.Flush(flushToDisk: true);
        }
        // another PinBot (e.g. `pinbot status`) may be reading state.json this instant, which blocks the replace briefly
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(tmp, FilePath, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10) { Thread.Sleep(100); }
        }
    }
}

/// <param name="Release">the agent release that saw it (0 for development builds)</param>
/// <param name="Evidence">folder with the competition file, the file the ROM left, and the player's original</param>
public sealed record RejectedRom(DateTime AtUtc, long Release, string CompetitionId, string Evidence)
{
    /// <summary>
    /// ROMs whose rejection a fix in this release addresses: a rejection recorded by an earlier release is lifted, so the
    /// ROM is offered again (and is rejected again, once, should the fix not hold). Add a ROM here with its fix.
    /// </summary>
    public static readonly IReadOnlySet<string> FixedInThisRelease = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public const string Reason = Contracts.CompetitionStates.RomRejected;

    /// <summary>Whether <paramref name="rom"/> is still rejected here: recorded, and not lifted by a newer release that fixes it.</summary>
    public static bool Applies(LocalState state, string? rom, long currentRelease, IReadOnlySet<string>? fixedRoms = null) =>
        rom != null && state.RejectedRoms.TryGetValue(rom, out var r)
        && !((fixedRoms ?? FixedInThisRelease).Contains(rom) && currentRelease > r.Release);
}
