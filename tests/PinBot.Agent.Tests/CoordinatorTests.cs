using PinBot.Agent.Nvram;
using PinBot.Agent.Scoring;

namespace PinBot.Agent.Tests;

/// <summary>
/// NVRAM safety: competition NVRAM is prepared, kept and restored byte-for-byte, and every interruption either
/// recovers to the original or stops with both copies kept. Uses real afm_113b NVRAM in temp folders.
/// </summary>
public class CoordinatorTests
{
    sealed class Rig
    {
        public string NvDir = TestData.TempDir();
        public string Root = TestData.TempDir();
        public DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        public bool Idle = true;
        public byte[] Original;
        public string NvPath;
        public List<LocalCompetition> Comps = [];

        public Rig(string rom = "afm_113b", string fixture = "afm_113b")
        {
            NvPath = Path.Combine(NvDir, rom + ".nv");
            Original = TestData.Nv(fixture);
            File.WriteAllBytes(NvPath, Original);
        }

        public NvramCoordinator Coordinator(Action<string>? fault = null, Func<bool>? idle = null) =>
            new(Root, TestData.Maps, _ => (idle ?? (() => Idle))(), () => Now) { FaultInjection = fault };

        public LocalCompetition Start(string id = "c1", double hours = 24, string? rom = null, string? nvPath = null)
        {
            var c = new LocalCompetition(id, rom ?? "afm_113b", nvPath ?? NvPath, "AFM", Now, Now.AddHours(hours));
            Comps.Add(c);
            return c;
        }

        public void Tick(NvramCoordinator? c = null) => (c ?? Coordinator()).Tick(Comps);
        public byte[] Live => File.ReadAllBytes(NvPath);
        public string LiveSha => HighScoreReader.Sha256(Live);
        public JournalEntry Entry(string id = "c1") => Coordinator().LoadEntries().Last(e => e.CompetitionId == id);
        public string? CompetitionSha => TestData.Map("afm_113b").BuildCompetitionImage(Original).Bytes is { } b ? HighScoreReader.Sha256(b) : null;

        /// <summary>What PinMAME does when a player sets a new Grand Champion during the competition.</summary>
        public void PlayGame(string initials, long score)
        {
            var nv = Live;
            TestData.SetEntry(nv, TestData.Map("afm_113b"), 0, initials, score);
            File.WriteAllBytes(NvPath, nv);
        }
    }

    [Fact]
    public void Full_lifecycle_prepares_keeps_and_restores_byte_for_byte()
    {
        var r = new Rig();
        r.Start(hours: 24);
        r.Tick();

        var e = r.Entry();
        Assert.Equal(CompState.Ready, e.State);
        Assert.Equal(r.CompetitionSha, r.LiveSha);
        Assert.Equal(r.Original, File.ReadAllBytes(e.BackupPath));
        Assert.True(File.GetAttributes(e.BackupPath).HasFlag(FileAttributes.ReadOnly));
        var main = HighScoreReader.Extract(TestData.Maps, "afm_113b", r.Live).Entries.Where(x => x.IsHighScore).Select(x => x.Score);
        Assert.Equal([50_000L, 40_000, 30_000, 20_000, 10_000], main);

        // the competition file is kept across sessions: no reset between games
        r.PlayGame("SKY", 900_000_000);
        r.Now = r.Now.AddHours(1);
        r.Tick();
        var played = r.Live;
        Assert.Equal(CompState.Ready, r.Entry().State);

        r.Now = r.Now.AddHours(24);
        r.Tick();
        Assert.Equal(CompState.Restored, r.Entry().State);
        Assert.Equal(r.Original, r.Live);
        Assert.Equal(played, File.ReadAllBytes(Path.Combine(r.Entry().Dir, "competition-final.nv")));
    }

    [Fact]
    public void Every_replacement_of_the_live_file_is_reported()
    {
        // the session watcher uses this to tell a game's own NVRAM writes from PinBot's swaps while another table runs
        var r = new Rig();
        var replaced = new List<string>();
        var coordinator = new NvramCoordinator(r.Root, TestData.Maps, _ => true, () => r.Now) { Replaced = replaced.Add };
        r.Start(hours: 1);
        coordinator.Tick(r.Comps);
        Assert.Equal([r.NvPath], replaced); // swapped in

        r.Now = r.Now.AddHours(2);
        coordinator.Tick(r.Comps);
        Assert.Equal([r.NvPath, r.NvPath], replaced); // restored
        Assert.Equal(r.Original, r.Live);
    }

