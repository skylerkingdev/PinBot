using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PinBot.Contracts;
using PinBot.Server.Discord;
using static PinBot.Server.Tests.DiscordRequests;

namespace PinBot.Server.Tests;

public class TableUpdateDetectionTests
{
    static TableFile F(string table, string? version, string? rom = "twenty4_150", string? name = "24 (Stern 2009)") =>
        new(table, rom, name, version, "RobbyKingPin", null);

    [Fact]
    public void First_report_is_a_silent_baseline() =>
        Assert.Empty(TableUpdates.Detect([], [F("24", "2.4.1")], baseline: true));

    [Fact]
    public void New_table_and_new_version_are_changes()
    {
        var changes = TableUpdates.Detect([F("24", "2.0")], [F("24", "2.4.1"), F("Elvira", "2.4", "eatpm_l4", "Elvira")], baseline: false);
        Assert.Equal(2, changes.Count);
        Assert.Equal(("updated", "2.0"), (changes[0].Kind, changes[0].OldVersion));
        Assert.Equal(("added", (string?)null), (changes[1].Kind, changes[1].OldVersion));
    }

    [Fact]
    public void Same_version_missing_version_and_removals_are_silent()
    {
        Assert.Empty(TableUpdates.Detect([F("24", "2.4.1")], [F("24", " 2.4.1 ")], baseline: false)); // a re-save
        Assert.Empty(TableUpdates.Detect([F("24", "2.4.1")], [F("24", null)], baseline: false));      // can't tell
        Assert.Empty(TableUpdates.Detect([F("24", "2.4.1"), F("Elvira", "1")], [F("24", "2.4.1")], baseline: false));
    }

    [Fact]
    public void Renamed_file_is_the_same_table()
    {
        // file names often carry the version: "24 v2.0" -> "24 v2.4.1" is an update, not a new table
        var changes = TableUpdates.Detect([F("24 v2.0", "2.0")], [F("24 v2.4.1", "2.4.1")], baseline: false);
        Assert.Equal(("updated", "2.0", "24 v2.4.1"), (changes.Single().Kind, changes.Single().OldVersion, changes.Single().File.Table));
        Assert.Empty(TableUpdates.Detect([F("24", "2.4.1")], [F("24 (Stern 2009)", "2.4.1")], baseline: false)); // only renamed
        // a table without a ROM is matched by its own name
        Assert.Equal("updated", TableUpdates.Detect([F("Old EM", "1.0", null, "Old EM")], [F("Old EM v1.1", "1.1", null, "Old EM")], baseline: false).Single().Kind);
    }

    [Fact]
    public void Untrusted_fields_are_capped_and_a_bad_hash_dropped()
    {
        var f = TableUpdates.Sanitize(new TableFile(" 24 ", "TWENTY4_150", new string('x', 500), "", "a", "not-a-hash", BackglassDmdType: 99));
        Assert.Equal("24", f.Table);
        Assert.Equal(TableUpdates.MaxField, f.Name!.Length);
        Assert.Null(f.Version);
        Assert.Null(f.ScriptHash);
        Assert.Null(f.BackglassDmdType);
    }
}

public class TableUpdateFlowTests
{
    const string Owner = "111222333";

    static TableFile File24(string version, string table = "24 (Stern 2009)") =>
        new(table, "twenty4_150", "24 (Stern 2009)", version, "freneticamnesic, 32assassin, RobbyKingPin", new string('a', 64),
            "24 (Stern 2009)", "HauntFreaks", 3, AltColor: true);

    static async Task<(TestServer S, HttpClient Cab)> SetupAsync(bool channel = true, FakeVpsSource? vps = null)
    {
        var s = await TestServer.StartWithDiscordAsync(vps is null ? null : services => services.AddSingleton<PinBot.Server.Vps.IVpsSource>(vps));
        var (id, cab, _) = await s.PairCabinetAsync("Garage");
        await ExecAsync(s, $"UPDATE cabinet SET paired_by = '{Owner}' WHERE id = '{id}'");
        if (channel) await AskAsync(s, Command("channels", [Opt("updates", "chanU")], permissions: Manager, command: "pinbot-admin"));
        await ReportAsync(cab, File24("2.0")); // the first report: silent
        return (s, cab);
    }

