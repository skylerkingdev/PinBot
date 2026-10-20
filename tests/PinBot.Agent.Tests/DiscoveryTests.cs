using System.Text;
using OpenMcdf;
using PinBot.Agent.Discovery;
using PinBot.Agent.Scoring;

namespace PinBot.Agent.Tests;

public class TableScriptTests
{
    [Fact]
    public void Commented_rom_is_ignored_and_active_one_found()
    {
        // pattern seen in a real Avengers (Stern 2012) script
        var script = "'Const cGameName = \"avs_170\"\r\nConst cGameName = \"avs_170c\" 'Colour Rom\r\n";
        Assert.Equal(["avs_170c"], TableScript.FindRoms(script));
    }

    [Fact]
    public void Multi_constant_line_is_parsed()
    {
        // pattern seen in a real Flash (Williams 1979) script
        Assert.Equal(["flash_l1"], TableScript.FindRoms("Const cGameName=\"flash_l1\",UseSolenoids=2,UseLamps=0,UseGI=1"));
    }

    [Fact]
    public void Literal_controller_gamename_is_found_but_variable_reference_is_not()
    {
        Assert.Equal(["tz_92"], TableScript.FindRoms(".GameName = \"tz_92\""));
        Assert.Empty(TableScript.FindRoms("Controller.GameName = cGameName"));
    }

    [Fact]
    public void Script_choosing_between_roms_is_ambiguous()
    {
        var roms = TableScript.FindRoms("If FreePlay Then\n cGameName = \"mm_109c\"\nElse\n cGameName = \"mm_109\"\nEnd If");
        Assert.Equal(["mm_109c", "mm_109"], roms);
    }

    [Fact]
    public void Rem_comments_and_apostrophes_inside_strings_are_handled()
    {
        Assert.Empty(TableScript.FindRoms("Rem cGameName = \"x_1\""));
        Assert.Equal(["q_1"], TableScript.FindRoms("msg = \"don't\" : cGameName = \"q_1\""));
    }

    [Theory]
    [InlineData("NVOffset (2)", 2)]
    [InlineData("Controller.NVOffset 3", 3)]
    [InlineData("'NVOffset (2)", 0)]
    [InlineData("Const cGameName = \"afm_113b\"", 0)]
    public void Nvoffset_is_found_in_code_only(string script, int expected) =>
        Assert.Equal(expected, TableScript.FindNvOffset(script));

    [Fact]
    public void Controller_stop_is_detected_outside_comments()
    {
        Assert.True(TableScript.CallsControllerStop("Sub Table1_Exit : Controller.Stop : End Sub"));
        Assert.False(TableScript.CallsControllerStop("Sub Table1_Exit\n ' Controller.Stop\nEnd Sub"));
    }

    static byte[] GameData(string script)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Record(string tag, byte[] payload) { w.Write(4 + payload.Length); w.Write(Encoding.ASCII.GetBytes(tag)); w.Write(payload); }
        Record("NAME", Encoding.Unicode.GetBytes("Test Table"));
        Record("WDTH", BitConverter.GetBytes(952f));
        var code = Encoding.Latin1.GetBytes(script);
        w.Write(4); w.Write(Encoding.ASCII.GetBytes("CODE")); w.Write(code.Length); w.Write(code);
        Record("ENDB", []);
        return ms.ToArray();
    }

    [Fact]
    public void Code_record_is_extracted_from_biff_stream()
    {
        Assert.Equal("Const cGameName = \"afm_113b\"", TableScript.ExtractCode(GameData("Const cGameName = \"afm_113b\"")));
        Assert.Null(TableScript.ExtractCode([1, 2, 3]));
    }

    [Fact]
    public void Script_is_read_from_vpx_compound_file_and_sidecar_wins()
    {
        var dir = TestData.TempDir();
        var vpx = Path.Combine(dir, "AFM.vpx");
        using (var root = RootStorage.Create(vpx))
        {
            var gameStg = root.CreateStorage("GameStg");
            using var s = gameStg.CreateStream("GameData");
            s.Write(GameData("Const cGameName = \"afm_113b\""));
        }
        var (script, source) = TableScript.Read(vpx);
        Assert.Equal(".vpx embedded", source);
        Assert.Equal(["afm_113b"], TableScript.FindRoms(script!));

        File.WriteAllText(Path.ChangeExtension(vpx, ".vbs"), "Const cGameName = \"afm_113\"");
        (script, source) = TableScript.Read(vpx);
        Assert.Equal("sidecar .vbs", source);
        Assert.Equal(["afm_113"], TableScript.FindRoms(script!));
    }

    [Fact]
    public void Non_compound_file_is_reported_unreadable()
    {
        var vpx = Path.Combine(TestData.TempDir(), "broken.vpx");
        File.WriteAllText(vpx, "not a vpx");
        var (script, source) = TableScript.Read(vpx);
        Assert.Null(script);
        Assert.StartsWith(".vpx unreadable", source);
    }
}

public class NvramLocationTests
{
    static (string Table, string TableDir) Table()
    {
        var dir = TestData.TempDir();
        var vpx = Path.Combine(dir, "AFM.vpx");
        File.WriteAllText(vpx, "");
        return (vpx, dir);
    }

    static Installations Inst(params string[] nvramDirs) => new([], [], nvramDirs, []);

