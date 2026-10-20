using PinBot.Contracts;
using PinBot.Server.Discord;
using static PinBot.Server.Tests.DiscordRequests;

namespace PinBot.Server.Tests;

public class GuestTests
{
    static async Task<(TestServer S, HttpClient Cab, long Comp)> SetupAsync()
    {
        var s = await TestServer.StartWithDiscordAsync();
        var (_, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        await s.SetMembersAsync(("111", "Alex | [SKY]"), ("222", "JB | [JBJ]"));
        var comp = await s.StartCompetitionAsync("afm_113b");
        s.Clock.Advance(TimeSpan.FromHours(1));
        return (s, cab, comp.Id);
    }

    static Task<System.Text.Json.Nodes.JsonObject> Guest(TestServer s, string initials, string user = "222", string nick = "JB | [JBJ]", string? name = null) =>
        AskAsync(s, Command("guest", name is null ? [Opt("initials", initials)] : [Opt("initials", initials), Opt("name", name)], userId: user, nick: nick));

    /// <summary>A guest without Discord scores with their own initials, credited after the fact too; the host is pinged.</summary>
    [Fact]
    public async Task A_guest_scores_with_their_own_initials_and_the_host_hears_about_it()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        Assert.Equal(ResultStatus.Unattributed, Assert.Single(await TestServer.SubmitAsync(cab, s.Event(comp, "ABC", 5_000_000))).Status);

        await s.Service<LeaderboardPublisher>().RunOnceAsync(); // the card exists before the guest counts
        var reply = await Guest(s, "abc"); // the guest played first; JB adds them afterwards
        Assert.True(IsEphemeral(reply));
        Assert.Contains("count for **JB's guest ABC**", reply["content"]!.GetValue<string>());
        Assert.Contains("1 earlier score now counts", reply["content"]!.GetValue<string>());

        await using var conn = new Npgsql.NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        var top = Assert.Single(await Competitions.LeaderboardAsync(conn, comp));
        Assert.Equal(("guest:222:ABC", "JB's guest ABC"), (top.DiscordUserId, top.DisplayName));

        await s.Service<LeaderboardPublisher>().RunOnceAsync();
        var lead = Assert.Single(s.Discord!.Messages.Values, m => m.Message["embeds"] is null).Message;
        Assert.StartsWith("👑 <@222>'s guest **ABC** takes the lead", lead["content"]!.GetValue<string>());
        Assert.Equal("222", lead["allowed_mentions"]!["users"]![0]!.GetValue<string>());

        // a named guest; the 24 hours run out and later games no longer count
        Assert.Contains("**Alex (JB's guest ABC)**", (await Guest(s, "ABC", name: "Alex"))["content"]!.GetValue<string>());
        s.Clock.Advance(TimeSpan.FromHours(25));
        var late = await s.StartCompetitionAsync("afm_113b"); // the first one has ended
        s.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ResultStatus.Unattributed, Assert.Single(await TestServer.SubmitAsync(cab, s.Event(late.Id, "ABC", 10))).Status);
    }

    [Fact]
    public async Task Guest_initials_never_clash_with_a_member_or_another_hosts_guest()
    {
        var (s, _, _) = await SetupAsync();
        await using var _ = s;
        Assert.Contains("belong to **Alex**", (await Guest(s, "SKY"))["content"]!.GetValue<string>());
        Assert.Contains("your own initials", (await Guest(s, "JBJ"))["content"]!.GetValue<string>());
        await Guest(s, "ABC");
        Assert.Contains("in use by **JB's guest ABC**", (await Guest(s, "ABC", user: "111", nick: "Alex | [SKY]"))["content"]!.GetValue<string>());
        Assert.Contains("Until", (await Guest(s, "ABC"))["content"]!.GetValue<string>()); // the same host extends
    }

    /// <summary>Initials two members use count for nobody: the nickname sync tells both right away, once; others hear nothing.</summary>
    [Fact]
    public async Task Members_sharing_initials_are_told_privately_as_soon_as_the_sync_sees_it()
    {
        var (s, _, _) = await SetupAsync();
        await using var _ = s;
        s.Discord!.Members = [new("111", "Alex | [SKY,DUP]", false), new("222", "JB | [JBJ]", false), new("333", "Dan | [DUP]", false)];
        var sync = s.Service<MemberSync>();
        await sync.RunOnceAsync();
        await sync.RunOnceAsync(); // still clashing: not told again
        Assert.StartsWith("⚠️ Your tag **[DUP]** is also used by **Dan**", Assert.Single(s.Discord.DirectMessages("111")));
        Assert.Contains("is also used by **Alex**", Assert.Single(s.Discord.DirectMessages("333")));
        Assert.Empty(s.Discord.DirectMessages("222"));
    }
}

public class AdminCommandTests
{
    [Fact]
    public void Manager_settings_are_a_separate_command_Discord_shows_only_to_managers()
    {
        var commands = Interactions.CommandDefinitions();
        var admin = commands.Single(c => c!["name"]!.GetValue<string>() == "pinbot-admin")!;
        Assert.Equal("32", admin["default_member_permissions"]!.GetValue<string>()); // Manage Server
        var member = commands.Single(c => c!["name"]!.GetValue<string>() == "pinbot")!["options"]!.AsArray().Select(o => o!["name"]!.GetValue<string>());
        Assert.DoesNotContain("weekly", member);
        Assert.DoesNotContain("info-channel", member);
    }

    /// <summary>Channels are set from Discord, apply to new competitions at once, and survive a restart.</summary>
    [Fact]
    public async Task Channels_are_set_from_Discord_and_kept()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        Assert.Contains("Only server managers", (await AskAsync(s, Command("channels", [Opt("challenges", "chanNew")], command: "pinbot-admin")))["content"]!.GetValue<string>());

        var saved = await AskAsync(s, Command("channels", [Opt("challenges", "chanNew"), Opt("info", "chanI")], permissions: Manager, command: "pinbot-admin"));
        Assert.Contains("• Challenges: <#chanNew>", saved["content"]!.GetValue<string>());
        Assert.Contains("• Guide and new VPins: <#chanI>", saved["content"]!.GetValue<string>());

        await AskAsync(s, Command("start", [Opt("game", "afm_113b")]));
        await s.Service<LeaderboardPublisher>().RunOnceAsync();
        Assert.Equal("chanNew", Assert.Single(s.Discord!.Messages.Values, m => m.Message["embeds"] is not null && m.Channel != "chanI").Channel);

        var fresh = DiscordOptions.From(s.Service<Microsoft.Extensions.Configuration.IConfiguration>()); // what a restarted server starts from
        Assert.Equal("chan1", fresh.ChannelId);
        await using var conn = new Npgsql.NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        await Settings.ApplyChannelsAsync(conn, fresh);
        Assert.Equal("chanNew", fresh.ChannelId);
    }
}
