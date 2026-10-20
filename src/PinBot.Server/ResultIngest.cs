using System.Globalization;
using Dapper;
using Npgsql;
using PinBot.Contracts;

namespace PinBot.Server;

public static class ResultIngest
{
    const long MaxScore = 999_999_999_999_999; // 15 digits: beyond any ROM's BCD score field

    /// <summary>
    /// Validates, attributes and stores one event exactly once. A replayed event (same id, same content) returns the
    /// stored outcome; the same id with different content is refused and not stored.
    /// </summary>
    public static async Task<ResultOutcome> IngestAsync(NpgsqlConnection conn, string cabinetId, ResultEvent ev, DateTime now, ServerOptions options)
    {
        var hash = PayloadHash(ev);
        var existing = await conn.QuerySingleOrDefaultAsync<(string Status, string? Reason, string PayloadHash, string CabinetId)?>(
            "SELECT status, reason, payload_hash, cabinet_id FROM result WHERE event_id = @EventId", new { ev.EventId });
        if (existing is { } e)
            return e.PayloadHash == hash && e.CabinetId == cabinetId
                ? new(ev.EventId, e.Status, e.Reason)
                : new(ev.EventId, ResultStatus.Rejected, "event id was already used for a different result");

        await using var tx = await conn.BeginTransactionAsync();
        var comp = await Competitions.GetAsync(conn, ev.CompetitionId, tx);
        if (comp is null)
            return new(ev.EventId, ResultStatus.Rejected, "unknown competition"); // not stored: nothing to reference

        var (status, reason, userId) = await JudgeAsync(conn, tx, comp, cabinetId, ev, now, options);
        var inserted = await conn.ExecuteAsync("""
            INSERT INTO result (event_id, competition_id, cabinet_id, session_id, rom_id, initials, score,
                                session_exited_at_utc, received_at_utc, parser_version, payload_hash, status, reason, discord_user_id)
            VALUES (@EventId, @CompetitionId, @cabinetId, @SessionId, @rom, @initials, @Score,
                    @exited, @now, @ParserVersion, @hash, @status, @reason, @userId)
            ON CONFLICT (event_id) DO NOTHING
            """, new
        {
            ev.EventId, ev.CompetitionId, cabinetId, ev.SessionId, rom = Competitions.NormalizeRom(ev.RomId),
            initials = Players.NormalizeInitials(ev.Initials), Score = Math.Clamp(ev.Score, 1, MaxScore),
            exited = DateTime.SpecifyKind(ev.SessionExitedAtUtc, DateTimeKind.Utc), now, ev.ParserVersion, hash, status, reason, userId,
        }, tx);
        await tx.CommitAsync();
        if (inserted == 0) return await IngestAsync(conn, cabinetId, ev, now, options); // lost a race with a retry: report the stored outcome
        return new(ev.EventId, status, reason);
    }

    static async Task<(string Status, string? Reason, string? UserId)> JudgeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CompetitionRow comp, string cabinetId, ResultEvent ev, DateTime now, ServerOptions options)
    {
        var exited = DateTime.SpecifyKind(ev.SessionExitedAtUtc, DateTimeKind.Utc);
        string? Invalid()
        {
            if (comp.CancelledAtUtc != null) return "competition was cancelled";
            if (ev.Score <= 0 || ev.Score > MaxScore) return "score out of range";
            var initials = Players.NormalizeInitials(ev.Initials);
            if (initials.Length is 0 or > 3 || initials.Any(c => c < 0x21 || c > 0x7E)) return "initials missing or invalid";
            if (exited > now + options.MaxClockSkew) return "session exit time is in the future (check the VPin clock)";
            if (exited < comp.StartsAtUtc) return "session exited before the competition started";
            if (exited >= comp.EffectiveEndUtc) return "session exited at or after the competition deadline";
            if (now >= comp.EffectiveEndUtc + options.UploadGrace) return "arrived after the competition became final";
            return null;
        }
        if (Invalid() is { } why) return (ResultStatus.Rejected, why, null);

        var game = await Competitions.CanonicalGameAsync(conn, ev.RomId, tx);
        if (game != comp.CanonicalGameId) return (ResultStatus.Rejected, $"ROM {ev.RomId} is not part of this competition's game", null);
        // a result comes from a table the VPin has (listed in its inventory, eligible or not): a paired PC cannot post
        // scores for games it does not have
        if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM installation WHERE cabinet_id = @cabinetId AND rom_id = @rom)",
                new { cabinetId, rom = Competitions.NormalizeRom(ev.RomId) }, tx))
            return (ResultStatus.Rejected, $"this VPin has no table with ROM {ev.RomId}", null);

        var (userId, unresolved) = await Players.ResolveAsync(conn, ev.Initials, exited, tx);
        return userId is null ? (ResultStatus.Unattributed, unresolved, null) : (ResultStatus.Accepted, null, userId);
    }

    static string PayloadHash(ResultEvent ev) => Security.Sha256(string.Join("|",
        ev.CompetitionId, ev.SessionId, Competitions.NormalizeRom(ev.RomId), Players.NormalizeInitials(ev.Initials), ev.Score,
        DateTime.SpecifyKind(ev.SessionExitedAtUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture), ev.ParserVersion));
}
