using PinBot.Agent.Scoring;

namespace PinBot.Agent.Tests;

/// <summary>Before/after comparison on real afm_113b NVRAM with simulated ROM high-score writes.</summary>
public class ScoreDiffTests
{
    // afm_113 map order: 0 GC, 1 1st, 2 2nd, 3 3rd, 4 4th, 5-8 Buy-In #1-4, 9 Martian Champion, 10 Ruler of the Universe
    static readonly RomMap Afm = TestData.Map("afm_113b");

    static (IReadOnlyList<ScoreEntry> Before, IReadOnlyList<ScoreEntry> After) Play(Action<byte[]> session)
    {
        var before = TestData.Nv("afm_113b");
        var after = (byte[])before.Clone();
        session(after);
        var b = HighScoreReader.Extract(TestData.Maps, "afm_113b", before);
        var a = HighScoreReader.Extract(TestData.Maps, "afm_113b", after);
        Assert.True(b.Ok, b.Detail);
        Assert.True(a.Ok, a.Detail);
        return (b.Entries, a.Entries);
    }

    [Fact]
    public void Unchanged_file_has_no_new_entries()
    {
        var (b, a) = Play(_ => { });
        var d = ScoreDiff.Compare(b, a);
        Assert.False(d.Any);
        Assert.False(d.SuspectedReset);
    }

    [Fact]
    public void New_second_place_shifts_lower_slots_but_reports_exactly_one_entry()
    {
        // ROM inserts SKY at 2nd: old 2nd (LFS) → 3rd, old 3rd (RCF) → 4th, old 4th (DTW) drops off
        var (b, a) = Play(nv =>
        {
            TestData.SetEntry(nv, Afm, 4, "RCF", 6_000_000_000);
            TestData.SetEntry(nv, Afm, 3, "LFS", 6_500_000_000);
            TestData.SetEntry(nv, Afm, 2, "SKY", 6_800_000_000);
        });
        var d = ScoreDiff.Compare(b, a);
        var e = Assert.Single(d.NewEntries);
        Assert.Equal(("SKY", 6_800_000_000L, "Second Place"), (e.Initials, e.Score!.Value, e.Label));
        Assert.Equal(3, d.ChangedSlots.Count);
        Assert.False(d.SuspectedReset);
    }

    [Fact]
    public void Two_games_in_one_session_report_both_entries_highest_first()
    {
        var (b, a) = Play(nv =>
        {
            TestData.SetEntry(nv, Afm, 1, "AVK", 7_200_000_000);
            TestData.SetEntry(nv, Afm, 0, "SKY", 9_100_000_000);
        });
        var d = ScoreDiff.Compare(b, a);
        Assert.Equal(["SKY", "AVK"], d.NewEntries.Select(e => e.Initials));
    }

    [Fact]
    public void A_map_that_reads_two_slots_from_the_same_bytes_is_not_trusted_and_runs_no_competitions()
    {
        // Dracula's map reads the top score's initials from the Loop Champion's: a player's best game (SKY, written by
        // the ROM next to its score) would be credited to CD. Its new scores are held back instead of misattributed.
        var map = TestData.Map("drac_l1");
        Assert.Contains("same bytes", map.OverlapProblem);
        Assert.Contains("same bytes", map.CompetitionBlocker);
        Assert.False(map.BuildCompetitionImage(TestData.Nv("drac_l1")).Ok);

        var dir = TestData.TempDir();
        var path = Path.Combine(dir, "drac_l1.nv");
        File.WriteAllBytes(path, TestData.Nv("drac_l1"));
        var snap = Sessions.NvramSnapshot.Capture([dir], ["drac_l1"], TestData.Maps);
        var after = File.ReadAllBytes(path);
        "SKY"u8.CopyTo(after.AsSpan(7481));
        new byte[] { 0x03, 0x10, 0x00, 0x00, 0x00 }.CopyTo(after, 7484); // 310,000,000
        TestData.FixChecksums(after, map);
        File.WriteAllBytes(path, after);

        var o = snap.Analyze(path, TestData.Maps);
        Assert.False(o.Trusted);
        Assert.Contains(o.Problems, p => p.Contains("same bytes"));
    }

