using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PinBot.Contracts;
using PinBot.Server.Discord;
using static PinBot.Server.Tests.DiscordRequests;

namespace PinBot.Server.Tests;

public class InfoTests
{
    /// <summary>Pairs a cabinet the way a member does: /pinbot pair, then the agent redeems the line and reports its tables.</summary>
    static async Task PairFromDiscordAsync(TestServer s, string userId, string nick, string cabinet, params InventoryTable[] tables)
    {
        var reply = (await AskAsync(s, Command("pair", userId: userId, nick: nick)))["content"]!.GetValue<string>();
        var code = Regex.Match(reply, @"/pair/([A-Z0-9]{5}-[A-Z0-9]{5})").Groups[1].Value;
        var paired = await (await s.Client().PostAsJsonAsync($"{ApiV1.Prefix}/pair/redeem", new PairRedeemRequest(code, cabinet))).Content.ReadFromJsonAsync<PairRedeemResponse>();
        (await s.Client(paired!.Credential).PutAsJsonAsync($"{ApiV1.Prefix}/agent/inventory", new InventoryRequest(tables, "test"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_new_cabinet_is_announced_once_with_the_games_it_adds()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("Mine", TestServer.Table("afm_113b", "Attack from Mars")); // paired by hand: never announced
        await PairFromDiscordAsync(s, "222", "Violet | [VW]", "GAMEROOM-PC",
            TestServer.Table("afm_113b", "Attack from Mars"), TestServer.Table("tz_94h", "Twilight Zone"), TestServer.Table("mm_109b", "Medieval Madness"));
        var info = s.Service<InfoPublisher>();
        await info.RunOnceAsync();
        await info.RunOnceAsync();

        // no info channel yet: announced in the one-off channel, once
        var (channel, message) = Assert.Single(s.Discord!.Messages.Values);
        Assert.Equal("chan1", channel);
        Assert.Equal("🎰 **Violet** connected a VPin (**GAMEROOM-PC**): **3** games ready for competitions, **2** of them new here: " +
                     "Medieval Madness, Twilight Zone. See them all with `/pinbot games`.", message["content"]!.GetValue<string>());
        Assert.Empty(message["allowed_mentions"]!["parse"]!.AsArray());
    }

    [Fact]
    public async Task The_info_channel_gets_a_pinned_guide_kept_current()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        Assert.Contains("Only server managers", (await AskAsync(s, Command("channels", [Opt("info", "chanI")], command: "pinbot-admin")))["content"]!.GetValue<string>());
        Assert.Contains("<#chanI>", (await AskAsync(s, Command("channels", [Opt("info", "chanI")], permissions: Manager, command: "pinbot-admin")))["content"]!.GetValue<string>());

        var info = s.Service<InfoPublisher>();
        await info.RunOnceAsync();
        var (guideId, (channel, guide)) = Assert.Single(s.Discord!.Messages);
        Assert.Equal("chanI", channel);
        Assert.Equal([guideId], s.Discord.Pinned);
        Assert.Contains("Weekly competitions are off", FakeDiscord.Body(guide));

        await info.RunOnceAsync(); // unchanged: left alone
        Assert.Empty(s.Discord.Edits);

        // the weekly schedule is part of the guide: setting it edits the same message
        await AskAsync(s, Command("weekly", [Opt("enabled", true), Opt("day", "Monday"), Opt("time", "19:00"), Opt("timezone", "America/New_York")], permissions: Manager, command: "pinbot-admin"));
        await info.RunOnceAsync();
        Assert.Equal([guideId], s.Discord.Edits);
        Assert.Contains("every **Monday at 19:00** (America/New_York)", FakeDiscord.Body(s.Discord.Messages[guideId].Message));

        // /pinbot help shows the same guide privately, pointing at the channel
        var help = await AskAsync(s, Command("help"));
        Assert.True(IsEphemeral(help));
        Assert.Equal("Also pinned in <#chanI>.", help["content"]!.GetValue<string>());
        Assert.Equal("🎯 How PinBot works", help["embeds"]![0]!["title"]!.GetValue<string>());
    }
}
