using PinBot.Agent.Discovery;

namespace PinBot.Agent.Tests;

public class ScriptFixTests
{
    static string Script(int lines, Func<int, string>? line = null) =>
        string.Join("\r\n", Enumerable.Range(1, lines).Select(i => line?.Invoke(i) ?? $"Sub Line{i}() ' line {i}"));

    [Fact]
    public void A_diff_holds_only_the_changed_lines_and_reproduces_the_script_exactly()
    {
        var before = Script(500);
        var lines = ScriptDiff.Lines(before).ToList();
        lines.Insert(100, "    Controller.Run GetPlayerHWnd");
        lines[300] = "Sub Line301() ' fast flips";
        lines.RemoveAt(450);
        lines.Add("' the end");
        var after = string.Join("\r\n", lines);

        var diff = ScriptDiff.Make(before, after)!;
        Assert.Equal(5, ScriptDiff.Changed(diff)); // 1 added, 1 replaced (- and +), 1 removed, 1 added
        Assert.Equal(4, diff.Split('\n').Count(l => l.StartsWith("@@")));
        Assert.True(diff.Length < 1500); // a few lines of context, never the script
        Assert.Equal(after, ScriptDiff.Apply(before, diff));
        Assert.Equal(TableMetadata.HashScript(after), TableMetadata.HashScript(ScriptDiff.Apply(before.Replace("\r\n", "\n"), diff)));
        Assert.Null(ScriptDiff.Make(before, before));
    }

    [Fact]
    public void A_diff_does_not_apply_to_any_other_script()
    {
        var before = Script(50);
        var after = before.Replace("Sub Line20()", "Sub Line20Fixed()");
        var diff = ScriptDiff.Make(before, after)!;
        Assert.Null(ScriptDiff.Apply(before.Replace("Sub Line21()", "Sub Line21Other()"), diff)); // a context line differs
        Assert.Null(ScriptDiff.Apply(after, diff));                                               // already applied
        Assert.Null(ScriptDiff.Apply(before, "garbage"));
    }

    [Fact]
    public void Large_rewrites_are_not_fixes()
    {
        Assert.Null(ScriptDiff.Make(Script(1000), Script(1000, i => $"Other{i}")));
    }

    [Fact]
    public void Added_lines_that_reach_outside_the_table_are_flagged()
    {
        Assert.Null(FixFiles.RiskyLine("@@ -1,1 +1,2 @@\n Sub A\n+  Controller.Run GetPlayerHWnd ' CreateObject in a comment is fine\n"));
        Assert.Equal("Set x = CreateObject(\"WScript.Shell\")", FixFiles.RiskyLine("@@ -1,1 +1,2 @@\n Sub A\n+Set x = CreateObject(\"WScript.Shell\")\n"));
        Assert.NotNull(FixFiles.RiskyLine("@@ -1,0 +1,1 @@\n+fso.DeleteFile \"x\"\n"));
        Assert.Null(FixFiles.RiskyLine("@@ -1,1 +1,0 @@\n-Set x = CreateObject(\"WScript.Shell\")\n")); // removing such a line is fine
    }

    [Fact]
    public void A_fix_installs_as_a_sidecar_only_on_its_exact_script_and_undo_restores_the_old_one()
    {
        var home = TestData.TempDir();
        var tables = TestData.TempDir();
        var before = Environment.GetEnvironmentVariable("PINBOT_HOME");
        Environment.SetEnvironmentVariable("PINBOT_HOME", home);
        try
        {
            var vpx = Path.Combine(tables, "24.vpx");
            File.WriteAllText(vpx, "not a real table"); // its own script can't be read: the sidecar is the script
            var sidecar = Path.ChangeExtension(vpx, ".vbs");
            var original = Script(40);
            File.WriteAllText(sidecar, original, System.Text.Encoding.Latin1);
            var fixedScript = original.Replace("Sub Line10()", "Sub Line10() : FastFlips = True");
            var diff = ScriptDiff.Make(original, fixedScript)!;
            var baseHash = TableMetadata.HashScript(original)!;
            var target = TableMetadata.HashScript(fixedScript)!;

            Assert.Throws<InvalidDataException>(() => FixFiles.Install(vpx, 1, diff, new string('0', 64), target)); // not its script
            Assert.Throws<InvalidDataException>(() => FixFiles.Install(vpx, 1, diff, baseHash, new string('0', 64))); // wouldn't match
            Assert.Equal(original, File.ReadAllText(sidecar, System.Text.Encoding.Latin1)); // untouched

            Assert.Equal((baseHash, target), FixFiles.Install(vpx, 7, diff, baseHash, target));
            Assert.Equal(target, TableMetadata.HashScript(TableScript.Read(vpx).Script));
            Assert.Throws<InvalidDataException>(() => FixFiles.Install(vpx, 8, diff, baseHash, target)); // already has it

            Assert.Equal(baseHash, FixFiles.Undo(vpx, 7));
            Assert.Equal(original, File.ReadAllText(sidecar, System.Text.Encoding.Latin1));
            Assert.Throws<InvalidDataException>(() => FixFiles.Undo(vpx, 7)); // only once
        }
        finally { Environment.SetEnvironmentVariable("PINBOT_HOME", before); }
    }
}
