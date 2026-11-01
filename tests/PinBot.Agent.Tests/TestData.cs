using System.Text;
using PinBot.Agent.Scoring;

namespace PinBot.Agent.Tests;

static class TestData
{
    public static readonly MemoryMaps Maps = new(MemoryMaps.DefaultRoot);
    public static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "nvram");
    public static byte[] Nv(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, name + ".nv"));

    /// <summary>"afm_113b-buyin.nv" → "afm_113b".</summary>
    public static string RomOf(string fileName) => Path.GetFileName(fileName).Split('.', '-')[0];

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pinbot-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Test-only writer simulating what a ROM does when a player enters a high score: 8-bit ASCII initials and
    /// big-endian BCD score at the map's offsets, then recomputes checksum16 regions (WPC layout).
    /// </summary>
    public static void SetEntry(byte[] nv, RomMap map, int index, string initials, long score)
    {
        var def = map.HighScores.Concat(map.ModeChampions).ElementAt(index);
        var ini = def.Initials!.FileOffsets;
        var chars = Encoding.ASCII.GetBytes(initials.PadRight(ini.Length));
        for (int i = 0; i < ini.Length; i++) nv[ini[i]] = chars[i];
        var sc = def.Score!.FileOffsets;
        long v = score;
        for (int i = sc.Length - 1; i >= 0; i--)
        {
            nv[sc[i]] = (byte)((v % 10) | ((v / 10 % 10) << 4));
            v /= 100;
        }
        FixChecksums(nv, map);
    }

    public static void FixChecksums(byte[] nv, RomMap map)
    {
        foreach (var c in map.Checksums.Where(c => c.Bits == 16)) c.Write(nv, map.LittleEndian);
    }

    public static RomMap Map(string rom) => Maps.TryLoad(rom, out var why) ?? throw new InvalidOperationException(why);
}
