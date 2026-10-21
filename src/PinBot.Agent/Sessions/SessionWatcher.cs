using System.Diagnostics;
using System.Text.Json;
using PinBot.Agent.Discovery;
using PinBot.Agent.Scoring;
using PinBot.Contracts;

namespace PinBot.Agent.Sessions;

/// <summary>
/// Observes real VPX processes (VPinball*.exe) independently of any frontend. On exit it waits for the
/// NVRAM folders to settle, finds the .nv files that changed, and reports new high-score entries.
///
/// Footprint: while a table runs PinBot sleeps until VPX exits (no checks, no disk, no network). When idle it looks
/// for VPX every 5 s, keeps one waiting connection to the server, and does any other work
/// (competition files, readiness, inventory, uploads, updates) only when something happened. Reads NVRAM only; the
/// coordinator alone swaps competition files.
/// </summary>
public sealed partial class SessionWatcher(
    MemoryMaps maps, Installations inst, IReadOnlyList<TableInfo> initialTables, LocalState state,
    Nvram.NvramCoordinator? nvram = null, Sync.AgentSync? sync = null, Sync.Outbox? outbox = null, Sync.AutoUpdate? updater = null,
    Discovery.TableScriptCache? scriptCache = null)
{
    /// <summary>The cabinet's tables; replaced as a whole when one is added, changed or played for the first time.</summary>
    volatile IReadOnlyList<TableInfo> _tables = initialTables;

    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan MaxSyncBackoff = TimeSpan.FromMinutes(2); // the server is back within minutes after an update
    DateTime _nextSync = DateTime.MinValue; // MaxValue: nothing to send or fetch until something happens
    int _syncFailures;
    volatile bool _inventorySent;
    volatile bool _liveConnection;          // the waiting connection works: competitions arrive through it
    volatile bool _updatePending = true;    // check for a release once at start, then when the server announces one

    /// <summary>
    /// Inventory up (when it changed), queued results up, competitions down. Runs at start, after a game, and as the
    /// fallback while the waiting connection is down; otherwise not at all. Failures back off up to 2 minutes; gameplay
    /// and NVRAM handling never wait for the server.
    /// </summary>
    async Task MaybeSyncAsync(bool now, CancellationToken ct)
    {
        if (sync is null || (!now && DateTime.UtcNow < _nextSync)) return;
        try
        {
            // results first: a game finished just before a deadline is uploaded as early as possible, and the server
            // accepts a result only for a ROM this cabinet lists, so results go up before a new list can drop that ROM
            var left = await sync.FlushOutboxAsync(ct);
            if (!_inventorySent)
            {
                var tables = _tables;
                await sync.PushInventoryAsync(tables, ct);
                _inventorySent = true;
                Log.Info($"[sync] inventory sent: {tables.Count(t => t.Eligible)} eligible tables");
            }
            ApplyCompetitions(await sync.PullCompetitionsAsync(_tables, ct));
            // the waiting connection normally announces releases; without it (a proxy that cuts long requests) this does
            if (sync.LatestRelease > Sync.AutoUpdate.Version && Sync.AutoUpdate.Enabled && !_updatePending) _updatePending = true;
            if (_syncFailures > 0) Log.Good("[sync] server reachable again");
            _syncFailures = 0;
            _nextSync = left > 0 || !_liveConnection ? DateTime.UtcNow + RetryInterval : DateTime.MaxValue;
            if (left > 0) Log.Warn($"[sync] {left} results still queued");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) return;
            _syncFailures++;
            var backoff = TimeSpan.FromSeconds(Math.Min(MaxSyncBackoff.TotalSeconds, RetryInterval.TotalSeconds * Math.Pow(2, _syncFailures - 1)));
            _nextSync = DateTime.UtcNow + backoff;
            var queued = outbox?.Pending().Count ?? 0;
            Log.Warn($"[sync] server unreachable ({ex.Message}); {queued} results queued; retry in {backoff.TotalMinutes:0.#} min");
        }
    }

    /// <summary>With no table running for a while: install a newer signed release if the server has one.</summary>
    async Task MaybeUpdateAsync(CancellationToken ct)
    {
        if (updater is null || !_updatePending || !Quiet) return;
        _updatePending = false;
        try
        {
            await updater.MaybeUpdateAsync(ct, () => Quiet && !VpxRunning());
            if (updater.Deferred) _updatePending = true; // a table started during the download: after it closes
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            // a server without a published release (or a hiccup) just means no update this time
        }
    }

    /// <summary>Durably queues every new, trusted, attributable entry that counts for a server competition.</summary>
    void QueueResults(Session s, DateTime exited, List<RomOutcome> outcomes, List<string> sessionProblems,
        Dictionary<string, (string Id, bool Counts, string Text)?> verdicts)
    {
        if (outbox is null || sessionProblems.Count > 0) return;
        foreach (var o in outcomes.Where(o => o.Trusted && o.Diff != null))
        {
            if (verdicts.GetValueOrDefault(o.NvPath) is not { Counts: true } v || !long.TryParse(v.Id, out var competitionId)) continue;
            foreach (var e in o.Diff!.NewEntries.Where(e => e.Initials != null && e.Score > 0))
            {
                var ev = new ResultEvent(Guid.NewGuid(), competitionId, s.Id, o.Rom, e.Initials!, e.Score!.Value, exited, MemoryMaps.ParserVersion);
                outbox.Enqueue(ev);
                Log.Info($"[sync] queued {e.Initials} {e.Score:N0} for competition {competitionId} (event {ev.EventId})");
            }
        }
    }

    /// <summary>
    /// After a table closes (and its NVRAM has settled), this long before its competition file is swapped: covers a
    /// frontend relaunching. Other tables' files are handled even while a table runs (see <see cref="MayTouch"/>).
    /// </summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
    DateTime _lastExit = DateTime.MinValue;
    bool Quiet => _active.Count == 0 && DateTime.UtcNow - _lastExit >= QuietPeriod;

    /// <summary>
    /// Whether the competition coordinator may replace this NVRAM file now: no running VPX may be using it. Each
    /// running table uses only its own ROM's file, so other files are free; a VPX whose table or ROM is unknown (or one
    /// not seen yet) keeps every file off limits. Asks Windows afresh, so a table that just started is never missed.
    /// </summary>
    public bool MayTouch(string nvPath)
    {
        var running = Native.VpxProcessIds();
        if (running.Count == 0) return DateTime.UtcNow - _lastExit >= QuietPeriod;
        return FileFree(nvPath, running, _active.Values.Select(s => (s.Pid, s.RomConfirmed ? s.ExpectedRoms : [], LearnedNv(s))).ToList());
    }

    /// <summary>
    /// Whether no running VPX can be using <paramref name="nvPath"/>: every running process is a known session, each
    /// with a known ROM, and none of them uses this file (by ROM name, or the NVRAM file learned for its table).
    /// </summary>
    internal static bool FileFree(string nvPath, IReadOnlyCollection<int> runningPids,
        IReadOnlyCollection<(int Pid, IReadOnlyList<string> Roms, string? LearnedNv)> sessions)
    {
        if (runningPids.Any(pid => sessions.All(s => s.Pid != pid))) return false; // a table PinBot has not looked at yet
        var name = Path.GetFileNameWithoutExtension(nvPath);
        return sessions.All(s => s.Roms.Count > 0 && !s.Roms.Any(r => UsesFile(r, name))
                                 && !string.Equals(s.LearnedNv, nvPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"afm_113b" uses afm_113b.nv, and "afm_113b v2.nv" with an NVRAM offset.</summary>
    internal static bool UsesFile(string rom, string nvName) =>
        nvName.Equals(rom, StringComparison.OrdinalIgnoreCase) || nvName.StartsWith(rom + " v", StringComparison.OrdinalIgnoreCase);

    string? LearnedNv(Session s) => s.TablePath is null ? null
        : _tables.FirstOrDefault(t => t.TablePath.Equals(s.TablePath, StringComparison.OrdinalIgnoreCase))?.NvPath;


    public static bool VpxRunning() => Native.VpxProcessIds().Count > 0;

    /// <summary>
    /// When the competition files next need attention: set by events (competitions arrived, a table closed) and by
    /// the clock only where a competition starts or ends, or the coordinator has work it could not finish.
    /// </summary>
    DateTime _nextTick = DateTime.MinValue;
    volatile bool _tickSoon;
    volatile bool _readinessChanged = true;

    void TickIfDue()
    {
        // while a table runs too: the coordinator asks MayTouch for each file, so only that table's file waits
        if (nvram is null || DateTime.UtcNow - _lastExit < QuietPeriod || (!_tickSoon && DateTime.UtcNow < _nextTick)) return;
        _tickSoon = false;
        var comps = LocalState.Load().Competitions;
        nvram.Tick(comps);
        _readinessChanged = true;
        _nextTick = NextTick(comps, nvram.LoadEntries().Where(e => MayTouch(e.NvPath)), DateTime.UtcNow);
    }

    /// <summary>The next moment the coordinator has something to do on its own: a competition starts or ends, or it retries.</summary>
    internal static DateTime NextTick(IEnumerable<LocalCompetition> comps, IEnumerable<Nvram.JournalEntry> entries, DateTime now)
    {
        var next = comps.SelectMany(c => new[] { c.StartsUtc, c.EffectiveEndUtc }).Where(t => t > now).DefaultIfEmpty(DateTime.MaxValue).Min();
        // work the coordinator could not finish (a locked file, a temp file that did not verify): try again shortly;
        // a file whose table is running is not retried on the clock (that table closing triggers the next pass)
        if (entries.Any(e => e.State is Nvram.CompState.BackedUp or Nvram.CompState.PendingRestore or Nvram.CompState.Preparing or Nvram.CompState.Restoring))
            next = Min(next, now + RetryInterval);
        return next;
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Applies the server's competitions: remembered for offline use, new ones logged and prepared right away.</summary>
    /// <remarks>
    /// Called from the waiting connection's thread and from the main loop. The rejected-ROM filter and the comparison
    /// run on the state loaded under the state lock, so a ROM rejected a moment ago is never written back.
    /// </remarks>
    void ApplyCompetitions(List<LocalCompetition> offered)
    {
        HashSet<string> before = [];
        List<LocalCompetition> applied = [];
        bool changed = false;
        LocalState.Update(s =>
        {
            // never prepared again here (the server stops sending them once the inventory says so; this covers the gap)
            applied = offered.Where(c => !RejectedRom.Applies(s, c.Rom, Sync.AutoUpdate.Version)).ToList();
            before = s.Competitions.Select(c => c.Id).ToHashSet();
            changed = applied.Count != s.Competitions.Count || applied.Any(c => !s.Competitions.Contains(c));
            if (changed) s.Competitions = applied;
            return changed; // nothing new: no disk write
        });
        if (!changed) return;
        foreach (var c in applied.Where(c => !before.Contains(c.Id)))
            Log.Info($"[sync] competition {c.Id}: {c.Table} ({c.Rom}) until {c.EffectiveEndUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        _tickSoon = true;
        _readinessChanged = true;
        Wake();
    }

    /// <summary>Wakes the idle loop early (competitions arrived, a table file appeared). Wakes coalesce into one pass.</summary>
    readonly SemaphoreSlim _wake = new(0, 1);

    void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // a pass is already due
    }

    /// <summary>
    /// Keeps one request open with the server; the server answers as soon as a competition starts or ends (or a
    /// release is published), so a competition started in Discord is on this cabinet within seconds, also while
    /// another table is being played. An idle connection: a few bytes every 25 s. If it cannot be held, the sync falls
    /// back to fetching every minute.
    /// </summary>
    async Task WatchCompetitionsAsync(CancellationToken ct)
    {
        if (sync is null) return;
        long known = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (comps, version, release) = await sync.WaitForCompetitionsAsync(_tables, known, ct);
                _liveConnection = true;
                if (version != known) ApplyCompetitions(comps);
                known = version;
                if (release > Sync.AutoUpdate.Version && Sync.AutoUpdate.Enabled && !_updatePending) { _updatePending = true; Wake(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // anything at all (the server, the network, a busy state file): this loop must outlive it, and while it
                // is down the minute-by-minute sync takes over
                if (ex is not (HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException))
                    Log.Warn($"[sync] waiting connection: {ex.GetType().Name}: {ex.Message}; retrying in 30 s");
                if (_liveConnection) { _liveConnection = false; _nextSync = Min(_nextSync, DateTime.UtcNow + RetryInterval); }
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    Dictionary<long, (string State, string? Detail)> _reported = [];

    /// <summary>Tells the server, when it changes, which competitions this cabinet is ready for (shown on the Discord card).</summary>
    async Task ReportReadinessAsync(CancellationToken ct)
    {
        if (sync is null || nvram is null || !_readinessChanged) return;
        _readinessChanged = false;
        var now = DateTime.UtcNow;
        var entries = nvram.LoadEntries();
        var states = new Dictionary<long, (string State, string? Detail)>();
        foreach (var c in LocalState.Load().Competitions.Where(c => c.IsActive(now)))
        {
            if (!long.TryParse(c.Id, out var id)) continue;
            var e = entries.LastOrDefault(x => x.CompetitionId == c.Id && string.Equals(x.NvPath, c.NvPath, StringComparison.OrdinalIgnoreCase));
            var state = e?.State switch
            {
                Nvram.CompState.Ready => (CompetitionStates.Ready, (string?)null),
                Nvram.CompState.Refused or Nvram.CompState.NeedsRecovery => (CompetitionStates.Refused, e.Problem ?? "competition file could not be prepared"),
                _ => (CompetitionStates.Waiting, MayTouch(c.NvPath) ? "preparing" : "its table is running"),
            };
            // a cabinet with several revisions of the game is ready once one of them is
            if (!states.TryGetValue(id, out var have) || state.Item1 == CompetitionStates.Ready || have.State != CompetitionStates.Ready && state.Item1 == CompetitionStates.Waiting)
                states[id] = state;
        }
        if (states.Count == _reported.Count && states.All(kv => _reported.TryGetValue(kv.Key, out var r) && r == kv.Value)) return;
        try
        {
            await sync.PushStatesAsync(states.Select(kv => new CompetitionState(kv.Key, kv.Value.State, kv.Value.Detail)).ToList(), ct);
            _reported = states;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _readinessChanged = true; } // again next time
    }

    /// <summary>Tells the server a table is running (the cabinet is busy, not offline) or no longer is: one request each.</summary>
    async Task ReportPlayingAsync(bool playing, CancellationToken ct)
    {
        if (sync is null) return;
        try { await sync.SetPlayingAsync(playing, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { } // shown as offline at worst
    }

    /// <summary>Table files that appeared, changed or were played for the first time: re-read when idle.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _tablesToRead = new(StringComparer.OrdinalIgnoreCase);

    public void TableFileChanged(string vpxPath)
    {
        _tablesToRead[vpxPath] = 0;
        Wake();
    }

    /// <summary>Re-reads only the tables that changed; the server hears about it only if the list really changed.</summary>
    void RefreshTables()
    {
        if (_tablesToRead.IsEmpty || !Quiet) return;
        var paths = _tablesToRead.Keys.ToList();
        foreach (var p in paths) _tablesToRead.TryRemove(p, out _);
        var current = LocalState.Load();
        var list = _tables.ToList();
        bool changed = false;
        foreach (var path in paths)
        {
            int i = list.FindIndex(t => t.TablePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(path))
            {
                if (i >= 0) { list.RemoveAt(i); changed = true; }
                continue;
            }
            var info = Inventory.Describe(path, inst, maps, current, scriptCache);
            if (i >= 0 && Sync.AgentSync.Inventory([list[i]], current).SequenceEqual(Sync.AgentSync.Inventory([info], current))) { list[i] = info; continue; }
            if (i >= 0) list[i] = info; else list.Add(info);
            changed = true;
            Log.Info($"[tables] {Path.GetFileNameWithoutExtension(path)}: {(info.Eligible ? $"eligible ({info.Rom})" : info.Problem)}");
        }
        scriptCache?.Save();
        _tables = list;
        if (changed) { _inventorySent = false; _nextSync = DateTime.MinValue; }
    }

    /// <summary>The competition for this NVRAM file that was running when a game started (it had begun, not ended), if any.</summary>
    internal static LocalCompetition? CompetitionAtStart(IEnumerable<LocalCompetition> competitions, string nvPath, DateTime startedUtc) =>
        competitions.FirstOrDefault(c => string.Equals(c.NvPath, nvPath, StringComparison.OrdinalIgnoreCase)
                                         && startedUtc >= c.StartsUtc && startedUtc < c.EffectiveEndUtc);

    /// <summary>Whether this session's new entries count for the competition that owns the NVRAM file, if any.</summary>
    (string Id, bool Counts, string Text)? CompetitionVerdict(Session s, DateTime exited, string nvPath)
    {
        var owner = nvram?.Owner(nvPath);
        if (owner is null || owner.State is not (Nvram.CompState.Ready or Nvram.CompState.PendingRestore))
        {
            // the competition had begun but its file was not in place yet (the game started first): the game ran on
            // the table's own high scores, and a new entry there was still earned during the competition
            var running = s.StartObserved ? CompetitionAtStart(LocalState.Load().Competitions, nvPath, s.StartedUtc) : null;
            if (running is null || nvram is null) return null;
            if (exited >= running.EffectiveEndUtc) return (running.Id, false, $"session exited after the deadline {running.EffectiveEndUtc:yyyy-MM-dd HH:mm}Z");
            return (running.Id, true, $"deadline {running.EffectiveEndUtc:yyyy-MM-dd HH:mm}Z; played on the table's own high scores (the competition file was not in place yet)");
        }
        var comp = LocalState.Load().Competitions.FirstOrDefault(c => c.Id == owner.CompetitionId);
        string id = owner.CompetitionId;
        if (comp is null) return (id, false, "competition no longer exists");
        if (owner.ReadyAtUtc is null || owner.ReadyAtUtc > s.StartedUtc || !s.StartObserved)
            return (id, false, "competition file was not in place when this session started");
        if (exited >= comp.EffectiveEndUtc) return (id, false, $"session exited after the deadline {comp.EffectiveEndUtc:yyyy-MM-dd HH:mm}Z");
        return (id, true, $"deadline {comp.EffectiveEndUtc:yyyy-MM-dd HH:mm}Z");
    }

    internal sealed class Session
    {
        public required string Id;
        public required int Pid;
        public required string Exe;
        public Process? Handle;
        public string? CommandLine;
        public string? TablePath;
        public DateTime StartedUtc;
        public bool StartObserved;
        /// <summary>VPX processes that ran at the same time as this one.</summary>
        public readonly List<Session> Others = [];
        /// <summary>Predecessors this session's baseline was re-read after (their exit-time NVRAM writes are theirs, not ours).</summary>
        public readonly HashSet<Session> RebasedAfter = [];
        public IReadOnlyList<string> ExpectedRoms = [];
        /// <summary>The table's ROM is confirmed (seen in a session, or its script's ROM matches an existing .nv): only then
        /// are other tables' competition files handled while this one runs.</summary>
        public bool RomConfirmed;
        public DateTime? ExitedUtc;
        public required NvramSnapshot Baseline;
        /// <summary>Other tables' NVRAM files PinBot swapped or restored while this one ran: changed, but not by this game.</summary>
        public readonly HashSet<string> ReplacedByPinBot = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly Dictionary<int, Session> _active = [];
    readonly IReadOnlyList<string> _allDirs = inst.GlobalNvramDirs
        .Concat(initialTables.SelectMany(t => Inventory.NvramDirsFor(t.TablePath, inst)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// How often PinBot looks for a starting VPX while none runs. Noticing a start late loses nothing: a game's start
    /// time comes from Windows, PinMAME writes NVRAM only when it exits, and competition files are never touched without
    /// asking Windows afresh (<see cref="MayTouch"/>). A "game" shorter than this cannot post a score anyway.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"watching for VPinball*.exe; NVRAM folders watched: {_allDirs.Count}");
        foreach (var d in _allDirs) Log.Info($"  nvram: {d}");
        if (_allDirs.Count == 0) Log.Warn("no NVRAM folder known: sessions will be observed but no scores can be read");

        if (nvram != null)
        {
            // the coordinator runs on this loop's thread, like everything that reads _active
            nvram.Replaced = path => { foreach (var s in _active.Values) s.ReplacedByPinBot.Add(path); };
            nvram.Recover();
            Log.Info($"competition NVRAM: journal at {nvram.Root}; a table's file changes only while that table is closed");
        }

        Poll(startup: true);
        if (_active.Count > 0) await ReportPlayingAsync(true, ct);
        using var tableFiles = WatchTableFolders();
        var watching = Task.Run(() => WatchCompetitionsAsync(ct), CancellationToken.None);
        bool trimmed = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_active.Count > 0 && _active.Values.All(s => s.Handle != null))
                    await WaitForExitOrWakeAsync(ct); // a table runs: asleep until it exits or a competition arrives
                else
                    await _wake.WaitAsync(PollInterval, ct);

                bool wasPlaying = _active.Count > 0;
                var ended = Poll(startup: false);
                if (!wasPlaying && _active.Count > 0) await ReportPlayingAsync(true, ct);
                foreach (var s in ended)
                    await ReportAsync(s, ct);
                if (ended.Count > 0)
                {
                    _lastExit = DateTime.UtcNow; // its NVRAM has settled: the quiet period counts from here
                    _tickSoon = true;
                    _readinessChanged = true;
                    trimmed = false;
                }

                // event-driven, also while a table runs: competition files of other tables, and the card's readiness
                TickIfDue();
                await ReportReadinessAsync(ct);
                if (_active.Count > 0) continue;

                if (ended.Count > 0)
                {
                    await ReportPlayingAsync(false, ct);
                    await MaybeSyncAsync(now: true, ct); // upload the game's results at once
                }
                // idle: each step checks in-memory flags first and touches disk or network only when due
                RefreshTables();
                await MaybeSyncAsync(now: false, ct);
                await MaybeUpdateAsync(ct);
                if (!trimmed && Quiet) { Native.TrimMemory(); trimmed = true; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // one bad pass must never stop monitoring
                Log.Error($"watch loop: {ex}");
            }
        }
        await watching;
    }

    /// <summary>
    /// Returns when a running table exits or something needs attention (Windows signals the exit; nothing is checked
    /// meanwhile). The waits that did not fire are cancelled, so none of them consumes a later signal.
    /// </summary>
    async Task WaitForExitOrWakeAsync(CancellationToken ct)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var waits = _active.Values.Select(s => s.Handle!.WaitForExitAsync(done.Token)).Append(_wake.WaitAsync(done.Token)).ToList();
            await await Task.WhenAny(waits);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Task.Delay(PollInterval, ct); // a handle cannot be waited on: fall back to looking
        }
        finally { done.Cancel(); }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// New or changed .vpx files in the table folders: Windows reports them (no scanning); they are read when idle.
    /// </summary>
    IDisposable WatchTableFolders()
    {
        var watchers = new List<FileSystemWatcher>();
        foreach (var dir in inst.TableDirs.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(dir, "*.vpx")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 8192,
                };
                w.Created += (_, e) => TableFileChanged(e.FullPath);
                w.Changed += (_, e) => TableFileChanged(e.FullPath);
                w.Deleted += (_, e) => TableFileChanged(e.FullPath);
                w.Renamed += (_, e) => { TableFileChanged(e.OldFullPath); TableFileChanged(e.FullPath); };
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Log.Warn($"new tables in {dir} are noticed only when PinBot restarts: {ex.Message}");
            }
        }
        return new Disposables(watchers);
    }

    sealed class Disposables(IEnumerable<IDisposable> items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) i.Dispose(); }
    }

    /// <summary>
    /// Finds VPX processes that started and ones that exited. After a table ran, a VPX found now may have started
    /// while PinBot slept: its real start time (from Windows) is what the overlap and relaunch rules judge.
    /// </summary>
    List<Session> Poll(bool startup)
    {
        var running = Native.VpxProcessIds().ToHashSet();
        foreach (var pid in running.Where(pid => !_active.ContainsKey(pid)))
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { continue; } // already gone
            var s = Begin(p, startObserved: !startup);
            foreach (var other in _active.Values)
            {
                other.Others.Add(s);
                s.Others.Add(other);
            }
            _active[pid] = s;
        }


        var ended = _active.Values.Where(s => !running.Contains(s.Pid) || (s.Handle?.HasExited ?? false)).ToList();
        foreach (var s in ended) _active.Remove(s.Pid);

        return ended;
    }

    Session Begin(Process p, bool startObserved)
    {
        int pid = p.Id;
        string exe = p.ProcessName + ".exe";
        Process? handle = null;
        DateTime started = DateTime.UtcNow;
        try { started = p.StartTime.ToUniversalTime(); handle = p; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            p.Dispose(); // e.g. VPX elevated while the agent is not: exit time falls back to detection time
        }
        var cmd = QueryCommandLine(pid);
        var table = ResolveTable(TableFromCommandLine(cmd), inst.VpxDirs.Concat(inst.TableDirs), _tables.Select(t => t.TablePath));
        var info = table is null ? null : _tables.FirstOrDefault(t => t.TablePath.Equals(table, StringComparison.OrdinalIgnoreCase))
                                          ?? Inventory.Describe(table, inst, maps, state);
        var expected = info is null ? [] : info.ScriptRoms.Concat(info.Rom is null ? [] : [info.Rom])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dirs = table is null ? _allDirs : _allDirs.Union(Inventory.NvramDirsFor(table, inst), StringComparer.OrdinalIgnoreCase).ToList();

        var s = new Session
        {
            Id = "s-" + Guid.NewGuid().ToString("N")[..8],
            Pid = pid,
            Exe = exe,
            Handle = handle,
            CommandLine = cmd,
            TablePath = table,
            StartedUtc = started,
            StartObserved = startObserved,
            ExpectedRoms = expected,
            RomConfirmed = info is { Rom: not null, NvState: NvState.Resolved, RomSource: RomSource.Observed or RomSource.Script },
            Baseline = NvramSnapshot.Capture(dirs, expected, maps),
        };
        Log.Info($"[{s.Id}] VPX start  pid={s.Pid} exe={s.Exe} table={table ?? "(unknown)"} rom={(expected.Count > 0 ? string.Join("/", expected) : "(unknown)")}"
                 + $" baseline_files={s.Baseline.Bytes.Count}{(startObserved ? "" : " ALREADY RUNNING AT AGENT START")}");
        if (cmd is null) Log.Warn($"[{s.Id}] command line unavailable (access denied?) - table identity unknown");
        else if (table is null) Log.Info($"[{s.Id}] no .vpx on the command line (table unknown; scores still read from NVRAM): {cmd}");
        return s;
    }

    async Task ReportAsync(Session s, CancellationToken ct)
    {
        DateTime exited = DateTime.UtcNow;
        try { if (s.Handle is { HasExited: true }) exited = s.Handle.ExitTime.ToUniversalTime(); }
        catch (InvalidOperationException) { }
        s.Handle?.Dispose();

        Log.Info($"[{s.Id}] VPX exit   pid={s.Pid} exited_utc={exited:O} - waiting for NVRAM to settle");
        var (stable, after) = await NvramSnapshot.WaitForStableAsync(s.Baseline.Dirs, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), ct);
        var changed = ChangedByGame(s.Baseline.ChangedFiles(after), s.ReplacedByPinBot);

        var outcomes = new List<RomOutcome>();
        foreach (var path in changed)
        {
            var o = s.Baseline.Analyze(path, maps);
            for (int i = 0; i < 5 && o.After?.Status == ExtractStatus.FileLocked; i++)
            {
                await Task.Delay(1000, ct);
                o = s.Baseline.Analyze(path, maps);
            }
            outcomes.Add(o);
        }

        var sessionProblems = new List<string>();
        if (!s.StartObserved) sessionProblems.Add("session began before the agent started: baseline not trustworthy");
        if (OverlapProblem(s, exited) is { } overlap) sessionProblems.Add(overlap);
        if (!stable) sessionProblems.Add("NVRAM folders kept changing for 30 s after exit");

        var verdicts = outcomes.ToDictionary(o => o.NvPath, o => CompetitionVerdict(s, exited, o.NvPath), StringComparer.OrdinalIgnoreCase);
        foreach (var o in outcomes.Where(o => o.Diff?.SuspectedReset == true)) DetectRejection(s, o);
        LearnAssociation(s, changed);
        if (s.TablePath != null) _tablesToRead[s.TablePath] = 0; // a first play can make a table eligible
        Print(s, exited, changed, outcomes, sessionProblems, verdicts);
        QueueResults(s, exited, outcomes, sessionProblems, verdicts);
        AppendJournal(s, exited, outcomes, sessionProblems, verdicts);
        s.ExitedUtc = exited;

        // a table relaunched as this one closed: what this session just saved is not the newcomer's doing, so the
        // newcomer starts counting from here (PinMAME writes NVRAM when it exits, so it has written nothing yet)
        foreach (var o in s.Others.Where(o => IsHandoff(s, exited, o) && _active.ContainsKey(o.Pid)))
        {
            o.Baseline = NvramSnapshot.Capture(o.Baseline.Dirs, o.ExpectedRoms, maps);
            o.RebasedAfter.Add(s);
            Log.Info($"[{o.Id}] started while {s.Id} was closing: its starting point was re-read after {s.Id}'s scores were saved");
        }
    }

    /// <summary>
    /// A ROM that wiped its high-score table during a game on a competition file rejected that file (its checks found
    /// something the competition image got wrong). The ROM is taken off this cabinet's competition list until a
    /// release fixes it: the competition is dropped here (so the player's own file is restored once the table is
    /// closed), the server is told at once, and the three files needed to find the fix are kept.
    /// </summary>
    void DetectRejection(Session s, RomOutcome o)
    {
        var owner = nvram?.Owner(o.NvPath);
        if (owner is null || !PlayedOnCompetitionFile(owner, s.StartObserved, s.StartedUtc)) return;
        if (RejectedRom.Applies(LocalState.Load(), o.Rom, Sync.AutoUpdate.Version)) return;

        var now = DateTime.UtcNow;
        var dir = Path.Combine(LocalState.Dir, "diagnostics", $"{o.Rom}-{now:yyyyMMddTHHmmss}");
        try
        {
            Directory.CreateDirectory(dir);
            if (s.Baseline.Bytes.TryGetValue(o.NvPath, out var before)) File.WriteAllBytes(Path.Combine(dir, "competition-file.nv"), before);
            if (Scoring.HighScoreReader.ReadShared(o.NvPath).Bytes is { } after) File.WriteAllBytes(Path.Combine(dir, "after-game.nv"), after);
            if (File.Exists(owner.BackupPath)) File.Copy(owner.BackupPath, Path.Combine(dir, "original.nv"), overwrite: true);
        }
        catch (IOException ex) { Log.Warn($"[{s.Id}] some diagnostic files could not be saved: {ex.Message}"); }

        LocalState.Update(st =>
        {
            st.RejectedRoms[o.Rom] = new RejectedRom(now, Sync.AutoUpdate.Version, owner.CompetitionId, dir);
            st.Competitions.RemoveAll(c => string.Equals(c.Rom, o.Rom, StringComparison.OrdinalIgnoreCase)); // restore even offline
        });
        _tickSoon = true;
        Log.Error($"[{s.Id}] {o.Rom} reset its high scores on the competition file: it is no longer offered for competitions on this " +
                  $"VPin, and your own high scores are put back once no table is running. Please send the folder {dir} " +
                  "to the PinBot maintainer so a fix can be made.");
        _inventorySent = false;      // the server learns the ROM is not eligible here
        _nextSync = DateTime.MinValue; // at once
    }

    /// <summary>Whether the ROM loaded the competition file: it was in place before the game started (seen starting).</summary>
    internal static bool PlayedOnCompetitionFile(Nvram.JournalEntry owner, bool startObserved, DateTime startedUtc) =>
        owner.State is Nvram.CompState.Ready or Nvram.CompState.PendingRestore
        && startObserved && owner.ReadyAtUtc is { } ready && ready <= startedUtc;

    /// <summary>A VPX started this close to another one's exit is a relaunch, not a second game running alongside.</summary>
    public static readonly TimeSpan HandoffWindow = TimeSpan.FromSeconds(15);

    internal static bool IsHandoff(Session earlier, DateTime earlierExit, Session later) =>
        later.StartedUtc >= earlier.StartedUtc && earlierExit - later.StartedUtc <= HandoffWindow;

    /// <summary>
    /// Why changes cannot be attributed to this session because of other VPX processes, or null. Two games running side
    /// by side are untrustworthy; a handoff (one starting in the last seconds of the other) is not, on either side: the
    /// earlier one saved its scores at exit, and the later one's baseline was re-read after that.
    /// </summary>
    internal static string? OverlapProblem(Session s, DateTime exited)
    {
        foreach (var o in s.Others)
        {
            var fine = o.StartedUtc >= s.StartedUtc
                ? exited - o.StartedUtc <= HandoffWindow   // o is a relaunch that began as we were closing
                : s.RebasedAfter.Contains(o);              // we are that relaunch, and counted only from after o's exit
            if (!fine) return "another VPX process ran at the same time: changes cannot be attributed";
        }
        return null;
    }

    /// <summary>The changed NVRAM files a game wrote: files PinBot itself replaced during the session are not the game's.</summary>
    internal static List<string> ChangedByGame(IReadOnlyList<string> changed, IReadOnlySet<string> replacedByPinBot)
    {
        var mine = changed.Where(p => !replacedByPinBot.Contains(p)).ToList();
        if (mine.Count < changed.Count)
            Log.Info($"    ignored (competition file swapped or restored by PinBot meanwhile): {string.Join(", ", changed.Except(mine).Select(Path.GetFileName))}");
        return mine;
    }

    /// <summary>A table launch that wrote exactly one .nv file confirms the table's actual ROM and NVRAM path.</summary>
    void LearnAssociation(Session s, IReadOnlyList<string> changed)
    {
        if (s.TablePath is null || s.Others.Count > 0 || !s.StartObserved) return;
        if (changed.Count != 1)
        {
            if (changed.Count > 1) Log.Warn($"[{s.Id}] {changed.Count} NVRAM files changed; association not learned");
            return;
        }
        var rom = HighScoreReader.RomFromFileName(changed[0]);
        var prev = state.Observed.GetValueOrDefault(s.TablePath);
        if (prev is not null && prev.Rom == rom && prev.NvPath.Equals(changed[0], StringComparison.OrdinalIgnoreCase)) return;
        var assoc = new ObservedAssociation(rom, changed[0], DateTime.UtcNow);
        state.Observed[s.TablePath] = assoc;
        LocalState.Update(st => st.Observed[s.TablePath!] = assoc);
        var script = _tables.FirstOrDefault(t => t.TablePath.Equals(s.TablePath, StringComparison.OrdinalIgnoreCase))?.ScriptRoms ?? [];
        var note = script.Count == 1 && !script[0].Equals(rom, StringComparison.OrdinalIgnoreCase) ? $" (script said {script[0]})" : "";
        Log.Good($"[{s.Id}] learned: {Path.GetFileName(s.TablePath)} -> {rom} @ {changed[0]}{note}");
    }
}
