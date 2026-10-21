using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace PinBot.Server.Discord;

/// <summary>
/// Discord HTTP interactions: one /pinbot command with subcommands. Every request is verified (Ed25519 over the raw
/// body + a fresh timestamp) before it is read; permissions are re-checked here, never trusted from Discord's UI.
/// All replies are immediate; slower work (leaderboard messages) happens in the background.
/// </summary>
public static class Interactions
{
    const int Ephemeral = 64;
    const ulong Administrator = 1UL << 3, ManageGuild = 1UL << 5;
    const int MaxActiveOneOffsPerMember = 2;
    const int MaxPairingCodesPerHour = 3;
    static readonly int[] DurationHours = [1, 2, 4, 24, 72, 168];
    const int DefaultHours = 72;
    const int MaxChoices = 25;   // Discord's limit for autocomplete lists
    const string MoreHint = "?"; // value of the "type to search" hint entry
    const string RunningMark = " (already running)";

    /// <summary>
    /// Two commands: /pinbot for everyone, and /pinbot-admin for server managers. Discord shows /pinbot-admin only to
    /// members with Manage Server (default_member_permissions 32), so others never see settings they cannot change; the
    /// handlers check the permission again.
    /// </summary>
    public static JsonArray CommandDefinitions() => JsonNode.Parse("""
        [{
          "name": "pinbot", "description": "Virtual pinball competitions", "type": 1, "dm_permission": false,
          "options": [
            { "type": 1, "name": "start", "description": "Start a competition on a game",
              "options": [
                { "type": 3, "name": "game", "description": "Type part of the game's name", "required": true, "autocomplete": true },
                { "type": 4, "name": "length", "description": "How long it runs (default 3 days)", "required": false,
                  "choices": [ { "name": "1 hour", "value": 1 }, { "name": "2 hours", "value": 2 }, { "name": "4 hours", "value": 4 },
                               { "name": "1 day", "value": 24 }, { "name": "3 days", "value": 72 }, { "name": "7 days", "value": 168 } ] } ] },
            { "type": 1, "name": "status", "description": "Running competitions, their leaders, and every VPin" },
            { "type": 1, "name": "games", "description": "Every game you can start a competition on" },
            { "type": 1, "name": "guest", "description": "Let a guest without Discord score with their own initials for 24 hours",
              "options": [
                { "type": 3, "name": "initials", "description": "The initials they will enter (1-3 letters or digits)", "required": true, "min_length": 1, "max_length": 3 },
                { "type": 3, "name": "name", "description": "Their name, shown on leaderboards (optional)", "required": false, "max_length": 24 } ] },
            { "type": 1, "name": "end", "description": "End a competition early (one you started; managers: any)",
              "options": [ { "type": 4, "name": "competition", "description": "Which competition to end", "required": true, "autocomplete": true } ] },
            { "type": 1, "name": "pair", "description": "Connect your VPin: get pinbot.exe and a one-time pairing line" },
            { "type": 1, "name": "help", "description": "How PinBot works (only you see it)" }
          ]
        },
        {
          "name": "pinbot-admin", "description": "PinBot settings (server managers)", "type": 1, "dm_permission": false,
          "default_member_permissions": "32",
          "options": [
            { "type": 1, "name": "channels", "description": "Choose where PinBot posts (leave empty to see the current channels)",
              "options": [
                { "type": 7, "name": "challenges", "description": "Channel for challenge leaderboards", "required": false, "channel_types": [0] },
                { "type": 7, "name": "weekly", "description": "Channel for weekly competition leaderboards", "required": false, "channel_types": [0] },
                { "type": 7, "name": "info", "description": "Channel for the guide, new VPins and update news", "required": false, "channel_types": [0] } ] },
            { "type": 1, "name": "weekly", "description": "Automatic weekly competition: on/off and when it starts",
              "options": [
                { "type": 5, "name": "enabled", "description": "On or off", "required": true },
                { "type": 3, "name": "day", "description": "Start day", "required": false,
                  "choices": [ { "name": "Monday", "value": "Monday" }, { "name": "Tuesday", "value": "Tuesday" }, { "name": "Wednesday", "value": "Wednesday" },
                               { "name": "Thursday", "value": "Thursday" }, { "name": "Friday", "value": "Friday" }, { "name": "Saturday", "value": "Saturday" },
                               { "name": "Sunday", "value": "Sunday" } ] },
                { "type": 3, "name": "time", "description": "Start time, 24-hour HH:MM (e.g. 19:00)", "required": false, "min_length": 4, "max_length": 5 },
                { "type": 3, "name": "timezone", "description": "Time zone for day and time", "required": false,
                  "choices": [ { "name": "Eastern (US/Canada)", "value": "America/New_York" }, { "name": "Central (US/Canada)", "value": "America/Chicago" },
                               { "name": "Mountain (US/Canada)", "value": "America/Denver" }, { "name": "Arizona", "value": "America/Phoenix" },
                               { "name": "Pacific (US/Canada)", "value": "America/Los_Angeles" }, { "name": "UK", "value": "Europe/London" },
                               { "name": "Central Europe", "value": "Europe/Berlin" }, { "name": "UTC", "value": "UTC" } ] } ] }
          ]
        }]
        """)!.AsArray();

