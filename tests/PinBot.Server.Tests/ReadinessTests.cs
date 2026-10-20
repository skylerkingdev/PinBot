using System.Net.Http.Json;
using PinBot.Contracts;
using PinBot.Server.Discord;
using static PinBot.Server.Tests.DiscordRequests;

namespace PinBot.Server.Tests;

public class ReadinessTests
{
    static Task<AgentCompetitionsResponse?> Competitions(HttpClient cab, string query = "") =>
        cab.GetFromJsonAsync<AgentCompetitionsResponse>($"{ApiV1.Prefix}/agent/competitions{query}");

    [Fact]
    public async Task A_waiting_cabinet_hears_about_a_new_competition_at_once()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var (_, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        var known = (await Competitions(cab))!.Version;

        var waiting = Competitions(cab, $"?known={known}&wait=20");
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted); // nothing changed: the server holds the request

        await AskAsync(s, Command("start", [Opt("game", "afm_113b")]));
        var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(5)); // returns right after /pinbot start
        Assert.Single(answer!.Competitions);
        Assert.NotEqual(known, answer.Version);

        // an out-of-date version is answered immediately; no wait parameter means no waiting either
        Assert.Single((await Competitions(cab, $"?known={known}&wait=20").WaitAsync(TimeSpan.FromSeconds(5)))!.Competitions);
        Assert.Single((await Competitions(cab))!.Competitions);
    }

    [Fact]
    public async Task The_card_shows_where_the_competition_is_ready()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var (_, vpin, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        var (_, garage, _) = await s.PairCabinetAsync("Garage", TestServer.Table("afm_113b", "Attack from Mars"));
        await s.PairCabinetAsync("Den", TestServer.Table("tz_94h", "Twilight Zone")); // no AFM: not listed
        await AskAsync(s, Command("start", [Opt("game", "afm_113b")]));
        var publisher = s.Service<LeaderboardPublisher>();
        async Task<string> Card() { await publisher.RunOnceAsync(); return FakeDiscord.Body(Assert.Single(s.Discord!.Cards)); }
        async Task Report(HttpClient cab, string state, string? detail = null) =>
            (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/competition-states", new CompetitionStatesRequest([new(1, state, detail)]))).EnsureSuccessStatusCode();

        var card = await Card();
        Assert.Contains("⏳ Getting ready: Garage, VPin", card); // nobody has reported yet
        Assert.DoesNotContain("Den", card);

        await Report(vpin, CompetitionStates.Ready);
        await Report(garage, CompetitionStates.Waiting, "a table is running");
        card = await Card();
        Assert.Contains("🟢 Ready on: VPin", card);
        Assert.Contains("⏳ Getting ready: Garage (a table is running)", card);

        await Report(garage, CompetitionStates.Refused, "Capcom keeps backup copies of its high-score table, and none were found");
        Assert.Contains("⚠️ Can't run on Garage: Capcom keeps backup copies", await Card());

        s.Clock.Advance(TimeSpan.FromDays(4)); // over: no readiness lines on the final card
        Assert.DoesNotContain("Ready on", await Card());
    }

    /// <summary>/pinbot status lists each cabinet's PinBot: current, catching up by itself, or unable to update itself.</summary>
    [Fact]
    public async Task Status_shows_which_cabinets_run_the_current_release()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        async Task Pair(string name, string version)
        {
            var (_, cab, _) = await s.PairCabinetAsync(name);
            (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/inventory", new InventoryRequest([TestServer.Table("afm_113b")], version))).EnsureSuccessStatusCode();
        }
        await Pair("Current", "20261013232655");
        await Pair("Behind", "20261013171742");
        await Pair("Old", "1.0.0.0");
        await s.PairCabinetAsync("Silent"); // paired, never sent an inventory
        var manifest = Path.GetTempFileName();
        await File.WriteAllTextAsync(manifest, """{"version":20261013232655,"size":1,"sha256":"00","signature":"00"}""");

        await using var conn = new Npgsql.NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        var lines = await Interactions.CabinetLinesAsync(conn, s.Clock.UtcNow, s.Service<DiscordOptions>(), manifest);
        File.Delete(manifest);

        Assert.Contains("🟢 Current · ✅ up to date", lines);
        Assert.Contains("Behind · ⏳ updating itself", lines);
        Assert.Contains("Old · ⚠️ too old to update itself", lines);
        Assert.Contains("/download/pinbot.exe", lines);
        Assert.Matches("Silent · .*version not known yet", lines);
        Assert.Contains("**VPins**", (await AskAsync(s, Command("status")))["content"]!.GetValue<string>());
    }

    /// <summary>A cabinet tells the server when a table starts and when none runs any more: shown as in a game.</summary>
    [Fact]
    public async Task Status_shows_a_cabinet_in_a_game()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var (_, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"));
        await using var conn = new Npgsql.NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        async Task<string> Line() => await Interactions.CabinetLinesAsync(conn, s.Clock.UtcNow, s.Service<DiscordOptions>(), "none.json");

        (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/activity", new AgentActivity(true))).EnsureSuccessStatusCode();
        Assert.StartsWith("🎮 VPin · in a game ·", await Line());
        (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/activity", new AgentActivity(false))).EnsureSuccessStatusCode();
        Assert.StartsWith("🟢 VPin · ", await Line());

        (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/activity", new AgentActivity(true))).EnsureSuccessStatusCode();
        (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/inventory", new InventoryRequest([TestServer.Table("afm_113b")], "1"))).EnsureSuccessStatusCode();
        Assert.StartsWith("🟢 VPin · ", await Line()); // PinBot restarted (it sends its tables): no table is running
    }

    [Fact]
    public async Task A_cabinet_whose_rom_reset_on_a_competition_file_is_shown_as_unable_to_run_it()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var (_, vpin, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        var (_, garage, _) = await s.PairCabinetAsync("Garage", TestServer.Table("afm_113b", "Attack from Mars"));
        await AskAsync(s, Command("start", [Opt("game", "afm_113b")]));
        (await garage.PutAsJsonAsync($"{ApiV1.Prefix}/agent/competition-states", new CompetitionStatesRequest([new(1, CompetitionStates.Ready, null)]))).EnsureSuccessStatusCode();

        // the game reset its high scores on Garage: its agent re-sends the inventory with the ROM rejected
        (await garage.PutAsJsonAsync($"{ApiV1.Prefix}/agent/inventory", new InventoryRequest(
            [new InventoryTable("Attack from Mars", "afm_113b", false, CompetitionStates.RomRejected)], "1"))).EnsureSuccessStatusCode();

        Assert.Empty((await Competitions(garage))!.Competitions); // no longer sent there
        Assert.Single((await Competitions(vpin))!.Competitions);
        await s.Service<LeaderboardPublisher>().RunOnceAsync();
        var card = FakeDiscord.Body(Assert.Single(s.Discord!.Cards));
        Assert.Contains("⚠️ Can't run on Garage: this ROM reset its high scores", card); // its stale "ready" is not shown
        Assert.Contains("⏳ Getting ready: VPin", card);
    }
}

public class CabinetLabelTests
{
    /// <summary>Members know each other's tags, not their PCs' names: cabinets are shown by their owner's tag.</summary>
    [Fact]
    public async Task Cabinets_are_shown_by_their_owners_tags()
    {
        await using var s = await TestServer.StartAsync();
        var a = (await s.PairCabinetAsync("DESKTOP-1")).Id;
        var b = (await s.PairCabinetAsync("GARAGE-PC")).Id;
        var c = (await s.PairCabinetAsync("LUPC")).Id;
        var admin = (await s.PairCabinetAsync("Old")).Id; // paired before Discord pairing: no owner known
        await using var conn = new Npgsql.NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        foreach (var (id, user, name) in new[] { (a, "u1", "Alex | [SKY]"), (b, "u1", "Alex | [SKY]"), (c, "u2", "Blake | [BLK]") })
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE cabinet SET paired_by = @user, paired_by_name = @name WHERE id = @id", new { user, name, id });

        var labels = await Players.CabinetLabelsAsync(conn);
        Assert.Equal("[SKY] DESKTOP-1", labels[a]); // one member, two cabinets: the PC name tells them apart
        Assert.Equal("[SKY] GARAGE-PC", labels[b]);
        Assert.Equal("[BLK]", labels[c]);
        Assert.Equal("Old", labels[admin]);

        // the tag a member uses now (from member sync) wins over the one they had when pairing
        await Dapper.SqlMapper.ExecuteAsync(conn, "INSERT INTO member_initial VALUES ('u2', 'BKG', 'Blake | [BKG]', now())");
        Assert.Equal("[BKG]", (await Players.CabinetLabelsAsync(conn))[c]);
    }

    /// <summary>A cabinet paired before Discord pairing has no recorded owner: the member who scores on it most is.</summary>
    [Fact]
    public async Task A_cabinet_without_a_recorded_owner_belongs_to_whoever_scores_on_it_most()
    {
        await using var s = await TestServer.StartAsync();
        var (id, cab, _) = await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b"));
        await s.SetMembersAsync(("u1", "Alex | [SKY]"), ("u2", "Bo [BOB]"));
        var comp = await s.StartCompetitionAsync("afm_113b");
        s.Clock.Advance(TimeSpan.FromHours(1));
        await TestServer.SubmitAsync(cab, s.Event(comp.Id, "SKY", 1_000));
        await TestServer.SubmitAsync(cab, s.Event(comp.Id, "SKY", 2_000));
        await TestServer.SubmitAsync(cab, s.Event(comp.Id, "BOB", 3_000)); // a guest's game
        await using var conn = new Npgsql.NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        Assert.Equal("[SKY]", (await Players.CabinetLabelsAsync(conn))[id]);
    }

    [Fact]
    public void The_games_list_is_split_into_pages_of_whole_lines()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"• Game number {i:000} (Maker 1990) · [AAA] [BBB] [CCC]\n").ToList();
        var pages = Interactions.GamePages(lines);
        Assert.True(pages.Count > 1);
        Assert.All(pages, p => Assert.True(p.Length <= 3800));
        Assert.Equal(string.Concat(lines), string.Concat(pages)); // nothing lost, nothing split
        Assert.Single(Interactions.GamePages([]));
    }

    [Fact]
    public async Task The_page_buttons_update_the_games_list_in_place()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("VPin", TestServer.Table("afm_113b", "Attack from Mars"));
        var press = Command("games");
        press["type"] = 3;
        press["data"] = new System.Text.Json.Nodes.JsonObject { ["custom_id"] = "games:1" };
        var r = await SendAsync(s, press);
        r.EnsureSuccessStatusCode();
        var json = (await r.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!;
        Assert.Equal(7, json["type"]!.GetValue<int>()); // edits the message the button is on
        Assert.Equal("1 game", json["data"]!["embeds"]![0]!["title"]!.GetValue<string>()); // one page: clamped
    }
}
