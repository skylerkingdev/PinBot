using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PinBot.Contracts;
using PinBot.Server.Discord;
using static PinBot.Server.Tests.DiscordRequests;

namespace PinBot.Server.Tests;

public class DiscordInteractionTests
{
    static async Task<TestServer> SetupAsync()
    {
        var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars (Bally 1995)"), TestServer.Table("tz_94h", "Twilight Zone (Bally 1993)"));
        return s;
    }

    [Fact]
    public async Task Ping_is_answered_and_unsigned_tampered_or_stale_requests_are_refused()
    {
        await using var s = await SetupAsync();
        var ping = new JsonObject { ["type"] = 1 };
        var ok = await SendAsync(s, ping);
        Assert.Equal(1, (await ok.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(s, Command("status"), tamper: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(s, Command("status"), signedAt: s.Clock.UtcNow.AddMinutes(-10))).StatusCode);
        var unsigned = await s.Client().PostAsync("/api/v1/discord/interactions", new StringContent("{\"type\":1}"));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var other = await AskAsync(s, Command("status", guild: "someone-elses-server"));
        Assert.Contains("not set up", other["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Any_member_starts_a_competition_and_the_leaderboard_is_posted_once()
    {
        await using var s = await SetupAsync();
        var reply = await AskAsync(s, Command("start", [Opt("game", "afm_113b"), Opt("length", 72)], userId: "u7", nick: "Avery | [AVK]"));
        Assert.False(IsEphemeral(reply)); // started from another channel: announced there
        Assert.Contains("**Avery** started **Attack from Mars (Bally 1995)**", reply["content"]!.GetValue<string>());

        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync();
        var (channel, message) = Assert.Single(s.Discord!.Messages.Values);
        Assert.Equal("chan1", channel);
        Assert.Contains("Attack from Mars", message["embeds"]![0]!["title"]!.GetValue<string>());
        Assert.Contains("No scores yet", FakeDiscord.Body(message));
        Assert.Empty(message["allowed_mentions"]!["parse"]!.AsArray());

        await publisher.RunOnceAsync(); // nothing changed: no edit
        Assert.Empty(s.Discord.Edits);

        var clash = await AskAsync(s, Command("start", [Opt("game", "afm_113b")], userId: "u8"));
        Assert.True(IsEphemeral(clash));
        Assert.Contains("already has competition", clash["content"]!.GetValue<string>());
    }

    /// <summary>10 minutes before the deadline: one reminder that games must be finished by then, without mentions.</summary>
    [Fact]
    public async Task A_last_call_is_posted_once_ten_minutes_before_the_end()
    {
        await using var s = await SetupAsync();
        await AskAsync(s, Command("start", [Opt("game", "afm_113b"), Opt("length", 1)]));
        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync();
        Assert.Empty(s.Discord!.Notices);

        s.Clock.Advance(TimeSpan.FromMinutes(51)); // 9 minutes left
        await publisher.RunOnceAsync();
        await publisher.RunOnceAsync();
        var call = Assert.Single(s.Discord.Notices);
        Assert.StartsWith("⏰ **Attack from Mars (Bally 1995)** ends <t:", call);
        Assert.Contains("A game counts if you've exited the table by", call);
        Assert.Contains("No scores yet.", call);
        Assert.Empty(s.Discord.Messages.Values.Single(m => m.Message["embeds"] is null).Message["allowed_mentions"]!["parse"]!.AsArray());

        // a competition shorter than 30 minutes gets none
        await s.StartCompetitionAsync("tz_94h", hours: 0.25);
        s.Clock.Advance(TimeSpan.FromMinutes(6));
        await publisher.RunOnceAsync();
        Assert.DoesNotContain(s.Discord.Notices, n => n.Contains("Twilight Zone"));
    }

    /// <summary>Started in the leaderboard channel, the card's 📣 headline is the one notification members get.</summary>
    [Fact]
    public async Task A_start_in_the_leaderboard_channel_is_confirmed_only_to_the_starter()
    {
        await using var s = await SetupAsync();
        var start = Command("start", [Opt("game", "afm_113b")]);
        start["channel_id"] = "chan1";
        var reply = await AskAsync(s, start);
        Assert.True(IsEphemeral(reply));
        Assert.Contains("started **Attack from Mars", reply["content"]!.GetValue<string>());

        await s.Service<LeaderboardPublisher>().RunOnceAsync();
        Assert.Contains("📣 New challenge", Assert.Single(s.Discord!.Messages.Values).Message["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task One_member_cannot_flood_competitions()
    {
        await using var s = await SetupAsync();
        await s.PairCabinetAsync("Second", TestServer.Table("afm_113b", "Attack from Mars (Bally 1995)"),
            TestServer.Table("tz_94h", "Twilight Zone (Bally 1993)"), TestServer.Table("mm_109b", "Medieval Madness"));
        Assert.False(IsEphemeral(await AskAsync(s, Command("start", [Opt("game", "afm_113b")]))));
        Assert.False(IsEphemeral(await AskAsync(s, Command("start", [Opt("game", "tz_94h")]))));
        var third = await AskAsync(s, Command("start", [Opt("game", "mm_109b")]));
        Assert.True(IsEphemeral(third));
        Assert.Contains("already have 2", third["content"]!.GetValue<string>());

        // the same member as a server manager is not limited (Medieval Madness is on one cabinet: confirmed first)
        var asManager = await AskAsync(s, Command("start", [Opt("game", "mm_109b")], permissions: Manager));
        Assert.Contains("Only **Second** has **Medieval Madness**", asManager["content"]!.GetValue<string>());
        var started = await PressAsync(s, "start:mm_109b:72", permissions: Manager);
        Assert.Contains("started **Medieval Madness**", started["content"]!.GetValue<string>());
        Assert.Empty(started["components"]!.AsArray());
    }

    [Fact]
    public async Task A_game_only_one_cabinet_has_is_confirmed_before_it_starts()
    {
        await using var s = await SetupAsync();
        await s.PairCabinetAsync("Second", TestServer.Table("afm_113b", "Attack from Mars (Bally 1995)"));
        var ask = await AskAsync(s, Command("start", [Opt("game", "tz_94h")]));
        Assert.True(IsEphemeral(ask));
        Assert.Contains("Start anyway?", ask["content"]!.GetValue<string>());
        Assert.Equal("Not started.", (await PressAsync(s, "start:cancel"))["content"]!.GetValue<string>());
        Assert.Equal(0, await s.CountAsync("SELECT count(*) FROM competition"));
        Assert.False(IsEphemeral(await AskAsync(s, Command("start", [Opt("game", "afm_113b")])))); // on both: no question
    }

    static async Task<JsonObject> PressAsync(TestServer s, string customId, ulong permissions = 0)
    {
        var press = Command("start", permissions: permissions);
        press["type"] = 3;
        press["data"] = new JsonObject { ["custom_id"] = customId };
        var r = await SendAsync(s, press);
        r.EnsureSuccessStatusCode();
        var json = (await r.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(7, json["type"]!.GetValue<int>()); // the answer replaces the question
        return json["data"]!.AsObject();
    }

    [Fact]
    public async Task Game_search_lists_eligible_games_and_marks_running_ones()
    {
        await using var s = await SetupAsync();
        await AskAsync(s, Command("start", [Opt("game", "tz_94h")]));
        var all = await AskAsync(s, Command("start", [Opt("game", "", focused: true)], type: 4));
        var names = all["choices"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Attack from Mars (Bally 1995)", "Twilight Zone (Bally 1993) (already running)"], names);

        var filtered = await AskAsync(s, Command("start", [Opt("game", "mars", focused: true)], type: 4));
        Assert.Equal("afm_113b", Assert.Single(filtered["choices"]!.AsArray())!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_creator_or_a_manager_can_end_and_the_leaderboard_follows()
    {
        await using var s = await SetupAsync();
        await AskAsync(s, Command("start", [Opt("game", "afm_113b")], userId: "u1"));
        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync();

        var denied = await AskAsync(s, Command("end", [Opt("competition", 1)], userId: "u2", nick: "Bob [BOB]"));
        Assert.True(IsEphemeral(denied));
        var byManager = await AskAsync(s, Command("end", [Opt("competition", 1)], userId: "u3", nick: "Mod", permissions: Manager));
        Assert.Contains("ended **Attack from Mars (Bally 1995)** early", byManager["content"]!.GetValue<string>());

        await publisher.RunOnceAsync(); // ended: games already finished can still arrive for a few minutes
        var message = Assert.Single(s.Discord!.Cards);
        Assert.Contains("Final standings in a few minutes", FakeDiscord.Body(message));
        Assert.Single(s.Discord.Edits);
        Assert.Empty(s.Discord.Notices);

        s.Clock.Advance(TimeSpan.FromMinutes(5)); // final: the card says so and the end message is posted, once
        await publisher.RunOnceAsync();
        Assert.Contains("Final standings", FakeDiscord.Body(Assert.Single(s.Discord.Cards)));
        Assert.StartsWith("🏁 **Attack from Mars (Bally 1995)** has ended with no scores.", Assert.Single(s.Discord.Notices));
        await publisher.RunOnceAsync();
        Assert.Single(s.Discord.Notices);
        Assert.Contains("is not running", (await AskAsync(s, Command("end", [Opt("competition", 1)], permissions: Manager)))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Standings_are_final_five_minutes_after_the_deadline()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var (_, cab, _) = await s.PairCabinetAsync("A", TestServer.Table("afm_113b", "Attack from Mars"));
        await AskAsync(s, Command("start", [Opt("game", "afm_113b"), Opt("length", 1)], userId: "111"));
        s.Discord!.Members = [new("111", "Alex | [SKY]", false)];
        await s.Service<MemberSync>().RunOnceAsync();
        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync();

        // a game that ended 10 s before the deadline, uploaded 40 s after it: counts
        s.Clock.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(40));
        var lastSecond = s.Event(1, "SKY", 1000, exitedAgo: TimeSpan.FromSeconds(50));
        Assert.Equal(ResultStatus.Accepted, Assert.Single(await TestServer.SubmitAsync(cab, lastSecond)).Status);
        await publisher.RunOnceAsync();
        Assert.DoesNotContain(s.Discord.Notices, n => n.StartsWith("🏁")); // not final yet

        s.Clock.Advance(TimeSpan.FromMinutes(5)); // deadline + 5 min: final, end message with the podium
        await publisher.RunOnceAsync();
        Assert.Contains("These are the final standings.", Assert.Single(s.Discord.Notices, n => n.StartsWith("🏁")));
        var tooLate = s.Event(1, "SKY", 5000, exitedAgo: TimeSpan.FromMinutes(6)); // finished in time, arrived after: no
        Assert.Contains("became final", Assert.Single(await TestServer.SubmitAsync(cab, tooLate)).Reason);
    }

    [Fact]
    public async Task Any_member_gets_a_private_pairing_line_that_pairs_their_cabinet()
    {
        await using var s = await SetupAsync();
        var reply = await AskAsync(s, Command("pair", userId: "777", nick: "Violet | [VW]"));
        Assert.True(IsEphemeral(reply));
        var content = reply["content"]!.GetValue<string>();
        var code = System.Text.RegularExpressions.Regex.Match(content, @"https://pinbot\.example\.ts\.net/pair/([A-Z0-9]{5}-[A-Z0-9]{5})\n").Groups[1].Value;
        Assert.NotEmpty(code);
        Assert.Contains("Double-click it and paste the line above", content);

        var r = await s.Client().PostAsJsonAsync($"{ApiV1.Prefix}/pair/redeem", new PairRedeemRequest(code, "Garage"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM cabinet WHERE name = 'Garage' AND paired_by = '777' AND paired_by_name = 'Violet | [VW]'"));

        // a few codes an hour per member; managers are not limited
        await AskAsync(s, Command("pair", userId: "777"));
        await AskAsync(s, Command("pair", userId: "777"));
        Assert.Contains("Try again later", (await AskAsync(s, Command("pair", userId: "777")))["content"]!.GetValue<string>());
        Assert.Contains("/pair/", (await AskAsync(s, Command("pair", userId: "777", permissions: Manager)))["content"]!.GetValue<string>());
        s.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Contains("/pair/", (await AskAsync(s, Command("pair", userId: "777")))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Short_competitions_run_for_hours()
    {
        await using var s = await SetupAsync();
        await AskAsync(s, Command("start", [Opt("game", "afm_113b"), Opt("length", 2)]));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM competition WHERE ends_at_utc - starts_at_utc = interval '2 hours'"));
        Assert.Contains("Pick a length", (await AskAsync(s, Command("start", [Opt("game", "tz_94h"), Opt("length", 5)])))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_typed_title_works_when_no_entry_was_picked()
    {
        await using var s = await SetupAsync();
        Assert.Contains("started **Twilight Zone", (await AskAsync(s, Command("start", [Opt("game", "twilight zone (bally 1993)")])))["content"]!.GetValue<string>());
        Assert.Contains("started **Attack from Mars", (await AskAsync(s, Command("start", [Opt("game", "mars")], userId: "u2")))["content"]!.GetValue<string>());
        var unknown = (await AskAsync(s, Command("start", [Opt("game", "Zaccaria")], userId: "u3")))["content"]!.GetValue<string>();
        Assert.Contains("No game on a connected VPin matches **Zaccaria**", unknown);
        Assert.Contains("/pinbot games", (await AskAsync(s, Command("start", [Opt("game", "?")], userId: "u3")))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Big_libraries_say_there_are_more_and_games_lists_them_all()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("VPin", Enumerable.Range(1, 30).Select(i => TestServer.Table($"rom{i:00}", $"Game {i:00}")).ToArray());
        var choices = (await AskAsync(s, Command("start", [Opt("game", "", focused: true)], type: 4)))["choices"]!.AsArray();
        Assert.Equal(25, choices.Count);
        Assert.Equal("…6 more: type part of a name to narrow the list", choices[24]!["name"]!.GetValue<string>());
        Assert.Single((await AskAsync(s, Command("start", [Opt("game", "Game 30", focused: true)], type: 4)))["choices"]!.AsArray());

        var games = await AskAsync(s, Command("games"));
        Assert.True(IsEphemeral(games));
        var embed = games["embeds"]![0]!;
        Assert.Equal("30 games", embed["title"]!.GetValue<string>());
        Assert.Contains("• Game 30", embed["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Status_lists_running_competitions_with_leaders()
    {
        await using var s = await SetupAsync();
        Assert.Contains("No competitions running", (await AskAsync(s, Command("status")))["content"]!.GetValue<string>());
        await AskAsync(s, Command("start", [Opt("game", "afm_113b")]));
        var status = (await AskAsync(s, Command("status")))["content"]!.GetValue<string>();
        Assert.Contains("#1 Attack from Mars", status);
        Assert.Contains("no scores yet", status);
    }
}

public class DiscordMembersAndLeaderboardTests
{
    [Fact]
    public async Task Nickname_tags_attribute_scores_and_late_tags_still_count_while_open()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var (_, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        await DiscordRequests.AskAsync(s, DiscordRequests.Command("start", [DiscordRequests.Opt("game", "afm_113b")]));
        s.Discord!.Members = [new("u1", "Alex | [SKY]", false), new("bot1", "VPin Studio [AVK]", true)];
        var sync = s.Service<MemberSync>();
        await sync.RunOnceAsync();

        s.Clock.Advance(TimeSpan.FromHours(1));
        var o = await TestServer.SubmitAsync(cab, s.Event(1, "SKY", 1_033_239_240), s.Event(1, "AVK", 900_000_000));
        Assert.Equal([ResultStatus.Accepted, ResultStatus.Unattributed], o.Select(x => x.Status)); // bots never claim initials

        // Avery adds a tag afterwards: the held score counts at the next sync, the competition still being open
        s.Discord.Members = [.. s.Discord.Members, new("u2", "Avery [AVK]", false)];
        await sync.RunOnceAsync();
        var board = await s.LeaderboardAsync(1);
        Assert.Equal(["u1", "u2"], board.Select(r => r.DiscordUserId));

        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync();
        var body = FakeDiscord.Body(Assert.Single(s.Discord.Messages.Values).Message);
        Assert.Contains("🥇 **Alex**: 1,033,239,240", body);
        Assert.Contains("🥈 **Avery**: 900,000,000", body);
    }

    [Fact]
    public async Task Passed_members_are_mentioned_and_a_new_leader_is_announced()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var (_, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        await DiscordRequests.AskAsync(s, DiscordRequests.Command("start", [DiscordRequests.Opt("game", "afm_113b")]));
        s.Discord!.Members = [new("111", "Alex | [SKY]", false), new("222", "Avery [AVK]", false), new("333", "Bob [BOB]", false)];
        await s.Service<MemberSync>().RunOnceAsync();
        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync(); // card posted, nothing announced
        List<JsonObject> Notices() => s.Discord.Messages.Values.Where(m => m.Message["embeds"] is null).Select(m => m.Message).ToList();
        const string Link = " · [leaderboard](<https://discord.com/channels/guild1/chan1/1000>)"; // every message links to the card
        string Text(JsonObject m) { var t = m["content"]!.GetValue<string>(); Assert.EndsWith(Link, t); return t[..^Link.Length]; }
        string[] Pinged(JsonObject m) => m["allowed_mentions"]!["users"]!.AsArray().Select(u => u!.GetValue<string>()).ToArray();
        Assert.Empty(Notices());

        s.Clock.Advance(TimeSpan.FromMinutes(10));
        await TestServer.SubmitAsync(cab, s.Event(1, "AVK", 500), s.Event(1, "BOB", 300));
        await publisher.RunOnceAsync(); // first scores: Avery leads, Bob enters second; nobody had a score to be passed
        Assert.Equal(["👑 **Avery** takes the lead on **Attack from Mars** with **500**.",
                      "⚡ **Bob** takes **2nd place** on **Attack from Mars** with **300**."], Notices().Select(Text));
        Assert.All(Notices(), n => Assert.Empty(Pinged(n)));

        await TestServer.SubmitAsync(cab, s.Event(1, "SKY", 400)); // passes Bob only
        await publisher.RunOnceAsync();
        var passing = Notices()[2];
        Assert.Equal("⚡ **Alex** takes **2nd place** on **Attack from Mars** with **400**, passing <@333>.", Text(passing));
        Assert.Equal(["333"], Pinged(passing));

        await TestServer.SubmitAsync(cab, s.Event(1, "SKY", 450)); // better, but still second: quiet
        await publisher.RunOnceAsync();
        Assert.Equal(3, Notices().Count);

        await TestServer.SubmitAsync(cab, s.Event(1, "BOB", 900)); // from last to first
        await publisher.RunOnceAsync();
        var lead = Notices()[3];
        Assert.Equal("👑 **Bob** takes the lead on **Attack from Mars** with **900**, passing <@222>, <@111>.", Text(lead));
        Assert.Equal(["222", "111"], Pinged(lead));
        Assert.Empty(lead["allowed_mentions"]!["parse"]!.AsArray()); // never @everyone or roles

        await TestServer.SubmitAsync(cab, s.Event(1, "BOB", 950)); // the leader beats their own top score
        await publisher.RunOnceAsync();
        Assert.Equal("👑 **Bob** raises the top score on **Attack from Mars** to **950**.", Text(Notices()[4]));
        Assert.Empty(Pinged(Notices()[4]));

        await publisher.RunOnceAsync(); // nothing new: nothing repeated
        Assert.Equal(5, Notices().Count);

        s.Clock.Advance(TimeSpan.FromDays(3)); // the 3-day competition ends: podium, top three mentioned
        await publisher.RunOnceAsync();
        var end = Notices()[5];
        Assert.StartsWith("🏁 **Attack from Mars** has ended!\n🥇 <@333> 950\n🥈 <@222> 500\n🥉 <@111> 450\n", Text(end));
        Assert.Equal(["333", "222", "111"], Pinged(end));
    }

    [Fact]
    public void Card_lists_every_participant_and_places_read_naturally()
    {
        var comp = new CompetitionRow { Id = 1, Title = "AFM", Kind = "oneoff", CreatedBy = "1", StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(1) };
        var rows = Enumerable.Range(1, 40).Select(i => new LeaderboardRow(i, i.ToString(), $"Player {i}", 1000 - i, "AAA", "afm", DateTime.UtcNow)).ToList();
        var body = FakeDiscord.Body(LeaderboardPublisher.Render(comp, rows, DateTime.UtcNow, TimeSpan.FromHours(24)));
        Assert.Contains("**Player 40**: 960", body);
        Assert.DoesNotContain("more_", body);
        Assert.Equal(["1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "22nd", "101st"],
            new[] { 1, 2, 3, 4, 11, 12, 13, 21, 22, 101 }.Select(LeaderboardPublisher.Ordinal));
    }

    [Fact]
    public async Task Card_is_pinned_while_running_repinned_after_a_repost_and_unpinned_when_final()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"), TestServer.Table("tz_94h", "Twilight Zone"));
        await DiscordRequests.AskAsync(s, DiscordRequests.Command("start", [DiscordRequests.Opt("game", "afm_113b"), DiscordRequests.Opt("length", 1)]));
        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync();
        var card = Assert.Single(s.Discord!.Messages).Key;
        Assert.Equal([card], s.Discord.Pinned);

        s.Discord.Messages.Clear(); // someone deleted the card: the new one is pinned again
        s.Discord.Pinned.Clear();
        await DiscordRequests.AskAsync(s, DiscordRequests.Command("end", [DiscordRequests.Opt("competition", 1)]));
        await publisher.RunOnceAsync();
        var reposted = Assert.Single(s.Discord.Cards);
        Assert.Equal([s.Discord.Messages.Single(m => m.Value.Message == reposted).Key], s.Discord.Pinned);

        s.Clock.Advance(TimeSpan.FromHours(25)); // final: unpinned
        await publisher.RunOnceAsync();
        Assert.Empty(s.Discord.Pinned);

        s.Discord.RefusePins = true; // no permission in the channel: the card is posted anyway, pinning is not retried
        await DiscordRequests.AskAsync(s, DiscordRequests.Command("start", [DiscordRequests.Opt("game", "tz_94h")]));
        await publisher.RunOnceAsync();
        await publisher.RunOnceAsync();
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM competition WHERE id = 2 AND pin_state = 'denied' AND discord_message_id IS NOT NULL"));
    }

    [Fact]
    public async Task Missing_members_permission_is_reported_not_fatal()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        s.Discord!.RefuseMembers = true;
        await s.Service<MemberSync>().RunOnceAsync(); // must not throw
    }

    [Fact]
    public async Task Deleted_leaderboard_message_is_reposted()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        await DiscordRequests.AskAsync(s, DiscordRequests.Command("start", [DiscordRequests.Opt("game", "afm_113b")]));
        var publisher = s.Service<LeaderboardPublisher>();
        await publisher.RunOnceAsync();
        s.Discord!.Messages.Clear(); // someone deleted it
        await DiscordRequests.AskAsync(s, DiscordRequests.Command("end", [DiscordRequests.Opt("competition", 1)]));
        await publisher.RunOnceAsync();
        Assert.Single(s.Discord.Cards);
    }
}

public class DiscordRenderTests
{
    [Theory]
    [InlineData("Alex | [SKY]", "Alex")]
    [InlineData("[SKY,AVK] Alex", "Alex")]
    [InlineData("[SKY]", "[SKY]")]
    [InlineData("No tag", "No tag")]
    public void Display_names_lose_their_initials_tag(string nick, string expected) =>
        Assert.Equal(expected, Players.StripTag(nick));

    [Fact]
    public void Markdown_in_names_is_escaped() =>
        Assert.Equal(@"\*\*Bold\*\* \_x\_", Interactions.Md("**Bold** _x_"));
}
