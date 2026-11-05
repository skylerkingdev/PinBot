using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using PinBot.Contracts;

namespace PinBot.Server.Discord;

/// <summary>
/// Installing what a member shared on another member's VPin (docs/TABLE-UPDATES.md, POVs phase B): "Use this POV" on a
/// post checks the clicker's VPins privately, an accept queues an install, the VPin applies it when no table is running
/// and reports back, and the member gets a DM with an Undo button. Generic by kind: POVs now, script fixes later.
/// </summary>
public static class SetupInstalls
{
    public const string Use = "tu:usepov", Go = "tu:povgo", Undo = "tu:povundo";
    /// <summary>An install a VPin hasn't picked up in this long is dropped (the member is told).</summary>
    public static readonly TimeSpan Within = TimeSpan.FromDays(7);
    const int MaxDetail = 300;

    public static JsonObject UseButtonRow(long updateId) => new()
    {
        ["type"] = 1,
        ["components"] = new JsonArray(new JsonObject { ["type"] = 2, ["style"] = 1, ["label"] = "Use this POV", ["custom_id"] = $"{Use}:{updateId}" }),
    };

    sealed record Shared(long Id, string CabinetId, string TableName, string? Name, string? RomId, string? Version, string? PovMode, string? Pov,
        string? OwnerUserId);

    sealed record Candidate(string CabinetId, string CabinetName, string TableName, string? Name, string? RomId, string? Version, string? PovMode, string? Pov);

    static Task<Shared?> SharedAsync(NpgsqlConnection conn, long updateId) =>
        conn.QuerySingleOrDefaultAsync<Shared?>("""
            SELECT id, cabinet_id, table_name, name, rom_id, version, pov_mode, pov, owner_user_id FROM table_update
            WHERE id = @updateId AND kind = 'pov' AND state = 'shared' AND pov IS NOT NULL
            """, new { updateId });

    /// <summary>The clicker's VPins' files of the same game (same table name inside the file, or the same ROM).</summary>
    static async Task<List<Candidate>> CandidatesAsync(NpgsqlConnection conn, Shared s, string userId) =>
        (await conn.QueryAsync<Candidate>($"""
            SELECT c.id AS cabinet_id, c.name AS cabinet_name, f.table_name, f.name, f.rom_id, f.version, f.pov_mode, f.pov
            FROM cabinet c JOIN table_file f ON f.cabinet_id = c.id
            WHERE c.revoked_at_utc IS NULL AND {Players.OwnerOf("c")} = @userId
              AND ((f.name IS NOT NULL AND lower(f.name) = lower(@Name)) OR (f.rom_id IS NOT NULL AND f.rom_id = @RomId))
            ORDER BY c.name, f.table_name
            """, new { userId, s.Name, s.RomId })).ToList();

