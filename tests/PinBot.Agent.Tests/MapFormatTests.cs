using System.Text;
using PinBot.Agent.Scoring;

namespace PinBot.Agent.Tests;

/// <summary>_fileformat 0.9: validation.checksum and validation.mirror, and "repeat" lists (on a small made-up map).</summary>
public class MapFormatTests
{
    const string Platform = """
        { "endian": "big", "memory_layout": [ { "label": "NVRAM", "address": "0x0000", "size": "0x400", "type": "nvram" } ] }
        """;

    // two high scores (initials then a 4-byte BCD score, a spare byte between them), a repeated pair of mode champions
    const string HighScores = """
        "high_scores": [
          { "label": "Grand Champion", "initials": { "start": "0x100", "length": 3, "encoding": "ch" }, "score": { "start": "0x103", "length": 4, "encoding": "bcd" } },
          { "label": "First", "initials": { "start": "0x108", "length": 3, "encoding": "ch" }, "score": { "start": "0x10B", "length": 4, "encoding": "bcd" } }
        ],
        "mode_champions": [
          { "label": "Mode #{#}", "short_label": "M{#}", "repeat": { "count": 2, "step": 8 },
            "initials": { "start": "0x180", "length": 3, "encoding": "ch" }, "score": { "start": "0x183", "length": 2, "encoding": "bcd" } }
        ]
        """;

    static MemoryMaps Maps(string mapJson)
    {
        var dir = TestData.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "platforms"));
        Directory.CreateDirectory(Path.Combine(dir, "maps"));
        File.WriteAllText(Path.Combine(dir, "platforms", "test.json"), Platform);
        File.WriteAllText(Path.Combine(dir, "maps", "test.map.json"), mapJson);
        File.WriteAllText(Path.Combine(dir, "index.json"), """{ "test": "maps/test.map.json" }""");
        return new MemoryMaps(dir);
    }

    static string Map09(string validation) => $$"""
        { "_fileformat": 0.9, "_metadata": { "platform": "test", "validation": {{validation}} },
          "checksum16": [ { "start": "0x300", "end": "0x30F", "label": "legacy, ignored in 0.9" } ],
          {{HighScores}} }
        """;

    static byte[] Nv()
    {
        var nv = new byte[0x400];
        void Entry(int at, string initials, byte[] bcd) { Encoding.ASCII.GetBytes(initials).CopyTo(nv, at); bcd.CopyTo(nv, at + 3); }
        Entry(0x100, "AAA", [0x00, 0x50, 0x00, 0x00]);
        Entry(0x108, "BBB", [0x00, 0x40, 0x00, 0x00]);
        Entry(0x180, "CCC", [0x00, 0x07]);
        Entry(0x188, "DDD", [0x00, 0x09]);
        return nv;
    }

    [Fact]
    public void Repeated_entries_are_numbered_and_shifted()
    {
        var map = Maps(Map09("{}")).TryLoad("test", out var why)!;
        Assert.Null(why);
        Assert.Equal(["Mode #1", "Mode #2"], map.ModeChampions.Select(e => e.Label));
        Assert.Equal(["M1", "M2"], map.ModeChampions.Select(e => e.ShortLabel));
        var x = HighScoreReader.Extract(map, Nv());
        Assert.Equal([("CCC", 7L), ("DDD", 9L)], x.Entries.Where(e => !e.IsHighScore).Select(e => (e.Initials, e.Score!.Value)));
    }

    [Fact]
    public void Validation_checksums_replace_the_legacy_lists_with_their_size_and_sum_style()
    {
        var map = Maps(Map09("""
            { "checksum": [
                { "label": "Scores", "start": "0x100", "end": "0x111", "bits": 16, "complement": true },
                { "label": "Plain", "start": "0x200", "end": "0x203", "checksum": "0x210", "complement": false },
                { "label": "Nibble", "start": "0x220", "end": "0x221", "bits": 4, "complement": false },
                { "label": "Row {#}", "start": "0x240", "length": 3, "checksum": "0x260", "complement": true, "repeat": { "count": 2, "step": 4 } } ] }
            """)).TryLoad("test", out _)!;
        Assert.Equal(
            [("Scores", 0x100, 0x10F, 0x110, 16, true), ("Plain", 0x200, 0x203, 0x210, 8, false), ("Nibble", 0x220, 0x220, 0x221, 4, false),
             ("Row 1", 0x240, 0x242, 0x260, 8, true), ("Row 2", 0x244, 0x246, 0x264, 8, true)],
            map.Checksums.Select(c => (c.Label, c.Start, c.End, c.StoredAt, c.Bits, c.Complement)));

        var nv = Nv();
        nv[0x200] = 0x10; nv[0x201] = 0x20;
        nv[0x220] = 0x37; nv[0x221] = 0xA0;
        foreach (var c in map.Checksums) c.Write(nv, map.LittleEndian);
        Assert.Equal(0x30, nv[0x210]);                     // the sum itself
        Assert.Equal(0xA7, nv[0x221]);                     // low nibble = 7, high nibble kept
        Assert.Empty(map.VerifyChecksums(nv, scoreRegionsOnly: false));
        nv[0x103] ^= 1;
        Assert.Equal(["Scores"], map.VerifyChecksums(nv));
    }

    [Fact]
    public void A_competition_file_keeps_declared_copies_in_step()
    {
        var mirrored = Map09("""{ "mirror": [ { "label": "High Scores", "offsets": ["0x100", "0x200"], "length": 16 } ] }""");
        var map = Maps(mirrored).TryLoad("test", out _)!;
        var nv = Nv();
        Array.Copy(nv, 0x100, nv, 0x200, 16);              // the ROM's copy matches: found and written too
        var image = map.BuildCompetitionImage(nv);
        Assert.True(image.Ok, image.Problem);
        Assert.Equal(image.Bytes!.AsSpan(0x100, 16).ToArray(), image.Bytes.AsSpan(0x200, 16).ToArray());

        nv[0x207] = 0x55;                                  // a spare byte differs: the copy is not found, so it would go stale
        image = map.BuildCompetitionImage(nv);
        Assert.False(image.Ok);
        Assert.Contains("copies of 'High Scores'", image.Problem);
    }
}
