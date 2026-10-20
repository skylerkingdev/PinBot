using PinBot.Agent;
using PinBot.Agent.Discovery;
using PinBot.Agent.Scoring;
using PinBot.Agent.Sessions;

const string Usage = """
    PinBot cabinet agent - automatic pinball competitions for your Discord server

      pinbot                                     set up: paste the line from /pinbot pair, then PinBot installs itself,
                                                 starts with Windows (minimized) and runs. Run again to see its status.
      pinbot status                              pairing, whether it runs and starts with Windows, competitions
      pinbot uninstall                           restore any competition NVRAM, unpair, stop starting with Windows
      pinbot report                              put logs and state in one zip on the desktop, to send when asked

      pinbot scan  [--tables DIR] [--vpx DIR]    discover VPX, tables, ROMs, NVRAM; report eligibility
      pinbot watch [--tables DIR] [--vpx DIR]    observe VPX sessions, report new high scores, and prepare/restore
                                                 competition NVRAM while no table is running (Ctrl+C stops)
      pinbot read  FILE.nv [--rom ROM]           decode one NVRAM file
      pinbot diff  BEFORE.nv AFTER.nv [--rom ROM] compare two NVRAM files of the same ROM

      pinbot pair LINE | URL CODE [--name NAME]           pair with a PinBot server (competitions come from it)

    --tables / --vpx are remembered (only needed when discovery fails).
    --maps DIR overrides the built-in memory maps (Pinball Memory Maps, ODbL 1.0 / DbCL 1.0; see 'pinbot notice').
    Program, logs, state and NVRAM backups: %LOCALAPPDATA%\PinBot (override with PINBOT_HOME).
    NVRAM is only changed for a competition: original backed up and verified first, restored byte-for-byte after.
    """;

if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

Log.Init();
Console.Title = "PinBot";
var cmd = args.Length == 0 ? "setup" : args[0].ToLowerInvariant();
var positional = new List<string>();
var opts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
for (int i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length)
    {
        var key = args[i][2..];
        if (!opts.TryGetValue(key, out var list)) opts[key] = list = [];
        list.Add(args[++i]);
        continue;
    }
    positional.Add(args[i]);
}
string? Opt(string name) => opts.TryGetValue(name, out var v) ? v[^1] : null;

MemoryMaps maps;
try
{
    maps = Opt("maps") is { } mapsDir ? new MemoryMaps(mapsDir) : MemoryMaps.Default();
}
catch (FileNotFoundException ex)
{
    Log.Error(ex.Message);
    return 2;
}
var commands = new Commands(maps, opts);

try
{
    switch (cmd)
    {
        case "read" when positional.Count == 1:
            return commands.Read(positional[0], Opt("rom"));
        case "diff" when positional.Count == 2:
            return commands.Diff(positional[0], positional[1], Opt("rom"));
        case "scan":
        {
            var (inst, tables, _) = commands.Discover();
            commands.PrintInventory(inst, tables);
            return 0;
        }
        case "setup":
            return await commands.SetUp();
        case "update":
            return commands.InstallUpdate();
        case "status":
            return commands.Status();
        case "report":
            return commands.Report();
        case "uninstall":
            return commands.Uninstall();
        case "notice":
        {
            using var notice = typeof(MemoryMaps).Assembly.GetManifestResourceStream("NOTICE.md")!;
            Console.WriteLine(new StreamReader(notice).ReadToEnd());
            return 0;
        }
        case "watch":
        {
            if (args.Contains("--minimized")) Setup.MinimizeConsole();
            Native.RunAsBackground(); // below-normal priority, efficiency mode: never competes with VPX
            // after a crash or an update, the previous watcher may still be exiting: wait for it rather than give up
            using var watcher = Setup.ClaimWatcher(args.Contains("--after-crash") ? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Error($"unexpected failure: {e.ExceptionObject}"); Setup.RestartAfterCrash(); };
            if (watcher is null)
            {
                Log.Error("PinBot is already running on this PC (see 'pinbot status').");
                return 1;
            }
            var (inst, tables, state) = commands.Discover();
            commands.PrintInventory(inst, tables);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            using var stopRequests = Setup.ListenForStop(cts);
            PinBot.Agent.Sync.AgentSync? sync = null;
            PinBot.Agent.Sync.AutoUpdate? updater = null;
            var outbox = new PinBot.Agent.Sync.Outbox(Setup.OutboxDir);
            if (state.Server is { } link)
            {
                sync = new PinBot.Agent.Sync.AgentSync(link.CreateClient(), outbox);
                Log.Info($"paired with {link.Url} as {link.CabinetId}: competitions come from the server; results are uploaded");
                if (PinBot.Agent.Sync.AutoUpdate.Enabled)
                {
                    var updates = link.CreateClient();
                    updates.Timeout = TimeSpan.FromMinutes(5); // the exe is ~35 MB
                    updater = new PinBot.Agent.Sync.AutoUpdate(updates);
                }
            }
            else Log.Info("not paired with a server: high scores are read, competitions start once paired (run pinbot)");
            Log.Info(PinBot.Agent.Sync.AutoUpdate.Enabled
                ? $"release {PinBot.Agent.Sync.AutoUpdate.Version}; installs newer signed releases from the server by itself"
                : "development build: does not update itself");
            // competition files are prepared even while a table runs, except the files that table may be using
            SessionWatcher? running = null;
            var coordinator = new PinBot.Agent.Nvram.NvramCoordinator(Path.Combine(LocalState.Dir, "nvram"), maps,
                nv => running?.MayTouch(nv) ?? !SessionWatcher.VpxRunning(), () => DateTime.UtcNow);
            running = new SessionWatcher(maps, inst, tables, state, coordinator, sync, sync is null ? null : outbox, updater, PinBot.Agent.Discovery.TableScriptCache.Default);
            await running.RunAsync(cts.Token);
            Log.Info("stopped");
            return 0;
        }
        case "pair" when positional is [var line] && Setup.ParsePairingLine(line) is { } p:
            return await Setup.PairAsync(p.Url, p.Code, Opt("name") ?? Environment.MachineName) ? 0 : 1;
        case "pair" when positional is [var url, var code]:
            return await Setup.PairAsync(url, code, Opt("name") ?? Environment.MachineName) ? 0 : 1;
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception ex)
{
    Log.Error($"unexpected failure: {ex}");
    if (cmd == "watch") Setup.RestartAfterCrash(); // scores keep being read: a fresh watcher takes over
    return 3;
}
