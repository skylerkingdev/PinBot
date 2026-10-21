using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PinBot.Server.Discord;
using static PinBot.Server.Tests.DiscordRequests;

namespace PinBot.Server.Tests;

public class WeeklyTests
{
    static readonly DateTime Monday1900 = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc); // 2026-10-12 is a Monday
    static readonly DateTime Wednesday = Monday1900.AddDays(2);

    static JsonArray Schedule(bool enabled = true, string? day = "Monday", string? time = "19:00", string? zone = "UTC")
    {
        var a = new JsonArray(Opt("enabled", enabled));
        if (day != null) a.Add(Opt("day", day));
        if (time != null) a.Add(Opt("time", time));
        if (zone != null) a.Add(Opt("timezone", zone));
        return a;
    }

    static async Task<string> WeeklyAsync(TestServer s, JsonArray options, ulong permissions = Manager) =>
        (await AskAsync(s, Command("weekly", options, permissions: permissions, command: "pinbot-admin")))["content"]!.GetValue<string>();

    static string T(DateTime utc, string style) => $"<t:{new DateTimeOffset(utc).ToUnixTimeSeconds()}:{style}>";

    static async Task<TestServer> ServerAsync(params string[] roms)
    {
        var s = await TestServer.StartWithDiscordAsync();
        await s.PairCabinetAsync("VPin", roms.Select(r => TestServer.Table(r, r.ToUpperInvariant())).ToArray());
        return s;
    }

    [Fact]
    public async Task Nothing_runs_until_a_manager_sets_the_schedule_once()
    {
        await using var s = await ServerAsync("afm_113b");
        Assert.Contains("Only server managers", await WeeklyAsync(s, Schedule(), permissions: 0));
        Assert.Contains("First set the schedule", await WeeklyAsync(s, Schedule(day: null, time: null, zone: null)));
        Assert.Contains("HH:MM", await WeeklyAsync(s, Schedule(time: "7pm")));
        s.Clock.UtcNow = Monday1900.AddMinutes(1);
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync());
        Assert.Equal(0, await s.CountAsync("SELECT count(*) FROM competition"));
    }

    [Fact]
    public async Task Setting_it_up_starts_the_first_weekly_right_away_on_any_day()
    {
        await using var s = await ServerAsync("afm_113b", "tz_94h");
        s.Clock.UtcNow = Wednesday;
        var reply = await WeeklyAsync(s, Schedule());
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync()); // the scheduler does not add a second one
        Assert.Contains("The first one starts **now**", reply);
        Assert.Contains($"until {T(Monday1900.AddDays(7), "F")}", reply); // runs to the next Monday 19:00 slot
        Assert.Equal(1, await s.CountAsync("SELECT count(*) FROM competition WHERE kind = 'weekly' AND ends_at_utc = '2026-10-19 19:00:00+00'"));

        // from then on the regular schedule: the next one starts at that slot, on a different game
        s.Clock.UtcNow = Monday1900.AddDays(7).AddMinutes(1);
        var second = await s.Service<WeeklyScheduler>().RunOnceAsync();
        Assert.Equal((Monday1900.AddDays(7), Monday1900.AddDays(14)), (second!.StartsAtUtc, second.EndsAtUtc));
        Assert.Equal(2, await s.CountAsync("SELECT count(DISTINCT canonical_game_id) FROM competition WHERE kind = 'weekly'"));
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync());
        await s.RestartAsync();
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync());
        Assert.Equal(2, await s.CountAsync("SELECT count(*) FROM competition WHERE kind = 'weekly'"));
    }

    [Fact]
    public async Task A_first_weekly_never_lasts_under_two_days()
    {
        await using var s = await ServerAsync("afm_113b");
        s.Clock.UtcNow = Monday1900.AddHours(-1); // the slot is an hour away
        var reply = await WeeklyAsync(s, Schedule());
        Assert.Contains($"until {T(Monday1900.AddDays(7), "F")}", reply); // runs through to the following slot
    }

    [Fact]
    public async Task A_change_while_a_weekly_runs_confirms_and_applies_after_it_ends()
    {
        await using var s = await ServerAsync("afm_113b", "tz_94h");
        s.Clock.UtcNow = Monday1900;
        await WeeklyAsync(s, Schedule()); // first weekly: now until next Monday 19:00

        s.Clock.UtcNow = Monday1900.AddDays(1); // Tuesday: move it to Wednesday 20:30 Eastern
        var reply = await WeeklyAsync(s, Schedule(day: "Wednesday", time: "20:30", zone: "America/New_York"));
        Assert.Contains("Wednesday at 20:30 Eastern", reply);
        Assert.Contains($"continues until {T(Monday1900.AddDays(7), "f")}", reply);
        var nextStart = new DateTime(2026, 10, 22, 0, 30, 0, DateTimeKind.Utc); // Wed 2026-10-21 20:30 EDT, first slot after it ends
        Assert.Contains($"The next one starts {T(nextStart, "F")}", reply);

        s.Clock.UtcNow = Monday1900.AddDays(7).AddMinutes(1); // old weekly ended; nothing until the new slot
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync());
        s.Clock.UtcNow = nextStart.AddMinutes(1);
        Assert.Equal(nextStart, (await s.Service<WeeklyScheduler>().RunOnceAsync())!.StartsAtUtc);
    }

    [Fact]
    public async Task Turning_it_back_on_after_a_pause_also_starts_one_now()
    {
        await using var s = await ServerAsync("afm_113b", "tz_94h");
        s.Clock.UtcNow = Monday1900;
        await WeeklyAsync(s, Schedule());
        s.Clock.UtcNow = Monday1900.AddDays(8);
        Assert.Contains("**off**", await WeeklyAsync(s, Schedule(enabled: false, day: null, time: null, zone: null)));
        s.Clock.UtcNow = Monday1900.AddDays(16); // Wednesday, a week later
        Assert.Contains("The first one starts **now**", await WeeklyAsync(s, Schedule(day: null, time: null, zone: null)));
    }

    [Fact]
    public async Task No_free_game_is_explained_and_a_skipped_week_announced_in_the_weekly_channel()
    {
        await using var s = await ServerAsync("afm_113b");
        s.Clock.UtcNow = Monday1900.AddDays(-2);
        await AskAsync(s, Command("start", [Opt("game", "afm_113b"), Opt("length", 72)])); // the only game is busy until Tuesday
        var reply = await WeeklyAsync(s, Schedule());
        Assert.Contains("No eligible game is free", reply);
        Assert.Contains($"The next one starts {T(Monday1900, "F")}", reply);

        s.Clock.UtcNow = Monday1900.AddMinutes(1); // still busy at the slot: skipped, announced once
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync());
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync());
        var (channel, notice) = Assert.Single(s.Discord!.Messages.Values);
        Assert.Equal("chanW", channel);
        Assert.Contains("No weekly competition this week", notice["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_one_game_library_repeats_the_weekly_rather_than_skipping()
    {
        await using var s = await ServerAsync("afm_113b");
        s.Clock.UtcNow = Wednesday.AddDays(-7);
        await WeeklyAsync(s, Schedule()); // first weekly runs until Monday1900
        s.Clock.UtcNow = Monday1900.AddMinutes(1);
        Assert.Equal("afm_113b", (await s.Service<WeeklyScheduler>().RunOnceAsync())!.CanonicalGameId);
    }

    [Fact]
    public async Task Weekly_and_one_off_leaderboards_go_to_their_own_channels()
    {
        await using var s = await ServerAsync("afm_113b", "tz_94h");
        s.Clock.UtcNow = Wednesday;
        await WeeklyAsync(s, Schedule());
        var weeklyGame = (await s.Admin.GetFromJsonAsync<List<CompetitionView>>("/api/v1/admin/competitions"))!.Single().CanonicalGameId;
        await AskAsync(s, Command("start", [Opt("game", weeklyGame == "afm_113b" ? "tz_94h" : "afm_113b")]));

        await s.Service<LeaderboardPublisher>().RunOnceAsync();
        var byChannel = s.Discord!.Messages.Values.ToDictionary(m => m.Channel, m => FakeDiscord.Body(m.Message));
        Assert.Contains("Weekly competition", byChannel["chanW"]);
        Assert.Contains("Started by", byChannel["chan1"]);

        // a plain-text headline so notifications say what started; no mentions
        var headlines = s.Discord.Messages.Values.ToDictionary(m => m.Channel, m => m.Message["content"]!.GetValue<string>());
        Assert.StartsWith("📣 New weekly competition: **", headlines["chanW"]);
        Assert.StartsWith("📣 New challenge from **Alex**: **", headlines["chan1"]);
        Assert.All(s.Discord.Messages.Values, m => Assert.Empty(m.Message["allowed_mentions"]!["parse"]!.AsArray()));
    }

    [Fact]
    public async Task Server_off_for_the_first_day_of_a_slot_skips_it_rather_than_starting_late()
    {
        await using var s = await ServerAsync("afm_113b", "tz_94h");
        s.Clock.UtcNow = Wednesday;
        await WeeklyAsync(s, Schedule()); // runs until the next Monday 19:00
        s.Clock.UtcNow = Monday1900.AddDays(9);  // server was off from that Monday until Wednesday
        Assert.Null(await s.Service<WeeklyScheduler>().RunOnceAsync());
    }

    [Theory]
    [InlineData("2026-10-12T19:00:00", "2026-10-12T19:00:00", "2026-10-12T19:00:00")] // exactly at the slot
    [InlineData("2026-10-12T18:59:59", "2026-10-05T19:00:00", "2026-10-12T19:00:00")] // just before
    [InlineData("2026-10-15T08:00:00", "2026-10-12T19:00:00", "2026-10-19T19:00:00")] // later that week
    public void Current_and_next_slots(string now, string current, string next)
    {
        var w = new WeeklySchedule(DayOfWeek.Monday, TimeSpan.FromHours(19), TimeZoneInfo.Utc);
        static DateTime U(string s) => DateTime.SpecifyKind(DateTime.Parse(s), DateTimeKind.Utc);
        Assert.Equal(U(current), WeeklyScheduler.CurrentSlot(w, U(now)));
        Assert.Equal(U(next), WeeklyScheduler.NextSlot(w, U(now)));
    }

    [Fact]
    public void Slots_keep_local_time_across_daylight_saving_changes()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var w = new WeeklySchedule(DayOfWeek.Monday, TimeSpan.FromHours(19), eastern);
        // DST ends Sunday 2026-11-01: Monday 19:00 Eastern is 23:00 UTC before, 00:00 UTC (Tuesday) after
        Assert.Equal(new DateTime(2026, 10, 26, 23, 0, 0, DateTimeKind.Utc), WeeklyScheduler.NextSlot(w, new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), WeeklyScheduler.NextSlot(w, new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc)));
    }
}
