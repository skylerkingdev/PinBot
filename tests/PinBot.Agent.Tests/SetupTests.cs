using PinBot.Agent.Scoring;

namespace PinBot.Agent.Tests;

public class SetupTests
{
    [Theory]
    [InlineData("https://pinbot.example.ts.net/pair/ABCDE-FGHIJ", "https://pinbot.example.ts.net", "ABCDE-FGHIJ")]
    [InlineData("  `https://pinbot.example.ts.net/pair/ABCDE-FGHIJ`  ", "https://pinbot.example.ts.net", "ABCDE-FGHIJ")]
    [InlineData("pinbot.example.ts.net/pair/abcde-fghij/", "https://pinbot.example.ts.net", "abcde-fghij")]
    [InlineData("http://192.168.1.5:5080/pair/ABCDE-FGHIJ", "http://192.168.1.5:5080", "ABCDE-FGHIJ")]
    public void Pairing_line_is_split_into_server_and_code(string line, string url, string code) =>
        Assert.Equal((url, code), Setup.ParsePairingLine(line));

    [Theory]
    [InlineData("")]
    [InlineData("ABCDE-FGHIJ")]
    [InlineData("https://pinbot.example.ts.net/pair/")]
    [InlineData("ftp://pinbot.example.ts.net/pair/ABCDE-FGHIJ")]
    public void Anything_else_is_refused(string line) => Assert.Null(Setup.ParsePairingLine(line));

    [Fact]
    public void Built_in_maps_match_the_maps_folder()
    {
        var embedded = MemoryMaps.Embedded();
        Assert.Equal(TestData.Maps.RomCount, embedded.RomCount);
        foreach (var file in Directory.GetFiles(TestData.FixtureDir, "*.nv"))
        {
            var rom = TestData.RomOf(file);
            var a = HighScoreReader.Extract(TestData.Maps, rom, file);
            var b = HighScoreReader.Extract(embedded, rom, file);
            Assert.Equal(a.Status, b.Status);
            Assert.Equal(a.Entries.Select(e => e.ToString()), b.Entries.Select(e => e.ToString()));
        }
    }
}
