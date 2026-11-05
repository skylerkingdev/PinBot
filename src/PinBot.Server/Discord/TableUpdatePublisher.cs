using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace PinBot.Server.Discord;

/// <summary>A recorded table update with what is needed to ask about it and post it.</summary>
public sealed record TableUpdateRow(long Id, string CabinetId, string TableName, string Kind, string? OldVersion, string? RomId,
    string? Name, string? Version, string? Authors, string? BackglassName, string? BackglassAuthor, int? BackglassDmd,
    bool AltColor, bool AltSound, DateTime DetectedAtUtc, string State, string? OwnerUserId, string? DmChannelId, string? DmMessageId,
    string? VpsTableId = null, string? VpsB2sId = null, string? PostChannelId = null, string? PostMessageId = null);

/// <summary>
/// Table updates (docs/TABLE-UPDATES.md): asks the member who paired the cabinet whether to share each new table or
/// version (one DM per batch: installing several tables at once gives one question), then posts the ones they share
/// in the updates channel. Nothing is posted without the member's choice; with no updates channel set, nothing happens.
/// </summary>
public sealed class TableUpdatePublisher(Db db, IDiscordApi api, TimeProvider clock, ILogger<TableUpdatePublisher> log, Vps.VpsCatalog? vps = null)
{
    /// <summary>Changes are asked about once a cabinet has had none for this long: a batch of installs is one question.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);
    /// <summary>Unanswered questions (and updates never asked about) are dropped after this.</summary>
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromDays(7);
    const int MaxPerQuestion = 10;

    public const string SharePrefix = "tu:";
    public const string Share = "tu:share", Always = "tu:always", Skip = "tu:skip", Never = "tu:never";
    /// <summary>The dropdowns on a one-table question: which VPS table entry / backglass entry it is.</summary>
    public const string PickTable = "tu:pick:table", PickBackglass = "tu:pick:b2s", NotOnVps = "none";
    /// <summary>"Wrong match?" on a question PinBot is sure about: shows the dropdowns.</summary>
    public const string ShowPicks = "tu:fix";
    /// <summary>On a question about several tables: which table the dropdowns are for.</summary>
    public const string Which = "tu:which";
    /// <summary>/pinbot updates: the member's shared posts (pick one to fix its VPS links), then its dropdowns.</summary>
    public const string FixPost = "tu:fixpost", FixPick = "tu:fixpick";

    /// <summary>A question's dropdown: "tu:pick:table:&lt;update id&gt;" (or, on questions sent before ids, "tu:pick:table").</summary>
    public static bool IsPick(string customId) =>
        customId == PickTable || customId == PickBackglass || customId.StartsWith(PickTable + ":") || customId.StartsWith(PickBackglass + ":");
    const int MaxOptions = 25; // Discord's limit for a dropdown

