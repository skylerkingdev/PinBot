using System.Text;
using OpenMcdf;
using PinBot.Agent.Discovery;
using PinBot.Agent.Sync;
using PinBot.Contracts;

namespace PinBot.Agent.Tests;

public class TableMetadataTests
{
    static string Vpx(string dir, string name, Dictionary<string, string>? info, string script = "Const cGameName = \"twenty4_150\"")
    {
        var vpx = Path.Combine(dir, name + ".vpx");
        using var root = RootStorage.Create(vpx);
        var gameStg = root.CreateStorage("GameStg");
        using (var s = gameStg.CreateStream("GameData"))
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            var code = Encoding.Latin1.GetBytes(script);
            w.Write(4); w.Write(Encoding.ASCII.GetBytes("CODE")); w.Write(code.Length); w.Write(code);
            w.Write(4); w.Write(Encoding.ASCII.GetBytes("ENDB"));
            s.Write(ms.ToArray());
        }
        if (info != null)
        {
            var ti = root.CreateStorage("TableInfo");
            foreach (var (k, v) in info)
            {
                using var s = ti.CreateStream(k);
                s.Write(Encoding.Unicode.GetBytes(v));
            }
        }
        return vpx;
    }

    [Fact]
    public void Table_info_and_script_hash_are_read_from_the_vpx()
    {
        var vpx = Vpx(TestData.TempDir(), "24", new()
        {
            ["TableName"] = "24 (Stern 2009)",
            ["TableVersion"] = "2.4.1",
            ["AuthorName"] = "freneticamnesic, 32assassin,\r\nRobbyKingPin",
        });
        var (script, _) = TableScript.Read(vpx);
        var meta = TableMetadata.Read(vpx, script);
        Assert.Equal("24 (Stern 2009)", meta.Name);
        Assert.Equal("2.4.1", meta.Version);
        Assert.Equal("freneticamnesic, 32assassin, RobbyKingPin", meta.Authors); // line breaks become spaces
        Assert.Equal(TableMetadata.HashScript("Const cGameName = \"twenty4_150\""), meta.ScriptHash);
    }

    [Fact]
    public void Table_without_table_info_still_gets_its_script_hash()
    {
        var vpx = Vpx(TestData.TempDir(), "Plain", null);
        var meta = TableMetadata.Read(vpx, "x");
        Assert.Equal(new TableMeta(null, null, null, TableMetadata.HashScript("x")), meta);
    }

    [Fact]
    public void Unreadable_vpx_gives_empty_description()
    {
        var vpx = Path.Combine(TestData.TempDir(), "broken.vpx");
        File.WriteAllText(vpx, "not a vpx");
        Assert.Equal(new TableMeta(null, null, null, null), TableMetadata.Read(vpx, null));
    }

    [Fact]
    public void Script_hash_ignores_line_endings_and_sees_edits()
    {
        Assert.Equal(TableMetadata.HashScript("a\r\nb\r\n"), TableMetadata.HashScript("a\nb\n"));
        Assert.NotEqual(TableMetadata.HashScript("a\nb\n"), TableMetadata.HashScript("a\nb2\n"));
        Assert.Null(TableMetadata.HashScript(null));
        // the hash of the bytes as they are on disk (Latin-1 read), as any other tool would compute it
        Assert.Equal("ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb", TableMetadata.HashScript("a"));
    }

    [Fact]
    public void Backglass_header_is_read_and_the_images_are_not()
    {
        var path = Path.Combine(TestData.TempDir(), "24.directb2s");
        File.WriteAllText(path, """
            <DirectB2SData Version="1.27">
              <Name Value="24 (Stern 2009)" />
              <TableType Value="3" />
              <DMDType Value="3" />
              <DMDDefaultLocation LocX="0" LocY="0" />
              <DualBackglass Value="0" />
              <Author Value="HauntFreaks" />
              <Artwork Value="HauntFreaks" />
              <GameName Value="twenty4_150" />
              <Animations>
                <Animation Name="on"><AnimationStep Step="1" /></Animation>
              </Animations>
              <Images><BackglassImage Value="this part is never read &amp; broken <" /></Images>
            """); // no closing tag: reading stops at the header, long before the broken part
        Assert.Equal(new BackglassInfo("24 (Stern 2009)", "HauntFreaks", 3, false), Backglass.Read(path));
    }

    [Fact]
    public void Missing_or_foreign_backglass_is_null()
    {
        var dir = TestData.TempDir();
        Assert.Null(Backglass.Read(Path.Combine(dir, "none.directb2s")));
        var other = Path.Combine(dir, "other.directb2s");
        File.WriteAllText(other, "<Something><Name Value=\"x\" /></Something>");
        Assert.Null(Backglass.Read(other));
        var garbage = Path.Combine(dir, "garbage.directb2s");
        File.WriteAllBytes(garbage, [0, 1, 2, 3]);
        Assert.Null(Backglass.Read(garbage));
    }

    [Fact]
    public void Backglass_with_a_dtd_is_refused()
    {
        var path = Path.Combine(TestData.TempDir(), "dtd.directb2s");
        File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY e \"boom\">]><DirectB2SData><Author Value=\"&e;\" /></DirectB2SData>");
        Assert.Null(Backglass.Read(path));
    }

    [Fact]
    public void Inventory_files_include_tables_without_a_rom_and_their_description()
    {
        var dir = TestData.TempDir();
        var vpx = Vpx(dir, "24", new() { ["TableName"] = "24", ["TableVersion"] = "2.4.1", ["AuthorName"] = "RobbyKingPin" });
        File.WriteAllText(Backglass.PathFor(vpx), "<DirectB2SData><Name Value=\"24\" /><Author Value=\"HauntFreaks\" /><DMDType Value=\"3\" /><Images></Images></DirectB2SData>");
        var em = Vpx(dir, "Old EM", null, "' no rom here");
        var nvram = Path.Combine(dir, "VPinMAME", "nvram");
        Directory.CreateDirectory(nvram);
        Directory.CreateDirectory(Path.Combine(dir, "VPinMAME", "altcolor", "twenty4_150"));
        var inst = new Installations([], [dir], [nvram], []);
        var tables = Inventory.Build(inst, TestData.Maps, new LocalState());

        var files = AgentSync.Files(tables).OrderBy(f => f.Table).ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(new TableFile("24", "twenty4_150", "24", "2.4.1", "RobbyKingPin", TableMetadata.HashScript("Const cGameName = \"twenty4_150\""),
            "24", "HauntFreaks", 3, AltColor: true, AltSound: false), files[0]);
        Assert.Equal("Old EM", files[1].Table);
        Assert.Null(files[1].RomId);
        Assert.Null(files[1].Version);
        // competitions still see only tables with a ROM
        Assert.Single(AgentSync.Inventory(tables));
    }
}