    [Fact]
    public void Nothing_is_touched_while_a_game_is_running()
    {
        var r = new Rig { Idle = false };
        r.Start();
        r.Tick();
        Assert.Empty(r.Coordinator().LoadEntries());
        Assert.Equal(r.Original, r.Live);

        r.Idle = true;
        r.Tick();
        Assert.Equal(CompState.Ready, r.Entry().State);

        // deadline passes while someone is still playing: restoration waits, never interrupts
        r.Idle = false;
        r.Now = r.Now.AddDays(2);
        r.Tick();
        Assert.Equal(CompState.PendingRestore, r.Entry().State);
        Assert.Equal(r.CompetitionSha, r.LiveSha);

        r.Idle = true;
        r.Tick();
        Assert.Equal(r.Original, r.Live);
    }

    [Fact]
    public void Game_starting_between_idle_check_and_swap_defers_the_swap()
    {
        var r = new Rig();
        r.Start();
        int calls = 0;
        r.Tick(r.Coordinator(idle: () => ++calls < 2)); // idle for Tick's check, busy just before the rename
        Assert.Equal(CompState.BackedUp, r.Entry().State);
        Assert.Equal(r.Original, r.Live);
        Assert.False(File.Exists(r.NvPath + ".pinbot-tmp"));

        r.Tick();
        Assert.Equal(CompState.Ready, r.Entry().State);
    }

    public static IEnumerable<object[]> CrashPoints() =>
        new[] { "prepare.journaled", "prepare.backup-written", "prepare.backed-up", "swap.tmp-written", "swap.moved",
                "restore.journaled", "restore.tmp-written", "restore.moved" }.Select(p => new object[] { p });

    /// <summary>The process dies at each step; a restarted agent always ends with the original byte-for-byte.</summary>
    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void Crash_at_any_step_recovers_to_the_original(string point)
    {
        var r = new Rig();
        r.Start(hours: 24);
        var crashing = r.Coordinator(fault: p => { if (p == point) throw new SimulatedCrash(p); });

        if (point.StartsWith("restore"))
        {
            r.Tick();
            Assert.Equal(CompState.Ready, r.Entry().State);
            r.PlayGame("SKY", 900_000_000);
            r.Now = r.Now.AddDays(2);
        }
        Assert.Throws<SimulatedCrash>(() => r.Tick(crashing));

        // at the moment of the crash the live file is always one whole file, never a mix
        var live = r.Live;
        Assert.True(live.SequenceEqual(r.Original) || HighScoreReader.Sha256(live) == r.CompetitionSha
                    || (point.StartsWith("restore") && HighScoreReader.Extract(TestData.Maps, "afm_113b", live).Ok));

        // restart: keep ticking until the competition is over and everything settles
        for (int i = 0; i < 3; i++) r.Tick();
        r.Now = r.Now.AddDays(3);
        for (int i = 0; i < 3; i++) r.Tick();

        Assert.Equal(r.Original, r.Live);
        Assert.DoesNotContain(r.Coordinator().LoadEntries(), e => e.State == CompState.NeedsRecovery);
        Assert.False(File.Exists(r.NvPath + ".pinbot-tmp"));
    }

    [Fact]
    public void Absent_original_is_never_prepared()
    {
        var r = new Rig();
        File.Delete(r.NvPath);
        r.Start();
        r.Tick();
        Assert.Empty(r.Coordinator().LoadEntries());
        Assert.False(File.Exists(r.NvPath));
    }

    [Fact]
    public void Rom_without_verified_competition_file_is_refused_without_touching_anything()
    {
        var r = new Rig("xenon", "xenon"); // stores no initials
        r.Start(rom: "xenon");
        r.Tick();
        var e = r.Entry();
        Assert.Equal(CompState.Refused, e.State);
        Assert.Contains("no initials", e.Problem);
        Assert.False(File.Exists(e.BackupPath));
        Assert.Equal(r.Original, r.Live);

        r.Tick(); // refused once, not retried every tick
        Assert.Single(r.Coordinator().LoadEntries());
    }

