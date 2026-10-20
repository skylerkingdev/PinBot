using PinBot.Agent.Sessions;
using PinBot.Agent.Sync;
using PinBot.Contracts;

namespace PinBot.Agent.Tests;

/// <summary>Edge cases found in review: re-pairing, NVRAM files PinBot changed mid-game, relative table paths.</summary>
public class HardeningTests
{
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