    [Fact]
    public void No_other_map_reads_two_slots_from_the_same_bytes()
    {
        var index = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(MemoryMaps.DefaultRoot, "index.json")));
        var overlapping = index.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_'))
            .Select(p => TestData.Maps.TryLoad(p.Name, out _)).OfType<RomMap>()
            .Where(m => m.OverlapProblem != null).Select(m => Path.GetFileName(m.MapFile)).Distinct().ToList();
        Assert.Equal(["drac_l1.map.json"], overlapping); // a fixed map drops out of this list: update the test then
    }

    [Fact]
    public void Player_beating_own_entry_counts_the_new_score_only()
    {
        // BRE held 1st with 7.0B; BRE scores 7.3B → new 1st, old BRE 7.0B moves to 2nd
        var (b, a) = Play(nv =>
        {
            TestData.SetEntry(nv, Afm, 4, "RCF", 6_000_000_000);
            TestData.SetEntry(nv, Afm, 3, "LFS", 6_500_000_000);
            TestData.SetEntry(nv, Afm, 2, "BRE", 7_000_000_000);
            TestData.SetEntry(nv, Afm, 1, "BRE", 7_300_000_000);
        });
        var e = Assert.Single(ScoreDiff.Compare(b, a).NewEntries);
        Assert.Equal(7_300_000_000, e.Score);
    }

    [Fact]
    public void Mode_champion_change_is_reported_as_slot_change_not_qualifying_score()
    {
        var (b, a) = Play(nv => TestData.SetEntry(nv, Afm, 9, "SKY", 25));
        var d = ScoreDiff.Compare(b, a);
        Assert.Empty(d.NewEntries);
        Assert.Contains(d.ChangedSlots, c => c.After.Label == "Martian Champion" && c.After.Initials == "SKY");
    }

    [Fact]
    public void Buy_in_leaderboard_is_shown_but_never_qualifies()
    {
        var (b, a) = Play(nv => TestData.SetEntry(nv, Afm, 5, "SKY", 5_500_000_000)); // Buy-In Score #1
        var d = ScoreDiff.Compare(b, a);
        Assert.Empty(d.NewEntries);
        Assert.Contains(d.ChangedSlots, c => c.After.Section == RomMap.SecondarySection && c.After.Initials == "SKY");
        Assert.Equal(5, a.Count(e => e.IsHighScore)); // GC + 1st-4th
    }

    [Theory]
    [InlineData("Buy-In Score #1", true)]
    [InlineData("BuyIn Score #2", true)]
    [InlineData("Buy-In #3", true)]
    [InlineData("Buyin Champion", true)]
    [InlineData("Grand Champion", false)]
    [InlineData("Under-Achievers #2", false)] // The Simpsons' themed main list
    [InlineData("Autobot #1", false)]
    public void Only_buy_in_tables_are_secondary(string label, bool secondary) =>
        Assert.Equal(secondary, RomMap.IsSecondaryLeaderboard(label));

    [Fact]
    public void Wholesale_replacement_of_every_score_is_flagged_as_reset()
    {
        var (b, a) = Play(nv =>
        {
            for (int i = 0; i <= 4; i++) TestData.SetEntry(nv, Afm, i, "AAA", 1_000_000_000L - i * 1_000_000);
        });
        Assert.True(ScoreDiff.Compare(b, a).SuspectedReset);
    }

    [Fact]
    public void Blank_or_default_initials_normalize_to_null()
    {
        Assert.Null(ScoreEntry.NormalizeInitials("   "));
        Assert.Null(ScoreEntry.NormalizeInitials("\0\0\0"));
        Assert.Null(ScoreEntry.NormalizeInitials("ÿÿÿ"));
        Assert.Null(ScoreEntry.NormalizeInitials("ABC", isDefault: true));
        Assert.Equal("SKY", ScoreEntry.NormalizeInitials(" sky"));
        Assert.Equal("G S", ScoreEntry.NormalizeInitials("G S"));
    }

    /// <summary>South Park (Whitestar, 10-character names) as seen on the cabinet: tags SKY and JBJ.</summary>
    [Fact]
    public void Whitestar_long_names_end_at_the_marker_without_the_repeated_letter()
    {
        Assert.Equal("SKY", ScoreEntry.NormalizeInitials("SKYY[[[[[["));
        Assert.Equal("JBJ", ScoreEntry.NormalizeInitials("JBJJ[[[[[["));
        Assert.Equal("JEK", ScoreEntry.NormalizeInitials("JEK       ")); // factory entries: plain padding
        Assert.Equal("A[B", ScoreEntry.NormalizeInitials("A[B"));         // short initials are left alone
    }

    /// <summary>Monopoly and Starship Troopers keep the player's token picture in a byte after the initials.</summary>
    [Fact]
    public void A_picture_byte_after_the_initials_ends_the_name()
    {
        Assert.Equal("PML", ScoreEntry.NormalizeInitials("PML\x08      "));
        Assert.Equal("CG", ScoreEntry.NormalizeInitials("CG \x0A      "));
        Assert.Equal("JEK", ScoreEntry.NormalizeInitials("JEK\x0E"));
        Assert.Null(ScoreEntry.NormalizeInitials("\u0005ABC"));   // nothing before it: no name
        Assert.Null(ScoreEntry.NormalizeInitials("A\u0001B"));    // a 3-character field with a control byte is junk
    }

    [Fact]
    public void Monopoly_and_Starship_Troopers_initials_survive_the_picture_byte()
    {
        foreach (var (rom, expected) in new[] { ("monopoly", "PML"), ("startrp", "JEK") })
        {
            var x = HighScoreReader.Extract(TestData.Maps, rom, TestData.Nv(rom));
            Assert.True(x.Ok, x.Detail);
            Assert.Contains(expected, x.Entries.Where(e => e.IsHighScore).Select(e => e.Initials));
            Assert.All(x.Entries.Where(e => e.IsHighScore && e.Score is > 0), e => Assert.NotNull(e.Initials));
        }
    }
}