    [Fact]
    public void Second_competition_on_the_same_file_waits_for_the_first_to_restore()
    {
        var r = new Rig();
        r.Start("c1", hours: 2);
        r.Tick();
        r.PlayGame("SKY", 900_000_000);
        r.Start("c2", hours: 48);   // overlapping competition for the same NVRAM file (e.g. a second table using the ROM)
        r.Tick();
        Assert.DoesNotContain(r.Coordinator().LoadEntries(), e => e.CompetitionId == "c2");

        r.Now = r.Now.AddHours(3);  // c1 ends; c1 restores, then c2 prepares from the restored original
        r.Tick();
        r.Tick();
        Assert.Equal(CompState.Restored, r.Entry("c1").State);
        var c2 = r.Entry("c2");
        Assert.Equal(CompState.Ready, c2.State);
        Assert.Equal(r.Original, File.ReadAllBytes(c2.BackupPath)); // the backup is the personal file, not c1's competition file
    }

    [Fact]
    public void Different_roms_are_handled_independently()
    {
        var r = new Rig();
        var tz = Path.Combine(r.NvDir, "tz_92.nv");
        File.WriteAllBytes(tz, TestData.Nv("tz_92"));
        r.Start("afm");
        r.Start("tz", rom: "tz_92", nvPath: tz);
        r.Tick();
        Assert.Equal(CompState.Ready, r.Entry("afm").State);
        Assert.Equal(CompState.Ready, r.Entry("tz").State);
        r.Now = r.Now.AddDays(2);
        r.Tick();
        Assert.Equal(r.Original, r.Live);
        Assert.Equal(TestData.Nv("tz_92"), File.ReadAllBytes(tz));
    }

    [Fact]
    public void Game_played_between_backup_and_swap_restarts_preparation_from_the_new_file()
    {
        var r = new Rig();
        r.Start();
        Assert.Throws<SimulatedCrash>(() => r.Tick(r.Coordinator(fault: p => { if (p == "prepare.backed-up") throw new SimulatedCrash(p); })));
        r.PlayGame("NEW", 800_000_000); // agent was down; someone played on the personal file
        var personal = r.Live;

        r.Tick(); // stale backup detected -> abandoned (kept on disk)
        r.Tick(); // fresh preparation
        Assert.Equal(CompState.Ready, r.Entry().State);
        Assert.Equal(personal, File.ReadAllBytes(r.Entry().BackupPath));

        r.Now = r.Now.AddDays(2);
        r.Tick();
        Assert.Equal(personal, r.Live);
    }

    [Fact]
    public void Unjournaled_swap_followed_by_a_game_is_recognised_as_the_competition_file()
    {
        var r = new Rig();
        r.Start();
        Assert.Throws<SimulatedCrash>(() => r.Tick(r.Coordinator(fault: p => { if (p == "swap.moved") throw new SimulatedCrash(p); })));
        r.PlayGame("SKY", 900_000_000); // played on the competition file while the agent was down

        r.Tick();
        Assert.Equal(CompState.Ready, r.Entry().State);
        r.Now = r.Now.AddDays(2);
        r.Tick();
        Assert.Equal(r.Original, r.Live);
    }

    [Fact]
    public void Tampered_backup_stops_restoration_and_keeps_both_copies()
    {
        var r = new Rig();
        r.Start();
        r.Tick();
        r.PlayGame("SKY", 900_000_000);
        var competitionState = r.Live;
        var backup = r.Entry().BackupPath;
        File.SetAttributes(backup, FileAttributes.Normal);
        var bad = File.ReadAllBytes(backup);
        bad[100] ^= 0xFF;
        File.WriteAllBytes(backup, bad);

        r.Now = r.Now.AddDays(2);
        r.Tick();
        Assert.Equal(CompState.NeedsRecovery, r.Entry().State);
        Assert.Equal(competitionState, r.Live); // never overwritten with an unverified backup
        r.Tick();
        Assert.Equal(competitionState, r.Live);
    }

