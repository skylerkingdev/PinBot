using Dapper;
using Npgsql;

namespace PinBot.Server;

/// <summary>Small key/value settings changed at runtime (from Discord), e.g. whether weekly competitions run.</summary>
public static class Settings
{
    public const string WeeklyEnabled = "weekly_enabled";
    public const string WeeklySkippedSlot = "weekly_skipped_slot";
    public const string WeeklyDay = "weekly_day";
    public const string WeeklyTime = "weekly_time";
    public const string WeeklyTimeZone = "weekly_timezone";
    /// <summary>Set when the schedule is set or changed: slots that began earlier are not started.</summary>
    public const string WeeklyNotBefore = "weekly_not_before";
    /// <summary>Channels set with /pinbot-admin channels: the guide and "VPin connected" notices, one-off and weekly cards.</summary>
    public const string InfoChannel = "info_channel";
    public const string ChallengesChannel = "challenges_channel";
    public const string WeeklyChannel = "weekly_channel";
    /// <summary>The guide message PinBot keeps in the info channel: "channel/message", and the hash of its content.</summary>
    public const string InfoGuideMessage = "info_guide_message";
    public const string InfoGuideHash = "info_guide_hash";
    /// <summary>The release last announced in the info channel, and the newest skipped release announced.</summary>
    public const string ReleaseAnnounced = "release_announced";
    public const string ReleaseSkipAnnounced = "release_skip_announced";

    public static Task DeleteAsync(NpgsqlConnection conn, string key) =>
        conn.ExecuteAsync("DELETE FROM setting WHERE key = @key", new { key });

    public static Task<string?> GetAsync(NpgsqlConnection conn, string key) =>
        conn.ExecuteScalarAsync<string?>("SELECT value FROM setting WHERE key = @key", new { key });

    public static Task SetAsync(NpgsqlConnection conn, string key, string value) =>
        conn.ExecuteAsync("INSERT INTO setting (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value",
            new { key, value });

    /// <summary>Channels chosen in Discord win over the ones in configuration (which only seed a new server).</summary>
    public static async Task ApplyChannelsAsync(NpgsqlConnection conn, Discord.DiscordOptions discord)
    {
        if (await GetAsync(conn, ChallengesChannel) is { } challenges) discord.ChannelId = challenges;
        if (await GetAsync(conn, WeeklyChannel) is { } weekly) discord.WeeklyChannelId = weekly;
    }
}

public static class Pairing
{
    /// <summary>A single-use pairing code; only its hash is stored.</summary>
    public static async Task<(string Code, DateTime ExpiresAtUtc)> CreateCodeAsync(NpgsqlConnection conn, DateTime now, TimeSpan lifetime,
        string? createdBy = null, string? createdByName = null)
    {
        var code = Security.NewPairingCode();
        var expires = now + lifetime;
        await conn.ExecuteAsync(
            "INSERT INTO pairing_code (code_hash, created_at_utc, expires_at_utc, created_by, created_by_name) VALUES (@hash, @now, @expires, @createdBy, @createdByName)",
            new { hash = Security.Sha256(Security.NormalizeCode(code)), now, expires, createdBy, createdByName });
        return (code, expires);
    }

    /// <summary>The one line a member pastes into pinbot.exe: server address and code together.</summary>
    public static string PairingLine(string publicUrl, string code) => $"{publicUrl.TrimEnd('/')}/pair/{code}";
}

/// <summary>Where the server keeps the cabinet agent for download (deploy/agent/pinbot.exe, copied into the image).</summary>
public static class AgentDownload
{
    public const string Route = "/download/pinbot.exe";
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "agent", "pinbot.exe");
    /// <summary>The signed description of that exe (version, hash, signature) that cabinets check before updating.</summary>
    public const string ManifestRoute = "/download/pinbot.json";
    public static string ManifestPath => System.IO.Path.Combine(AppContext.BaseDirectory, "agent", "pinbot.json");

    /// <summary>The release cabinets are offered, or null when none is published.</summary>
    public static long? LatestRelease(string? manifestPath = null)
    {
        var path = manifestPath ?? ManifestPath;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path); // read again only when the file changes (every waiting request asks)
            if (_cached is { } c && c.Path == path && c.Stamp == stamp) return c.Release;
            var json = File.ReadAllText(path);
            var release = System.Text.Json.JsonSerializer.Deserialize<PinBot.Contracts.UpdateManifest>(json,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Version;
            _cached = (path, stamp, release);
            return release;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; }
    }

    static (string Path, DateTime Stamp, long? Release)? _cached;

    /// <summary>A signed release reports its number (yyyyMMddHHmmss); older copies report a build version.</summary>
    public static long? ReleaseOf(string? agentVersion) =>
        long.TryParse(agentVersion, out var v) && v >= 20260101000000 ? v : null;
}
