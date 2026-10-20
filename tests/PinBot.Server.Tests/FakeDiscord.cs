using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Signers;
using PinBot.Server.Discord;

namespace PinBot.Server.Tests;

/// <summary>Records what PinBot would send to Discord's REST API and serves a configurable member list.</summary>
public sealed class FakeDiscord : IDiscordApi
{
    public Dictionary<string, (string Channel, JsonObject Message)> Messages { get; } = [];
    public List<string> Edits { get; } = [];
    public List<DiscordMember> Members { get; set; } = [];
    public bool RefuseMembers { get; set; }
    int _next = 1000;

    /// <summary>Direct messages, by recipient (channel "dm-    public Task<string> PostMessageAsync(string channelId, JsonObject message, CancellationToken ct = default)lt;user id    public Task<string> PostMessageAsync(string channelId, JsonObject message, CancellationToken ct = default)gt;").</summary>
    public List<string> DirectMessages(string userId) =>
        Messages.Values.Where(m => m.Channel == "dm-" + userId).Select(m => m.Message["content"]!.GetValue<string>()).ToList();

    /// <summary>Direct messages to these users fail like a Discord outage (500).</summary>
    public HashSet<string> FailDmsTo { get; } = [];

    public Task<string> OpenDmAsync(string userId, CancellationToken ct = default) =>
        FailDmsTo.Contains(userId) ? throw new DiscordRestException(HttpStatusCode.InternalServerError, "Internal Server Error") : Task.FromResult("dm-" + userId);

    public Task<string> PostMessageAsync(string channelId, JsonObject message, CancellationToken ct = default)
    {
        var id = (_next++).ToString();
        Messages[id] = (channelId, message);
        return Task.FromResult(id);
    }

    public HashSet<string> Pinned { get; } = [];
    public bool RefusePins { get; set; }

    public Task SetPinnedAsync(string channelId, string messageId, bool pinned, CancellationToken ct = default)
    {
        if (RefusePins) throw new DiscordRestException(HttpStatusCode.Forbidden, "Missing Permissions");
        if (!Messages.ContainsKey(messageId)) throw new DiscordRestException(HttpStatusCode.NotFound, "Unknown Message");
        if (pinned) Pinned.Add(messageId); else Pinned.Remove(messageId);
        return Task.CompletedTask;
    }

    public Task EditMessageAsync(string channelId, string messageId, JsonObject message, CancellationToken ct = default)
    {
        if (!Messages.ContainsKey(messageId)) throw new DiscordRestException(HttpStatusCode.NotFound, "Unknown Message");
        Messages[messageId] = (channelId, message);
        Edits.Add(messageId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DiscordMember>> ListMembersAsync(string guildId, CancellationToken ct = default) =>
        RefuseMembers ? throw new DiscordRestException(HttpStatusCode.Forbidden, "Missing Access") : Task.FromResult<IReadOnlyList<DiscordMember>>(Members);

    public Task RegisterGuildCommandsAsync(string applicationId, string guildId, JsonArray commands, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Leaderboard cards (embeds) and plain notices (lead, pass and end messages).</summary>
    public List<JsonObject> Cards => Messages.Values.Where(m => m.Message["embeds"] is not null).Select(m => m.Message).ToList();
    public List<string> Notices => Messages.Values.Where(m => m.Message["embeds"] is null && !m.Channel.StartsWith("dm-")).Select(m => m.Message["content"]!.GetValue<string>()).ToList();

    /// <summary>Text of an embed's description (the leaderboard body).</summary>
    public static string Body(JsonObject message) => message["embeds"]![0]!["description"]!.GetValue<string>();
}

/// <summary>Builds and signs interaction requests exactly as Discord does (Ed25519 over timestamp + raw body).</summary>
public static class DiscordRequests
{
    public const ulong Manager = 1UL << 5;

    public static JsonObject Command(string sub, JsonArray? options = null, string userId = "u1", string nick = "Alex | [SKY]",
        ulong permissions = 0, int type = 2, string guild = "guild1", string command = "pinbot") => new()
    {
        ["type"] = type,
        ["guild_id"] = guild,
        ["member"] = new JsonObject
        {
            ["user"] = new JsonObject { ["id"] = userId, ["username"] = "user_" + userId },
            ["nick"] = nick,
            ["permissions"] = permissions.ToString(),
        },
        ["data"] = new JsonObject
        {
            ["name"] = command,
            ["options"] = new JsonArray(new JsonObject { ["type"] = 1, ["name"] = sub, ["options"] = options ?? [] }),
        },
    };

    public static JsonObject Opt(string name, JsonNode value, bool focused = false)
    {
        var o = new JsonObject { ["name"] = name, ["value"] = value };
        if (focused) o["focused"] = true;
        return o;
    }

    public static async Task<HttpResponseMessage> SendAsync(TestServer s, JsonObject interaction, DateTime? signedAt = null, bool tamper = false)
    {
        var body = interaction.ToJsonString();
        var ts = new DateTimeOffset(signedAt ?? s.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds().ToString();
        var signer = new Ed25519Signer();
        signer.Init(true, s.DiscordKey!);
        var bytes = Encoding.UTF8.GetBytes(ts + body);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        var sig = Convert.ToHexString(signer.GenerateSignature()).ToLowerInvariant();
        if (tamper) body = body.Replace("u1", "u2");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/discord/interactions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Signature-Ed25519", sig);
        req.Headers.Add("X-Signature-Timestamp", ts);
        return await s.Client().SendAsync(req);
    }

    /// <summary>Sends and returns the interaction response's data object (content, flags, choices).</summary>
    public static async Task<JsonObject> AskAsync(TestServer s, JsonObject interaction)
    {
        var r = await SendAsync(s, interaction);
        r.EnsureSuccessStatusCode();
        var json = (await r.Content.ReadFromJsonAsync<JsonObject>())!;
        return json["data"]!.AsObject();
    }

    public static bool IsEphemeral(JsonObject data) => data["flags"]?.GetValue<int>() == 64;
}
