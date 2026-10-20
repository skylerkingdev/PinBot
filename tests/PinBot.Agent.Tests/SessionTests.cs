using PinBot.Agent.Sessions;

namespace PinBot.Agent.Tests;

/// <summary>Exit-time analysis against a temp NVRAM folder, simulating what PinMAME writes during a session.</summary>
public class SessionTests
{
    static readonly IReadOnlyList<string> Afm = ["afm_113b"];

    static (string Dir, string Path) NvramFolderWithAfm()
    {
        var dir = TestData.TempDir();
        var path = System.IO.Path.Combine(dir, "afm_113b.nv");
        File.Copy(System.IO.Path.Combine(TestData.FixtureDir, "afm_113b.nv"), path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        return (dir, path);
    }

    static void RomWritesScore(string path, int slot, string initials, long score)
    {
        var nv = File.ReadAllBytes(path);
        TestData.SetEntry(nv, TestData.Map("afm_113b"), slot, initials, score);
        File.WriteAllBytes(path, nv);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    [Fact]
    public void Session_with_new_grand_champion_is_trusted_and_reported()
    {
        var (dir, path) = NvramFolderWithAfm();
        var snap = NvramSnapshot.Capture([dir], Afm, TestData.Maps);
        RomWritesScore(path, 0, "SKY", 9_000_000_000);

        var changed = snap.ChangedFiles(NvramSnapshot.Stamp([dir]));
        Assert.Equal([path], changed);
        var o = snap.Analyze(path, TestData.Maps);
        Assert.True(o.Trusted, string.Join("; ", o.Problems));
        var e = Assert.Single(o.Diff!.NewEntries);
        Assert.Equal(("SKY", 9_000_000_000L), (e.Initials, e.Score!.Value));
    }

    [Fact]
    public void Repeated_sessions_each_report_only_their_own_scores()
    {
        var (dir, path) = NvramFolderWithAfm();
        var first = NvramSnapshot.Capture([dir], Afm, TestData.Maps);
        RomWritesScore(path, 0, "SKY", 9_000_000_000);
        Assert.Single(first.Analyze(path, TestData.Maps).Diff!.NewEntries);

        var second = NvramSnapshot.Capture([dir], Afm, TestData.Maps);
        Assert.Empty(second.ChangedFiles(NvramSnapshot.Stamp([dir])));
        RomWritesScore(path, 0, "AVK", 9_500_000_000);
        var o = second.Analyze(path, TestData.Maps);
        Assert.Equal(["AVK"], o.Diff!.NewEntries.Select(x => x.Initials));
    }

    [Fact]
    public void Session_without_a_finished_game_changes_nothing()
    {
        var (dir, _) = NvramFolderWithAfm();
        var snap = NvramSnapshot.Capture([dir], Afm, TestData.Maps);
        Assert.Empty(snap.ChangedFiles(NvramSnapshot.Stamp([dir])));
    }

    [Fact]
    public void Nvram_created_during_session_contains_defaults_and_is_not_trusted()
    {
        var dir = TestData.TempDir();
        var snap = NvramSnapshot.Capture([dir], Afm, TestData.Maps);
        var path = Path.Combine(dir, "afm_113b.nv");
        File.Copy(Path.Combine(TestData.FixtureDir, "afm_113b.nv"), path);

        Assert.Equal([path], snap.ChangedFiles(NvramSnapshot.Stamp([dir])));
        var o = snap.Analyze(path, TestData.Maps);
        Assert.False(o.Trusted);
        Assert.Contains(o.Problems, p => p.Contains("created during this session"));
    }

    [Fact]
    public void Unexpected_rom_without_baseline_is_not_trusted()
    {
        var (dir, path) = NvramFolderWithAfm();
        var snap = NvramSnapshot.Capture([dir], ["tz_92"], TestData.Maps); // script said tz_92, ROM actually afm_113b
        RomWritesScore(path, 0, "SKY", 9_000_000_000);
        var o = snap.Analyze(path, TestData.Maps);
        Assert.False(o.Trusted);
        Assert.Contains(o.Problems, p => p.Contains("no baseline"));
    }

    [Fact]
    public void Unknown_table_baselines_every_supported_rom()
    {
        var (dir, path) = NvramFolderWithAfm();
        File.WriteAllBytes(Path.Combine(dir, "notarom_l1.nv"), new byte[64]);
        var snap = NvramSnapshot.Capture([dir], [], TestData.Maps);
        Assert.Equal([path], snap.Bytes.Keys);
    }

    [Fact]
    public void Unsupported_rom_change_is_explicit()
    {
        var dir = TestData.TempDir();
        var path = Path.Combine(dir, "notarom_l1.nv");
        File.WriteAllBytes(path, new byte[64]);
        var snap = NvramSnapshot.Capture([dir], [], TestData.Maps);
        File.WriteAllBytes(path, new byte[65]);
        var o = snap.Analyze(path, TestData.Maps);
        Assert.Contains(o.Problems, p => p.StartsWith("unsupported ROM"));
    }

    [Fact]
    public async Task Quiet_folder_is_stable_quickly()
    {
        var (dir, _) = NvramFolderWithAfm();
        var (stable, stamps) = await NvramSnapshot.WaitForStableAsync([dir], TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5), default);
        Assert.True(stable);
        Assert.Single(stamps);
    }

    [Theory]
    [InlineData("\"C:\\VP\\VPinballX64.exe\" -minimized -play \"C:\\VP\\Tables\\Attack From Mars.vpx\"", "C:\\VP\\Tables\\Attack From Mars.vpx")]
    [InlineData("VPinballX.exe -Play C:\\T\\TZ.VPX -Primary", "C:\\T\\TZ.VPX")]
    [InlineData("\"C:\\VP\\VPinballX.exe\"", null)]
    [InlineData(null, null)]
    public void Table_is_read_from_the_vpx_command_line(string? cmd, string? table) =>
        Assert.Equal(table, SessionWatcher.TableFromCommandLine(cmd));
}

public class OverlapTests
{
    static readonly DateTime T0 = new(2026, 10, 12, 23, 0, 0, DateTimeKind.Utc);

