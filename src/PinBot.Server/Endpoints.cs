using Dapper;
using PinBot.Contracts;

namespace PinBot.Server;

public sealed record CreatePairingCodeResponse(string Code, DateTime ExpiresAtUtc);
public sealed record CreateCompetitionRequest(string Game, double Hours, string? Kind, string? CreatedBy, DateTime? StartsAtUtc);
public sealed record CompetitionView(long Id, string CanonicalGameId, string Title, string Kind, string CreatedBy,
    DateTime StartsAtUtc, DateTime EndsAtUtc, string Status);
public sealed record MembersRequest(IReadOnlyList<Member> Members);
public sealed record AliasesRequest(IReadOnlyDictionary<string, string> RomToGame);

public static class Endpoints
{
    const int MaxEventsPerRequest = 200;
    const int MaxWaitSeconds = 25;
    const int MaxTablesPerInventory = 5000;

    static DateTime Now(this HttpContext ctx) => ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

    public static void MapAgentApi(this WebApplication app)
    {
        // pairing: the only unauthenticated agent call; single-use code, rate limited per address
        app.MapPost($"{ApiV1.Prefix}/pair/redeem", async (PairRedeemRequest req, HttpContext ctx, Db db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code) || req.CabinetName is not { Length: > 0 and <= 100 })
                return Results.BadRequest("code and cabinetName are required");
            var now = ctx.Now();
            await using var conn = await db.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var codeHash = Security.Sha256(Security.NormalizeCode(req.Code));
            var claimed = await conn.ExecuteAsync(
                "UPDATE pairing_code SET used_at_utc = @now WHERE code_hash = @codeHash AND used_at_utc IS NULL AND expires_at_utc > @now",
                new { now, codeHash }, tx);
            if (claimed == 0) return Results.Problem("pairing code is invalid, expired or already used", statusCode: 403);
            var id = "cab-" + Guid.NewGuid().ToString("N")[..10];
            var credential = Security.NewCredential();
            await conn.ExecuteAsync("""
                INSERT INTO cabinet (id, name, credential_hash, paired_at_utc, paired_by, paired_by_name)
                SELECT @id, @name, @hash, @now, created_by, created_by_name FROM pairing_code WHERE code_hash = @codeHash
                """, new { id, name = req.CabinetName.Trim(), hash = Security.Sha256(credential), now, codeHash }, tx);
            await conn.ExecuteAsync("UPDATE pairing_code SET cabinet_id = @id WHERE code_hash = @codeHash", new { id, codeHash }, tx);
            await tx.CommitAsync();
            return Results.Ok(new PairRedeemResponse(id, credential));
        }).RequireRateLimiting("pairing");

        var agent = app.MapGroup($"{ApiV1.Prefix}/agent").AddEndpointFilter(Security.RequireCabinet);

        agent.MapPut("/inventory", async (InventoryRequest req, HttpContext ctx, Db db, Discord.LeaderboardPublisher publisher) =>
        {
            if (req.Tables is null || req.Tables.Count > MaxTablesPerInventory) return Results.BadRequest("invalid inventory");
            var cabinet = ctx.CabinetId();
            var now = ctx.Now();
            var rows = req.Tables
                .Where(t => !string.IsNullOrWhiteSpace(t.Table) && !string.IsNullOrWhiteSpace(t.RomId))
                .GroupBy(t => t.Table.Trim()).Select(g => g.First())
                .Select(t => new { cabinet, table = t.Table.Trim(), rom = Competitions.NormalizeRom(t.RomId), t.Eligible, t.Reason, now })
                .ToList();
            await using var conn = await db.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("DELETE FROM installation WHERE cabinet_id = @cabinet", new { cabinet }, tx);
            var version = req.AgentVersion is { Length: > 0 and <= 40 } v ? v : null;
            // sent when PinBot starts or its tables change, never mid-game: no table is running
            await conn.ExecuteAsync("UPDATE cabinet SET agent_version = @version, playing_since = NULL WHERE id = @cabinet", new { version, cabinet }, tx);
            await conn.ExecuteAsync("""
                INSERT INTO installation (cabinet_id, table_name, rom_id, eligible, reason, reported_at_utc)
                VALUES (@cabinet, @table, @rom, @Eligible, @Reason, @now)
                """, rows, tx);
            await tx.CommitAsync();
            publisher.Wake(); // a newly paired cabinet gets announced
            return Results.Ok(new InventoryResponse(rows.Count, rows.Where(r => r.Eligible).Select(r => r.rom).Distinct().Count()));
        });

        // known=<version>&wait=<seconds>: hold the request until a competition starts or ends (at most 25 s), so a new
        // competition reaches every online cabinet within a second instead of at its next minute-by-minute sync
        agent.MapGet("/competitions", async (long? known, int? wait, HttpContext ctx, Db db, ServerOptions options, CompetitionChanges changes) =>
        {
            if (known is { } k && wait is > 0)
                await changes.WaitAsync(k, TimeSpan.FromSeconds(Math.Min(wait.Value, MaxWaitSeconds)), ctx.RequestAborted);
            if (ctx.RequestAborted.IsCancellationRequested) return Results.Empty; // the cabinet hung up: nobody to answer
            var version = changes.Version;
            var now = ctx.Now();
            await using var conn = await db.OpenAsync();
            return Results.Ok(new AgentCompetitionsResponse(now, await Competitions.ForCabinetAsync(conn, ctx.CabinetId(), now, options.UploadGrace), version,
                AgentDownload.LatestRelease() ?? 0));
        });

        // a table started on this cabinet, or none runs any more: shown as busy (not offline) on cards and in /pinbot status
        agent.MapPut("/activity", async (AgentActivity req, HttpContext ctx, Db db, Discord.LeaderboardPublisher publisher) =>
        {
            var now = ctx.Now();
            await using var conn = await db.OpenAsync();
            await conn.ExecuteAsync("UPDATE cabinet SET playing_since = CASE WHEN @Playing THEN COALESCE(playing_since, @now) END WHERE id = @cabinet",
                new { req.Playing, now, cabinet = ctx.CabinetId() });
            publisher.Wake();
            return Results.NoContent();
        });

        // where this cabinet stands with each competition it takes part in: shown live on the Discord card
        agent.MapPut("/competition-states", async (CompetitionStatesRequest req, HttpContext ctx, Db db, Discord.LeaderboardPublisher publisher) =>
        {
            if (req.States is null || req.States.Count > 200) return Results.BadRequest("send up to 200 states");
            var cabinet = ctx.CabinetId();
            var now = ctx.Now();
            var rows = req.States
                .Where(s => s.State is CompetitionStates.Ready or CompetitionStates.Waiting or CompetitionStates.Refused)
                .Select(s => new { s.CompetitionId, cabinet, s.State, detail = s.Detail is { Length: > 200 } d ? d[..200] : s.Detail, now }).ToList();
            await using var conn = await db.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO competition_cabinet (competition_id, cabinet_id, state, detail, updated_at_utc)
                SELECT @CompetitionId, @cabinet, @State, @detail, @now WHERE EXISTS (SELECT 1 FROM competition WHERE id = @CompetitionId)
                ON CONFLICT (competition_id, cabinet_id) DO UPDATE SET state = EXCLUDED.state, detail = EXCLUDED.detail, updated_at_utc = EXCLUDED.updated_at_utc
                """, rows);
            publisher.Wake();
            return Results.NoContent();
        });

        agent.MapPost("/results", async (ResultsRequest req, HttpContext ctx, Db db, ServerOptions options, Discord.LeaderboardPublisher publisher) =>
        {
            if (req.Events is null || req.Events.Count > MaxEventsPerRequest) return Results.BadRequest($"send 1-{MaxEventsPerRequest} events");
            var now = ctx.Now();
            await using var conn = await db.OpenAsync();
            var outcomes = new List<ResultOutcome>(req.Events.Count);
            foreach (var ev in req.Events)
                outcomes.Add(await ResultIngest.IngestAsync(conn, ctx.CabinetId(), ev, now, options));
            if (outcomes.Any(o => o.Status == ResultStatus.Accepted)) publisher.Wake();
            return Results.Ok(new ResultsResponse(outcomes));
        });
    }

    /// <summary>Optional operations API (pairing codes, revoking a cabinet, ROM aliases, competitions) for scripts and tests.
    /// Only mapped when PinBot:AdminToken is set; Discord is the normal way to run PinBot.</summary>
    public static void MapAdminApi(this WebApplication app)
    {
        var admin = app.MapGroup($"{ApiV1.Prefix}/admin").AddEndpointFilter(Security.RequireAdmin);

        admin.MapPost("/pairing-codes", async (HttpContext ctx, Db db, ServerOptions options) =>
        {
            await using var conn = await db.OpenAsync();
            var (code, expires) = await Pairing.CreateCodeAsync(conn, ctx.Now(), options.PairingCodeLifetime);
            return Results.Ok(new CreatePairingCodeResponse(code, expires));
        });

        admin.MapPost("/cabinets/{id}/revoke", async (string id, HttpContext ctx, Db db) =>
        {
            await using var conn = await db.OpenAsync();
            var n = await conn.ExecuteAsync("UPDATE cabinet SET revoked_at_utc = @now WHERE id = @id AND revoked_at_utc IS NULL", new { id, now = ctx.Now() });
            return n == 1 ? Results.NoContent() : Results.NotFound();
        });

        admin.MapGet("/games", async (Db db) =>
        {
            await using var conn = await db.OpenAsync();
            return Results.Ok(await Competitions.EligibleGamesAsync(conn));
        });

        admin.MapPut("/rom-aliases", async (AliasesRequest req, Db db) =>
        {
            await using var conn = await db.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("DELETE FROM rom_alias", transaction: tx);
            await conn.ExecuteAsync("INSERT INTO rom_alias (rom_id, canonical_game_id) VALUES (@rom, @game)",
                req.RomToGame.Select(kv => new { rom = Competitions.NormalizeRom(kv.Key), game = kv.Value.Trim().ToLowerInvariant() }), tx);
            await tx.CommitAsync();
            return Results.NoContent();
        });

        admin.MapPut("/members", async (MembersRequest req, HttpContext ctx, Db db) =>
        {
            await using var conn = await db.OpenAsync();
            return Results.Ok(await Players.ReplaceMembersAsync(conn, req.Members, ctx.Now()));
        });

        admin.MapGet("/competitions", async (HttpContext ctx, Db db, ServerOptions options) =>
        {
            await using var conn = await db.OpenAsync();
            var now = ctx.Now();
            return Results.Ok((await Competitions.ListAsync(conn)).Select(c => View(c, now, options)));
        });

        admin.MapPost("/competitions", async (CreateCompetitionRequest req, HttpContext ctx, Db db, ServerOptions options, CompetitionChanges changes) =>
        {
            if (string.IsNullOrWhiteSpace(req.Game) || req.Hours is <= 0 or > 24 * 31) return Results.BadRequest("game and 0 < hours <= 744 are required");
            var kind = req.Kind ?? "oneoff";
            if (kind is not ("oneoff" or "weekly")) return Results.BadRequest("kind must be oneoff or weekly");
            var now = ctx.Now();
            var starts = req.StartsAtUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : now;
            await using var conn = await db.OpenAsync();
            var result = await Competitions.CreateAsync(conn, req.Game, kind, req.CreatedBy ?? "admin", starts, starts.AddHours(req.Hours), now);
            if (result is CreateResult.Created) changes.Notify();
            return result switch
            {
                CreateResult.Created c => Results.Created($"{ApiV1.Prefix}/admin/competitions/{c.Competition.Id}", View(c.Competition, now, options)),
                CreateResult.Conflict c => Results.Conflict(new { error = "a competition for this game already overlaps", existing = View(c.Existing, now, options) }),
                CreateResult.NotEligible n => Results.UnprocessableEntity(new { error = n.Reason }),
                _ => throw new InvalidOperationException(),
            };
        });

        admin.MapPost("/competitions/{id:long}/end", async (long id, HttpContext ctx, Db db, ServerOptions options, CompetitionChanges changes) =>
        {
            await using var conn = await db.OpenAsync();
            var now = ctx.Now();
            var ended = await Competitions.EndEarlyAsync(conn, id, now);
            changes.Notify();
            return ended is { } c ? Results.Ok(View(c, now, options)) : Results.NotFound();
        });

        admin.MapGet("/competitions/{id:long}/leaderboard", async (long id, Db db) =>
        {
            await using var conn = await db.OpenAsync();
            return await Competitions.GetAsync(conn, id) is null ? Results.NotFound() : Results.Ok(await Competitions.LeaderboardAsync(conn, id));
        });
    }

    static CompetitionView View(CompetitionRow c, DateTime now, ServerOptions o) =>
        new(c.Id, c.CanonicalGameId, c.Title, c.Kind, c.CreatedBy, c.StartsAtUtc, c.EffectiveEndUtc, c.Status(now, o.UploadGrace));
}
