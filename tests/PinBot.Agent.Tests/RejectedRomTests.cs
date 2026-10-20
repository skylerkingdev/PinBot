using PinBot.Agent.Discovery;
using PinBot.Agent.Sessions;
using PinBot.Agent.Sync;
using PinBot.Contracts;

namespace PinBot.Agent.Tests;

public class RejectedRomTests
{
    static LocalState Rejected(string rom, long release)
    {
        var s = new LocalState();
        s.RejectedRoms[rom] = new RejectedRom(DateTime.UtcNow, release, "7", @"C:\diag");
        return s;
    }

    [Fact]
    public void A_rejection_holds_until_a_newer_release_lists_the_rom_as_fixed()
    {
        var s = Rejected("acd_170h", 20261013000000);
        var fixedRoms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ACD_170H" };

        Assert.True(RejectedRom.Applies(s, "acd_170h", 20261014000000));                  // a newer release without a fix
        Assert.True(RejectedRom.Applies(s, "acd_170h", 20261013000000, fixedRoms));       // the same release that saw it
        Assert.False(RejectedRom.Applies(s, "acd_170h", 20261014000000, fixedRoms));      // a newer release with the fix
        Assert.False(RejectedRom.Applies(s, "afm_113b", 20261014000000));                 // another ROM
        Assert.False(RejectedRom.Applies(s, null, 20261014000000));
    }

    /// <summary>Only a reset on the competition file itself is the ROM rejecting it; a reset of the player's own file is not.</summary>
    [Fact]
    public void A_reset_counts_as_a_rejection_only_when_the_game_ran_on_the_competition_file()
    {
        var ready = new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc);
        var entry = new Nvram.JournalEntry { State = Nvram.CompState.Ready, ReadyAtUtc = ready };

        Assert.True(SessionWatcher.PlayedOnCompetitionFile(entry, true, ready.AddMinutes(1)));
        Assert.False(SessionWatcher.PlayedOnCompetitionFile(entry, true, ready.AddMinutes(-1)));  // started before the swap
        Assert.False(SessionWatcher.PlayedOnCompetitionFile(entry, false, ready.AddMinutes(1)));  // start not seen
        Assert.True(SessionWatcher.PlayedOnCompetitionFile(new() { State = Nvram.CompState.PendingRestore, ReadyAtUtc = ready }, true, ready.AddMinutes(1)));
        Assert.False(SessionWatcher.PlayedOnCompetitionFile(new() { State = Nvram.CompState.BackedUp }, true, ready.AddMinutes(1)));
    }

    [Fact]
    public void A_rejected_rom_is_sent_as_not_eligible_with_the_reason()
    {
        static TableInfo Table(string name, string rom) =>
            new($@"C:\Tables\{name}.vpx", rom, RomSource.Observed, [rom], "", $@"C:\nvram\{rom}.nv", NvState.Resolved, [], true, null);
        var tables = new[] { Table("AC-DC", "acd_170h"), Table("Attack from Mars", "afm_113b") };

        var inv = AgentSync.Inventory(tables, Rejected("acd_170h", 0));

        Assert.Equal(new InventoryTable("AC-DC", "acd_170h", false, CompetitionStates.RomRejected), inv[0]);
        Assert.Equal(new InventoryTable("Attack from Mars", "afm_113b", true, null), inv[1]);
        Assert.True(AgentSync.Inventory(tables).All(t => t.Eligible)); // nothing recorded: unchanged
    }
}
