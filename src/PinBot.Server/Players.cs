using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace PinBot.Server;

public sealed record Member(string DiscordUserId, string DisplayName, bool IsBot);

public sealed record MemberMapReport(int Members, int Tagged, IReadOnlyDictionary<string, IReadOnlyList<string>> Ambiguous);

/// <summary>
/// Player identity is the Discord user id; initials only route a ROM entry to exactly one member.
/// Unknown or ambiguous initials are never guessed.
/// </summary>
public static partial class Players
{
    // "[SKY,AVK]" - one to three letters/digits per entry, comma separated
    [GeneratedRegex(@"\[\s*([A-Za-z0-9]{1,3}(?:\s*,\s*[A-Za-z0-9]{1,3})*)\s*\]")]
    private static partial Regex TagList();

    /// <summary>Initials from the first bracketed list in a server nickname, e.g. "Blake | [BLK,MAK]" -> BLK, MAK.</summary>
    public static IReadOnlyList<string> ParseTag(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return [];
        var m = TagList().Match(displayName);
        if (!m.Success) return [];
        return m.Groups[1].Value.Split(',').Select(s => s.Trim().ToUpperInvariant()).Distinct().ToList();
    }

    /// <summary>"Alex | [SKY,AVK]" -> "Alex", for display. Falls back to the full name if nothing else is left.</summary>
    public static string StripTag(string displayName)
    {
        var stripped = TagList().Replace(displayName, "").Trim().TrimEnd('|', '-', '·', ':').Trim();
        return stripped.Length > 0 ? stripped : displayName;
    }

    /// <summary>
    /// SQL for a cabinet's owner (Discord user id): the member who paired it; for a cabinet paired before Discord
    /// pairing, the member with the most accepted scores on it (in practice its owner).
    /// </summary>
    internal static string OwnerOf(string cabinet) => $"""
        COALESCE({cabinet}.paired_by, (SELECT r.discord_user_id FROM result r WHERE r.cabinet_id = {cabinet}.id AND r.status = 'accepted'
                                       GROUP BY r.discord_user_id ORDER BY count(*) DESC, r.discord_user_id LIMIT 1))
        """;

    /// <summary>
    /// How each cabinet is shown in Discord: its owner's tag, e.g. "[SKY]" (members know each other's tags, not their
    /// PCs' names), with the PC name added when one member has several cabinets; the PC name when no tag is known. The
    /// tag is the owner's current one (from member sync, else from pairing time).
    /// </summary>
    public static async Task<Dictionary<string, string>> CabinetLabelsAsync(NpgsqlConnection conn)
    {
        var cabinets = (await conn.QueryAsync<(string Id, string Name, string? OwnerId, string? OwnerName)>($"""
            SELECT c.id, c.name,
                   {OwnerOf("c")},
                   c.paired_by_name
            FROM cabinet c WHERE c.revoked_at_utc IS NULL
            """)).ToList();
        var current = (await conn.QueryAsync<(string User, string Initials)>("SELECT discord_user_id, initials FROM member_initial"))
            .GroupBy(r => r.User).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Initials).Order().ToList());
        string? Tag(string? ownerId, string? ownerName)
        {
            var initials = ownerId != null && current.TryGetValue(ownerId, out var c) ? c : ParseTag(ownerName);
            return initials.Count > 0 ? $"[{string.Join(",", initials)}]" : null;
        }
        var tagged = cabinets.Select(c => (c.Id, c.Name, Tag: Tag(c.OwnerId, c.OwnerName))).ToList();
        return tagged.ToDictionary(t => t.Id, t =>
            t.Tag is null ? t.Name : tagged.Count(o => o.Tag == t.Tag) > 1 ? $"{t.Tag} {t.Name}" : t.Tag);
    }

    /// <summary>Same normalization the agent applies to ROM initials: trimmed, uppercase.</summary>
    public static string NormalizeInitials(string initials) => initials.Trim().ToUpperInvariant();

    /// <summary>
    /// Whether a new member list lost so many tagged members at once (all of them, or more than half of at least 4)
    /// that it looks like a partial answer from Discord rather than people changing nicknames.
    /// </summary>
    public static bool SuspiciousShrink(int taggedBefore, int taggedNow) =>
        taggedBefore > 0 && (taggedNow == 0 || taggedBefore >= 4 && taggedNow * 2 < taggedBefore);

    /// <summary>Replaces the whole initials map from a full member list (bots ignored).</summary>
    public static async Task<MemberMapReport> ReplaceMembersAsync(NpgsqlConnection conn, IReadOnlyList<Member> members, DateTime now)
    {
        var rows = members.Where(m => !m.IsBot)
            .SelectMany(m => ParseTag(m.DisplayName).Select(i => new { m.DiscordUserId, Initials = i, m.DisplayName, now }))
            .ToList();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("DELETE FROM member_initial", transaction: tx);
        await conn.ExecuteAsync(
            "INSERT INTO member_initial (discord_user_id, initials, display_name, observed_at_utc) VALUES (@DiscordUserId, @Initials, @DisplayName, @now)",
            rows, tx);
        await tx.CommitAsync();

        var ambiguous = rows.GroupBy(r => r.Initials)
            .Where(g => g.Select(r => r.DiscordUserId).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.DisplayName).Distinct().ToList());
        return new(members.Count, rows.Select(r => r.DiscordUserId).Distinct().Count(), ambiguous);
    }

    /// <summary>
    /// Exactly one member claims these initials -> that member; none -> a guest using them when the game was played
    /// (<paramref name="playedAtUtc"/>), if any; several members -> null with a reason.
    /// </summary>
    public static async Task<(string? DiscordUserId, string? Reason)> ResolveAsync(NpgsqlConnection conn, string initials, DateTime playedAtUtc, NpgsqlTransaction? tx = null)
    {
        var normalized = NormalizeInitials(initials);
        var ids = (await conn.QueryAsync<string>(
            "SELECT DISTINCT discord_user_id FROM member_initial WHERE initials = @normalized", new { normalized }, tx)).ToList();
        if (ids.Count == 0 && await conn.ExecuteScalarAsync<string?>(
                "SELECT id FROM guest WHERE initials = @normalized AND expires_at_utc > @playedAtUtc ORDER BY expires_at_utc DESC LIMIT 1",
                new { normalized, playedAtUtc }, tx) is { } guest)
            return (guest, null);
        return ids.Count switch
        {
            1 => (ids[0], null),
            0 => (null, $"no member has [{initials}] in their nickname"),
            _ => (null, $"initials [{initials}] are claimed by {ids.Count} members"),
        };
    }

    /// <summary>A guest's player id: the same for the same host and initials on every visit. Never a Discord id (never mentioned).</summary>
    public static string GuestId(string hostUserId, string initials) => $"guest:{hostUserId}:{NormalizeInitials(initials)}";

    /// <summary>The host's Discord id of a guest player id, or null for a member.</summary>
    public static string? GuestHost(string playerId) =>
        playerId.StartsWith("guest:", StringComparison.Ordinal) && playerId.Split(':') is [_, var host, _] ? host : null;

    public static string GuestInitials(string playerId) => playerId[(playerId.LastIndexOf(':') + 1)..];

    /// <summary>The name given for a guest ("Alex (JB's guest ABC)" -> "Alex"), or null when none was given.</summary>
    public static string? GuestName(string displayName) =>
        displayName.EndsWith(')') && displayName.IndexOf(" (", StringComparison.Ordinal) is > 0 and var i ? displayName[..i] : null;

    public static readonly TimeSpan GuestFor = TimeSpan.FromHours(24);
    public const int MaxGuestsPerHost = 5;
}
