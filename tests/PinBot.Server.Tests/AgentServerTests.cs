extern alias agent;
using System.Net;
using System.Net.Http.Json;
using agent::PinBot.Agent.Discovery;
using agent::PinBot.Agent.Sync;
using PinBot.Contracts;

namespace PinBot.Server.Tests;

/// <summary>The real agent sync code (outbox, inventory, competitions) against the real server, as two cabinets.</summary>
public class AgentServerTests
{
    sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("network down");
    }

    static TableInfo Table(string rom, string nvPath, string name) =>
        new($@"C:\Tables\{name}.vpx", rom, RomSource.Observed, [rom], "test", nvPath, NvState.Resolved, [nvPath], true, null);

    static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pinbot-e2e", Guid.NewGuid().ToString("N")[..8])).FullName;

    sealed record Cabinet(AgentSync Sync, Outbox Outbox, string OutboxDir, HttpClient Http, IReadOnlyList<TableInfo> Tables);

    static async Task<Cabinet> CabinetAsync(TestServer s, string name, params TableInfo[] tables)
    {
        var (_, http, _) = await s.PairCabinetAsync(name);
        var dir = TempDir();
        var outbox = new Outbox(dir);
        var sync = new AgentSync(http, outbox);
        await sync.PushInventoryAsync(tables);
        return new(sync, outbox, dir, http, tables);
    }

    static ResultEvent Score(long competitionId, string initials, long score, DateTime exited, string rom = "afm_113b") =>
        new(Guid.NewGuid(), competitionId, "s-test", rom, initials, score, exited, "test");

    [Fact]
    public async Task Two_cabinets_compete_and_rank_by_best_score_per_member()
    {
        await using var s = await TestServer.StartAsync();
        var a = await CabinetAsync(s, "Living room", Table("afm_113b", @"C:\nv\afm_113b.nv", "Attack from Mars"));
        var b = await CabinetAsync(s, "Garage", Table("afm_113b", @"D:\VPinMAME\nvram\afm_113b.nv", "AFM"), Table("tz_94h", @"D:\nv\tz_94h.nv", "Twilight Zone"));
        await s.SetMembersAsync(("u1", "Alex | [SKY]"), ("u2", "Avery [AVK]"));
        var comp = await s.StartCompetitionAsync("afm_113b", 24);

        // each cabinet maps the server competition onto its own NVRAM file
        var localA = Assert.Single(await a.Sync.PullCompetitionsAsync(a.Tables));
        var localB = Assert.Single(await b.Sync.PullCompetitionsAsync(b.Tables));
        Assert.Equal((comp.Id.ToString(), @"C:\nv\afm_113b.nv"), (localA.Id, localA.NvPath));
        Assert.Equal(@"D:\VPinMAME\nvram\afm_113b.nv", localB.NvPath);

        s.Clock.Advance(TimeSpan.FromHours(2));
        var t = s.Clock.UtcNow.AddMinutes(-1);
        a.Outbox.Enqueue(Score(comp.Id, "SKY", 400_000_000, t));
        a.Outbox.Enqueue(Score(comp.Id, "AVK", 900_000_000, t));
        b.Outbox.Enqueue(Score(comp.Id, "SKY", 1_100_000_000, t));
        b.Outbox.Enqueue(Score(comp.Id, "XYZ", 2_000_000_000, t)); // nobody's initials: not counted
        Assert.Equal(0, await a.Sync.FlushOutboxAsync());
        Assert.Equal(0, await b.Sync.FlushOutboxAsync());

        var board = await s.LeaderboardAsync(comp.Id);
        Assert.Equal([("u1", 1_100_000_000L, 1), ("u2", 900_000_000L, 2)], board.Select(r => (r.DiscordUserId, r.Score, r.Rank)));
    }

    [Fact]
    public async Task Offline_queue_survives_restart_and_lost_acknowledgements_never_duplicate()
    {
        await using var s = await TestServer.StartAsync();
        var a = await CabinetAsync(s, "VPin", Table("afm_113b", @"C:\nv\afm_113b.nv", "AFM"));
        await s.SetMembersAsync(("u1", "Alex [SKY]"));
        var comp = await s.StartCompetitionAsync("afm_113b", 24);
        s.Clock.Advance(TimeSpan.FromHours(1));
        var ev = Score(comp.Id, "SKY", 500_000_000, s.Clock.UtcNow.AddMinutes(-1));
        a.Outbox.Enqueue(ev);

        // network down: nothing lost, nothing sent
        var offline = new AgentSync(new HttpClient(new Offline()) { BaseAddress = new Uri("http://localhost/") }, a.Outbox);
        await Assert.ThrowsAsync<HttpRequestException>(() => offline.FlushOutboxAsync());
        Assert.Single(a.Outbox.Pending());

        // agent restarts (new objects, same folder) and the server is back
        var restarted = new AgentSync(a.Http, new Outbox(a.OutboxDir));
        Assert.Equal(0, await restarted.FlushOutboxAsync());

        // the acknowledgement was "lost": the same event is uploaded again
        new Outbox(a.OutboxDir).Enqueue(ev);
        Assert.Equal(0, await restarted.FlushOutboxAsync());
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM result"));
        Assert.Equal(500_000_000, Assert.Single(await s.LeaderboardAsync(comp.Id)).Score);
    }

    [Fact]
    public async Task Upload_counts_only_inside_the_short_grace_after_the_deadline()
    {
        await using var s = await TestServer.StartAsync();
        var a = await CabinetAsync(s, "VPin", Table("afm_113b", @"C:\nv\afm_113b.nv", "AFM"));
        await s.SetMembersAsync(("u1", "Alex [SKY]"), ("u2", "Bo [BOB]"));
        var comp = await s.StartCompetitionAsync("afm_113b", 24);

        // played before the deadline, uploaded 3 minutes after it: counts
        a.Outbox.Enqueue(Score(comp.Id, "SKY", 700, comp.EndsAtUtc.AddMinutes(-30)));
        s.Clock.UtcNow = comp.EndsAtUtc.AddMinutes(3);
        Assert.Single(await a.Sync.PullCompetitionsAsync(a.Tables)); // finalizing competitions are still sent to agents
        await a.Sync.FlushOutboxAsync();

        // played before the deadline, but uploaded once the standings were final: rejected, removed from the queue
        a.Outbox.Enqueue(Score(comp.Id, "BOB", 900, comp.EndsAtUtc.AddMinutes(-30)));
        s.Clock.UtcNow = comp.EndsAtUtc.AddMinutes(6);
        Assert.Equal(0, await a.Sync.FlushOutboxAsync());

        Assert.Equal(["u1"], (await s.LeaderboardAsync(comp.Id)).Select(r => r.DiscordUserId));
        Assert.Empty(await a.Sync.PullCompetitionsAsync(a.Tables)); // complete: the agent can restore and forget it
    }

    [Fact]
    public async Task Grouped_revisions_map_to_each_revisions_own_nvram_file()
    {
        await using var s = await TestServer.StartAsync();
        var a = await CabinetAsync(s, "VPin",
            Table("afm_113b", @"C:\nv\afm_113b.nv", "AFM"), Table("afm_113", @"C:\nv\afm_113.nv", "AFM classic"),
            Table("afm_113b", @"C:\nv\afm_113b.nv", "AFM (second copy, same ROM)"));
        (await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(s.Admin, $"{ApiV1.Prefix}/admin/rom-aliases",
            new AliasesRequest(new Dictionary<string, string> { ["afm_113"] = "afm", ["afm_113b"] = "afm" }))).EnsureSuccessStatusCode();
        var comp = await s.StartCompetitionAsync("afm_113b");

        var local = await a.Sync.PullCompetitionsAsync(a.Tables);
        Assert.Equal([@"C:\nv\afm_113.nv", @"C:\nv\afm_113b.nv"], local.Select(l => l.NvPath).Order());
        Assert.All(local, l => Assert.Equal(comp.Id.ToString(), l.Id));
    }

    [Fact]
    public async Task Revoked_cabinet_cannot_sync()
    {
        await using var s = await TestServer.StartAsync();
        var (id, http, _) = await s.PairCabinetAsync("VPin");
        (await s.Admin.PostAsync($"{ApiV1.Prefix}/admin/cabinets/{id}/revoke", null)).EnsureSuccessStatusCode();
        var sync = new AgentSync(http, new Outbox(TempDir()));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sync.PushInventoryAsync([]));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }
}