    readonly SemaphoreSlim _wake = new(0);
    public void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }
    public Task WaitAsync(TimeSpan max, CancellationToken ct) => _wake.WaitAsync(max, ct);

    const string Columns = """
        u.id, u.cabinet_id, u.table_name, u.kind, u.old_version, u.rom_id, u.name, u.version, u.authors, u.backglass_name,
        u.backglass_author, u.backglass_dmd, u.alt_color, u.alt_sound, u.detected_at_utc, u.state, u.owner_user_id,
        u.dm_channel_id, u.dm_message_id, u.vps_table_id, u.vps_b2s_id, u.post_channel_id, u.post_message_id
        """;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE table_update SET state = 'expired', decided_at_utc = @now
            WHERE state IN ('pending', 'asked') AND detected_at_utc < @old
            """, new { now, old = now - AnswerWithin });
        await CloseQuestionsAsync(conn, now, ct);
        await CompletePostsAsync(conn, ct);
        if (await Settings.GetAsync(conn, Settings.UpdatesChannel) is not { } channel) return; // the feature is off

        foreach (var u in await conn.QueryAsync<TableUpdateRow>($"SELECT {Columns} FROM table_update u WHERE u.state = 'approved' ORDER BY u.id"))
            await PostAsync(conn, channel, u, ct);

        // updates waiting to be asked about, and those that had nobody to ask (tried again: an owner may be known now)
        var pending = (await conn.QueryAsync<TableUpdateRow>($"""
            SELECT {Columns} FROM table_update u
            WHERE u.state = 'pending' OR (u.state = 'undeliverable' AND u.owner_user_id IS NULL AND u.detected_at_utc >= @old)
            ORDER BY u.id
            """, new { old = now - AnswerWithin })).ToList();
        foreach (var group in pending.GroupBy(u => u.CabinetId))
        {
            if (group.Max(u => DateTime.SpecifyKind(u.DetectedAtUtc, DateTimeKind.Utc)) > now - Settle) continue; // more may follow
            var cabinet = group.Key;
            var ids = group.Select(u => u.Id).ToArray();
            // the member who paired it; for a cabinet paired before PinBot recorded that, the one with the most scores on it
            var owner = await conn.ExecuteScalarAsync<string?>($"SELECT {Players.OwnerOf("c")} FROM cabinet c WHERE c.id = @cabinet", new { cabinet });
            if (owner is null || !owner.All(char.IsAsciiDigit))
            {
                if (group.Any(u => u.State == "pending"))
                    log.LogInformation("table updates on cabinet {Cabinet} not asked about: nobody known to have paired it or scored on it", cabinet);
                await conn.ExecuteAsync("UPDATE table_update SET state = 'undeliverable', decided_at_utc = @now WHERE id = ANY(@ids) AND state = 'pending'",
                    new { now, ids });
                continue;
            }
            await conn.ExecuteAsync("UPDATE table_update SET owner_user_id = @owner, state = 'pending' WHERE id = ANY(@ids)", new { owner, ids });
            var batch = group.Select(u => u with { OwnerUserId = owner, State = "pending" }).ToList();
            var pref = await conn.ExecuteScalarAsync<string?>("SELECT share_updates FROM member_pref WHERE discord_user_id = @owner", new { owner }) ?? "ask";
            log.LogInformation("table updates on cabinet {Cabinet} ({Count}): member {Owner} shares {Pref}", cabinet, batch.Count, owner, pref);
            if (pref == "never") { await SetStateAsync(conn, ids, "skipped", now); continue; }
            if (pref == "always")
            {
                foreach (var u in batch) await PostAsync(conn, channel, u, ct);
                continue;
            }
            if (!await AskAsync(conn, owner, channel, batch, now, ct)) return; // Discord trouble: next pass
        }
    }

    /// <summary>One DM for a cabinet's batch. False when Discord failed in a way worth trying again later.</summary>
    async Task<bool> AskAsync(NpgsqlConnection conn, string owner, string channel, List<TableUpdateRow> batch, DateTime now, CancellationToken ct)
    {
        var ids = batch.Select(u => u.Id).ToArray();
        var cabinet = await conn.ExecuteScalarAsync<string?>("SELECT name FROM cabinet WHERE id = @id", new { id = batch[0].CabinetId }) ?? "your VPin";
        // what the member already said about the same file and version: not asked again
        await conn.ExecuteAsync("""
            UPDATE table_update u SET vps_table_id = COALESCE(u.vps_table_id, c.table_id), vps_b2s_id = COALESCE(u.vps_b2s_id, c.b2s_id)
            FROM table_vps_choice c
            WHERE u.id = ANY(@ids) AND c.cabinet_id = u.cabinet_id AND c.table_name = u.table_name AND c.version = COALESCE(u.version, '')
            """, new { ids });
        batch = (await conn.QueryAsync<TableUpdateRow>($"SELECT {Columns} FROM table_update u WHERE u.id = ANY(@ids) ORDER BY u.id", new { ids })).ToList();
        try
        {
            var dm = await api.OpenDmAsync(owner, ct);
            var message = await api.PostMessageAsync(dm, Question(cabinet, channel, batch, vps?.Current), ct);
            await conn.ExecuteAsync("""
                UPDATE table_update SET state = 'asked', dm_channel_id = @dm, dm_message_id = @message
                WHERE id = ANY(@ids) AND state = 'pending'
                """, new { dm, message, ids });
            log.LogInformation("asked member {Owner} about {Count} table update(s)", owner, ids.Length);
            return true;
        }
        catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest)
        {
            log.LogInformation("could not ask a member about their table updates (DMs closed?): {Message}", ex.Message);
            await SetStateAsync(conn, ids, "undeliverable", now);
            return true;
        }
        catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("table-update question not sent: {Message}", ex.Message);
            return false;
        }
    }

    async Task PostAsync(NpgsqlConnection conn, string channel, TableUpdateRow u, CancellationToken ct)
    {
        try
        {
            // VPS only adds links: without a catalog (VPS unreachable, turned off, never loaded) the post goes out without them
            var match = Resolve(vps?.Current, u);
            var message = await api.PostMessageAsync(channel, Post(u, match), ct);
            await conn.ExecuteAsync("""
                UPDATE table_update SET state = 'shared', post_channel_id = @channel, post_message_id = @message, decided_at_utc = @now,
                                        vps_game_id = @game, vps_table_id = @table, vps_b2s_id = @b2s
                WHERE id = @Id
                """, new { channel, message, now = clock.GetUtcNow().UtcDateTime, u.Id,
                           game = match?.Game.Id, table = match?.Table?.Id, b2s = match?.Backglass?.Id });
        }
        catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            // the channel is gone or PinBot may not post there: keep the update until a manager fixes the setting
            log.LogWarning("cannot post table updates in channel {Channel}: {Message}", channel, ex.Message);
            await conn.ExecuteAsync("UPDATE table_update SET state = 'approved' WHERE id = @Id AND state = 'pending'", new { u.Id });
        }
    }

    /// <summary>
    /// DMs whose question can no longer be answered (every table in it removed from the VPin, changed again, or not
    /// answered in time): edited once to say so, without buttons, so nobody shares a setup that isn't there.
    /// </summary>
    async Task CloseQuestionsAsync(NpgsqlConnection conn, DateTime now, CancellationToken ct)
    {
        var messages = (await conn.QueryAsync<(string Channel, string Message)>("""
            SELECT u.dm_channel_id, u.dm_message_id FROM table_update u
            WHERE u.dm_message_id IS NOT NULL AND u.dm_channel_id IS NOT NULL AND u.dm_closed_at_utc IS NULL
              AND u.state IN ('superseded', 'removed', 'expired')
              AND NOT EXISTS (SELECT 1 FROM table_update o
                              WHERE o.dm_message_id = u.dm_message_id AND o.state NOT IN ('superseded', 'removed', 'expired'))
            GROUP BY u.dm_channel_id, u.dm_message_id
            """)).ToList();
        foreach (var (dm, message) in messages)
        {
            var rows = (await conn.QueryAsync<TableUpdateRow>($"SELECT {Columns} FROM table_update u WHERE u.dm_message_id = @message ORDER BY u.id",
                new { message })).ToList();
            var cabinet = await conn.ExecuteScalarAsync<string?>("SELECT name FROM cabinet WHERE id = @id", new { id = rows[0].CabinetId }) ?? "your VPin";
            try
            {
                await api.EditMessageAsync(dm, message, Closed(cabinet, rows), ct);
            }
            catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                // the member deleted it or closed their DMs: nothing left to edit
            }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("table-update question not closed: {Message}", ex.Message);
                return; // next pass
            }
            await conn.ExecuteAsync("UPDATE table_update SET dm_closed_at_utc = @now WHERE dm_message_id = @message", new { now, message });
        }
    }

    /// <summary>
    /// Shared posts whose table got its backglass or extras afterwards (TableUpdates.RecordAsync marks them): edited to
    /// show the whole setup, with the VPS match worked out again (a member's own picks still win).
    /// </summary>
    async Task CompletePostsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var posts = (await conn.QueryAsync<TableUpdateRow>($"""
            SELECT {Columns} FROM table_update u WHERE u.post_refresh AND u.post_message_id IS NOT NULL ORDER BY u.id
            """)).ToList();
        foreach (var u in posts)
        {
            var match = Resolve(vps?.Current, u);
            try
            {
                await api.EditMessageAsync(u.PostChannelId!, u.PostMessageId!, Post(u, match), ct);
                await conn.ExecuteAsync("""
                    UPDATE table_update SET post_refresh = false, vps_game_id = @game, vps_table_id = @table, vps_b2s_id = @b2s WHERE id = @Id
                    """, new { u.Id, game = match?.Game.Id, table = match?.Table?.Id ?? u.VpsTableId, b2s = match?.Backglass?.Id ?? u.VpsB2sId });
                log.LogInformation("table update {Id}: post completed with what was added since", u.Id);
            }
            catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                await conn.ExecuteAsync("UPDATE table_update SET post_refresh = false WHERE id = @Id", new { u.Id }); // deleted, or no access
            }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("table-update post not completed: {Message}", ex.Message);
                return; // next pass
            }
        }
    }

    /// <summary>The question, closed: what became of each table, and no buttons.</summary>
    public static JsonObject Closed(string cabinet, IReadOnlyList<TableUpdateRow> rows)
    {
        var sb = new StringBuilder($"🔄 PinBot noticed {(rows.Count == 1 ? "a table change" : $"{rows.Count} table changes")} on **{Interactions.Md(cabinet)}**:\n");
        foreach (var u in rows.Take(MaxPerQuestion))
            sb.Append($"• ~~**{Title(u)}**: {(u.Kind == "added" ? "new" : "updated")}{(Change(u) is { Length: > 0 } c ? $", {c}" : "")}~~ {ClosedText(u.State)}\n");
        if (rows.Count > MaxPerQuestion) sb.Append($"• …and {rows.Count - MaxPerQuestion} more\n");
        sb.Append("Nothing was shared, and this question is closed.");
        return new JsonObject
        {
            ["content"] = sb.ToString(),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["components"] = new JsonArray(),
        };
    }

    static string ClosedText(string state) => state switch
    {
        "removed" => "(no longer on the VPin)",
        "superseded" => "(changed again: see the newer message)",
        "expired" => "(not answered within 7 days)",
        _ => "",
    };

    static Task SetStateAsync(NpgsqlConnection conn, long[] ids, string state, DateTime now) =>
        conn.ExecuteAsync("UPDATE table_update SET state = @state, decided_at_utc = @now WHERE id = ANY(@ids) AND state IN ('pending', 'asked')",
            new { state, now, ids });

    /// <summary>
    /// A member's answer to the question with message id <paramref name="messageId"/>. Only the member it was sent to
    /// counts (checked against the stored owner, never trusted from the button). Returns the text that replaces the
    /// question, or null when there is nothing to answer (already answered, expired, or someone else's).
    /// </summary>
    public static async Task<string?> AnswerAsync(NpgsqlConnection conn, string customId, string userId, string messageId, DateTime now)
    {
        var ids = (await conn.QueryAsync<long>("""
            SELECT id FROM table_update WHERE dm_message_id = @messageId AND owner_user_id = @userId AND state = 'asked'
            """, new { messageId, userId })).ToArray();
        if (ids.Length == 0) return null;
        // the answer redraws the message: never edited again when its other tables are closed later
        await conn.ExecuteAsync("UPDATE table_update SET dm_closed_at_utc = @now WHERE dm_message_id = @messageId", new { now, messageId });
        var channel = await Settings.GetAsync(conn, Settings.UpdatesChannel);
        var where = channel is null ? "the updates channel" : $"<#{channel}>";
        switch (customId)
        {
            case Share:
                await conn.ExecuteAsync("UPDATE table_update SET state = 'approved', decided_at_utc = @now WHERE id = ANY(@ids)", new { now, ids });
                return $"✅ Shared in {where}.";
            case Always:
                await SetPrefAsync(conn, userId, "always");
                await conn.ExecuteAsync("UPDATE table_update SET state = 'approved', decided_at_utc = @now WHERE id = ANY(@ids)", new { now, ids });
                return $"✅ Shared in {where}. From now on your table updates are shared automatically (change it with `/pinbot updates`).";
            case Skip:
                await SetStateAsync(conn, ids, "skipped", now);
                return "👍 Not shared.";
            case Never:
                await SetPrefAsync(conn, userId, "never");
                await SetStateAsync(conn, ids, "skipped", now);
                return "👍 Not shared, and PinBot won't ask again (change it with `/pinbot updates`).";
            default:
                return null;
        }
    }

    /// <summary>
    /// The latest table changes PinBot noticed on the member's VPins, and what became of each: for /pinbot updates, so
    /// a member can see why something was or wasn't shared.
    /// </summary>
    public static async Task<string> RecentAsync(NpgsqlConnection conn, string userId)
    {
        var cabinets = (await conn.QueryAsync<(string Id, string Name, DateTime? Reported)>($"""
            SELECT c.id, c.name, c.files_reported_at_utc FROM cabinet c
            WHERE c.revoked_at_utc IS NULL AND {Players.OwnerOf("c")} = @userId ORDER BY c.name
            """, new { userId })).ToList();
        if (cabinets.Count == 0) return "\nNo VPin is linked to you yet (pair one with `/pinbot pair`).";
        var sb = new StringBuilder();
        foreach (var c in cabinets)
        {
            sb.Append($"\n**{Interactions.Md(c.Name)}**: ");
            if (c.Reported is not { } reported)
            {
                sb.Append("hasn't sent its table details yet (its PinBot updates itself when no table is running).");
                continue;
            }
            sb.Append($"tables last reported {Interactions.Ts(DateTime.SpecifyKind(reported, DateTimeKind.Utc), "R")}.");
            var recent = (await conn.QueryAsync<TableUpdateRow>($"""
                SELECT {Columns} FROM table_update u WHERE u.cabinet_id = @Id ORDER BY u.id DESC LIMIT 5
                """, new { c.Id })).ToList();
            if (recent.Count == 0) sb.Append(" No new table or version noticed since then. (Putting back the same version counts as no change.)");
            foreach (var u in recent)
                sb.Append($"\n• **{Title(u)}** {(u.Kind == "added" ? "new" : "updated")}{(Change(u) is { Length: > 0 } ch ? $", {ch}" : "")}, " +
                          $"{Interactions.Ts(DateTime.SpecifyKind(u.DetectedAtUtc, DateTimeKind.Utc), "R")}: {StateText(u.State)}");
        }
        return sb.ToString();
    }

    static string StateText(string state) => state switch
    {
        "pending" => "about to ask you (a couple of minutes after the change)",
        "asked" => "waiting for your answer in a DM",
        "approved" => "being shared",
        "shared" => "shared",
        "skipped" => "not shared",
        "superseded" => "replaced by a newer change",
        "removed" => "removed from the VPin before you answered",
        "expired" => "not answered within 7 days",
        "undeliverable" => "PinBot couldn't DM you (Discord: allow direct messages from this server's members)",
        _ => state,
    };

    /// <summary>
    /// A pick from a question's dropdown: the VPS entry the member says a table is (or "not on VPS"). Only the member it
    /// was sent to, only entries of the game PinBot found, and only while the question is open. Remembered for this file
    /// and version. Returns the redrawn question, or null when there is nothing to change.
    /// </summary>
    public static async Task<JsonObject?> PickAsync(NpgsqlConnection conn, string customId, string? value, string userId, string messageId,
        Vps.VpsIndex? index, DateTime now)
    {
        var rows = await OpenQuestionAsync(conn, userId, messageId);
        var isTable = customId.StartsWith(PickTable);
        var id = customId.Split(':') is [_, _, _, var n] && long.TryParse(n, out var parsed) ? parsed : (long?)null;
        var u = id is { } want ? rows.FirstOrDefault(r => r.Id == want) : rows is [var only] ? only : null;
        if (u is null || value is null || Match(index, u) is not { } auto) return null;
        if (Chosen(auto, isTable, value) is not { } chosen) return null;
        await conn.ExecuteAsync($"UPDATE table_update SET {(isTable ? "vps_table_id" : "vps_b2s_id")} = @chosen WHERE id = @Id", new { chosen, u.Id });
        await SaveChoiceAsync(conn, u, isTable, chosen, now);
        var updated = isTable ? u with { VpsTableId = chosen } : u with { VpsB2sId = chosen };
        var (cabinet, channel) = await WhereAsync(conn, u);
        return Question(cabinet, channel, rows.Select(r => r.Id == u.Id ? updated : r).ToList(), index, showPicks: true, focus: u.Id);
    }

    /// <summary>"Wrong match?": the same open question, with its dropdowns. Null when it is no longer open.</summary>
    public static async Task<JsonObject?> ShowPicksAsync(NpgsqlConnection conn, string userId, string messageId, Vps.VpsIndex? index)
    {
        var rows = await OpenQuestionAsync(conn, userId, messageId);
        if (rows.Count == 0 || index is null) return null;
        var (cabinet, channel) = await WhereAsync(conn, rows[0]);
        return Question(cabinet, channel, rows, index, showPicks: true, focus: rows[0].Id);
    }

    /// <summary>"Which table?" on a question about several: the same question with that table's dropdowns.</summary>
    public static async Task<JsonObject?> WhichAsync(NpgsqlConnection conn, string? value, string userId, string messageId, Vps.VpsIndex? index)
    {
        var rows = await OpenQuestionAsync(conn, userId, messageId);
        if (index is null || !long.TryParse(value, out var id) || rows.All(r => r.Id != id)) return null;
        var (cabinet, channel) = await WhereAsync(conn, rows[0]);
        return Question(cabinet, channel, rows, index, showPicks: true, focus: id);
    }

    static async Task<List<TableUpdateRow>> OpenQuestionAsync(NpgsqlConnection conn, string userId, string messageId) =>
        (await conn.QueryAsync<TableUpdateRow>($"""
            SELECT {Columns} FROM table_update u WHERE u.dm_message_id = @messageId AND u.owner_user_id = @userId AND u.state = 'asked'
            ORDER BY u.id
            """, new { messageId, userId })).ToList();

    static async Task<(string Cabinet, string Channel)> WhereAsync(NpgsqlConnection conn, TableUpdateRow u) =>
        (await conn.ExecuteScalarAsync<string?>("SELECT name FROM cabinet WHERE id = @CabinetId", new { u.CabinetId }) ?? "your VPin",
         await Settings.GetAsync(conn, Settings.UpdatesChannel) ?? "0");

    /// <summary>The picked value if it is an entry of the matched game (or "not on VPS", stored as ""); null if not.</summary>
    static string? Chosen(Vps.VpsMatch auto, bool isTable, string value)
    {
        var options = isTable ? auto.Game.Tables : auto.Game.Backglasses;
        if (value == NotOnVps) return "";
        return options.Any(o => o.Id == value) ? value : null;
    }

    /// <summary>Remembered for this file and version: the same file is never asked about again.</summary>
    static Task SaveChoiceAsync(NpgsqlConnection conn, TableUpdateRow u, bool isTable, string chosen, DateTime now)
    {
        var column = isTable ? "table_id" : "b2s_id";
        return conn.ExecuteAsync($"""
            INSERT INTO table_vps_choice (cabinet_id, table_name, version, {column}, chosen_at_utc)
            VALUES (@CabinetId, @TableName, @version, @chosen, @now)
            ON CONFLICT (cabinet_id, table_name, version) DO UPDATE SET {column} = EXCLUDED.{column}, chosen_at_utc = EXCLUDED.chosen_at_utc
            """, new { u.CabinetId, u.TableName, version = u.Version ?? "", chosen, now });
    }

    // ---------------- fixing a shared post ----------------

    const int MaxFixable = 25;

    /// <summary>For /pinbot updates: a dropdown of the member's shared posts, to fix one's VPS links. Null when there are none.</summary>
    public static async Task<JsonObject?> FixMenuAsync(NpgsqlConnection conn, string userId)
    {
        var posts = (await conn.QueryAsync<TableUpdateRow>($"""
            SELECT {Columns} FROM table_update u
            WHERE u.owner_user_id = @userId AND u.state = 'shared' AND u.post_message_id IS NOT NULL
            ORDER BY u.id DESC LIMIT {MaxFixable}
            """, new { userId })).ToList();
        if (posts.Count == 0) return null;
        var options = new JsonArray();
        foreach (var u in posts)
            options.Add(new JsonObject
            {
                ["label"] = Trim($"{u.Name ?? u.TableName}{(u.Version is { } v ? $" · v{v}" : "")}", 100),
                ["description"] = $"shared {DateTime.SpecifyKind(u.DetectedAtUtc, DateTimeKind.Utc):d MMM yyyy}",
                ["value"] = u.Id.ToString(),
            });
        return new JsonObject
        {
            ["type"] = 1,
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = 3, ["custom_id"] = FixPost, ["placeholder"] = "Fix the VPS links of a post you shared…", ["options"] = options,
            }),
        };
    }

    static async Task<TableUpdateRow?> SharedPostAsync(NpgsqlConnection conn, long id, string userId) =>
        await conn.QuerySingleOrDefaultAsync<TableUpdateRow>($"""
            SELECT {Columns} FROM table_update u
            WHERE u.id = @id AND u.owner_user_id = @userId AND u.state = 'shared' AND u.post_message_id IS NOT NULL
            """, new { id, userId });

    /// <summary>A post picked from the /pinbot updates menu: its VPS entries, with dropdowns. Null if it isn't the member's.</summary>
    public static async Task<JsonObject?> FixPostAsync(NpgsqlConnection conn, string? value, string userId, Vps.VpsIndex? index) =>
        long.TryParse(value, out var id) && await SharedPostAsync(conn, id, userId) is { } u ? Editor(u, index, null) : null;

    /// <summary>
    /// A pick on the fix editor: saved like a pick on the question, and the post in the updates channel is edited to link
    /// the right entries. Returns the redrawn editor, or null when the post isn't the member's or the pick isn't valid.
    /// </summary>
    public async Task<JsonObject?> FixPickAsync(NpgsqlConnection conn, string customId, string? value, string userId, DateTime now, CancellationToken ct)
    {
        if (customId.Split(':') is not [_, _, var kind and ("table" or "b2s"), var n] || !long.TryParse(n, out var id)) return null;
        var index = vps?.Current;
        if (value is null || await SharedPostAsync(conn, id, userId) is not { } u || Match(index, u) is not { } auto) return null;
        var isTable = kind == "table";
        if (Chosen(auto, isTable, value) is not { } chosen) return null;
        await SaveChoiceAsync(conn, u, isTable, chosen, now);
        u = isTable ? u with { VpsTableId = chosen } : u with { VpsB2sId = chosen };
        var m = Resolve(index, u);
        await conn.ExecuteAsync("""
            UPDATE table_update SET vps_game_id = @game, vps_table_id = @table, vps_b2s_id = @b2s WHERE id = @Id
            """, new { u.Id, game = m?.Game.Id, table = isTable ? chosen : m?.Table?.Id, b2s = isTable ? m?.Backglass?.Id : chosen });
        string note;
        try
        {
            await api.EditMessageAsync(u.PostChannelId!, u.PostMessageId!, Post(u, m), ct);
            note = "✅ Post updated. ";
            log.LogInformation("member {Owner} fixed the VPS links of table update {Id}", userId, u.Id);
        }
        catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            note = "⚠️ Saved, but PinBot couldn't edit the post (it was deleted, or PinBot can no longer post there). ";
        }
        catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
        {
            note = "⚠️ Saved, but Discord didn't take the edit just now: pick it again in a minute. ";
        }
        return Editor(u, index, note);
    }

    /// <summary>The fix editor: what the post links now, and a dropdown for each part.</summary>
    static JsonObject Editor(TableUpdateRow u, Vps.VpsIndex? index, string? note)
    {
        var sb = new StringBuilder($"{note}Your post of **{Title(u)}**{(Change(u) is { Length: > 0 } c ? $" ({c})" : "")} in <#{u.PostChannelId}>:\n");
        var rows = new JsonArray();
        if (Resolve(index, u) is not { } m)
            sb.Append(index is null
                ? "PinBot can't reach the Virtual Pinball Spreadsheet right now, so there are no entries to pick. Try again later."
                : "PinBot couldn't find this game on the Virtual Pinball Spreadsheet, so there are no entries to pick.");
        else
        {
            sb.Append($"↳ Table: {Identified(m.Table, u.VpsTableId)}\n");
            if (HasB2s(u)) sb.Append($"↳ Backglass: {Identified(m.Backglass, u.VpsB2sId)}\n");
            sb.Append("Pick the right entries below; the post is updated right away.");
            if (m.Game.Tables.Count > 0) rows.Add(Dropdown($"{FixPick}:table:{u.Id}", "Which table entry is it?", m.Game.Tables, m.Table?.Id ?? u.VpsTableId));
            if (HasB2s(u) && m.Game.Backglasses.Count > 0)
                rows.Add(Dropdown($"{FixPick}:b2s:{u.Id}", "Which backglass is it?", m.Game.Backglasses, m.Backglass?.Id ?? u.VpsB2sId));
        }
        return new JsonObject
        {
            ["content"] = sb.ToString(),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["components"] = rows,
        };
    }

    public static Task SetPrefAsync(NpgsqlConnection conn, string userId, string pref) =>
        conn.ExecuteAsync("""
            INSERT INTO member_pref (discord_user_id, share_updates) VALUES (@userId, @pref)
            ON CONFLICT (discord_user_id) DO UPDATE SET share_updates = EXCLUDED.share_updates
            """, new { userId, pref });

    // ---------------- messages ----------------

    static string Title(TableUpdateRow u) => Interactions.Md(u.Name ?? u.TableName);

    /// <summary>"2.0 → 2.4.1", "version 2.4.1", or "" when the table states no version.</summary>
    internal static string Change(TableUpdateRow u) =>
        u.Kind == "updated" && u.OldVersion is { } old && u.Version is { } v ? $"{Interactions.Md(old)} → **{Interactions.Md(v)}**"
        : u.Version is { } version ? $"version **{Interactions.Md(version)}**"
        : "";

    /// <summary>The DM to the member: what PinBot noticed on their VPin, and whether to share it.</summary>
    /// <param name="showPicks">show the dropdowns even when PinBot is sure (the member pressed "Wrong match?", or picked)</param>
    /// <param name="focus">on a question about several tables, the one whose dropdowns are shown</param>
    public static JsonObject Question(string cabinet, string channel, IReadOnlyList<TableUpdateRow> batch, Vps.VpsIndex? index = null,
        bool showPicks = false, long? focus = null)
    {
        var sb = new StringBuilder($"🔄 PinBot noticed {(batch.Count == 1 ? "a table change" : $"{batch.Count} table changes")} on **{Interactions.Md(cabinet)}**:\n");
        foreach (var u in batch.Take(MaxPerQuestion))
        {
            sb.Append($"• **{Title(u)}**: {(u.Kind == "added" ? "new" : "updated")}{(Change(u) is { Length: > 0 } c ? $", {c}" : "")}\n");
            if (index is null) continue;
            if (Resolve(index, u) is not { } m) { sb.Append("  ↳ not found on VPS: shared without links\n"); continue; }
            sb.Append($"  ↳ Table: {Identified(m.Table, u.VpsTableId)}\n");
            if (HasB2s(u)) sb.Append($"  ↳ Backglass: {Identified(m.Backglass, u.VpsB2sId)}\n");
        }
        if (batch.Count > MaxPerQuestion) sb.Append($"• …and {batch.Count - MaxPerQuestion} more\n");

        // the tables PinBot found on VPS (their entries can be picked), and the one whose dropdowns are shown: the one asked
        // for, else (several tables) the first PinBot isn't sure of, else (one table) only when it isn't sure
        var found = index is null ? [] : batch.Take(MaxOptions).Select(u => (U: u, M: Resolve(index, u))).Where(x => x.M is not null).ToList();
        var target = focus is { } f ? found.FirstOrDefault(x => x.U.Id == f) : default;
        if (target.U is null && found.FirstOrDefault(x => !Sure(x.U, x.M!)) is { U: not null } unsure) target = unsure;
        if (target.U is null && showPicks && found.Count > 0) target = found[0];
        var several = batch.Count > 1;
        if (target.U is not null)
            sb.Append(several ? $"If PinBot picked the wrong entry for **{Title(target.U)}**, choose the right one below. " : "If PinBot picked the wrong entry, choose the right one below. ");
        else if (several && found.Count > 0)
            sb.Append("To check a table's VPS entries, pick it under the buttons. ");
        sb.Append($"Share {(batch.Count == 1 ? "it" : "them")} in <#{channel}> so others can get the same setup?");

        var buttons = new JsonArray(
            Button(batch.Count == 1 ? "Share" : "Share all", Share, 3),
            Button("Always share", Always, 1),
            Button("Not this time", Skip, 2),
            Button("Don't ask again", Never, 2));
        if (!several && found.Count == 1 && target.U is null) buttons.Add(Button("Wrong match?", ShowPicks, 2));
        var rows = new JsonArray(new JsonObject { ["type"] = 1, ["components"] = buttons });
        if (several && found.Count > 0)
        {
            var options = new JsonArray();
            foreach (var (u, _) in found)
            {
                var o = new JsonObject { ["label"] = Trim($"{u.Name ?? u.TableName}{(u.Version is { } v ? $" · v{v}" : "")}", 100), ["value"] = u.Id.ToString() };
                if (u.Id == target.U?.Id) o["default"] = true;
                options.Add(o);
            }
            rows.Add(new JsonObject
            {
                ["type"] = 1,
                ["components"] = new JsonArray(new JsonObject { ["type"] = 3, ["custom_id"] = Which, ["placeholder"] = "Check a table's VPS entries…", ["options"] = options }),
            });
        }
        if (target is { U: { } t, M: { } tm })
        {
            if (tm.Game.Tables.Count > 0) rows.Add(Dropdown($"{PickTable}:{t.Id}", "Which table entry is it?", tm.Game.Tables, tm.Table?.Id ?? t.VpsTableId));
            if (HasB2s(t) && tm.Game.Backglasses.Count > 0)
                rows.Add(Dropdown($"{PickBackglass}:{t.Id}", "Which backglass is it?", tm.Game.Backglasses, tm.Backglass?.Id ?? t.VpsB2sId));
        }
        return new JsonObject
        {
            ["content"] = sb.ToString(),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["components"] = rows,
        };
    }

    static bool HasB2s(TableUpdateRow u) => u.BackglassAuthor is not null || u.BackglassName is not null;

    /// <summary>The table entry (and the backglass, if the VPin has one) found or chosen.</summary>
    static bool Sure(TableUpdateRow u, Vps.VpsMatch m) =>
        (m.Table is not null || u.VpsTableId == "") && (!HasB2s(u) || m.Backglass is not null || u.VpsB2sId == "");

    /// <summary>"v2.4.1 by RobbyKingPin, 32assassin +12 (VPS `QcOG228URt`)", "not on VPS (your choice)" or "not identified: pick it below".</summary>
    static string Identified(Vps.VpsFile? f, string? chosen) =>
        f is not null ? $"{Describe(f)} (VPS `{Code(f.Id)}`)"
        : chosen == "" ? "not on VPS (your choice)"
        : "not identified: pick it below";

    static string Describe(Vps.VpsFile f) =>
        $"{(f.Version is { } v ? $"v{Interactions.Md(v)}" : "no version")}{By(f.Authors)}{Features(f)}";

    static JsonObject Dropdown(string id, string placeholder, IReadOnlyList<Vps.VpsFile> files, string? selected)
    {
        var options = new JsonArray();
        foreach (var f in files.Take(MaxOptions - 1))
        {
            var o = new JsonObject
            {
                ["label"] = Trim($"{(f.Version is { } v ? $"v{v}" : "no version")} · {(f.Authors.Count == 0 ? "unknown author" : string.Join(", ", f.Authors))}", 100),
                ["value"] = f.Id,
            };
            var detail = string.Join(" · ", new[] { string.Join(", ", f.Features), f.Comment ?? "" }.Where(x => x.Length > 0));
            if (detail.Length > 0) o["description"] = Trim(detail, 100);
            if (f.Id == selected) o["default"] = true;
            options.Add(o);
        }
        options.Add(new JsonObject { ["label"] = "Not on VPS", ["value"] = NotOnVps, ["default"] = selected == "" });
        return new JsonObject
        {
            ["type"] = 1,
            ["components"] = new JsonArray(new JsonObject { ["type"] = 3, ["custom_id"] = id, ["placeholder"] = placeholder, ["options"] = options }),
        };
    }

    static JsonObject Button(string label, string id, int style) => new() { ["type"] = 2, ["style"] = style, ["label"] = label, ["custom_id"] = id };

    /// <summary>The post in the updates channel: who, which table, which version, and the setup that goes with it.</summary>
    public static Vps.VpsMatch? Match(Vps.VpsIndex? index, TableUpdateRow u) =>
        index?.Match(u.RomId, u.Name, u.TableName, u.Version, u.Authors, u.BackglassAuthor, u.BackglassDmd, u.AltColor, u.AltSound);

    /// <summary>PinBot's match, with the member's own picks (a VPS id, or "" for "not on VPS") taking precedence.</summary>
    public static Vps.VpsMatch? Resolve(Vps.VpsIndex? index, TableUpdateRow u)
    {
        if (Match(index, u) is not { } m) return null;
        static Vps.VpsFile? Pick(string? chosen, Vps.VpsFile? auto, IReadOnlyList<Vps.VpsFile> all) =>
            chosen is null ? auto : chosen == "" ? null : all.FirstOrDefault(f => f.Id == chosen) ?? auto;
        return m with { Table = Pick(u.VpsTableId, m.Table, m.Game.Tables), Backglass = Pick(u.VpsB2sId, m.Backglass, m.Game.Backglasses) };
    }

    /// <summary>
    /// The post in the updates channel: who, which table and version, and "Get this setup": each part of the member's
    /// setup linked to the author's own page for exactly that entry (and its VPS id), when PinBot is sure which it is.
    /// What isn't identified says so; nothing is guessed. Without a VPS match, the same post without links.
    /// </summary>
    public static JsonObject Post(TableUpdateRow u, Vps.VpsMatch? m = null)
    {
        var who = u.OwnerUserId is { } id && id.All(char.IsAsciiDigit) ? $"<@{id}>" : "A member";
        var what = u.Kind == "added" ? "added" : "updated";
        var desc = new StringBuilder($"{who} {what} this table{(Change(u) is { Length: > 0 } c ? $": {c}" : "")}");
        if (u.Authors is { } authors) desc.Append($"\nby {Interactions.Md(authors)}");

        var setup = new StringBuilder();
        var version = u.Version is { } v ? $"v{Interactions.Md(v)}" : "version not stated";
        if (m?.Table is { } t)
        {
            setup.Append($"🎰 Table: {LinkOr($"{version}{By(t.Authors)}", t.Url ?? m.Game.TablePageUrl(t))} · VPS `{Code(t.Id)}`\n");
            // the entry's own note on VPS ("10.8.1 Mod of TastyWasps version"): says when the link is a patch or mod of another table
            if (Note(t.Comment) is { } note) setup.Append($"↳ *{note}*\n");
        }
        else
            setup.Append($"🎰 Table: {version} · file `{Code(u.TableName)}`{(m is null ? "" : " · not identified on VPS")}\n");
        if (m?.Backglass is { } b)
            setup.Append($"🖼️ Backglass: {LinkOr($"{Names(b.Authors)}{(b.Version is { } bv ? $" v{Interactions.Md(bv)}" : "")}{Features(b)}", b.Url)} · VPS `{Code(b.Id)}`\n");
        else if (u.BackglassAuthor is not null || u.BackglassName is not null)
            setup.Append($"🖼️ Backglass{(u.BackglassAuthor is { } a ? $" by {Interactions.Md(a)}" : "")}{Dmd(u.BackglassDmd)}{(m is null ? "" : " · not identified on VPS")}\n");
        if (u.RomId is { } rom) setup.Append($"💾 ROM: {LinkOr($"`{Code(rom)}`", m?.Rom?.Url)}\n");
        if (u.AltColor) setup.Append($"🌈 Colorization: {(m?.AltColor is { } ac ? LinkOr($"{Names(ac.Authors)}{(ac.Version is { } av ? $" v{Interactions.Md(av)}" : "")}", ac.Url) : "installed")}\n");
        if (u.AltSound) setup.Append($"🔊 Alt sound: {(m?.AltSound is { } al ? LinkOr($"{Names(al.Authors)}{(al.Version is { } sv ? $" v{Interactions.Md(sv)}" : "")}", al.Url) : "installed")}\n");
        if (m is not null) setup.Append($"[All versions on VPS]({Url(m.Game.PageUrl)})");

        var embed = new JsonObject
        {
            ["title"] = Trim(m?.Game.Title ?? u.Name ?? u.TableName, 256),
            ["description"] = Trim(desc.ToString(), 4000),
            ["color"] = 0x2ECC71,
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "Get this setup", ["value"] = Trim(setup.ToString().TrimEnd(), 1024) }),
        };
        if (m is not null)
        {
            embed["url"] = m.Table is { } mt ? m.Game.TablePageUrl(mt) : m.Game.PageUrl;
            if ((m.Table?.ImgUrl ?? m.Game.ImgUrl) is { } img) embed["image"] = new JsonObject { ["url"] = img };
            embed["footer"] = new JsonObject { ["text"] = "Links go to the authors' own pages · matched with the Virtual Pinball Spreadsheet" };
        }
        return new JsonObject
        {
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() }, // shows the member's name, never pings
            ["embeds"] = new JsonArray(embed),
        };
    }

    /// <summary>A VPS note on one line, without markdown or links, at most 150 characters; null when empty.</summary>
    static string? Note(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return null;
        var line = string.Join(' ', comment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = Interactions.Md(line.Replace("http://", "").Replace("https://", "")); // no auto-links from VPS text
        return Trim(line, 150);
    }

    /// <summary>"[text](url)", or just the text when there is no link.</summary>
    static string LinkOr(string text, string? url) => url is null ? text : $"[{text.Replace("[", "(").Replace("]", ")")}]({Url(url)})";

    /// <summary>A URL that can't end a markdown link early.</summary>
    static string Url(string url) => url.Replace(")", "%29").Replace("(", "%28").Replace(" ", "%20");

    static string Names(IReadOnlyList<string> names) =>
        names.Count == 0 ? "unknown author" : Interactions.Md(names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(2))} +{names.Count - 2}");

    static string By(IReadOnlyList<string> names) => names.Count == 0 ? "" : $" by {Names(names)}";

    static string Features(Vps.VpsFile f) =>
        f.Features.Any(x => x.Equals("FullDMD", StringComparison.OrdinalIgnoreCase)) ? " · full DMD" : "";

    static string Dmd(int? type) => type switch { 3 => " · full DMD", 2 => " · DMD on the backglass", _ => "" };

    /// <summary>Text inside `code`: a backtick would end it.</summary>
    static string Code(string s) => s.Replace('`', '\'');

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