    [Fact]
    public void Corrupted_journal_is_reported_and_left_alone()
    {
        var r = new Rig();
        r.Start();
        r.Tick();
        var dir = r.Entry().Dir;
        File.WriteAllText(Path.Combine(dir, "entry.json"), "{ not json");

        r.Now = r.Now.AddDays(2);
        r.Tick(); // must not throw, must not restore from a journal it cannot read
        var (entries, unreadable) = r.Coordinator().LoadAll();
        Assert.Empty(entries);
        Assert.Single(unreadable);
        Assert.Equal(r.CompetitionSha, r.LiveSha);
        Assert.True(File.Exists(Path.Combine(dir, "original.nv")));
    }

    [Fact]
    public void Locked_file_defers_restoration()
    {
        var r = new Rig();
        r.Start();
        r.Tick();
        r.Now = r.Now.AddDays(2);
        using (new FileStream(r.NvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            r.Tick();
            Assert.Equal(CompState.PendingRestore, r.Entry().State);
        }
        r.Tick();
        Assert.Equal(CompState.Restored, r.Entry().State);
        Assert.Equal(r.Original, r.Live);
    }

    [Fact]
    public void Competition_that_ended_while_the_cabinet_was_off_is_never_prepared()
    {
        var r = new Rig();
        r.Start(hours: 1);
        r.Now = r.Now.AddDays(7);
        r.Tick();
        Assert.Empty(r.Coordinator().LoadEntries());
        Assert.Equal(r.Original, r.Live);
    }

    [Fact]
    public void Late_join_and_early_end_work()
    {
        var r = new Rig();
        var c = r.Start(hours: 24 * 7);
        r.Now = r.Now.AddDays(3); // table installed / agent started mid-competition
        r.Tick();
        Assert.Equal(CompState.Ready, r.Entry().State);

        r.Comps[0] = c with { EndedEarlyUtc = r.Now };
        r.Now = r.Now.AddMinutes(1);
        r.Tick();
        Assert.Equal(r.Original, r.Live);
    }

    [Fact]
    public void Deleted_competition_is_treated_as_cancelled_and_restored()
    {
        var r = new Rig();
        r.Start();
        r.Tick();
        r.Comps.Clear();
        r.Tick();
        Assert.Equal(r.Original, r.Live);
    }
}

public class CompetitionImageTests
{
    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(TestData.FixtureDir, "*.nv").Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>Every fixture ROM either yields a verified ladder image or is refused with a reason.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Image_is_verified_or_refused(string file)
    {
        var map = TestData.Map(TestData.RomOf(file));
        var original = File.ReadAllBytes(Path.Combine(TestData.FixtureDir, file));
        var img = map.BuildCompetitionImage(original);
        if (!map.HasInitials || map.CompetitionBlocker != null)
        {
            Assert.False(img.Ok);
            return;
        }
        if (HighScoreReader.Extract(map, original).Status != ExtractStatus.Ok)
        {
            Assert.False(img.Ok);
            return;
        }
        Assert.True(img.Ok, img.Problem);
        Assert.True(img.Ladder.Zip(img.Ladder.Skip(1)).All(p => p.First > p.Second) && img.Ladder[^1] > 0);
        Assert.Equal(original.Length, img.Bytes!.Length);
        Assert.True(HighScoreReader.Extract(map, img.Bytes).Ok);
    }

    /// <summary>
    /// AC/DC (Stern SAM) reset its high scores to factory defaults when given a ladder without entry checksums: the
    /// map lists none. Every entry (36 bytes apart from 0x37FC) ends with 0xFFFF minus the sum of its first 32 bytes.
    /// </summary>
    [Fact]
    public void Sam_competition_image_carries_the_entry_checksums_the_rom_checks()
    {
        var map = TestData.Map("acd_170h");
        var original = TestData.Nv("acd_170h");
        var img = map.BuildCompetitionImage(original);
        Assert.True(img.Ok, img.Problem);
        static bool EntryOk(byte[] nv, int k)
        {
            var s = 0x37FC + 36 * k;
            var sum = nv.Skip(s).Take(32).Sum(b => b);
            return (ushort)(0xFFFF - (sum & 0xFFFF)) == (ushort)(nv[s + 32] | nv[s + 33] << 8);
        }
        for (var k = 0; k < 5; k++)
        {
            Assert.True(EntryOk(original, k), $"original entry {k}");
            Assert.True(EntryOk(img.Bytes!, k), $"competition entry {k}");
        }
        Assert.NotEqual(original.Skip(0x37FC).Take(36 * 5), img.Bytes!.Skip(0x37FC).Take(36 * 5)); // the scores did change
    }