public class RevisionTests
{
    [Fact]
    public async Task Different_revisions_of_one_machine_stay_separate_games_with_the_rom_in_the_title()
    {
        await using var s = await TestServer.StartAsync();
        await s.PairCabinetAsync("Mine", TestServer.Table("cv_20h", "Cirqus Voltaire (Bally 1997)"), TestServer.Table("afm_113b", "Attack from Mars"));
        await s.PairCabinetAsync("Friend", TestServer.Table("cv_14", "Cirqus Voltaire (Bally 1997)"));
        var games = (await s.Admin.GetFromJsonAsync<List<GameInfo>>($"{ApiV1.Prefix}/admin/games"))!;
        Assert.Equal(["Attack from Mars", "Cirqus Voltaire (Bally 1997) · cv_14", "Cirqus Voltaire (Bally 1997) · cv_20h"], games.Select(g => g.Title));
        Assert.Equal("Attack from Mars · Mine", games[0].Choice); // only some cabinets have it: whose
    }

    /// <summary>Past five owners the pick list gives a count, and tells the member asking whether one cabinet is theirs.</summary>
    [Fact]
    public async Task In_a_larger_community_the_pick_list_counts_cabinets_and_says_if_one_is_yours()
    {
        await using var s = await TestServer.StartAsync();
        foreach (var (cab, member) in new[] { ("A", "u1"), ("B", "u2"), ("C", "u3"), ("D", "u4"), ("F", "u6"), ("G", "u7"), ("E", "u5") })
        {
            var (id, _, _) = await s.PairCabinetAsync(cab, TestServer.Table("tz_94h", "Twilight Zone"),
                cab == "E" ? TestServer.Table("mm_109c", "Medieval Madness") : TestServer.Table("afm_113b", "Attack from Mars"));
            await using var c = new Npgsql.NpgsqlConnection(s.ConnectionString);
            await c.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(c, "UPDATE cabinet SET paired_by = @member, paired_by_name = @name WHERE id = @id",
                new { member, name = $"Player {cab} | [{cab}{cab}{cab}]", id });
        }
        await using var conn = new Npgsql.NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        string Choice(List<GameInfo> games, string id) => games.Single(g => g.CanonicalGameId == id).Choice;

        var forU1 = await Competitions.EligibleGamesAsync(conn, "u1");
        Assert.Equal("Twilight Zone · all", Choice(forU1, "tz_94h"));
        Assert.Equal("Attack from Mars · 6 of 7 VPins, yours included", Choice(forU1, "afm_113b"));
        Assert.Equal("Medieval Madness · [EEE]", Choice(forU1, "mm_109c")); // shown by the owner's tag
        Assert.Equal("Attack from Mars · 6 of 7 VPins", Choice(await Competitions.EligibleGamesAsync(conn, "u5"), "afm_113b"));
    }

