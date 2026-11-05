using PinBot.Agent.Discovery;

namespace PinBot.Agent.Tests;

public class PovTests
{
    const string Ini = """
        [Player]
        ScreenWidth = 95.0
        BGSet = 1

        [TableOverride]
        ViewCabMode = 2
        ViewCabScaleX = 1.050000
        ViewCabFOV = 45
        ViewDTMode = 0
        ViewDTFOV = 50
        Difficulty = 0.5
        ViewCabNotes = hello there
        ; ViewCabCommented = 3

        [Other]
        ViewCabElsewhere = 9
        """;

    [Fact]
    public void Only_the_modes_numeric_view_keys_under_TableOverride_are_read()
    {
        Assert.Equal("ViewCabFOV = 45\nViewCabMode = 2\nViewCabScaleX = 1.050000", Pov.Parse(Ini.Split('\n'), "cabinet"));
        Assert.Equal("ViewDTFOV = 50\nViewDTMode = 0", Pov.Parse(Ini.Split('\n'), "desktop"));
        Assert.Null(Pov.Parse(Ini.Split('\n'), "fss"));
        Assert.Null(Pov.Parse(Ini.Split('\n'), "vr"));
    }

    [Fact]
    public void The_view_mode_comes_from_BGSet_and_defaults_to_desktop()
    {
        Assert.Equal("cabinet", Pov.ModeFrom(Ini.Split('\n')));
        Assert.Equal("fss", Pov.ModeFrom(["[Player]", "BGSet=2"]));
        Assert.Equal("desktop", Pov.ModeFrom(["[Player]", "Other = 1"]));
    }

    [Fact]
    public void Installing_replaces_the_modes_whole_view_and_keeps_everything_else()
    {
        var lines = new[] { "; my notes", "[TableOverride]", "ViewCabFOV = 45", "Difficulty = 0.5", "viewcabOldKey = 3", "ViewDTFOV = 50", "", "[Other]", "X = 1" };
        Assert.Equal(["; my notes", "[TableOverride]", "ViewCabMode = 2", "ViewCabScaleX = 1.2", "Difficulty = 0.5", "ViewDTFOV = 50", "", "[Other]", "X = 1"],
            Pov.Replace(lines, "cabinet", ["ViewCabMode = 2", "ViewCabScaleX = 1.2"]));
        Assert.Equal(["[Player]", "BGSet = 1", "", "[TableOverride]", "ViewCabMode = 2"], Pov.Replace(["[Player]", "BGSet = 1"], "cabinet", ["ViewCabMode = 2"]));
        // undo to the table's own view: the mode's lines removed, nothing added
        Assert.Equal(["[TableOverride]", "Difficulty = 0.5"], Pov.Replace(["[TableOverride]", "ViewCabFOV = 45", "Difficulty = 0.5"], "cabinet", []));
    }

    [Fact]
    public void A_vpin_writes_only_view_settings_of_its_own_mode()
    {
        Assert.Equal(["ViewCabMode = 2"], Pov.Validate("ViewCabMode = 2\n", "cabinet"));
        Assert.Empty(Pov.Validate("", "cabinet")!);
        Assert.Null(Pov.Validate("ViewCabMode = 2\nDifficulty = 1", "cabinet")); // not a view setting: the whole install is refused
        Assert.Null(Pov.Validate("ViewDTFOV = 50", "cabinet"));                 // another mode's
        Assert.Null(Pov.Validate("ViewCabMode = 2; del C:", "cabinet"));         // not a number
        Assert.Null(Pov.Validate("ViewCab Mode = 2", "cabinet"));
    }

    [Fact]
    public void Install_backs_up_the_ini_keeps_its_encoding_and_reports_before_and_after()
    {
        var dir = Directory.CreateTempSubdirectory("pinbot-pov");
        try
        {
            var vpx = Path.Combine(dir.FullName, "Table.vpx");
            File.WriteAllText(vpx, "");
            var backups = Path.Combine(dir.FullName, "backups");
            Assert.Equal((null, null), Pov.Install(vpx, "cabinet", "", backups)); // own view, no .ini: nothing written
            Assert.False(File.Exists(Pov.PathFor(vpx)));

            File.WriteAllBytes(Pov.PathFor(vpx), [0xEF, 0xBB, 0xBF, .. "[TableOverride]\r\nViewCabFOV = 45\r\nDifficulty = 1\r\n"u8.ToArray()]);
            var (before, after) = Pov.Install(vpx, "cabinet", "ViewCabMode = 2", backups);
            Assert.Equal("ViewCabFOV = 45", before);
            Assert.Equal("ViewCabMode = 2", after);
            var bytes = File.ReadAllBytes(Pov.PathFor(vpx));
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            Assert.Equal("[TableOverride]\r\nViewCabMode = 2\r\nDifficulty = 1\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
            Assert.Single(Directory.GetFiles(backups));
            Assert.Throws<InvalidDataException>(() => Pov.Install(vpx, "cabinet", "Difficulty = 5", backups));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_table_without_an_ini_still_says_its_mode()
    {
        var dir = Directory.CreateTempSubdirectory("pinbot-pov");
        try
        {
            var vpx = Path.Combine(dir.FullName, "Table.vpx");
            File.WriteAllText(vpx, "");
            Assert.Equal(new PovInfo("cabinet", null), Pov.Read(vpx, "cabinet"));
            Assert.Null(Pov.Read(vpx, null));
            File.WriteAllText(Pov.PathFor(vpx), Ini);
            Assert.Equal("cabinet", Pov.Read(vpx, "cabinet")!.Mode);
            Assert.StartsWith("ViewCabFOV = 45", Pov.Read(vpx, "cabinet")!.Settings);
        }
        finally { dir.Delete(true); }
    }
}