public class ExtractionTests
{
    [Fact]
    public void Unsupported_rom_is_explicit()
    {
        var x = HighScoreReader.Extract(TestData.Maps, "notarom_l1", Path.Combine(TestData.FixtureDir, "afm_113b.nv"));
        Assert.Equal(ExtractStatus.UnsupportedRom, x.Status);
    }

    [Fact]
    public void Missing_file_is_explicit()
    {
        var x = HighScoreReader.Extract(TestData.Maps, "afm_113b", Path.Combine(TestData.TempDir(), "afm_113b.nv"));
        Assert.Equal(ExtractStatus.NoFile, x.Status);
    }

    [Fact]
    public void Exclusively_locked_file_is_reported_not_waited_on()
    {
        var path = Path.Combine(TestData.TempDir(), "afm_113b.nv");
        File.Copy(Path.Combine(TestData.FixtureDir, "afm_113b.nv"), path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(ExtractStatus.FileLocked, HighScoreReader.Extract(TestData.Maps, "afm_113b", path).Status);
        Assert.True(HighScoreReader.Extract(TestData.Maps, "afm_113b", path).Ok);
    }

    [Fact]
    public void Reading_never_blocks_a_writer_and_never_modifies_the_file()
    {
        var path = Path.Combine(TestData.TempDir(), "afm_113b.nv");
        File.Copy(Path.Combine(TestData.FixtureDir, "afm_113b.nv"), path);
        var mtime = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);

        // a PinMAME-like writer that shares read access keeps working while we read
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.True(HighScoreReader.Extract(TestData.Maps, "afm_113b", path).Ok);
            writer.Position = 0;
            writer.WriteByte(bytes[0]); // writer can still write while our reads happen
        }
        File.SetLastWriteTimeUtc(path, mtime);
        for (int i = 0; i < 3; i++) HighScoreReader.Extract(TestData.Maps, "afm_113b", path);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Corrupted_score_bytes_fail_checksum_and_are_not_trusted()
    {
        var nv = TestData.Nv("afm_113b");
        var gc = TestData.Map("afm_113b").HighScores[0].Score!.FileOffsets[0];
        nv[gc] ^= 0x10;
        var x = HighScoreReader.Extract(TestData.Maps, "afm_113b", nv);
        Assert.Equal(ExtractStatus.Corrupt, x.Status);
        Assert.Contains("Grand Champion", x.Detail);
    }

    [Fact]
    public void Truncated_file_is_corrupt()
    {
        var nv = TestData.Nv("afm_113b")[..4000];
        Assert.Equal(ExtractStatus.Corrupt, HighScoreReader.Extract(TestData.Maps, "afm_113b", nv).Status);
    }

    [Fact]
    public void Factory_reset_file_decodes_as_rom_defaults()
    {
        var x = HighScoreReader.Extract(TestData.Maps, "bop_l8", TestData.Nv("bop_l8-factoryreset"));
        Assert.True(x.Ok, x.Detail);
        Assert.All(x.Entries.Where(e => e.IsHighScore), e => Assert.True(e.Score > 0));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 }, Nibble.Low, new byte[] { 0x12, 0x34 })]
    [InlineData(new byte[] { 0x10, 0x20, 0x30 }, Nibble.High, new byte[] { 0x01, 0x23 })]
    [InlineData(new byte[] { 0x12, 0x34 }, Nibble.Both, new byte[] { 0x12, 0x34 })]
    public void Nibble_cells_combine_like_the_reference(byte[] cells, Nibble n, byte[] expected) =>
        Assert.Equal(expected, RomMap.DeNibble(cells, n));
}