    [Fact]
    public void Single_existing_file_resolves()
    {
        var (vpx, _) = Table();
        var global = TestData.TempDir();
        File.WriteAllText(Path.Combine(global, "afm_113b.nv"), "");
        var (path, state, _) = Inventory.ResolveNv("afm_113b", vpx, Inst(global));
        Assert.Equal(NvState.Resolved, state);
        Assert.Equal(Path.Combine(global, "afm_113b.nv"), path);
    }

    [Fact]
    public void Same_rom_in_table_folder_and_global_folder_is_ambiguous()
    {
        var (vpx, tableDir) = Table();
        var local = Directory.CreateDirectory(Path.Combine(tableDir, "pinmame", "nvram")).FullName;
        var global = TestData.TempDir();
        File.WriteAllText(Path.Combine(local, "afm_113b.nv"), "");
        File.WriteAllText(Path.Combine(global, "afm_113b.nv"), "");
        var (path, state, candidates) = Inventory.ResolveNv("afm_113b", vpx, Inst(global));
        Assert.Equal(NvState.Ambiguous, state);
        Assert.Null(path);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Never_played_rom_with_one_folder_is_not_created_yet()
    {
        var (vpx, _) = Table();
        var global = TestData.TempDir();
        var (path, state, _) = Inventory.ResolveNv("afm_113b", vpx, Inst(global));
        Assert.Equal(NvState.NotCreatedYet, state);
        Assert.Equal(Path.Combine(global, "afm_113b.nv"), path);
    }

    [Fact]
    public void Vpmalias_lines_map_every_alias_to_the_last_rom()
    {
        var a = Installations.ParseAliases(["; comment", "acd_170hcPUP,acd_170hc", "mm1,mm2,mm_109c", "", "bad"]);
        Assert.Equal("acd_170hc", a["ACD_170HCPUP"]);
        Assert.Equal("mm_109c", a["mm1"]);
        Assert.Equal("mm_109c", a["mm2"]);
        Assert.Equal(3, a.Count);
    }

    [Fact]
    public void Aliased_script_rom_resolves_to_real_rom_nvram()
    {
        var (vpx, _) = Table();
        File.WriteAllText(Path.ChangeExtension(vpx, ".vbs"), "Const cGameName = \"afm_PUP\"");
        var global = TestData.TempDir();
        File.Copy(Path.Combine(TestData.FixtureDir, "afm_113b.nv"), Path.Combine(global, "afm_113b.nv"));
        var inst = new Installations([], [], [global], [], new Dictionary<string, string> { ["afm_PUP"] = "afm_113b" });

        var t = Inventory.Describe(vpx, inst, TestData.Maps, new LocalState());
        Assert.Equal("afm_113b", t.Rom);
        Assert.Equal(NvState.Resolved, t.NvState);
        Assert.True(t.Eligible, t.Problem);
        Assert.Contains("alias afm_PUP -> afm_113b", t.ScriptNote);
    }

    [Fact]
    public void Nvoffset_table_uses_its_own_nvram_file()
    {
        var (vpx, _) = Table();
        File.WriteAllText(Path.ChangeExtension(vpx, ".vbs"), "Const cGameName = \"afm_113b\"\r\nNVOffset (2)");
        var global = TestData.TempDir();
        File.Copy(Path.Combine(TestData.FixtureDir, "afm_113b.nv"), Path.Combine(global, "afm_113b.nv"));
        File.Copy(Path.Combine(TestData.FixtureDir, "afm_113b.nv"), Path.Combine(global, "afm_113b v2.nv"));

        var t = Inventory.Describe(vpx, Inst(global), TestData.Maps, new LocalState());
        Assert.Equal(Path.Combine(global, "afm_113b v2.nv"), t.NvPath);
        Assert.Equal(NvState.Resolved, t.NvState);
        Assert.Equal("afm_113b", Scoring.HighScoreReader.RomFromFileName(t.NvPath!));
    }

    [Fact]
    public void Script_rom_without_any_nvram_file_needs_one_play_before_eligible()
    {
        // e.g. an EM table whose cGameName happens to match a real ROM but never runs PinMAME
        var (vpx, _) = Table();
        File.WriteAllText(Path.ChangeExtension(vpx, ".vbs"), "Const cGameName = \"afm_113b\"");
        var t = Inventory.Describe(vpx, Inst(TestData.TempDir()), TestData.Maps, new LocalState());
        Assert.True(t.Supported);
        Assert.Equal(NvState.NotCreatedYet, t.NvState);
        Assert.False(t.Eligible);
        Assert.Contains("play once", t.Problem);
    }

    [Fact]
    public void Rom_without_initials_is_listed_but_not_eligible()
    {
        // Xenon (Bally 1980) stores a single high score with no initials
        var (vpx, _) = Table();
        File.WriteAllText(Path.ChangeExtension(vpx, ".vbs"), "Const cGameName = \"xenon\"");
        var global = TestData.TempDir();
        File.Copy(Path.Combine(TestData.FixtureDir, "xenon.nv"), Path.Combine(global, "xenon.nv"));
        var t = Inventory.Describe(vpx, Inst(global), TestData.Maps, new LocalState());
        Assert.Equal(NvState.Resolved, t.NvState);
        Assert.False(t.Eligible);
        Assert.Contains("no initials", t.Problem);
        Assert.True(HighScoreReader.Extract(TestData.Maps, "xenon", TestData.Nv("xenon")).Ok); // still readable
    }

    [Fact]
    public void No_nvram_folder_is_explicit()
    {
        var (vpx, _) = Table();
        Assert.Equal(NvState.NoNvramFolder, Inventory.ResolveNv("afm_113b", vpx, Inst()).State);
    }
}