    static bool SameVersion(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    static string Title(Shared s) => Interactions.Md(s.Name ?? s.TableName);

    /// <summary>"Use this POV": which of the clicker's VPins it fits, privately, with an install button for each.</summary>
    public static async Task<JsonObject> UseAsync(NpgsqlConnection conn, long updateId, string userId)
    {
        if (await SharedAsync(conn, updateId) is not { } s) return Private("This POV is no longer available.");
        var anyCabinet = await conn.ExecuteScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM cabinet c WHERE c.revoked_at_utc IS NULL AND {Players.OwnerOf("c")} = @userId)", new { userId });
        if (!anyCabinet) return Private("No VPin is linked to you yet: pair yours with `/pinbot pair`, then PinBot can install POVs on it.");
        var candidates = await CandidatesAsync(conn, s, userId);
        var fits = candidates.Where(c => SameVersion(c.Version, s.Version) && c.PovMode == s.PovMode).ToList();
        var mode = Mode(s.PovMode);
        var lines = new List<string>();
        foreach (var c in candidates.Except(fits))
            lines.Add(!SameVersion(c.Version, s.Version)
                ? $"• **{Interactions.Md(c.CabinetName)}** has v{Interactions.Md(c.Version ?? "?")}: this POV was made for v{Interactions.Md(s.Version ?? "?")}, and a POV only fits the same version."
                : $"• **{Interactions.Md(c.CabinetName)}** plays in {Mode(c.PovMode)} mode: this is a {mode} POV.");
        if (candidates.Count == 0) lines.Add($"None of your VPins has **{Title(s)}**.");
        var buttons = new JsonArray();
        foreach (var c in fits.Take(5))
        {
            if (c.Pov == s.Pov) { lines.Add($"• **{Interactions.Md(c.CabinetName)}** already has this view."); continue; }
            buttons.Add(new JsonObject
            {
                ["type"] = 2, ["style"] = 3, ["label"] = Trim($"Install on {c.CabinetName}", 80),
                ["custom_id"] = $"{Go}:{s.Id}:{c.CabinetId}",
            });
        }
        var text = buttons.Count > 0
            ? $"This {mode} POV for **{Title(s)}** v{Interactions.Md(s.Version ?? "?")} fits your VPin. Installing it replaces your {mode} view " +
              "of this table (your other settings stay), and you can undo it afterwards." + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "")
            : string.Join("\n", lines);
        var data = Private(text);
        if (buttons.Count > 0) data["components"] = new JsonArray(new JsonObject { ["type"] = 1, ["components"] = buttons });
        return data;
    }

    /// <summary>"Install on …": checked again, then queued for that VPin (which is told at once). Returns the reply text.</summary>
    public static async Task<string> GoAsync(NpgsqlConnection conn, CompetitionChanges changes, long updateId, string cabinetId, string userId, DateTime now)
    {
        if (await SharedAsync(conn, updateId) is not { } s) return "This POV is no longer available.";
        var target = (await CandidatesAsync(conn, s, userId))
            .FirstOrDefault(c => c.CabinetId == cabinetId && SameVersion(c.Version, s.Version) && c.PovMode == s.PovMode);
        if (target is null) return "This POV doesn't fit that VPin any more (a different version or view mode, or the table is gone).";
        var queued = await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM setup_install WHERE cabinet_id = @cabinetId AND table_name = @TableName AND state = 'pending')
            """, new { cabinetId, target.TableName });
        if (queued) return $"Something is already waiting to be installed on that table on **{Interactions.Md(target.CabinetName)}**: try again once it's done.";
        await conn.ExecuteAsync("""
            INSERT INTO setup_install (kind, action, update_id, cabinet_id, table_name, mode, content, requested_by, requested_at_utc)
            VALUES ('pov', 'install', @Id, @cabinetId, @TableName, @PovMode, @Pov, @userId, @now)
            """, new { s.Id, cabinetId, target.TableName, s.PovMode, s.Pov, userId, now });
        changes.Notify(); // wakes the VPin's waiting connection
        return $"⏳ Queued for **{Interactions.Md(target.CabinetName)}**: it's installed the next time no table is running there, and PinBot DMs you when it's done.";
    }

    /// <summary>"Undo" on an install's DM: queues putting back the view the VPin had before. Returns the reply text.</summary>
    public static async Task<string> UndoAsync(NpgsqlConnection conn, CompetitionChanges changes, long installId, string userId, DateTime now)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(long Id, string CabinetId, string TableName, string? Mode, string? Previous, long? UpdateId)?>("""
            SELECT id, cabinet_id, table_name, mode, previous, update_id FROM setup_install
            WHERE id = @installId AND requested_by = @userId AND kind = 'pov' AND action = 'install' AND state = 'installed'
              AND NOT EXISTS (SELECT 1 FROM setup_install u WHERE u.undo_of = @installId AND u.state IN ('pending', 'installed'))
            """, new { installId, userId });
        if (row is not { } r) return "There is nothing to undo here any more.";
        await conn.ExecuteAsync("""
            INSERT INTO setup_install (kind, action, undo_of, update_id, cabinet_id, table_name, mode, content, requested_by, requested_at_utc)
            VALUES ('pov', 'undo', @Id, @UpdateId, @CabinetId, @TableName, @Mode, @content, @userId, @now)
            """, new { r.Id, r.UpdateId, r.CabinetId, r.TableName, r.Mode, content = r.Previous ?? "", userId, now });
        changes.Notify();
        return "⏳ Putting your own view back: done the next time no table is running on that VPin, and PinBot DMs you.";
    }

    // ---------------- the VPin's side ----------------

    public static async Task<int> PendingCountAsync(NpgsqlConnection conn, string cabinet, DateTime now) =>
        await conn.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM setup_install WHERE cabinet_id = @cabinet AND state = 'pending' AND requested_at_utc >= @since
            """, new { cabinet, since = now - Within });

    public static async Task<List<SetupInstall>> PendingAsync(NpgsqlConnection conn, string cabinet, DateTime now) =>
        (await conn.QueryAsync<SetupInstall>("""
            SELECT id, kind, table_name AS "table", mode, content FROM setup_install
            WHERE cabinet_id = @cabinet AND state = 'pending' AND requested_at_utc >= @since ORDER BY id
            """, new { cabinet, since = now - Within })).ToList();

    /// <summary>
    /// What the VPin did. An installed POV also becomes the table's known view at once, so the VPin's next table list
    /// doesn't look like the member changed their view (no "share your POV?" for something they just installed).
    /// </summary>
    public static async Task<bool> ReportAsync(NpgsqlConnection conn, string cabinet, long id, SetupInstallResult result, DateTime now)
    {
        if (result.State is not ("installed" or "refused")) return false;
        await using var tx = await conn.BeginTransactionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(string Kind, string TableName, string? Mode)?>("""
            SELECT kind, table_name, mode FROM setup_install WHERE id = @id AND cabinet_id = @cabinet AND state = 'pending' FOR UPDATE
            """, new { id, cabinet }, tx);
        if (row is not { } r) return false;
        var detail = result.Detail is { } d ? (d.Length > MaxDetail ? d[..MaxDetail] : d) : null;
        await conn.ExecuteAsync("""
            UPDATE setup_install SET state = @State, detail = @detail, previous = @previous, done_at_utc = @now WHERE id = @id
            """, new { result.State, detail, previous = TableUpdates.CleanPov(r.Mode, result.Previous), now, id }, tx);
        if (result.State == "installed" && r.Kind == "pov")
            await conn.ExecuteAsync("""
                UPDATE table_file SET pov = @pov WHERE cabinet_id = @cabinet AND table_name = @TableName AND pov_mode = @Mode
                """, new { pov = TableUpdates.CleanPov(r.Mode, result.Now), cabinet, r.TableName, r.Mode }, tx);
        await tx.CommitAsync();
        return true;
    }

    // ---------------- telling the member ----------------

    sealed record Done(long Id, string Action, string State, string? Detail, string RequestedBy, string CabinetName, string? Title, string? Version,
        string? Mode, string? Sharer);

    /// <summary>DMs each member whose install finished, failed or expired (once), with Undo after an install.</summary>
    public static async Task NotifyAsync(NpgsqlConnection conn, IDiscordApi api, ILogger log, DateTime now, CancellationToken ct)
    {
        await conn.ExecuteAsync("""
            UPDATE setup_install SET state = 'expired', done_at_utc = @now WHERE state = 'pending' AND requested_at_utc < @since
            """, new { now, since = now - Within });
        var done = (await conn.QueryAsync<Done>("""
            SELECT i.id, i.action, i.state, i.detail, i.requested_by, c.name AS cabinet_name, COALESCE(u.name, i.table_name) AS title,
                   u.version, i.mode, u.owner_user_id AS sharer
            FROM setup_install i JOIN cabinet c ON c.id = i.cabinet_id LEFT JOIN table_update u ON u.id = i.update_id
            WHERE i.state IN ('installed', 'refused', 'expired') AND i.notified_at_utc IS NULL ORDER BY i.id
            """)).ToList();
        foreach (var d in done)
        {
            var cab = Interactions.Md(d.CabinetName);
            var title = Interactions.Md(d.Title ?? "the table");
            var message = new JsonObject { ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() } };
            message["content"] = (d.Action, d.State) switch
            {
                ("install", "installed") => $"✅ Installed {(d.Sharer is { } sh && sh.All(char.IsAsciiDigit) ? $"<@{sh}>'s" : "the shared")} {Mode(d.Mode)} POV " +
                                            $"for **{title}**{(d.Version is { } v ? $" v{Interactions.Md(v)}" : "")} on **{cab}**. " +
                                            "Undo puts your own view back.",
                ("undo", "installed") => $"↩️ Your own view of **{title}** is back on **{cab}**.",
                (_, "expired") => $"⚠️ Not done: **{cab}** didn't pick it up within 7 days (PinBot must be running on it).",
                _ => $"⚠️ Not done on **{cab}**: {Interactions.Md(d.Detail ?? "the VPin refused it")}",
            };
            if (d is { Action: "install", State: "installed" })
                message["components"] = new JsonArray(new JsonObject
                {
                    ["type"] = 1,
                    ["components"] = new JsonArray(new JsonObject { ["type"] = 2, ["style"] = 2, ["label"] = "Undo", ["custom_id"] = $"{Undo}:{d.Id}" }),
                });
            try
            {
                var dm = await api.OpenDmAsync(d.RequestedBy, ct);
                await api.PostMessageAsync(dm, message, ct);
            }
            catch (DiscordRestException ex) when (ex.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest)
            {
                log.LogInformation("could not DM a member about their setup install {Id}: {Message}", d.Id, ex.Message);
            }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("setup install notice not sent: {Message}", ex.Message);
                return; // next pass
            }
            await conn.ExecuteAsync("UPDATE setup_install SET notified_at_utc = @now WHERE id = @Id", new { now, d.Id });
        }
    }

    static string Mode(string? mode) => mode switch { "cabinet" => "cabinet", "fss" => "FSS", "desktop" => "desktop", _ => "unknown" };

    static JsonObject Private(string content) => new()
    {
        ["content"] = content,
        ["flags"] = 64, // only the clicker sees it
        ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
    };

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