    static SessionWatcher.Session At(DateTime started) => new()
    {
        Id = "s-" + started.Ticks, Pid = 1, Exe = "VPinballX64.exe", StartedUtc = started, StartObserved = true,
        Baseline = Sessions.NvramSnapshot.Capture([], [], TestData.Maps),
    };

    static (SessionWatcher.Session First, SessionWatcher.Session Second) Overlapping(TimeSpan secondStartsAfter)
    {
        var (a, b) = (At(T0), At(T0 + secondStartsAfter));
        a.Others.Add(b);
        b.Others.Add(a);
        return (a, b);
    }

    [Fact]
    public void A_relaunch_as_the_table_closes_does_not_void_either_game()
    {
        // the cabinet case: Jokerz started again one second before the previous game finished exiting
        var (first, second) = Overlapping(TimeSpan.FromMinutes(10));
        var firstExit = second.StartedUtc + TimeSpan.FromSeconds(1);
        Assert.Null(SessionWatcher.OverlapProblem(first, firstExit));
        Assert.True(SessionWatcher.IsHandoff(first, firstExit, second));

        // the relaunch counts only once its starting point was re-read after the first game's exit
        Assert.NotNull(SessionWatcher.OverlapProblem(second, firstExit + TimeSpan.FromMinutes(2)));
        second.RebasedAfter.Add(first);
        Assert.Null(SessionWatcher.OverlapProblem(second, firstExit + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Two_games_running_side_by_side_count_for_neither()
    {
        var (first, second) = Overlapping(TimeSpan.FromMinutes(5));
        var firstExit = second.StartedUtc + TimeSpan.FromMinutes(3); // both ran together for three minutes
        Assert.False(SessionWatcher.IsHandoff(first, firstExit, second));
        Assert.NotNull(SessionWatcher.OverlapProblem(first, firstExit));
        Assert.NotNull(SessionWatcher.OverlapProblem(second, firstExit + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void The_handoff_window_is_tight()
    {
        var (first, second) = Overlapping(TimeSpan.FromMinutes(10));
        Assert.Null(SessionWatcher.OverlapProblem(first, second.StartedUtc + SessionWatcher.HandoffWindow));
        Assert.NotNull(SessionWatcher.OverlapProblem(first, second.StartedUtc + SessionWatcher.HandoffWindow + TimeSpan.FromSeconds(1)));
    }
}

public class EarlyGameTests
{
    static readonly DateTime Start = new(2026, 10, 13, 16, 22, 0, DateTimeKind.Utc);
    static readonly LocalCompetition Genesis = new("15", "genesis", @"C:\nv\genesis.nv", "Genesis", Start, Start.AddHours(1));

    /// <summary>
    /// The cabinet case: Genesis was started 45 s after the competition began, before its file was swapped in. The game
    /// ran on the table's own high scores during the competition, so a new entry there counts.
    /// </summary>
    [Fact]
    public void A_game_started_during_the_competition_counts_even_before_its_file_is_in_place()
    {
        Assert.Same(Genesis, SessionWatcher.CompetitionAtStart([Genesis], @"c:\NV\Genesis.nv", Start.AddSeconds(45)));
        Assert.Null(SessionWatcher.CompetitionAtStart([Genesis], @"C:\nv\genesis.nv", Start.AddSeconds(-1)));  // began before it
        Assert.Null(SessionWatcher.CompetitionAtStart([Genesis], @"C:\nv\genesis.nv", Start.AddHours(1)));     // after the deadline
        Assert.Null(SessionWatcher.CompetitionAtStart([Genesis], @"C:\nv\afm_113b.nv", Start.AddMinutes(5)));  // another table
    }
}