    /// <summary>
    /// Big Bang Bar (Capcom) reset its table to factory values when only the main copy held the ladder: Capcom keeps
    /// two backup copies of the 77-byte high-score block (here at 0xBBA and 0x1684) and the ladder must be in all three.
    /// </summary>
    [Fact]
    public void Capcom_competition_image_updates_both_backup_copies_of_the_table()
    {
        var map = TestData.Map("bbb109");
        var original = TestData.Nv("bbb109");
        var img = map.BuildCompetitionImage(original);
        Assert.True(img.Ok, img.Problem);
        var table = img.Bytes!.AsSpan(0xF0, 77).ToArray();
        Assert.NotEqual(original.AsSpan(0xF0, 77).ToArray(), table);                 // the ladder is in the table
        Assert.Equal(table, img.Bytes!.AsSpan(0xBBA, 77).ToArray());                 // ...and in both copies
        Assert.Equal(table, img.Bytes!.AsSpan(0x1684, 77).ToArray());
        var changed = Enumerable.Range(0, original.Length).Where(i => original[i] != img.Bytes![i]).ToList();
        Assert.All(changed, i => Assert.True(i is >= 0xF0 and < 0xF0 + 77 or >= 0xBBA and < 0xBBA + 77 or >= 0x1684 and < 0x1684 + 77, $"offset {i:X}"));

        // copies that cannot be found (e.g. already damaged) mean no competition image rather than a reset
        var damaged = (byte[])original.Clone();
        damaged[0xBBA] ^= 0xFF;
        damaged[0x1684] ^= 0xFF;
        Assert.False(map.BuildCompetitionImage(damaged).Ok);
    }

    /// <summary>
    /// The emulator self-test showed Street Fighter II restoring the player's own table from its two untouched backup
    /// copies: Gottlieb System 3 keeps the 48-byte table (initials and scores, 0xD0C) three times, 256 bytes apart.
    /// </summary>
    [Fact]
    public void System3_competition_image_updates_both_backup_copies_of_the_table()
    {
        var map = TestData.Map("sfight2");
        var original = File.ReadAllBytes(Path.Combine(TestData.FixtureDir, "..", "cabinet", "sfight2.nv"));
        var img = map.BuildCompetitionImage(original);
        Assert.True(img.Ok, img.Problem);
        var table = img.Bytes!.AsSpan(0xD0C, 48).ToArray();
        Assert.NotEqual(original.AsSpan(0xD0C, 48).ToArray(), table);
        Assert.Equal(table, img.Bytes!.AsSpan(0xD0C + 256, 48).ToArray());
        Assert.Equal(table, img.Bytes!.AsSpan(0xD0C + 512, 48).ToArray());

        var damaged = (byte[])original.Clone(); // a copy that cannot be found: no competition file rather than a reset
        damaged[0xD0C + 512 + 30] ^= 0xFF;
        Assert.Contains("backup copies", map.BuildCompetitionImage(damaged).Problem);
    }

    /// <summary>Dr. Dude and Whirlwind raised a laddered Champion in the emulator: it stays the player's own.</summary>
    [Fact]
    public void System11_champion_is_kept_and_the_ladder_fills_the_places_below()
    {
        var map = TestData.Map("whirl_l3");
        var original = TestData.Nv("whirl_l3");
        var img = map.BuildCompetitionImage(original);
        Assert.True(img.Ok, img.Problem);
        var before = HighScoreReader.Extract(map, original).Entries.Where(e => e.IsHighScore).ToList();
        var after = HighScoreReader.Extract(map, img.Bytes!).Entries.Where(e => e.IsHighScore).ToList();
        Assert.Equal("Champion", after[0].Label);
        Assert.Equal(before[0].Score, after[0].Score);
        Assert.Equal([40_000L, 30_000, 20_000, 10_000], after.Skip(1).Select(e => e.Score!.Value));
    }

