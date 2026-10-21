using System.Net;
using System.Net.Http.Json;
using System.Text;
using PinBot.Contracts;
using PinBot.Server.Discord;

namespace PinBot.Server.Tests;

/// <summary>Edge cases found in review: forged results, the public pairing limit, oversized requests, Discord hiccups.</summary>
public class HardeningTests
{
    [Fact]
    public async Task A_cabinet_without_the_ROM_cannot_post_scores_for_it()
    {
        await using var s = await TestServer.StartAsync();
        var (_, owner, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"));
        var (_, stranger, _) = await s.PairCabinetAsync("Script", TestServer.Table("tz_94h"));
        await s.SetMembersAsync(("u1", "Alex [SKY]"));
        var comp = await s.StartCompetitionAsync("afm_113b");
        s.Clock.Advance(TimeSpan.FromHours(1));

        var forged = Assert.Single(await TestServer.SubmitAsync(stranger, s.Event(comp.Id, "SKY", 9_000_000_000)));
        Assert.Equal((ResultStatus.Rejected, "this VPin has no table with ROM afm_113b"), (forged.Status, forged.Reason));
        var real = Assert.Single(await TestServer.SubmitAsync(owner, s.Event(comp.Id, "SKY", 500_000_000)));
        Assert.Equal(ResultStatus.Accepted, real.Status);
    }

    [Fact]
    public async Task Pairing_attempts_are_limited_per_client_behind_the_proxy()
    {
        await using var s = await TestServer.StartAsync();
        var http = s.Client();
        async Task<HttpStatusCode> Redeem(string forwardedFor)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiV1.Prefix}/pair/redeem") { Content = JsonContent.Create(new PairRedeemRequest("WRONG-CODES", "x")) };
            req.Headers.Add("X-Forwarded-For", forwardedFor);
            return (await http.SendAsync(req)).StatusCode;
        }
        for (int i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Forbidden, await Redeem("203.0.113.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Redeem("203.0.113.7"));
        // the proxy appends the real client: an address the client made up in front of it changes nothing
        Assert.Equal(HttpStatusCode.TooManyRequests, await Redeem("10.9.9.9, 203.0.113.7"));
        // and someone else is not locked out by them
        Assert.Equal(HttpStatusCode.Forbidden, await Redeem("198.51.100.4"));
    }

    [Fact]
    public async Task Oversized_interaction_requests_are_refused_unread()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var body = new string('x', Interactions.MaxBodyBytes + 1);
        var r = await s.Client().PostAsync("/api/v1/discord/interactions", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }

    [Fact]
    public async Task A_member_list_that_lost_every_tag_is_applied_only_when_the_next_sync_agrees()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var sync = s.Service<MemberSync>();
        s.Discord!.Members = [new("111", "Alex | [SKY]", false), new("222", "JB | [JBJ]", false)];
        await sync.RunOnceAsync();
        Assert.Equal(2, await s.CountAsync("SELECT count(*) FROM member_initial"));

        s.Discord.Members = []; // a Discord hiccup
        await sync.RunOnceAsync();
        Assert.Equal(2, await s.CountAsync("SELECT count(*) FROM member_initial")); // kept

        await sync.RunOnceAsync(); // still empty 10 minutes later: real
        Assert.Equal(0, await s.CountAsync("SELECT count(*) FROM member_initial"));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, false)]  // small servers: one rename is just a rename
    [InlineData(10, 6, false)]
    [InlineData(10, 4, true)]
    public void Suspicious_shrink(int before, int now, bool suspicious) => Assert.Equal(suspicious, Players.SuspiciousShrink(before, now));

    [Fact]
    public async Task Shared_initials_notices_are_not_repeated_after_a_failed_delivery()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var sync = s.Service<MemberSync>();
        s.Discord!.Members = [new("111", "Alex | [DUP]", false), new("222", "Dan | [DUP]", false), new("333", "Dee | [DUP]", false)];
        s.Discord.FailDmsTo.Add("222");
        await sync.RunOnceAsync();
        s.Discord.FailDmsTo.Clear();
        await sync.RunOnceAsync();
        await sync.RunOnceAsync();
        foreach (var user in new[] { "111", "222", "333" })
            Assert.Single(s.Discord.DirectMessages(user));
    }

    [Fact]
    public async Task Healthz_reports_the_release_the_updater_waits_for()
    {
        await using var s = await TestServer.StartAsync();
        var health = await s.Client().GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/healthz");
        Assert.Equal("ok", health!["status"]!.GetValue<string>());
        Assert.True(health.ContainsKey("release")); // null in a development build; the release number in a release
    }

    [Fact]
    public void Background_loops_pause_after_a_failure()
    {
        Assert.Equal(DiscordBackground.FailurePause, DiscordBackground.PauseAfter(TimeSpan.Zero, failed: true));
        Assert.Equal(TimeSpan.Zero, DiscordBackground.PauseAfter(TimeSpan.Zero, failed: false));
        Assert.Equal(TimeSpan.FromMinutes(10), DiscordBackground.PauseAfter(TimeSpan.FromMinutes(10), failed: true));
    }
}