    [Fact]
    public async Task A_coin_play_build_is_the_same_game_as_its_base_rom()
    {
        await using var s = await TestServer.StartAsync();
        var (_, mine, _) = await s.PairCabinetAsync("VPin", TestServer.Table("cv_20h", "Cirqus Voltaire (Bally 1997)"));
        var (_, friend, _) = await s.PairCabinetAsync("Den", TestServer.Table("cv_20hc", "Cirqus Voltaire (Bally 1997)"));
        var cv = Assert.Single((await s.Admin.GetFromJsonAsync<List<GameInfo>>($"{ApiV1.Prefix}/admin/games"))!);
        Assert.Equal(("cv_20h", "Cirqus Voltaire (Bally 1997) · all"), (cv.CanonicalGameId, cv.Choice)); // both cabinets have it
        Assert.Equal(["cv_20h", "cv_20hc"], cv.RomIds);

        await s.StartCompetitionAsync("cv_20hc"); // either ROM name starts the one game
        foreach (var (cab, rom) in new[] { (mine, "cv_20h"), (friend, "cv_20hc") })
            Assert.Equal([rom], Assert.Single((await cab.GetFromJsonAsync<AgentCompetitionsResponse>($"{ApiV1.Prefix}/agent/competitions"))!.Competitions).RomIds);
        Assert.Equal("cv_20h", RomVariants.All["cv_20hc"]);
        Assert.False(RomVariants.All.ContainsKey("cv_14")); // a different version number is never a variant
    }

    [Fact]
    public async Task The_pick_list_says_whose_cabinets_have_each_revision()
    {
        await using var s = await TestServer.StartAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("cv_20h", "Cirqus Voltaire (Bally 1997)"));
        await s.PairCabinetAsync("Den", TestServer.Table("cv_14", "Cirqus Voltaire (Bally 1997)"));
        var choices = (await s.Admin.GetFromJsonAsync<List<GameInfo>>($"{ApiV1.Prefix}/admin/games"))!.Select(g => g.Choice);
        Assert.Equal(["Cirqus Voltaire (Bally 1997) · cv_14 · Den", "Cirqus Voltaire (Bally 1997) · cv_20h · VPin"], choices);
    }

    [Fact]
    public async Task Automatic_revision_links_are_removed_once_and_manual_aliases_kept()
    {
        await using var s = await TestServer.StartAsync();
        await s.PairCabinetAsync("Mine", TestServer.Table("cv_20h", "Cirqus Voltaire"), TestServer.Table("tz_94h", "Twilight Zone"));
        await s.PairCabinetAsync("Friend", TestServer.Table("cv_14", "Cirqus Voltaire"), TestServer.Table("tz_92", "Twilight Zone"));
        // as left by the previous version: game keys reported, cv_14 linked automatically; tz_92 linked by hand
        await s.CountAsync("""
            WITH k AS (UPDATE installation SET game_key = CASE WHEN rom_id LIKE 'cv%' THEN 'cirqusvoltaire' END RETURNING 1),
                 a AS (INSERT INTO rom_alias VALUES ('cv_14', 'cv_20h'), ('tz_92', 'tz_94h') RETURNING 1),
                 d AS (DELETE FROM setting WHERE key = 'schema_v10_revision_links_removed' RETURNING 1)
            SELECT count(*) FROM k
            """);
        await s.RestartAsync(); // the schema migration runs at startup
        Assert.Equal(0, await s.CountAsync("SELECT count(*) FROM rom_alias WHERE rom_id = 'cv_14'"));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM rom_alias WHERE rom_id = 'tz_92'"));
    }
}