    static async Task ReportAsync(HttpClient cab, params TableFile[] files) =>
        (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/inventory",
            new InventoryRequest([TestServer.Table("twenty4_150", "24 (Stern 2009)")], "test", files))).EnsureSuccessStatusCode();

    static async Task ExecAsync(TestServer s, string sql)
    {
        await using var conn = new NpgsqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static JsonObject DmPress(string customId, string messageId, string userId = Owner) => new()
    {
        ["type"] = 3,
        ["user"] = new JsonObject { ["id"] = userId, ["username"] = "someone" }, // a DM: no guild, no member
        ["message"] = new JsonObject { ["id"] = messageId },
        ["data"] = new JsonObject { ["custom_id"] = customId },
    };

    static async Task<string> PressAsync(TestServer s, JsonObject press)
    {
        var r = await SendAsync(s, press);
        r.EnsureSuccessStatusCode();
        var json = (await r.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(7, json["type"]!.GetValue<int>());
        return json["data"]!["content"]!.GetValue<string>();
    }

    static List<(string Id, JsonObject Message)> In(TestServer s, string channel) =>
        s.Discord!.Messages.Where(m => m.Value.Channel == channel).Select(m => (m.Key, m.Value.Message)).ToList();

    [Fact]
    public async Task Member_is_asked_once_the_batch_settles_and_their_share_is_posted()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        var publisher = s.Service<TableUpdatePublisher>();
        await ReportAsync(cab, File24("2.4.1"), new TableFile("Old EM", null, "Old EM", "1.0", "someone", null));
        await publisher.RunOnceAsync(default);
        Assert.Empty(In(s, "dm-" + Owner)); // more changes may follow: not yet

        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        var (dmId, dm) = Assert.Single(In(s, "dm-" + Owner));
        var question = dm["content"]!.GetValue<string>();
        Assert.Contains("2 table changes", question);
        Assert.Contains("2.0 → **2.4.1**", question);
        Assert.Contains("Share all", dm.ToJsonString());

        Assert.Contains("already answered", await PressAsync(s, DmPress(TableUpdatePublisher.Share, dmId, userId: "999"))); // not theirs
        Assert.Contains("Shared in <#chanU>", await PressAsync(s, DmPress(TableUpdatePublisher.Share, dmId)));
        Assert.Contains("already answered", await PressAsync(s, DmPress(TableUpdatePublisher.Share, dmId))); // once only

        await publisher.RunOnceAsync(default);
        var posts = In(s, "chanU");
        Assert.Equal(2, posts.Count);
        var post = posts.Select(p => p.Message).Single(m => m["embeds"]![0]!["title"]!.GetValue<string>() == "24 (Stern 2009)");
        var embed = post["embeds"]![0]!;
        Assert.Contains($"<@{Owner}> updated this table: 2.0 → **2.4.1**", embed["description"]!.GetValue<string>());
        Assert.Empty(post["allowed_mentions"]!["parse"]!.AsArray()); // names the member, never pings
        var setup = embed["fields"]![0]!["value"]!.GetValue<string>();
        Assert.Contains("💾 ROM: `twenty4_150`", setup);
        Assert.Contains("Backglass by HauntFreaks · full DMD", setup);
        Assert.Contains("🌈 Colorization: installed", setup);
        Assert.Equal(2, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'shared'"));
    }

    [Fact]
    public async Task Always_shares_without_asking_and_never_neither_asks_nor_posts()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        var publisher = s.Service<TableUpdatePublisher>();
        var always = await AskAsync(s, Command("updates", [Opt("share", "always")], userId: Owner));
        Assert.Contains("always shared", always["content"]!.GetValue<string>());
        await ReportAsync(cab, File24("2.4.1"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        Assert.Empty(In(s, "dm-" + Owner));
        Assert.Single(In(s, "chanU"));

        await AskAsync(s, Command("updates", [Opt("share", "never")], userId: Owner));
        await ReportAsync(cab, File24("2.5"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        Assert.Empty(In(s, "dm-" + Owner));
        Assert.Single(In(s, "chanU"));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'skipped'"));
    }

    [Fact]
    public async Task Dont_ask_again_is_remembered()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        var publisher = s.Service<TableUpdatePublisher>();
        await ReportAsync(cab, File24("2.4.1"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        var (dmId, _) = Assert.Single(In(s, "dm-" + Owner));
        Assert.Contains("won't ask again", await PressAsync(s, DmPress(TableUpdatePublisher.Never, dmId)));
        Assert.Equal(1, await s.CountAsync($"SELECT count(*) FROM member_pref WHERE discord_user_id = '{Owner}' AND share_updates = 'never'"));
        await publisher.RunOnceAsync(default);
        Assert.Empty(In(s, "chanU"));
    }

    [Fact]
    public async Task Member_picks_the_right_vps_entry_in_the_dm_and_the_post_links_it()
    {
        var (s, cab) = await SetupAsync(vps: new FakeVpsSource { Database = VpsSample.Big(1200).TrimEnd(']') + "," + VpsSample.Json.Trim().TrimStart('[') });
        await using var _ = s;
        await s.Service<PinBot.Server.Vps.VpsCatalog>().RefreshAsync(default);
        var publisher = s.Service<TableUpdatePublisher>();
        // Transformers 1.1: two VPS entries share that version and HauntFreaks; PinBot can't tell which
        await ReportAsync(cab, File24("2.0"), new TableFile("Transformers", "tf_180", "Transformers (Stern 2011)", "1.1", "HauntFreaks", null));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        var (dmId, dm) = Assert.Single(In(s, "dm-" + Owner));
        Assert.Contains("Table: not identified: pick it below", dm["content"]!.GetValue<string>());
        var dropdown = dm["components"]![1]!["components"]![0]!;
        Assert.Equal(TableUpdatePublisher.PickTable, dropdown["custom_id"]!.GetValue<string>());
        Assert.Equal(["TF1", "TF2", "none"], dropdown["options"]!.AsArray().Select(o => o!["value"]!.GetValue<string>()));

        var pick = DmPress(TableUpdatePublisher.PickTable, dmId);
        pick["data"]!["values"] = new JsonArray("TF2");
        var r = await SendAsync(s, pick);
        var redrawn = (await r.Content.ReadFromJsonAsync<JsonObject>())!["data"]!;
        Assert.Contains("Table: v1.1 by UnclePaulie, HauntFreaks (VPS `TF2`)", redrawn["content"]!.GetValue<string>());
        var bogus = DmPress(TableUpdatePublisher.PickTable, dmId);
        bogus["data"]!["values"] = new JsonArray("T241"); // another game's entry: refused
        Assert.Contains("already answered", (await (await SendAsync(s, bogus)).Content.ReadFromJsonAsync<JsonObject>())!["data"]!["content"]!.GetValue<string>());
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_vps_choice WHERE table_id = 'TF2' AND version = '1.1'"));

        await PressAsync(s, DmPress(TableUpdatePublisher.Share, dmId));
        await publisher.RunOnceAsync(default);
        var setup = Assert.Single(In(s, "chanU")).Message["embeds"]![0]!["fields"]![0]!["value"]!.GetValue<string>();
        Assert.Contains("VPS `TF2`", setup);
    }

    [Fact]
    public async Task Posts_go_out_without_links_when_vps_was_never_reachable()
    {
        var (s, cab) = await SetupAsync(vps: new FakeVpsSource { Fail = new HttpRequestException("down") });
        await using var _ = s;
        await Assert.ThrowsAsync<HttpRequestException>(() => s.Service<PinBot.Server.Vps.VpsCatalog>().RefreshAsync(default));
        await AskAsync(s, Command("updates", [Opt("share", "always")], userId: Owner));
        await ReportAsync(cab, File24("2.4.1"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await s.Service<TableUpdatePublisher>().RunOnceAsync(default);
        var setup = Assert.Single(In(s, "chanU")).Message["embeds"]![0]!["fields"]![0]!["value"]!.GetValue<string>();
        Assert.Contains("🎰 Table: v2.4.1", setup);
        Assert.DoesNotContain("](", setup);
    }

    [Fact]
    public async Task Without_an_updates_channel_nothing_is_asked_and_old_updates_expire()
    {
        var (s, cab) = await SetupAsync(channel: false);
        await using var _ = s;
        var publisher = s.Service<TableUpdatePublisher>();
        await ReportAsync(cab, File24("2.4.1"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        Assert.Empty(s.Discord!.Messages);
        s.Clock.Advance(TimeSpan.FromDays(8));
        await publisher.RunOnceAsync(default);
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'expired'"));
    }

    [Fact]
    public async Task A_newer_update_replaces_an_unanswered_one_and_closed_dms_are_not_retried()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        await ReportAsync(cab, File24("2.4.1"));
        await ReportAsync(cab, File24("2.4.2", table: "24 v2.4.2")); // renamed and updated again before being asked
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'superseded'"));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'pending' AND old_version = '2.0' AND version = '2.4.2'")); // the whole change

        s.Discord!.FailDmsTo.Clear();
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await s.Service<TableUpdatePublisher>().RunOnceAsync(default);
        Assert.Single(In(s, "dm-" + Owner));
    }

    [Fact]
    public async Task A_vpin_paired_before_owners_were_recorded_asks_its_top_scorer_and_waits_until_one_is_known()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await AskAsync(s, Command("channels", [Opt("updates", "chanU")], permissions: Manager, command: "pinbot-admin"));
        var (id, cab, _) = await s.PairCabinetAsync("Old VPin", TestServer.Table("afm_113b"));
        Assert.Equal(1, await s.CountAsync($"SELECT count(*) FROM cabinet WHERE id = '{id}' AND paired_by IS NULL"));
        await ReportAsync(cab, File24("2.0"));
        await ReportAsync(cab, File24("2.4.1"));
        var publisher = s.Service<TableUpdatePublisher>();
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'undeliverable'")); // nobody to ask yet
        Assert.Empty(s.Discord!.Messages);

        // its owner plays and scores on it: from then on they are the one asked, including about that waiting update
        await s.SetMembersAsync(("555", "Bob | [BOB]"));
        var comp = await s.StartCompetitionAsync("twenty4_150");
        s.Clock.Advance(TimeSpan.FromMinutes(5)); // a game played after the start
        var outcome = Assert.Single(await TestServer.SubmitAsync(cab, s.Event(comp.Id, "BOB", 1_000_000, rom: "twenty4_150")));
        Assert.Equal("accepted", outcome.Status);
        await publisher.RunOnceAsync(default);
        Assert.Single(In(s, "dm-555"));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'asked' AND owner_user_id = '555'"));
    }

    [Fact]
    public async Task A_sure_match_hides_the_dropdowns_until_wrong_match_is_pressed()
    {
        var (s, cab) = await SetupAsync(vps: new FakeVpsSource { Database = VpsSample.Big(1200).TrimEnd(']') + "," + VpsSample.Json.Trim().TrimStart('[') });
        await using var _ = s;
        await s.Service<PinBot.Server.Vps.VpsCatalog>().RefreshAsync(default);
        // TF2 is the only Transformers 1.1 by UnclePaulie (the sample's 24 is repeated to fill the catalog, so it can't be sure)
        await ReportAsync(cab, File24("2.0"), new TableFile("Transformers", "tf_180", "Transformers (Stern 2011)", "1.1", "UnclePaulie, HauntFreaks", null));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await s.Service<TableUpdatePublisher>().RunOnceAsync(default);
        var (dmId, dm) = Assert.Single(In(s, "dm-" + Owner));
        Assert.Contains("VPS `TF2`", dm["content"]!.GetValue<string>());
        Assert.DoesNotContain("choose the right one", dm["content"]!.GetValue<string>());
        var rows = dm["components"]!.AsArray();
        Assert.Single(rows); // buttons only
        Assert.Contains("Wrong match?", rows.ToJsonString());

        var r = await SendAsync(s, DmPress(TableUpdatePublisher.ShowPicks, dmId));
        var redrawn = (await r.Content.ReadFromJsonAsync<JsonObject>())!["data"]!;
        Assert.Contains("choose the right one", redrawn["content"]!.GetValue<string>());
        Assert.Equal(2, redrawn["components"]!.AsArray().Count); // buttons, table (no backglass on the VPin)
        Assert.DoesNotContain("Wrong match?", redrawn["components"]!.ToJsonString());
    }

    [Fact]
    public async Task A_table_read_while_being_copied_is_still_announced_as_new()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        await ReportAsync(cab); // 24 deleted
        await ReportAsync(cab, File24("2.4.1") with { Version = null, Name = null, Authors = null }); // read halfway through copying
        await ReportAsync(cab, File24("2.4.1"));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'pending' AND kind = 'added' AND old_version IS NULL AND version = '2.4.1'"));

        await ReportAsync(cab, File24("2.5")); // updated again before being asked: still new
        await ReportAsync(cab, File24("2.6"));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'pending' AND kind = 'added' AND version = '2.6'"));
    }

    [Fact]
    public async Task A_backglass_copied_after_the_table_is_in_the_post()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        await AskAsync(s, Command("updates", [Opt("share", "always")], userId: Owner));
        var noB2s = File24("2.4.1") with { BackglassName = null, BackglassAuthor = null, BackglassDmdType = null };
        await ReportAsync(cab, noB2s);
        await ReportAsync(cab, File24("2.4.1")); // the .directb2s arrives a moment later: same version, no new update
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await s.Service<TableUpdatePublisher>().RunOnceAsync(default);
        var setup = Assert.Single(In(s, "chanU")).Message["embeds"]![0]!["fields"]![0]!["value"]!.GetValue<string>();
        Assert.Contains("Backglass by HauntFreaks", setup);
    }

    [Fact]
    public async Task Two_updates_before_answering_show_the_whole_change()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        await ReportAsync(cab, File24("2.4.1"));
        await ReportAsync(cab, File24("2.4.2"));
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'pending' AND kind = 'updated' AND old_version = '2.0' AND version = '2.4.2'"));
        await ReportAsync(cab, File24("2.0")); // back to what it was: an update from 2.4.2, not "2.0 -> 2.0"
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'pending' AND old_version = '2.4.2' AND version = '2.0'"));
    }

    [Fact]
    public async Task Removing_the_table_before_answering_withdraws_the_question_and_closes_the_dm()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        var publisher = s.Service<TableUpdatePublisher>();
        var other = new TableFile("Old EM", null, "Old EM", "1.0", "someone", null);
        await ReportAsync(cab, File24("2.4.1"), other);
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        var (dmId, _) = Assert.Single(In(s, "dm-" + Owner));

        await ReportAsync(cab, other); // 24 deleted: Old EM's question is still open, so the DM stays
        await publisher.RunOnceAsync(default);
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_update WHERE state = 'removed'"));
        Assert.Empty(s.Discord!.Edits);

        await ReportAsync(cab); // Old EM deleted too: nothing left to answer
        await publisher.RunOnceAsync(default);
        await publisher.RunOnceAsync(default);
        Assert.Equal([dmId], s.Discord.Edits); // edited once
        var closed = s.Discord.Messages[dmId].Message;
        Assert.Contains("(no longer on the VPin)", closed["content"]!.GetValue<string>());
        Assert.Empty(closed["components"]!.AsArray());
        Assert.Contains("already answered", await PressAsync(s, DmPress(TableUpdatePublisher.Share, dmId)));
        Assert.Contains("removed from the VPin before you answered", (await AskAsync(s, Command("updates", userId: Owner)))["content"]!.GetValue<string>());
        await publisher.RunOnceAsync(default);
        Assert.Empty(In(s, "chanU"));
    }

    [Fact]
    public async Task A_newer_change_closes_the_old_dm_and_asks_again_and_answered_dms_are_left_alone()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        var publisher = s.Service<TableUpdatePublisher>();
        await ReportAsync(cab, File24("2.4.1"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        var (oldDm, _) = Assert.Single(In(s, "dm-" + Owner));

        await ReportAsync(cab); // deleted, then put back: a new table
        await ReportAsync(cab, File24("2.4.1"));
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await publisher.RunOnceAsync(default);
        Assert.Equal([oldDm], s.Discord!.Edits);
        Assert.Contains("(no longer on the VPin)", s.Discord.Messages[oldDm].Message["content"]!.GetValue<string>());
        var newDm = Assert.Single(In(s, "dm-" + Owner), m => m.Id != oldDm).Id;
        Assert.Contains("**24 (Stern 2009)**: new", s.Discord.Messages[newDm].Message["content"]!.GetValue<string>());

        await PressAsync(s, DmPress(TableUpdatePublisher.Skip, newDm));
        await ReportAsync(cab); // deleted after answering: the answer stands
        await publisher.RunOnceAsync(default);
        Assert.Equal([oldDm], s.Discord.Edits);
    }

    [Fact]
    public async Task Updates_command_lists_what_happened_to_recent_changes()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        var none = (await AskAsync(s, Command("updates", userId: Owner)))["content"]!.GetValue<string>();
        Assert.Contains("**Garage**: tables last reported", none);
        Assert.Contains("No new table or version noticed", none);

        await ReportAsync(cab, File24("2.4.1"));
        var waiting = (await AskAsync(s, Command("updates", userId: Owner)))["content"]!.GetValue<string>();
        Assert.Contains("**24 (Stern 2009)** updated, 2.0 → **2.4.1**", waiting);
        Assert.Contains("about to ask you", waiting);
        Assert.Contains("No VPin is linked to you", (await AskAsync(s, Command("updates", userId: "999")))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_agent_without_files_leaves_the_tables_alone()
    {
        var (s, cab) = await SetupAsync();
        await using var _ = s;
        (await cab.PutAsJsonAsync($"{ApiV1.Prefix}/agent/inventory",
            new InventoryRequest([TestServer.Table("twenty4_150", "24 (Stern 2009)")], "old agent"))).EnsureSuccessStatusCode();
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM table_file"));
        Assert.Equal(0, await s.CountAsync("SELECT count(*) FROM table_update"));
    }

    [Fact]
    public async Task Channels_command_shows_and_sets_the_updates_channel()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        var shown = await AskAsync(s, Command("channels", permissions: Manager, command: "pinbot-admin"));
        Assert.Contains("Table updates: not set (off)", shown["content"]!.GetValue<string>());
        var set = await AskAsync(s, Command("channels", [Opt("updates", "chanU")], permissions: Manager, command: "pinbot-admin"));
        Assert.Contains("Table updates: <#chanU>", set["content"]!.GetValue<string>());
    }
}
