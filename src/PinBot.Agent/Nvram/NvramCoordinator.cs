using System.Text.Json;
using System.Text.Json.Serialization;
using PinBot.Agent.Scoring;

namespace PinBot.Agent.Nvram;

public enum CompState
{
    Preparing,       // journaled intent; live file untouched
    BackedUp,        // verified read-only backup exists; live file is still the original
    Ready,           // live file is the competition file (kept for the whole competition)
    PendingRestore,  // competition over; waiting for an idle moment
    Restoring,       // journaled intent to put the backup back
    Restored,        // live file verified byte-for-byte equal to the backup (terminal)
    Abandoned,       // stopped before the live file was touched; may be retried (terminal)
    Refused,         // this ROM cannot get a verified competition file (terminal)
    NeedsRecovery,   // something unexpected; both copies kept; manual check (terminal for automation)
}

/// <summary>One competition's ownership of one actual NVRAM file. Persisted as entry.json next to the backup.</summary>
public sealed class JournalEntry
{
    public string CompetitionId { get; set; } = "";
    public string Rom { get; set; } = "";
    public string NvPath { get; set; } = "";
    public CompState State { get; set; }
    public string? OriginalSha256 { get; set; }
    public long OriginalLength { get; set; }
    public string? CompetitionSha256 { get; set; }
    public List<long> Ladder { get; set; } = [];
    public DateTime? ReadyAtUtc { get; set; }
    public string? Problem { get; set; }
    public List<string> History { get; set; } = [];

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public bool Finished => State is CompState.Restored or CompState.Abandoned or CompState.Refused;
    [JsonIgnore] public bool Blocking => !Finished; // includes NeedsRecovery: never prepare that path again automatically
    [JsonIgnore] public string BackupPath => Path.Combine(Dir, "original.nv");
}

/// <summary>Thrown by fault-injection hooks in tests to simulate the process dying at a precise step.</summary>
public sealed class SimulatedCrash(string point) : Exception($"simulated crash at {point}");

/// <summary>
/// Owns the competition NVRAM lifecycle: verified backup, atomic swap to a ladder-seeded competition file,
/// retention for the whole competition, idle-only byte-for-byte restoration, and journal-driven crash recovery.
///
/// Safety rules: the journal is written before the live file changes; the live file is only ever replaced by an
/// atomic rename of a verified temp file in the same folder, so it is always either the original or the competition
/// file; nothing touches a live file while an emulator may be using it (mayTouch: no running table plays that ROM,
/// checked again just before the rename); a backup is never overwritten or deleted;
/// anything unexpected stops automation for that file (NeedsRecovery) with every copy kept.
/// </summary>
public sealed class NvramCoordinator(string root, MemoryMaps maps, Func<string, bool> mayTouch, Func<DateTime> utcNow)
{
    public Action<string>? FaultInjection { get; init; }

    /// <summary>Called with the path each time a live NVRAM file was replaced (swapped in or restored).</summary>
    public Action<string>? Replaced { get; set; }

    const string TempSuffix = ".pinbot-tmp";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string Root => root;

    readonly HashSet<string> _reportedUnreadable = [];

    // ---------------- journal ----------------

    public List<JournalEntry> LoadEntries() => LoadAll().Entries;