    public static void MapDiscordInteractions(this WebApplication app) =>
        app.MapPost("/api/v1/discord/interactions", HandleAsync);

    sealed record Caller(string UserId, string DisplayName, ulong Permissions)
    {
        public bool IsManager => (Permissions & (Administrator | ManageGuild)) != 0;
    }

    static async Task<IResult> HandleAsync(HttpContext ctx, DiscordOptions discord, Db db, TimeProvider clock,
        ServerOptions options, LeaderboardPublisher publisher, WeeklyScheduler weekly, CompetitionChanges changes)
    {
        if (!discord.Enabled) return Results.NotFound();
        // anyone can call this address: refuse oversized bodies before buffering them (Discord's are a few KB)
        if (ctx.Request.ContentLength > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (await ReadLimitedAsync(ctx.Request.Body, MaxBodyBytes, ctx.RequestAborted) is not { } body)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var now = clock.GetUtcNow().UtcDateTime;
        if (!Verify(discord.PublicKey!, ctx.Request.Headers["X-Signature-Ed25519"], ctx.Request.Headers["X-Signature-Timestamp"], body, now))
            return Results.Unauthorized();

        var interaction = JsonNode.Parse(body)!;
        var type = interaction["type"]!.GetValue<int>();
        if (type == 1) return Json(new JsonObject { ["type"] = 1 }); // PING
        if (interaction["guild_id"]?.GetValue<string>() != discord.GuildId) return Reply("PinBot is not set up for this server.", ephemeral: true);

        var member = interaction["member"]!;
        var user = member["user"]!;
        var caller = new Caller(user["id"]!.GetValue<string>(),
            member["nick"]?.GetValue<string>() ?? user["global_name"]?.GetValue<string>() ?? user["username"]!.GetValue<string>(),
            ulong.Parse(member["permissions"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture));
        // a button on one of PinBot's replies (type 3): the ◀ / ▶ pages of /pinbot games, or Start anyway / Cancel
        if (type == 3)
        {
            var id = interaction["data"]?["custom_id"]?.GetValue<string>() ?? "";
            if (id == StartCancel) return Update("Not started.");
            await using var c = await db.OpenAsync();
            if (id.StartsWith(GamesPagePrefix, StringComparison.Ordinal) && int.TryParse(id[GamesPagePrefix.Length..], out var page))
                return await GamesAsync(c, now, options, caller, page, update: true);
            // "start:<game>:<hours>" from the "Start anyway" button
            if (id.StartsWith(StartPrefix, StringComparison.Ordinal) && id[StartPrefix.Length..].Split(':') is [var game, var h]
                && int.TryParse(h, out var hours) && DurationHours.Contains(hours))
                return await StartGameAsync(c, game, hours, caller, now, discord, publisher, changes, null, confirmed: true);
            return Results.BadRequest();
        }

        var sub = interaction["data"]!["options"]![0]!;
        var args = (sub["options"]?.AsArray() ?? []).ToDictionary(o => o!["name"]!.GetValue<string>(), o => o!);

        await using var conn = await db.OpenAsync();
        if (type == 4) return await AutocompleteAsync(conn, sub["name"]!.GetValue<string>(), args, caller, now, options);
        if (type != 2) return Results.BadRequest();

        return (interaction["data"]!["name"]?.GetValue<string>(), sub["name"]!.GetValue<string>()) switch
        {
            ("pinbot-admin", "channels") => await ChannelsAsync(conn, args, caller, discord, publisher),
            ("pinbot-admin", "weekly") => await WeeklyAsync(conn, args, caller, now, weekly),
            (_, "start") => await StartAsync(conn, args, caller, now, discord, publisher, changes, interaction["channel_id"]?.GetValue<string>()),
            (_, "status") => await StatusAsync(conn, now, options, discord),
            (_, "games") => await GamesAsync(conn, now, options, caller),
            (_, "help") => await HelpAsync(conn, discord),
            (_, "end") => await EndAsync(conn, args, caller, now, options, publisher, changes),
            (_, "pair") => await PairAsync(conn, caller, now, options, discord),
            (_, "guest") => await GuestAsync(conn, args, caller, now, options, publisher),
            _ => Reply("Unknown command.", ephemeral: true),
        };
    }

    internal const int MaxBodyBytes = 64 * 1024;

    /// <summary>The whole body, or null when it is longer than <paramref name="limit"/> bytes (not read further).</summary>
    static async Task<byte[]?> ReadLimitedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > limit) return null;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>Discord signs timestamp + raw body with the application's Ed25519 key. Stale timestamps (> 5 min) are refused.</summary>
    internal static bool Verify(string publicKeyHex, string? signatureHex, string? timestamp, byte[] body, DateTime now)
    {
        if (string.IsNullOrEmpty(signatureHex) || string.IsNullOrEmpty(timestamp)) return false;
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)) return false;
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime).TotalMinutes) > 5) return false;
        byte[] signature, key;
        try { signature = Convert.FromHexString(signatureHex); key = Convert.FromHexString(publicKeyHex); }
        catch (FormatException) { return false; }
        if (signature.Length != 64 || key.Length != 32) return false;
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
        var ts = Encoding.UTF8.GetBytes(timestamp);
        verifier.BlockUpdate(ts, 0, ts.Length);
        verifier.BlockUpdate(body, 0, body.Length);
        return verifier.VerifySignature(signature);
    }

    // ---------------- commands ----------------

    static async Task<IResult> StartAsync(NpgsqlConnection conn, Dictionary<string, JsonNode> args, Caller caller, DateTime now,
        DiscordOptions discord, LeaderboardPublisher publisher, CompetitionChanges changes, string? fromChannel)
    {
        var typed = args["game"]["value"]!.GetValue<string>().Trim();
        var hours = args.TryGetValue("length", out var l) ? l["value"]!.GetValue<int>() : DefaultHours;
        if (!DurationHours.Contains(hours)) return Reply("Pick a length from the list.", ephemeral: true);
        if (typed == MoreHint) return Reply("Type part of the game's name in **game** to find it, or see them all with `/pinbot games`.", ephemeral: true);
        var game = await ResolveGameAsync(conn, typed);
        if (game is null)
            return Reply($"No game on a connected VPin matches **{Md(typed)}**. Type part of its name and pick it from the list, or see them all with `/pinbot games`.", ephemeral: true);
        return await StartGameAsync(conn, game, hours, caller, now, discord, publisher, changes, fromChannel, confirmed: false);
    }

    const string StartPrefix = "start:", StartCancel = "start:cancel";

    /// <summary>
    /// Starts <paramref name="game"/>. A game only one cabinet has (where several are paired) is confirmed first: the
    /// starter sees whose it is and "Start anyway" / "Cancel" buttons. From those buttons (<paramref name="confirmed"/>),
    /// the answer replaces the question.
    /// </summary>
    static async Task<IResult> StartGameAsync(NpgsqlConnection conn, string game, int hours, Caller caller, DateTime now,
        DiscordOptions discord, LeaderboardPublisher publisher, CompetitionChanges changes, string? fromChannel, bool confirmed)
    {
        IResult Answer(string text, bool ephemeral = true) => confirmed ? Update(text) : Reply(text, ephemeral);

        var running = await conn.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM competition WHERE kind = 'oneoff' AND created_by = @id AND cancelled_at_utc IS NULL
              AND starts_at_utc <= @now AND LEAST(ends_at_utc, COALESCE(ended_early_at_utc, ends_at_utc)) > @now
            """, new { id = caller.UserId, now });
        if (running >= MaxActiveOneOffsPerMember && !caller.IsManager) // managers are trusted; members cannot flood the channel
            return Answer($"You already have {running} competitions running. Let one finish (or `/pinbot end` it) first.");

        if (!confirmed && (await Competitions.EligibleGamesAsync(conn, caller.UserId)).FirstOrDefault(g => g.CanonicalGameId == game) is { Cabinets: 1, Where: { } only } info
            && only != "all")
            return Json(new JsonObject
            {
                ["type"] = 4,
                ["data"] = new JsonObject
                {
                    ["flags"] = Ephemeral,
                    ["content"] = $"Only **{Md(only)}** has **{Md(info.Title)}**, so nobody else can play it. Start anyway?",
                    ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
                    ["components"] = new JsonArray(new JsonObject
                    {
                        ["type"] = 1,
                        ["components"] = new JsonArray(
                            new JsonObject { ["type"] = 2, ["style"] = 1, ["label"] = "Start anyway", ["custom_id"] = $"{StartPrefix}{game}:{hours}" },
                            new JsonObject { ["type"] = 2, ["style"] = 2, ["label"] = "Cancel", ["custom_id"] = StartCancel }),
                    }),
                },
            });

        // validated again here: someone may have started this game while the menu was open
        var result = await Competitions.CreateAsync(conn, game, "oneoff", caller.UserId, now, now.AddHours(hours), now, caller.DisplayName);
        switch (result)
        {
            case CreateResult.Created c:
                publisher.Wake();
                changes.Notify(); // cabinets prepare it within seconds
                var cardChannel = discord.ChannelFor("oneoff");
                var where = cardChannel is { } ch ? $" Leaderboard in <#{ch}>." : "";
                // in the card's own channel the card (with its 📣 headline) is the announcement: one notification, not two
                var quiet = fromChannel != null && fromChannel == cardChannel;
                return Answer($"🎯 **{Md(Players.StripTag(caller.DisplayName))}** started **{Md(c.Competition.Title)}**: ends {Ts(c.Competition.EffectiveEndUtc, "f")} ({Ts(c.Competition.EffectiveEndUtc, "R")}). " +
                              $"Connected VPins get it within seconds (the leaderboard shows which are ready). Play it and enter the initials from your server nickname.{where}", quiet);
            case CreateResult.Conflict c:
                return Answer($"**{Md(c.Existing.Title)}** already has competition #{c.Existing.Id} running until {Ts(c.Existing.EffectiveEndUtc, "f")}.");
            case CreateResult.NotEligible:
                return Answer("That game is not available on any connected VPin. Pick one from the list as you type.");
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// /pinbot guest ABC [name]: for 24 hours, scores entered as ABC count for the caller's guest (a player without a
    /// Discord account). Running it again extends the time. Initials a member uses, or another member's guest is using,
    /// are refused. Scores already played with those initials count too, until their competition is final.
    /// </summary>
    static async Task<IResult> GuestAsync(NpgsqlConnection conn, Dictionary<string, JsonNode> args, Caller caller, DateTime now,
        ServerOptions options, LeaderboardPublisher publisher)
    {
        var initials = Players.NormalizeInitials(args["initials"]["value"]!.GetValue<string>());
        var name = args.TryGetValue("name", out var n) ? n["value"]!.GetValue<string>().Trim() : null;
        if (initials.Length is 0 or > 3 || !initials.All(char.IsAsciiLetterOrDigit))
            return Reply("Initials are 1 to 3 letters or digits, as entered on the machine.", ephemeral: true);
        if (await conn.QueryFirstOrDefaultAsync<(string User, string Display)?>(
                "SELECT discord_user_id, display_name FROM member_initial WHERE initials = @initials LIMIT 1", new { initials }) is { } member)
            return Reply(member.User == caller.UserId ? $"**{initials}** are your own initials: your scores already count."
                : $"**{initials}** belong to **{Md(Players.StripTag(member.Display))}**. Pick other initials for your guest.", ephemeral: true);
        var id = Players.GuestId(caller.UserId, initials);
        if (await conn.QueryFirstOrDefaultAsync<string?>(
                "SELECT display_name FROM guest WHERE initials = @initials AND id <> @id AND expires_at_utc > @now", new { initials, id, now }) is { } other)
            return Reply($"**{initials}** are in use by **{Md(other)}** for now. Pick other initials.", ephemeral: true);
        var hosting = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM guest WHERE host_user_id = @host AND id <> @id AND expires_at_utc > @now", new { host = caller.UserId, id, now });
        if (hosting >= Players.MaxGuestsPerHost) return Reply($"You already have {hosting} guests. Their 24 hours have to end first.", ephemeral: true);

        var host = Players.StripTag(caller.DisplayName);
        var display = name is { Length: > 0 } ? $"{name} ({host}'s guest {initials})" : $"{host}'s guest {initials}";
        var expires = now + Players.GuestFor;
        await conn.ExecuteAsync("""
            INSERT INTO guest (id, initials, host_user_id, display_name, expires_at_utc) VALUES (@id, @initials, @host, @display, @expires)
            ON CONFLICT (id) DO UPDATE SET display_name = EXCLUDED.display_name, expires_at_utc = EXCLUDED.expires_at_utc
            """, new { id, initials, host = caller.UserId, display, expires });
        var credited = await MemberSync.ReresolveAsync(conn, now, options.UploadGrace, initials); // games they already played
        if (credited > 0) publisher.Wake();
        return Reply($"👥 Until {Ts(expires, "f")}, scores entered as **{initials}** count for **{Md(display)}**." +
                     (credited > 0 ? $" {credited} earlier score{(credited == 1 ? "" : "s")} now count{(credited == 1 ? "s" : "")} too." : "") +
                     " Run it again to extend.", ephemeral: true);
    }

    /// <summary>Replaces the message a button was on (Discord interaction response type 7), removing its buttons.</summary>
    internal static IResult Update(string content) => Json(new JsonObject
    {
        ["type"] = 7,
        ["data"] = new JsonObject
        {
            ["content"] = content,
            ["components"] = new JsonArray(),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        },
    });

    /// <summary>
    /// The game for a /pinbot start value: normally the id from the search list, but Discord sends whatever was typed
    /// when no entry was picked, so an exact or unambiguous partial title also works.
    /// </summary>
    static async Task<string?> ResolveGameAsync(NpgsqlConnection conn, string typed)
    {
        var games = await Competitions.EligibleGamesAsync(conn);
        var id = await Competitions.CanonicalGameAsync(conn, typed);
        if (games.Any(g => g.CanonicalGameId == id)) return id;
        var title = typed.EndsWith(RunningMark, StringComparison.Ordinal) ? typed[..^RunningMark.Length].Trim() : typed;
        if (title.Length == 0) return null;
        var exact = games.Where(g => g.Title.Equals(title, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].CanonicalGameId;
        var partial = games.Where(g => g.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count == 0 && partial.Count == 1 ? partial[0].CanonicalGameId : null;
    }

    static async Task<HashSet<string>> BusyGamesAsync(NpgsqlConnection conn, DateTime now, ServerOptions options) =>
        (await Competitions.ListAsync(conn))
            .Where(c => c.Status(now, options.UploadGrace) is "active" or "scheduled").Select(c => c.CanonicalGameId).ToHashSet();

    static async Task<IResult> GamesAsync(NpgsqlConnection conn, DateTime now, ServerOptions options, Caller caller, int page = 0, bool update = false)
    {
        var games = await Competitions.EligibleGamesAsync(conn, caller.UserId);
        if (games.Count == 0) return Reply("No games yet: connect a VPin first (`/pinbot pair`).", ephemeral: true);
        var busy = await BusyGamesAsync(conn, now, options);
        var pages = GamePages(games.Select(g => $"• {Md(g.Choice)}{(busy.Contains(g.CanonicalGameId) ? " *(running)*" : "")}\n"));
        page = Math.Clamp(page, 0, pages.Count - 1);
        var data = new JsonObject
        {
            ["flags"] = Ephemeral,
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = $"{games.Count} game{(games.Count == 1 ? "" : "s")}{(pages.Count > 1 ? $" · page {page + 1} of {pages.Count}" : "")}",
                ["description"] = pages[page].TrimEnd(),
                ["footer"] = new JsonObject { ["text"] = "Start one with /pinbot start and type part of its name" },
            }),
            ["components"] = pages.Count > 1 ? new JsonArray(new JsonObject
            {
                ["type"] = 1,
                ["components"] = new JsonArray(PageButton("◀ Previous", page - 1, page == 0), PageButton("Next ▶", page + 1, page == pages.Count - 1)),
            }) : new JsonArray(),
        };
        return Json(new JsonObject { ["type"] = update ? 7 : 4, ["data"] = data }); // 7: the buttons edit the list in place
    }

    const string GamesPagePrefix = "games:";

    static JsonObject PageButton(string label, int page, bool disabled) => new()
    {
        ["type"] = 2, ["style"] = 2, ["label"] = label, ["custom_id"] = $"{GamesPagePrefix}{page}", ["disabled"] = disabled,
    };

    /// <summary>The game lines split into pages that fit an embed (4096 characters), whole lines only.</summary>
    internal static List<string> GamePages(IEnumerable<string> lines, int limit = 3800)
    {
        var pages = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length > limit && sb.Length > 0) { pages.Add(sb.ToString()); sb.Clear(); }
            sb.Append(line);
        }
        if (sb.Length > 0 || pages.Count == 0) pages.Add(sb.ToString());
        return pages;
    }

    static async Task<IResult> StatusAsync(NpgsqlConnection conn, DateTime now, ServerOptions options, DiscordOptions discord)
    {
        var comps = (await Competitions.ListAsync(conn))
            .Where(c => c.Status(now, options.UploadGrace) is "active" or "finalizing" or "scheduled")
            .OrderBy(c => c.EffectiveEndUtc).ToList();
        var sb = new StringBuilder();
        if (comps.Count == 0) sb.AppendLine("No competitions running. Start one with `/pinbot start`.");
        foreach (var c in comps)
        {
            var top = (await Competitions.LeaderboardAsync(conn, c.Id)).FirstOrDefault();
            var leader = top is null ? "no scores yet" : $"leader **{Md(Players.StripTag(top.DisplayName))}** {top.Score:N0}";
            var state = c.Status(now, options.UploadGrace) switch
            {
                "active" => $"ends {Ts(c.EffectiveEndUtc, "R")}",
                "scheduled" => $"starts {Ts(c.StartsAtUtc, "R")}",
                _ => "ended, final standings in a few minutes",
            };
            var link = c.DiscordChannelId != null && c.DiscordMessageId != null ? $" · [leaderboard](https://discord.com/channels/{discord.GuildId}/{c.DiscordChannelId}/{c.DiscordMessageId})" : "";
            sb.AppendLine($"**#{c.Id} {Md(c.Title)}**{(c.Kind == "weekly" ? " (weekly)" : "")}: {state}, {leader}{link}");
        }
        var cabinets = await CabinetLinesAsync(conn, now, discord);
        if (cabinets.Length > 0) sb.Append("\n**VPins**\n").Append(cabinets);
        return Reply(sb.ToString().TrimEnd(), ephemeral: true);
    }

    /// <summary>
    /// One line per paired cabinet: online or not, and whether its PinBot is current. A copy older than the first signed
    /// release cannot update itself and needs the download once; a signed one updates itself when no table is running.
    /// </summary>
    internal static async Task<string> CabinetLinesAsync(NpgsqlConnection conn, DateTime now, DiscordOptions discord, string? manifestPath = null)
    {
        var latest = AgentDownload.LatestRelease(manifestPath);
        var download = discord.PublicUrl is { } url ? $"<{url}{AgentDownload.Route}>" : "the latest pinbot.exe";
        var sb = new StringBuilder();
        var labels = await Players.CabinetLabelsAsync(conn);
        foreach (var (id, name, seen, version, playing) in await conn.QueryAsync<(string, string, DateTime?, string?, DateTime?)>(
                     "SELECT id, name, last_seen_at_utc, agent_version, playing_since FROM cabinet WHERE revoked_at_utc IS NULL ORDER BY name"))
        {
            var online = seen is { } s && now - DateTime.SpecifyKind(s, DateTimeKind.Utc) < TimeSpan.FromMinutes(2);
            var label = labels.GetValueOrDefault(id, name); // the owner's tag, and here the PC name too: this list is about machines
            var who = Md(label.Contains(name) ? label : $"{label} {name}");
            // "🟢 [SKY] VPin · ✅ up to date", "⚫ [JBJ] DESKTOP-ABC1234 · offline 30 minutes ago · ⏳ updating itself"
            var where = online ? (playing != null ? "🎮" : "🟢") : "⚫";
            var activity = online ? (playing != null ? " · in a game" : "")
                : seen is { } ls ? $" · offline {Ts(DateTime.SpecifyKind(ls, DateTimeKind.Utc), "R")}" : " · offline";
            var release = AgentDownload.ReleaseOf(version);
            var state = version is null ? "version not reported yet"
                : release is null ? $"⚠️ needs a one-time manual update: download and run {download}"
                : latest is null || release >= latest ? "✅ up to date"
                : "⏳ updating itself when no table is running";
            sb.AppendLine($"{where} {who}{activity} · {state}");
        }
        return sb.ToString();
    }

    static async Task<IResult> EndAsync(NpgsqlConnection conn, Dictionary<string, JsonNode> args, Caller caller, DateTime now,
        ServerOptions options, LeaderboardPublisher publisher, CompetitionChanges changes)
    {
        var id = args["competition"]["value"]!.GetValue<long>();
        var comp = await Competitions.GetAsync(conn, id);
        if (comp is null || comp.Status(now, options.UploadGrace) is not ("active" or "scheduled"))
            return Reply($"Competition #{id} is not running.", ephemeral: true);
        if (comp.CreatedBy != caller.UserId && !caller.IsManager)
            return Reply("Only the member who started it or a server manager can end it.", ephemeral: true);
        var ended = (await Competitions.EndEarlyAsync(conn, id, now))!;
        changes.Notify();
        publisher.Wake();
        return Reply($"⏹ **{Md(Players.StripTag(caller.DisplayName))}** ended **{Md(ended.Title)}** early. " +
                     $"Games already finished still count; final standings {Ts(ended.EffectiveEndUtc + options.UploadGrace, "R")}.");
    }

    static async Task<IResult> HelpAsync(NpgsqlConnection conn, DiscordOptions discord)
    {
        var weekly = await WeeklyScheduler.ScheduleAsync(conn) is { } w && await Settings.GetAsync(conn, Settings.WeeklyEnabled) == "true" ? w : null;
        var guide = InfoPublisher.Guide(discord, weekly);
        guide["flags"] = Ephemeral;
        if (await Settings.GetAsync(conn, Settings.InfoChannel) is { } info) guide["content"] = $"Also pinned in <#{info}>.";
        return Json(new JsonObject { ["type"] = 4, ["data"] = guide });
    }

    /// <summary>
    /// /pinbot-admin channels: where one-off cards, weekly cards, and the guide with "VPin connected" notices go. Applies
    /// at once (new competitions; running ones keep their card where it is) and is kept across restarts. Without
    /// options, shows the current channels.
    /// </summary>
    static async Task<IResult> ChannelsAsync(NpgsqlConnection conn, Dictionary<string, JsonNode> args, Caller caller,
        DiscordOptions discord, LeaderboardPublisher publisher)
    {
        if (!caller.IsManager) return Reply("Only server managers can change PinBot's channels.", ephemeral: true);
        string? Picked(string name) => args.TryGetValue(name, out var o) ? o["value"]!.GetValue<string>() : null;
        if (Picked("challenges") is { } challenges)
        {
            await Settings.SetAsync(conn, Settings.ChallengesChannel, challenges);
            discord.ChannelId = challenges;
        }
        if (Picked("weekly") is { } weeklyChannel)
        {
            await Settings.SetAsync(conn, Settings.WeeklyChannel, weeklyChannel);
            discord.WeeklyChannelId = weeklyChannel;
        }
        if (Picked("info") is { } info) await Settings.SetAsync(conn, Settings.InfoChannel, info); // the guide is posted there next pass
        if (args.Count > 0) publisher.Wake();
        string Show(string? id) => id is null ? "not set" : $"<#{id}>";
        var infoNow = await Settings.GetAsync(conn, Settings.InfoChannel);
        return Reply((args.Count > 0 ? "✅ Saved. New competitions use these channels:\n" : "PinBot's channels:\n") +
                     $"• Challenges: {Show(discord.ChannelId)}\n" +
                     $"• Weekly: {(discord.WeeklyChannelId is null ? $"{Show(discord.ChannelId)} (same as challenges)" : Show(discord.WeeklyChannelId))}\n" +
                     $"• Guide and new VPins: {Show(infoNow)}\n" +
                     "PinBot needs **Send Messages**, **Embed Links** and **Pin Messages** in each.", ephemeral: true);
    }

    static async Task<IResult> PairAsync(NpgsqlConnection conn, Caller caller, DateTime now, ServerOptions options, DiscordOptions discord)
    {
        // any member may pair their own cabinet; a few codes an hour each keeps it from being abused
        var recent = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pairing_code WHERE created_by = @id AND created_at_utc > @since", new { id = caller.UserId, since = now.AddHours(-1) });
        if (recent >= MaxPairingCodesPerHour && !caller.IsManager)
            return Reply($"You already asked for {recent} codes in the last hour. Try again later.", ephemeral: true);
        if (discord.PublicUrl is null) return Reply("PinBot's public address is not set (PINBOT_PUBLIC_URL), so VPins cannot connect yet.", ephemeral: true);
        var (code, expires) = await Pairing.CreateCodeAsync(conn, now, options.PairingCodeLifetime, caller.UserId, caller.DisplayName);
        var download = File.Exists(AgentDownload.Path) ? $"<{discord.PublicUrl.TrimEnd('/')}{AgentDownload.Route}>" : "the PinBot release (ask a server manager)";
        return Reply(
            $"**Connect your VPin** with this line (works once, expires {Ts(expires, "R")}):\n```\n{Pairing.PairingLine(discord.PublicUrl, code)}\n```" +
            $"1. On the VPin, download **pinbot.exe**: {download}\n" +
            "2. Double-click it and paste the line above. If Windows warns about it (*unknown publisher* or *Windows protected your PC*): **More info → Run anyway**.\n" +
            "3. Put your initials in your server nickname, e.g. `Name | [ABC]`, and enter them when you get a high score.",
            ephemeral: true);
    }

    static async Task<IResult> WeeklyAsync(NpgsqlConnection conn, Dictionary<string, JsonNode> args, Caller caller, DateTime now, WeeklyScheduler weekly)
    {
        if (!caller.IsManager) return Reply("Only server managers can change the weekly competition.", ephemeral: true);
        string? Arg(string name) => args.TryGetValue(name, out var v) ? v["value"]!.GetValue<string>().Trim() : null;
        var on = args["enabled"]["value"]!.GetValue<bool>();
        var (day, time, zone) = (Arg("day"), Arg("time"), Arg("timezone"));
        var existing = await WeeklyScheduler.ScheduleAsync(conn);

        // nothing runs until a manager has chosen the slot, so the very first weekly is when they want it
        if (on && existing is null && (day is null || time is null || zone is null))
            return Reply("First set the schedule: include **day**, **time** and **timezone**, e.g. `/pinbot-admin weekly enabled:True day:Monday time:19:00 timezone:Eastern (US/Canada)`.", ephemeral: true);

        DayOfWeek dow = default;
        TimeSpan t = default;
        if (day != null && !Enum.TryParse(day, ignoreCase: true, out dow)) return Reply("Pick a day from the list.", ephemeral: true);
        if (time != null && (!TimeSpan.TryParseExact(time, [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out t) || t >= TimeSpan.FromDays(1)))
            return Reply("Time must be 24-hour HH:MM, e.g. 19:00.", ephemeral: true);
        if (zone != null)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(zone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return Reply("Pick a time zone from the list.", ephemeral: true); }
        }

        var changed = day != null || time != null || zone != null;
        if (day != null) await Settings.SetAsync(conn, Settings.WeeklyDay, dow.ToString());
        if (time != null) await Settings.SetAsync(conn, Settings.WeeklyTime, t.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        if (zone != null) await Settings.SetAsync(conn, Settings.WeeklyTimeZone, zone);
        var wasOn = await Settings.GetAsync(conn, Settings.WeeklyEnabled) == "true";
        // a new or changed schedule (or turning it back on) applies from the next slot, never immediately
        if (changed || (on && !wasOn)) await Settings.SetAsync(conn, Settings.WeeklyNotBefore, now.ToString("O", CultureInfo.InvariantCulture));
        await Settings.SetAsync(conn, Settings.WeeklyEnabled, on ? "true" : "false");

        var schedule = await WeeklyScheduler.ScheduleAsync(conn);
        var running = await WeeklyScheduler.RunningAsync(conn, now);
        var current = running is null ? "" : $"\nThe current weekly, **{Md(running.Title)}**, continues until {Ts(running.EffectiveEndUtc, "f")}.";
        if (!on)
            return Reply($"Weekly competitions are **off**{(schedule is null ? "" : $" (schedule kept: every {schedule})")}.{current}", ephemeral: true);

        // nothing running: start one now, whatever the day, so people can play straight away
        if (running is null && await weekly.StartNowAsync(conn, schedule!, now) is { } started)
            return Reply($"📅 Weekly competitions are **on**: a new game every **{schedule}**.\n" +
                         $"The first one starts **now**: **{Md(started.Title)}**, until {Ts(started.EffectiveEndUtc, "F")} ({Ts(started.EffectiveEndUtc, "R")}). " +
                         "After that the regular schedule takes over.", ephemeral: true);
        var next = await WeeklyScheduler.NextStartAsync(conn, schedule!, now);
        var why = running is null ? "\nNo eligible game is free on a connected VPin right now." : current;
        return Reply($"📅 Weekly competitions are **on**: a new game every **{schedule}**.{why}\nThe next one starts {Ts(next, "F")} ({Ts(next, "R")}).", ephemeral: true);
    }

    // ---------------- autocomplete ----------------

    static async Task<IResult> AutocompleteAsync(NpgsqlConnection conn, string subcommand, Dictionary<string, JsonNode> args,
        Caller caller, DateTime now, ServerOptions options)
    {
        var focused = args.Values.FirstOrDefault(o => o["focused"]?.GetValue<bool>() == true);
        var typed = focused?["value"]?.ToString().Trim() ?? "";
        var choices = new JsonArray();
        if (subcommand == "start")
        {
            var busy = await BusyGamesAsync(conn, now, options);
            var matches = (await Competitions.EligibleGamesAsync(conn, caller.UserId))
                .Where(g => typed.Length == 0 || g.Choice.Contains(typed, StringComparison.OrdinalIgnoreCase)
                            || g.RomIds.Any(r => r.Contains(typed, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(g => busy.Contains(g.CanonicalGameId)).ThenBy(g => g.Title).ToList();
            // Discord shows at most 25 entries: when there are more, the last one says so instead of the list just stopping
            var shown = matches.Count > MaxChoices ? MaxChoices - 1 : matches.Count;
            foreach (var g in matches.Take(shown))
                choices.Add(new JsonObject { ["name"] = Trim(g.Choice + (busy.Contains(g.CanonicalGameId) ? RunningMark : "")), ["value"] = g.CanonicalGameId });
            if (shown < matches.Count)
                choices.Add(new JsonObject { ["name"] = $"…{matches.Count - shown} more: type part of a name to narrow the list", ["value"] = MoreHint });
        }
        else if (subcommand == "end")
        {
            foreach (var c in (await Competitions.ListAsync(conn))
                         .Where(c => c.Status(now, options.UploadGrace) is "active" or "scheduled" && (c.CreatedBy == caller.UserId || caller.IsManager))
                         .Take(25))
                choices.Add(new JsonObject { ["name"] = Trim($"#{c.Id} {c.Title}"), ["value"] = c.Id });
        }
        return Json(new JsonObject { ["type"] = 8, ["data"] = new JsonObject { ["choices"] = choices } });
    }

    // ---------------- helpers ----------------

    static IResult Json(JsonObject o) => Results.Content(o.ToJsonString(), "application/json");

    internal static IResult Reply(string content, bool ephemeral = false) => Json(new JsonObject
    {
        ["type"] = 4,
        ["data"] = new JsonObject
        {
            ["content"] = content,
            ["flags"] = ephemeral ? Ephemeral : 0,
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() }, // never ping anyone
        },
    });

    /// <summary>Discord timestamp markup: shown in each viewer's own time zone.</summary>
    internal static string Ts(DateTime utc, string style) => $"<t:{new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds()}:{style}>";

    /// <summary>Escapes Discord markdown in user-provided text.</summary>
    internal static string Md(string s) => new StringBuilder(s.Length).AppendJoin("", s.Select(c => "\\*_~`|>".Contains(c) ? "\\" + c : c.ToString())).ToString();

    static string Trim(string s) => s.Length <= 100 ? s : s[..97] + "...";

}
