using PinBot.Agent.Sessions;
using PinBot.Agent.Sync;
using PinBot.Contracts;

namespace PinBot.Agent.Tests;

/// <summary>Edge cases found in review: re-pairing, NVRAM files PinBot changed mid-game, relative table paths.</summary>
public class HardeningTests
{
    /// <summary>A console window with text selected in it: every write waits until the selection ends.</summary>
    sealed class PausedConsole : TextWriter
    {
        public readonly ManualResetEventSlim Resume = new();
        public readonly System.Text.StringBuilder Seen = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) { Resume.Wait(); lock (Seen) Seen.Append(value); }
        public override void Write(string? value) { Resume.Wait(); lock (Seen) Seen.Append(value); }
        public override void WriteLine(string? value) => Write(value + "\n");
    }

    [Fact]
    public async Task A_paused_console_window_never_holds_up_the_watcher()
    {
        // on the cabinet, text left selected in PinBot's window froze it at "VPX start": the whole game went unseen
        Log.ConsoleInBackground();
        var console = new PausedConsole();
        var before = Console.Out;
        Console.SetOut(console);
        try
        {
            var logged = Task.Run(() => Log.Info("[s-test] VPX start"));
            Assert.Same(logged, await Task.WhenAny(logged, Task.Delay(TimeSpan.FromSeconds(5)))); // else it waited for the window
            console.Resume.Set();                           // the selection ends: the window catches up
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!console.Seen.ToString().Contains("[s-test] VPX start") && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Contains("[s-test] VPX start", console.Seen.ToString());
        }
        finally
        {
            console.Resume.Set();
            Console.SetOut(before);
        }
    }

    [Fact]
    public void Queued_results_are_set_aside_not_lost()
    {
        var root = TestData.TempDir();
        var outbox = new Outbox(Path.Combine(root, "outbox"));
        Assert.Null(outbox.SetAside("unsent-cab-1")); // nothing queued: nothing to move

        var ev = new ResultEvent(Guid.NewGuid(), 12, "s-1", "afm_113b", "SKY", 1_000, DateTime.UtcNow, "test");
        outbox.Enqueue(ev);
        var aside = outbox.SetAside("unsent-cab-1");

        Assert.NotNull(aside);
        Assert.StartsWith(Path.Combine(root, "unsent-cab-1-"), aside);
        Assert.Empty(outbox.Pending());                                  // never sent to the new server
        Assert.True(File.Exists(Path.Combine(aside, ev.EventId + ".json"))); // but kept
    }

    [Fact]
    public void A_report_never_contains_the_server_credential()
    {
        var home = TestData.TempDir();
        var desk = TestData.TempDir();
        var before = Environment.GetEnvironmentVariable("PINBOT_HOME");
        Environment.SetEnvironmentVariable("PINBOT_HOME", home);
        try
        {
            var link = ServerLink.Create("https://pinbot.example.ts.net", "cab-1", "the-secret-credential");
            LocalState.Update(s => s.Server = link);
            var opts = new Dictionary<string, List<string>> { ["out"] = [desk] };
            Assert.Equal(0, new Commands(TestData.Maps, opts).Report());

            var zip = Assert.Single(Directory.GetFiles(desk, "PinBot-report-*.zip"));
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var state = new StreamReader(archive.GetEntry("state.json")!.Open()).ReadToEnd();
            Assert.Contains("(removed)", state);
            Assert.DoesNotContain(link.ProtectedCredential, state);
            Assert.DoesNotContain("the-secret-credential", state);
            Assert.Equal("the-secret-credential", LocalState.Load().Server!.Credential()); // the PC's own copy is untouched
        }
        finally { Environment.SetEnvironmentVariable("PINBOT_HOME", before); }
    }

    [Theory]
    [InlineData(20261021004511L, "21 Oct 2026, 00:45 UTC")]
    [InlineData(0L, "unknown date")]
    public void Releases_are_shown_with_their_date(long release, string shown) => Assert.Equal(shown, Commands.ReleaseDate(release));

    [Fact]
    public void Files_PinBot_replaced_during_a_game_are_not_the_games()
    {
        var changed = new[] { @"C:\nv\afm_113b.nv", @"C:\nv\tz_94h.nv" };
        var replaced = new HashSet<string>([@"C:\NV\TZ_94H.NV"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal([@"C:\nv\afm_113b.nv"], SessionWatcher.ChangedByGame(changed, replaced));
        Assert.Equal(changed, SessionWatcher.ChangedByGame(changed, new HashSet<string>()));
    }

    [Fact]
    public void Relative_table_paths_are_resolved()
    {
        var vpx = TestData.TempDir();
        var tables = TestData.TempDir();
        Directory.CreateDirectory(Path.Combine(vpx, "Tables"));
        var inVpx = Path.Combine(vpx, "Tables", "AFM.vpx");
        File.WriteAllText(inVpx, "");
        var inTables = Path.Combine(tables, "TZ.vpx");
        File.WriteAllText(inTables, "");

        Assert.Equal(inVpx, SessionWatcher.ResolveTable(@"Tables\AFM.vpx", [vpx, tables], []));  // VPX's own folder
        Assert.Equal(inTables, SessionWatcher.ResolveTable("TZ.vpx", [vpx, tables], []));         // a table folder
        Assert.Equal(@"D:\X\MM.vpx", SessionWatcher.ResolveTable(@"..\MM.vpx", [vpx], [@"D:\X\MM.vpx"])); // unique known name
        Assert.Equal(@"..\MM.vpx", SessionWatcher.ResolveTable(@"..\MM.vpx", [vpx], [@"D:\X\MM.vpx", @"E:\Y\MM.vpx"])); // ambiguous
        Assert.Equal(@"C:\T\A.vpx", SessionWatcher.ResolveTable(@"C:\T\A.vpx", [vpx], []));       // already full
        Assert.Null(SessionWatcher.ResolveTable(null, [vpx], []));
    }
}
