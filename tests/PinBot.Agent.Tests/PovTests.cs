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
