using PinBot.Agent.Discovery;
using PinBot.Agent.Nvram;
using PinBot.Agent.Sessions;

namespace PinBot.Agent.Tests;

public class FootprintTests
{
    static readonly IReadOnlyList<string> Afm = ["afm_113b"];

    /// <summary>
    /// A friend started a competition table 9 s after closing another one that had run for 70 minutes, and played on
    /// his own scores: competition files are now prepared while another table runs, sparing only that table's file.
    /// </summary>
    [Fact]
    public void A_running_table_keeps_only_its_own_nvram_file_off_limits()
    {
        var playing = new[] { (Pid: 10, Roms: Afm, LearnedNv: (string?)@"C:\nv\afm_113b.nv") };
        Assert.True(SessionWatcher.FileFree(@"C:\nv\swrds_l2.nv", [10], playing));
        Assert.False(SessionWatcher.FileFree(@"C:\nv\afm_113b.nv", [10], playing));
        Assert.False(SessionWatcher.FileFree(@"C:\nv\afm_113b v2.nv", [10], playing)); // NVRAM offset variant

        // a VPX not seen yet, or one whose table/ROM is unknown, keeps every file off limits
        Assert.False(SessionWatcher.FileFree(@"C:\nv\swrds_l2.nv", [10, 11], playing));
        Assert.False(SessionWatcher.FileFree(@"C:\nv\swrds_l2.nv", [12], [(12, [], null)]));

        // the NVRAM file learned for the running table counts even when its name differs from the ROM
        Assert.False(SessionWatcher.FileFree(@"C:\nv\afm_custom.nv", [10], [(10, Afm, @"C:\nv\afm_custom.nv")]));
    }

    [Fact]
    public void Competition_files_need_attention_only_at_a_start_an_end_or_a_retry()
    {
        var now = new DateTime(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);
        var comp = new LocalCompetition("24", "swrds_l2", @"C:\nv\swrds_l2.nv", "Swords of Fury", now.AddMinutes(-5), now.AddHours(1));
        Assert.Equal(now.AddHours(1), SessionWatcher.NextTick([comp], [], now));                       // its end
        Assert.Equal(DateTime.MaxValue, SessionWatcher.NextTick([], [], now));                          // nothing to do
        var stuck = new JournalEntry { State = CompState.PendingRestore };
        Assert.Equal(now.AddMinutes(1), SessionWatcher.NextTick([comp], [stuck], now));                 // retry soon
        Assert.Equal(now.AddMinutes(1), SessionWatcher.NextTick([], [new JournalEntry { State = CompState.BackedUp }], now));
        Assert.Equal(DateTime.MaxValue, SessionWatcher.NextTick([], [new JournalEntry { State = CompState.Ready }], now));
    }

    /// <summary>The coordinator prepares a competition whose file is free while another file is in use.</summary>
    [Fact]
    public void A_competition_is_prepared_while_another_table_is_played()
    {
        var nvDir = TestData.TempDir();
        var now = new DateTime(2026, 10, 14, 1, 37, 0, DateTimeKind.Utc);
        var afm = Path.Combine(nvDir, "afm_113b.nv");
        File.WriteAllBytes(afm, TestData.Nv("afm_113b"));
        var playing = Path.Combine(nvDir, "tz_94h.nv"); // Twilight Zone is being played
        var comp = new LocalCompetition("c1", "afm_113b", afm, "AFM", now, now.AddHours(1));

        var whilePlaying = new NvramCoordinator(TestData.TempDir(), TestData.Maps, nv => !nv.Equals(playing, StringComparison.OrdinalIgnoreCase), () => now);
        whilePlaying.Tick([comp]);
        Assert.Equal(CompState.Ready, whilePlaying.Owner(afm)!.State);

        var ownTable = new NvramCoordinator(TestData.TempDir(), TestData.Maps, nv => !nv.Equals(afm, StringComparison.OrdinalIgnoreCase), () => now);
        File.WriteAllBytes(afm, TestData.Nv("afm_113b"));
        ownTable.Tick([comp]); // Attack from Mars itself is being played: its file waits
        Assert.Null(ownTable.Owner(afm));
        Assert.Equal(TestData.Nv("afm_113b"), File.ReadAllBytes(afm));
    }

    [Fact]
    public void Table_scripts_are_read_once_and_again_only_when_the_file_changes()
    {
        var dir = Directory.CreateTempSubdirectory("pinbot-cache-").FullName;
        try
        {
            var vpx = Path.Combine(dir, "t.vpx");
            File.WriteAllText(vpx, "x");
            var cachePath = Path.Combine(dir, "cache.json");
            int reads = 0;
            ScriptFacts Read() { reads++; return new(["afm_113b"], 0, "test"); }

            var cache = new TableScriptCache(cachePath);
            Assert.Equal(["afm_113b"], cache.Get(vpx, Read).Roms);
            cache.Get(vpx, Read);
            Assert.Equal(1, reads);
            cache.Save();

            var reloaded = new TableScriptCache(cachePath); // the next PinBot start reads no table
            Assert.Equal("test", reloaded.Get(vpx, Read).Source);
            Assert.Equal(1, reads);

            File.WriteAllText(vpx, "changed");
            File.SetLastWriteTimeUtc(vpx, DateTime.UtcNow.AddMinutes(1));
            reloaded.Get(vpx, Read);
            Assert.Equal(2, reads);

            File.WriteAllText(Path.ChangeExtension(vpx, ".vbs"), "sidecar"); // a sidecar script counts too
            reloaded.Get(vpx, Read);
            Assert.Equal(3, reads);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Looking_for_VPX_finds_none_on_a_build_machine()
    {
        Assert.Empty(Native.VpxProcessIds());
        Assert.False(SessionWatcher.VpxRunning());
    }
}

public class RestartTests
{
    /// <summary>A watcher restarted after a crash waits for the old one, and takes over even if it died holding the slot.</summary>
    [Fact]
    public void A_restarted_watcher_takes_over_from_one_that_died()
    {
        var holder = new Thread(() => Setup.ClaimWatcher()); // claims and "dies" (the thread ends) without releasing
        holder.Start();
        holder.Join();
        using var next = Setup.ClaimWatcher(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);

        IDisposable? second = null;
        var other = new Thread(() => second = Setup.ClaimWatcher()); // a second PinBot while one runs: refused at once
        other.Start();
        other.Join();
        Assert.Null(second);
    }
}
