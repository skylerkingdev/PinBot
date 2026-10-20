using System.Net;
using System.Net.Http.Json;
using PinBot.Contracts;
using PinBot.Server;

namespace PinBot.Server.Tests;

public class PairingTests
{
    [Fact]
    public async Task Agent_download_is_served_only_when_packaged()
    {
        await using var s = await TestServer.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client().GetAsync(AgentDownload.Route)).StatusCode);
        Directory.CreateDirectory(Path.GetDirectoryName(AgentDownload.Path)!);
        await File.WriteAllBytesAsync(AgentDownload.Path, [0x4D, 0x5A]);
        var (priv, pub) = UpdateSigning.NewKey();
        await File.WriteAllTextAsync(AgentDownload.ManifestPath, System.Text.Json.JsonSerializer.Serialize(
            UpdateSigning.Sign(priv, 20261013120000, [0x4D, 0x5A]), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        try
        {
            var r = await s.Client().GetAsync(AgentDownload.Route);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("pinbot.exe", r.Content.Headers.ContentDisposition!.FileNameStar ?? r.Content.Headers.ContentDisposition.FileName);
            Assert.Equal([0x4D, 0x5A], await r.Content.ReadAsByteArrayAsync());

            // the signed manifest cabinets update from, exactly as an agent reads it
            var m = await s.Client().GetFromJsonAsync<UpdateManifest>(AgentDownload.ManifestRoute);
            Assert.Equal(20261013120000, m!.Version);
            Assert.True(UpdateSigning.Verify(m, pub) && UpdateSigning.Matches(m, [0x4D, 0x5A]));
        }
        finally { File.Delete(AgentDownload.Path); File.Delete(AgentDownload.ManifestPath); }
    }

    [Fact]
    public async Task Code_is_single_use_and_credential_authenticates()
    {
        await using var s = await TestServer.StartAsync();
        var code = (await (await s.Admin.PostAsync($"{ApiV1.Prefix}/admin/pairing-codes", null)).Content.ReadFromJsonAsync<CreatePairingCodeResponse>())!;
        var first = await s.Client().PostAsJsonAsync($"{ApiV1.Prefix}/pair/redeem", new PairRedeemRequest(code.Code.ToLowerInvariant().Replace("-", " "), "VPin"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var again = await s.Client().PostAsJsonAsync($"{ApiV1.Prefix}/pair/redeem", new PairRedeemRequest(code.Code, "Other"));
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        var paired = (await first.Content.ReadFromJsonAsync<PairRedeemResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await s.Client(paired.Credential).GetAsync($"{ApiV1.Prefix}/agent/competitions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client().GetAsync($"{ApiV1.Prefix}/agent/competitions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client("not-a-credential").GetAsync($"{ApiV1.Prefix}/agent/competitions")).StatusCode);
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM cabinet WHERE credential_hash <> ''"));
        Assert.Equal(0, await s.CountAsync($"SELECT count(*) FROM cabinet WHERE credential_hash = '{paired.Credential}'")); // only the hash is stored
    }

    [Fact]
    public async Task Expired_code_and_revoked_cabinet_are_refused()
    {
        await using var s = await TestServer.StartAsync();
        var code = (await (await s.Admin.PostAsync($"{ApiV1.Prefix}/admin/pairing-codes", null)).Content.ReadFromJsonAsync<CreatePairingCodeResponse>())!;
        s.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await s.Client().PostAsJsonAsync($"{ApiV1.Prefix}/pair/redeem", new PairRedeemRequest(code.Code, "VPin"))).StatusCode);

        var (id, cab, _) = await s.PairCabinetAsync("VPin");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.PostAsync($"{ApiV1.Prefix}/admin/cabinets/{id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cab.GetAsync($"{ApiV1.Prefix}/agent/competitions")).StatusCode);
    }

    [Fact]
    public async Task Admin_api_requires_the_token()
    {
        await using var s = await TestServer.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client().PostAsync($"{ApiV1.Prefix}/admin/pairing-codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client("wrong").GetAsync($"{ApiV1.Prefix}/admin/games")).StatusCode);
    }
}

public class CompetitionTests
{
    [Fact]
    public async Task Only_games_eligible_on_a_cabinet_can_start()
    {
        await using var s = await TestServer.StartAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"), new InventoryTable("Xenon.vpx", "xenon", false, "no initials"));
        var games = await s.Admin.GetFromJsonAsync<List<GameInfo>>($"{ApiV1.Prefix}/admin/games");
        Assert.Equal(["afm_113b"], games!.Select(g => g.CanonicalGameId));

        var r = await s.Admin.PostAsJsonAsync($"{ApiV1.Prefix}/admin/competitions", new CreateCompetitionRequest("xenon", 24, null, null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task Overlap_for_the_same_game_is_rejected_different_games_coexist()
    {
        await using var s = await TestServer.StartAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"), TestServer.Table("tz_94h"));
        await s.StartCompetitionAsync("afm_113b", 24);

        var clash = await s.Admin.PostAsJsonAsync($"{ApiV1.Prefix}/admin/competitions", new CreateCompetitionRequest("AFM_113B", 2, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        await s.StartCompetitionAsync("tz_94h", 24);
    }

    [Fact]
    public async Task Concurrent_starts_for_one_game_create_exactly_one()
    {
        await using var s = await TestServer.StartAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            s.Admin.PostAsJsonAsync($"{ApiV1.Prefix}/admin/competitions", new CreateCompetitionRequest("afm_113b", 24, null, null, null))));
        Assert.Single(attempts, a => a.StatusCode == HttpStatusCode.Created);
        Assert.Equal(11, attempts.Count(a => a.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM competition"));
    }

    [Fact]
    public async Task Related_revisions_share_one_game_only_when_explicitly_grouped()
    {
        await using var s = await TestServer.StartAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"), TestServer.Table("afm_113", "AFM older.vpx"));
        await s.StartCompetitionAsync("afm_113b");
        await s.StartCompetitionAsync("afm_113"); // not grouped: distinct games, both allowed (never grouped by name prefix)

        await using var s2 = await TestServer.StartAsync();
        var (_, cab, _) = await s2.PairCabinetAsync("VPin", TestServer.Table("afm_113b"), TestServer.Table("afm_113", "AFM older.vpx"));
        (await s2.Admin.PutAsJsonAsync($"{ApiV1.Prefix}/admin/rom-aliases",
            new AliasesRequest(new Dictionary<string, string> { ["afm_113"] = "attack-from-mars", ["afm_113b"] = "attack-from-mars" }))).EnsureSuccessStatusCode();
        var comp = await s2.StartCompetitionAsync("afm_113");
        Assert.Equal("attack-from-mars", comp.CanonicalGameId);
        var clash = await s2.Admin.PostAsJsonAsync($"{ApiV1.Prefix}/admin/competitions", new CreateCompetitionRequest("afm_113b", 24, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        var mine = (await cab.GetFromJsonAsync<AgentCompetitionsResponse>($"{ApiV1.Prefix}/agent/competitions"))!.Competitions;
        Assert.Equal(["afm_113", "afm_113b"], Assert.Single(mine).RomIds);
    }

    [Fact]
    public async Task Status_follows_the_clock_and_early_end_frees_the_game()
    {
        await using var s = await TestServer.StartAsync();
        var (_, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"));
        var comp = await s.StartCompetitionAsync("afm_113b", 2);
        Assert.Equal("active", comp.Status);

        var ended = await (await s.Admin.PostAsync($"{ApiV1.Prefix}/admin/competitions/{comp.Id}/end", null)).Content.ReadFromJsonAsync<CompetitionView>();
        Assert.Equal("finalizing", ended!.Status);
        await s.StartCompetitionAsync("afm_113b", 2); // a new one can start right away

        s.Clock.Advance(TimeSpan.FromHours(30));
        var list = await s.Admin.GetFromJsonAsync<List<CompetitionView>>($"{ApiV1.Prefix}/admin/competitions");
        Assert.All(list!, c => Assert.Equal("complete", c.Status));
        Assert.Empty((await cab.GetFromJsonAsync<AgentCompetitionsResponse>($"{ApiV1.Prefix}/agent/competitions"))!.Competitions);
    }
}

public class ResultTests
{
    static async Task<(TestServer S, HttpClient Cab, CompetitionView Comp)> SetupAsync()
    {
        var s = await TestServer.StartAsync();
        var (_, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"));
        var comp = await s.StartCompetitionAsync("afm_113b", 24);
        await s.SetMembersAsync(("u1", "Alex | [SKY,AVK]"), ("u2", "Bob [BOB]"), ("u3", "Ann [DUP]"), ("u4", "Dan [dup]"));
        s.Clock.Advance(TimeSpan.FromHours(1));
        return (s, cab, comp);
    }

    [Fact]
    public async Task Valid_result_is_accepted_and_attributed()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        var o = Assert.Single(await TestServer.SubmitAsync(cab, s.Event(comp.Id, "sky", 900_000_000)));
        Assert.Equal(ResultStatus.Accepted, o.Status);
        var row = Assert.Single(await s.LeaderboardAsync(comp.Id));
        Assert.Equal(("u1", 900_000_000L, "SKY"), (row.DiscordUserId, row.Score, row.Initials));
    }

    [Fact]
    public async Task Retries_never_duplicate_and_reused_ids_are_refused()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        var ev = s.Event(comp.Id, "SKY", 900_000_000);
        for (int i = 0; i < 3; i++)
            Assert.Equal(ResultStatus.Accepted, Assert.Single(await TestServer.SubmitAsync(cab, ev)).Status);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TestServer.SubmitAsync(cab, ev))); // concurrent retries
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM result"));

        var tampered = ev with { Score = 999_999_999 };
        var o = Assert.Single(await TestServer.SubmitAsync(cab, tampered));
        Assert.Equal(ResultStatus.Rejected, o.Status);
        Assert.Equal(900_000_000, Assert.Single(await s.LeaderboardAsync(comp.Id)).Score);

        var (_, other, _) = await s.PairCabinetAsync("Other", TestServer.Table("afm_113b"));
        Assert.Equal(ResultStatus.Rejected, Assert.Single(await TestServer.SubmitAsync(other, ev)).Status); // another cabinet replaying an id
    }

    [Fact]
    public async Task Unknown_and_ambiguous_initials_are_kept_but_never_counted()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        var outcomes = await TestServer.SubmitAsync(cab, s.Event(comp.Id, "ZZZ", 5), s.Event(comp.Id, "DUP", 7));
        Assert.All(outcomes, o => Assert.Equal(ResultStatus.Unattributed, o.Status));
        Assert.Contains("no member", outcomes[0].Reason);
        Assert.Contains("2 members", outcomes[1].Reason);
        Assert.Empty(await s.LeaderboardAsync(comp.Id));
    }

    [Fact]
    public async Task Deadline_is_by_session_exit_not_upload_time()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        s.Clock.UtcNow = comp.EndsAtUtc + TimeSpan.FromMinutes(2); // deadline passed, results of games finished in time still arrive

        var exitedBefore = s.Event(comp.Id, "SKY", 100) with { SessionExitedAtUtc = comp.EndsAtUtc.AddSeconds(-1) };
        var exitedAtDeadline = s.Event(comp.Id, "SKY", 200) with { SessionExitedAtUtc = comp.EndsAtUtc };
        var exitedBeforeStart = s.Event(comp.Id, "SKY", 300) with { SessionExitedAtUtc = comp.StartsAtUtc.AddSeconds(-1) };
        var o = await TestServer.SubmitAsync(cab, exitedBefore, exitedAtDeadline, exitedBeforeStart);
        Assert.Equal([ResultStatus.Accepted, ResultStatus.Rejected, ResultStatus.Rejected], o.Select(x => x.Status));
        Assert.Contains("deadline", o[1].Reason);

        s.Clock.UtcNow = comp.EndsAtUtc + TimeSpan.FromMinutes(5); // final
        var late = s.Event(comp.Id, "SKY", 400) with { SessionExitedAtUtc = comp.EndsAtUtc.AddMinutes(-5) };
        Assert.Contains("became final", Assert.Single(await TestServer.SubmitAsync(cab, late)).Reason);
    }

    [Fact]
    public async Task Implausible_or_foreign_results_are_rejected()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        var future = s.Event(comp.Id, "SKY", 100) with { SessionExitedAtUtc = s.Clock.UtcNow.AddMinutes(10) };
        var wrongRom = s.Event(comp.Id, "SKY", 100, rom: "tz_94h");
        var zero = s.Event(comp.Id, "SKY", 0);
        var noInitials = s.Event(comp.Id, "   ", 100);
        var o = await TestServer.SubmitAsync(cab, future, wrongRom, zero, noInitials);
        Assert.All(o, x => Assert.Equal(ResultStatus.Rejected, x.Status));

        var unknownComp = s.Event(9999, "SKY", 100);
        Assert.Equal("unknown competition", Assert.Single(await TestServer.SubmitAsync(cab, unknownComp)).Reason);
    }

    [Fact]
    public async Task Leaderboard_is_best_per_member_across_initials_and_cabinets_with_shared_ranks()
    {
        var (s, cab1, comp) = await SetupAsync();
        await using var _ = s;
        var (_, cab2, _) = await s.PairCabinetAsync("Second cabinet", TestServer.Table("afm_113b"));
        await TestServer.SubmitAsync(cab1, s.Event(comp.Id, "SKY", 500), s.Event(comp.Id, "BOB", 700));
        await TestServer.SubmitAsync(cab2, s.Event(comp.Id, "AVK", 700), s.Event(comp.Id, "BOB", 300)); // AVK is u1 too

        var board = await s.LeaderboardAsync(comp.Id);
        Assert.Equal([("u1", 700L, 1), ("u2", 700L, 1)], board.Select(r => (r.DiscordUserId, r.Score, r.Rank)).OrderBy(r => r.DiscordUserId));
    }

    [Fact]
    public async Task Accepted_score_keeps_its_member_after_a_nickname_change()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        await TestServer.SubmitAsync(cab, s.Event(comp.Id, "BOB", 700));
        await s.SetMembersAsync(("u2", "Bob (no tag now)"), ("u9", "New Bob [BOB]"));
        Assert.Equal("u2", Assert.Single(await s.LeaderboardAsync(comp.Id)).DiscordUserId);
    }

    [Fact]
    public async Task Everything_survives_a_server_restart()
    {
        var (s, cab, comp) = await SetupAsync();
        await using var _ = s;
        var ev = s.Event(comp.Id, "SKY", 900);
        await TestServer.SubmitAsync(cab, ev);

        await s.RestartAsync();
        var cabAfter = s.Client(cab.DefaultRequestHeaders.Authorization!.Parameter);
        Assert.Equal(ResultStatus.Accepted, Assert.Single(await TestServer.SubmitAsync(cabAfter, ev)).Status);
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM result"));
        Assert.Equal(900, Assert.Single(await s.LeaderboardAsync(comp.Id)).Score);
    }
}

public class PlayerTagTests
{
    [Theory]
    [InlineData("Player | [SKY,AVK]", new[] { "SKY", "AVK" })]
    [InlineData("Blake | [ blk , mak ]", new[] { "BLK", "MAK" })]
    [InlineData("[NF]", new[] { "NF" })]
    [InlineData("No tag here", new string[0])]
    [InlineData("Bad [TOOLONG]", new string[0])]
    [InlineData("Bob [B-B]", new string[0])]
    [InlineData(null, new string[0])]
    public void Nickname_tags_parse_to_normalized_initials(string? name, string[] expected) =>
        Assert.Equal(expected, Players.ParseTag(name));
}