    public (List<JournalEntry> Entries, List<string> Unreadable) LoadAll()
    {
        var entries = new List<JournalEntry>();
        var unreadable = new List<string>();
        if (!Directory.Exists(root)) return (entries, unreadable);
        foreach (var dir in Directory.GetDirectories(root).OrderBy(Directory.GetCreationTimeUtc).ThenBy(d => d, StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "entry.json");
            try
            {
                var e = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(file), Json)
                        ?? throw new JsonException("empty journal");
                e.Dir = dir;
                entries.Add(e);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add($"{dir}: {ex.Message}");
            }
        }
        return (entries, unreadable);
    }

    void Save(JournalEntry e)
    {
        Directory.CreateDirectory(e.Dir);
        WriteDurable(Path.Combine(e.Dir, "entry.json"), JsonSerializer.SerializeToUtf8Bytes(e, Json), atomic: true);
    }

    void Transition(JournalEntry e, CompState to, string note)
    {
        e.History.Add($"{utcNow():O} {e.State} -> {to}: {note}");
        e.State = to;
        if (to is CompState.NeedsRecovery or CompState.Refused) e.Problem = note;
        if (to == CompState.Ready) e.ReadyAtUtc = utcNow();
        Save(e);
        var line = $"[nvram] {e.CompetitionId} {e.Rom}: {to} - {note}";
        if (to is CompState.NeedsRecovery) Log.Error(line);
        else if (to is CompState.Refused or CompState.Abandoned) Log.Warn(line);
        else Log.Info(line);
    }

    void Fault(string point) => FaultInjection?.Invoke(point);

    // ---------------- main loop ----------------

    /// <summary>
    /// One reconciliation pass: recover interrupted steps, then prepare active competitions, keep running ones,
    /// and restore ended ones. File changes only happen while the emulator is idle.
    /// </summary>
    public void Tick(IReadOnlyList<LocalCompetition> competitions) => Exclusive(() => TickLocked(competitions));

    /// <summary>Reconciles interrupted steps (see RecoverLocked). Skipped if another PinBot process is busy.</summary>
    public void Recover() => Exclusive(RecoverLocked);

    /// <summary>
    /// Only one PinBot process at a time may work on this journal (e.g. watch plus uninstall):
    /// a busy lock skips the pass instead of waiting, and the next tick retries.
    /// </summary>
    void Exclusive(Action work)
    {
        using var mutex = new Mutex(false, @"Local\PinBot.nvram." +HighScoreReader.Sha256(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()))[..16]);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;
        try { work(); }
        finally { mutex.ReleaseMutex(); }
    }

    void TickLocked(IReadOnlyList<LocalCompetition> competitions)
    {
        RecoverLocked();
        var now = utcNow();
        var entries = LoadEntries();

        foreach (var e in entries.Where(e => e.State is CompState.BackedUp or CompState.Ready or CompState.PendingRestore))
        {
            var comp = competitions.FirstOrDefault(c => c.Id == e.CompetitionId && SamePath(c.NvPath, e.NvPath));
            bool over = comp is null || now >= comp.EffectiveEndUtc;
            if (e.State == CompState.Ready && over) Transition(e, CompState.PendingRestore, "competition ended");
            if (e.State == CompState.BackedUp && over)
            {
                // never swapped (Recover has already promoted a completed swap to Ready): the live file is the player's own
                if (Classify(e) == LiveKind.Original) Transition(e, CompState.Restored, "competition ended before the swap; live file is the player's own");
                else Transition(e, CompState.NeedsRecovery, "competition ended before the swap but the live file cannot be identified");
                continue;
            }
            if (!mayTouch(e.NvPath)) continue;
            if (e.State == CompState.BackedUp) Swap(e, image: null);
            else if (e.State == CompState.PendingRestore) Restore(e);
        }

        foreach (var comp in competitions.Where(c => c.IsActive(now)))
        {
            var mine = entries.Where(e => e.CompetitionId == comp.Id && SamePath(e.NvPath, comp.NvPath)).ToList();
            if (mine.Any(e => e.Blocking || e.State == CompState.Refused)) continue;
            if (entries.Any(e => e.Blocking && e.CompetitionId != comp.Id && SamePath(e.NvPath, comp.NvPath)))
                continue; // an earlier competition still owns this file: wait until it is restored
            if (mayTouch(comp.NvPath)) Prepare(comp);
        }
    }

    /// <summary>The entry currently owning this NVRAM file, if any.</summary>
    public JournalEntry? Owner(string nvPath) =>
        LoadEntries().LastOrDefault(e => e.Blocking && SamePath(e.NvPath, nvPath));

    // ---------------- prepare ----------------

    void Prepare(LocalCompetition comp)
    {
        var map = maps.TryLoad(comp.Rom, out var why);
        var (original, status, detail) = HighScoreReader.ReadShared(comp.NvPath);
        if (original is null)
        {
            // absent original: nothing to protect, but also no verified base for a competition file - play once first
            if (status != ExtractStatus.NoFile) Log.Warn($"[nvram] {comp.Id} {comp.Rom}: deferred, {status} {detail}");
            return;
        }

        var e = new JournalEntry
        {
            CompetitionId = comp.Id,
            Rom = comp.Rom,
            NvPath = comp.NvPath,
            State = CompState.Preparing,
            Dir = Path.Combine(root, $"{utcNow():yyyyMMddTHHmmss}-{Sanitize(comp.Id)}-{Sanitize(comp.Rom)}-{Guid.NewGuid().ToString("N")[..6]}"),
        };

        var image = map?.BuildCompetitionImage(original);
        if (map is null || image is not { Ok: true })
        {
            Save(e);
            Transition(e, CompState.Refused, map is null ? $"no decoder: {why}" : $"no verified competition file: {image!.Problem}");
            return;
        }

        Save(e);
        Fault("prepare.journaled");

        // verified, read-only backup of the player's own NVRAM
        var tmp = e.BackupPath + ".tmp";
        WriteDurable(tmp, original, atomic: false);
        if (HighScoreReader.Sha256(File.ReadAllBytes(tmp)) != HighScoreReader.Sha256(original))
        {
            Transition(e, CompState.Abandoned, "backup did not verify; live file untouched");
            return;
        }
        File.Move(tmp, e.BackupPath);
        File.SetAttributes(e.BackupPath, FileAttributes.ReadOnly);
        Fault("prepare.backup-written");

        e.OriginalSha256 = HighScoreReader.Sha256(original);
        e.OriginalLength = original.Length;
        e.CompetitionSha256 = HighScoreReader.Sha256(image.Bytes!);
        e.Ladder = image.Ladder.ToList();
        Transition(e, CompState.BackedUp, $"backup verified ({original.Length} bytes)");
        Fault("prepare.backed-up");

        Swap(e, image.Bytes);
    }

    void Swap(JournalEntry e, byte[]? image)
    {
        if (image is null)
        {
            // resuming after a restart: rebuild deterministically from the verified backup
            var backup = File.ReadAllBytes(e.BackupPath);
            var map = maps.TryLoad(e.Rom, out _);
            image = HighScoreReader.Sha256(backup) == e.OriginalSha256 ? map?.BuildCompetitionImage(backup).Bytes : null;
            if (image is null || HighScoreReader.Sha256(image) != e.CompetitionSha256)
            {
                Transition(e, CompState.NeedsRecovery, "could not rebuild the competition file from the backup");
                return;
            }
        }

        var (live, status, _) = HighScoreReader.ReadShared(e.NvPath);
        if (live is null)
        {
            if (status == ExtractStatus.FileLocked) return; // defer
            Transition(e, CompState.NeedsRecovery, "NVRAM file disappeared after the backup");
            return;
        }
        if (HighScoreReader.Sha256(live) != e.OriginalSha256)
        {
            // a game was played between backup and swap: that backup is stale, start over (old files kept)
            Transition(e, CompState.Abandoned, "NVRAM changed after the backup; preparing again");
            return;
        }

        var tmp = e.NvPath + TempSuffix;
        WriteDurable(tmp, image, atomic: false);
        if (HighScoreReader.Sha256(File.ReadAllBytes(tmp)) != e.CompetitionSha256)
        {
            File.Delete(tmp);
            Transition(e, CompState.NeedsRecovery, "competition temp file did not verify");
            return;
        }
        Fault("swap.tmp-written");
        if (!mayTouch(e.NvPath)) { File.Delete(tmp); return; } // its table started: defer, live file untouched
        File.Move(tmp, e.NvPath, overwrite: true); // atomic replace on the same volume
        Replaced?.Invoke(e.NvPath);
        Fault("swap.moved");

        if (LiveSha(e.NvPath) != e.CompetitionSha256)
        {
            Transition(e, CompState.NeedsRecovery, "live file does not match the competition file after the swap");
            return;
        }
        Transition(e, CompState.Ready, $"competition file in place (ladder {string.Join("/", e.Ladder.Select(v => v.ToString("N0")))})");
    }

    // ---------------- restore ----------------

    void Restore(JournalEntry e)
    {
        var backup = File.Exists(e.BackupPath) ? File.ReadAllBytes(e.BackupPath) : null;
        if (backup is null || HighScoreReader.Sha256(backup) != e.OriginalSha256)
        {
            Transition(e, CompState.NeedsRecovery, "backup missing or its checksum does not match the journal; nothing overwritten");
            return;
        }

        var (live, status, _) = HighScoreReader.ReadShared(e.NvPath);
        if (status == ExtractStatus.FileLocked) return; // defer

        Transition(e, CompState.Restoring, "restoring the original");
        Fault("restore.journaled");

        if (live != null) WriteDurable(Path.Combine(e.Dir, "competition-final.nv"), live, atomic: true); // keep the competition's last state

        var tmp = e.NvPath + TempSuffix;
        WriteDurable(tmp, backup, atomic: false);
        if (HighScoreReader.Sha256(File.ReadAllBytes(tmp)) != e.OriginalSha256)
        {
            File.Delete(tmp);
            Transition(e, CompState.PendingRestore, "restore temp file did not verify; will retry");
            return;
        }
        Fault("restore.tmp-written");
        if (!mayTouch(e.NvPath)) { File.Delete(tmp); Transition(e, CompState.PendingRestore, "its table started; will retry"); return; }
        File.Move(tmp, e.NvPath, overwrite: true);
        Replaced?.Invoke(e.NvPath);
        Fault("restore.moved");

        if (LiveSha(e.NvPath) != e.OriginalSha256)
        {
            Transition(e, CompState.NeedsRecovery, "live file does not match the backup after restore; backup kept");
            return;
        }
        Transition(e, CompState.Restored, "original restored byte-for-byte");
    }

    // ---------------- recovery ----------------

    /// <summary>
    /// Reconciles steps interrupted by a crash or power loss, using the journal and the live file's hash.
    /// Never writes the live NVRAM file; only the journal and stale temp files.
    /// </summary>
    void RecoverLocked()
    {
        var (entries, unreadable) = LoadAll();
        foreach (var u in unreadable.Where(_reportedUnreadable.Add))
            Log.Error($"[nvram] unreadable journal, left untouched for manual recovery: {u}");

        foreach (var e in entries.Where(e => !e.Finished && e.State != CompState.NeedsRecovery))
        {
            var tmp = e.NvPath + TempSuffix;
            if (File.Exists(tmp)) File.Delete(tmp);
            var backupTmp = e.BackupPath + ".tmp";
            if (File.Exists(backupTmp)) File.Delete(backupTmp);

            switch (e.State)
            {
                case CompState.Preparing:
                    Transition(e, CompState.Abandoned, "interrupted before the backup completed; live file untouched");
                    break;
                case CompState.BackedUp:
                    switch (Classify(e))
                    {
                        case LiveKind.Competition:
                            Transition(e, CompState.Ready, "swap had completed before the interruption");
                            break;
                        case LiveKind.Unknown:
                            Transition(e, CompState.NeedsRecovery, "after an interruption the live file is neither the player's file nor the competition file");
                            break;
                        // Original (possibly played on since the backup): the swap retries, and Swap() re-prepares if it changed
                    }
                    break;
                case CompState.Restoring:
                {
                    var backupOk = File.Exists(e.BackupPath) && HighScoreReader.Sha256(File.ReadAllBytes(e.BackupPath)) == e.OriginalSha256;
                    if (!backupOk) Transition(e, CompState.NeedsRecovery, "backup missing or changed; nothing overwritten");
                    else if (LiveSha(e.NvPath) == e.OriginalSha256) Transition(e, CompState.Restored, "restore had completed before the interruption");
                    else Transition(e, CompState.PendingRestore, "restore interrupted; will retry");
                    break;
                }
            }
        }
    }

    enum LiveKind { Original, Competition, Unknown }

    /// <summary>
    /// Is the live file the player's own NVRAM or the competition file, possibly with games played on it since?
    /// Exact hashes first; otherwise by content: a competition file's main table holds ladder values, while the
    /// player's file holds their own (initials, score) pairs, which the competition file never contains.
    /// </summary>
    LiveKind Classify(JournalEntry e)
    {
        var (live, _, _) = HighScoreReader.ReadShared(e.NvPath);
        if (live is null) return LiveKind.Unknown;
        var sha = HighScoreReader.Sha256(live);
        if (sha == e.OriginalSha256) return LiveKind.Original;
        if (sha == e.CompetitionSha256) return LiveKind.Competition;

        var map = maps.TryLoad(e.Rom, out _);
        if (map is null || !File.Exists(e.BackupPath)) return LiveKind.Unknown;
        var now = HighScoreReader.Extract(map, live);
        var orig = HighScoreReader.Extract(map, File.ReadAllBytes(e.BackupPath));
        if (!now.Ok || !orig.Ok) return LiveKind.Unknown;

        var ladder = e.Ladder.ToHashSet();
        var personal = orig.Entries.Where(x => x.IsHighScore && x.Score is > 0 && !ladder.Contains(x.Score!.Value))
            .Select(x => (x.RawInitials, x.Score)).ToHashSet();
        var main = now.Entries.Where(x => x.IsHighScore && x.Score is > 0).ToList();
        bool hasLadder = main.Any(x => ladder.Contains(x.Score!.Value));
        bool hasPersonal = main.Any(x => personal.Contains((x.RawInitials, x.Score)));
        return (hasLadder, hasPersonal) switch
        {
            (true, false) => LiveKind.Competition,
            (false, true) => LiveKind.Original,
            _ => LiveKind.Unknown,
        };
    }

    // ---------------- helpers ----------------

    static string? LiveSha(string path)
    {
        var (bytes, _, _) = HighScoreReader.ReadShared(path);
        return bytes is null ? null : HighScoreReader.Sha256(bytes);
    }

    /// <summary>Writes and flushes to disk; atomic=true replaces the target via a temp file + rename.</summary>
    static void WriteDurable(string path, byte[] data, bool atomic)
    {
        var target = atomic ? path + ".tmp" : path;
        using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        if (atomic) File.Move(target, path, overwrite: true);
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}