    [Fact]
    public void Ladder_new_score_shows_up_as_exactly_one_new_entry()
    {
        var map = TestData.Map("afm_113b");
        var img = map.BuildCompetitionImage(TestData.Nv("afm_113b")).Bytes!;
        var after = (byte[])img.Clone();
        TestData.SetEntry(after, map, 0, "SKY", 1_000_000);
        var d = ScoreDiff.Compare(HighScoreReader.Extract(map, img).Entries, HighScoreReader.Extract(map, after).Entries);
        Assert.Equal(["SKY"], d.NewEntries.Select(e => e.Initials));
        Assert.False(d.SuspectedReset);
    }
}

public class CompetitionBlockerTests
{
    /// <summary>System 80B layouts whose per-score checksum is not confirmed stay readable but are not offered.</summary>
    [Fact]
    public void Unconfirmed_System80B_layouts_are_readable_but_not_offered_for_competitions()
    {
        var map = TestData.Map("robowars");
        Assert.StartsWith("gottlieb-system80b", map.Platform);
        Assert.Contains("Gottlieb System 80B", map.CompetitionBlocker);
        Assert.Equal(ExtractStatus.Ok, HighScoreReader.Extract(map, TestData.Nv("robowars")).Status);
        Assert.False(map.BuildCompetitionImage(TestData.Nv("robowars")).Ok);
        Assert.Null(TestData.Map("bbb109").CompetitionBlocker);
    }

    /// <summary>Hook 5.01 and Star Wars 1.07 reset a correct ladder in the emulator self-test: not offered.</summary>
    [Fact]
    public void Roms_the_self_test_saw_rejecting_a_ladder_are_not_offered()
    {
        Assert.Contains("emulator self-test", TestData.Map("hook_501").CompetitionBlocker);
        Assert.Contains("emulator self-test", TestData.Map("stwr_107").CompetitionBlocker);
        Assert.Null(TestData.Map("lw3_208").CompetitionBlocker); // other Data East games kept it
    }

    static int NibbleSum(byte[] nv, EntryDef e) =>
        e.Initials!.FileOffsets.Concat(e.Score!.FileOffsets).Sum(o => nv[o] & 0xF) & 0xF;

    /// <summary>
    /// Genesis (same layout as Hollywood Heat) rejected a ladder written without its per-score checksums at 242-246:
    /// the low-nibble sum of the entry's initials and score, mod 16 (derived from the cabinet's own before/after files).
    /// </summary>
    [Fact]
    public void System80B_competition_image_carries_the_per_score_nibble_checksums()
    {
        var map = TestData.Map("hlywoodh");
        Assert.Null(map.CompetitionBlocker);
        Assert.Null(TestData.Map("genesis").CompetitionBlocker);
        var original = TestData.Nv("hlywoodh");
        var main = map.HighScores.Where(e => e.Section == "high_scores").ToList();
        for (int i = 0; i < main.Count; i++) Assert.Equal(NibbleSum(original, main[i]), original[242 + i] & 0xF); // the rule holds on the sample

        var img = map.BuildCompetitionImage(original);
        Assert.True(img.Ok, img.Problem);
        for (int i = 0; i < main.Count; i++) Assert.Equal(NibbleSum(img.Bytes!, main[i]), img.Bytes![242 + i] & 0xF);
        Assert.Contains(Enumerable.Range(242, main.Count), i => img.Bytes![i] != original[i]); // the checksums did change

        // the top entry is also kept in the triplicate data (offsets 0, 59, 118): all three copies carry the ladder's top
        var top = main[0].Initials!.FileOffsets.Concat(main[0].Score!.FileOffsets).ToList();
        int start = top.Min(), len = top.Max() - start + 1;
        var topNibbles = img.Bytes!.Skip(start).Take(len).Select(b => b & 0xF).ToArray();
        var copies = Enumerable.Range(0, 177 - len).Where(o => img.Bytes!.Skip(o).Take(len).Select(b => b & 0xF).SequenceEqual(topNibbles)).ToList();
        Assert.Equal(3, copies.Count);
        Assert.Equal([59, 59], new[] { copies[1] - copies[0], copies[2] - copies[1] });

        var broken = (byte[])original.Clone();
        broken[242] ^= 0x01; // a file whose checksums do not follow the rule gets no image
        Assert.False(map.BuildCompetitionImage(broken).Ok);
    }
}
