using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PinBot.Server.Discord;

/// <summary>
/// One-time Discord settings (env vars PinBot__Discord__*). Discord is enabled only when all four ids/secrets are set.
/// </summary>
public sealed record DiscordOptions(
    string? ApplicationId, string? PublicKey, string? BotToken, string? GuildId, string? ChannelId,
    string? PublicUrl, bool RunBackground, string? WeeklyChannelId = null)
{
    /// <summary>
    /// Where one-off cards go, and weekly ones (null: the same). Start from configuration; /pinbot-admin channels changes
    /// them (kept in settings and applied at every start, see <see cref="Settings.ApplyChannelsAsync"/>).
    /// </summary>
    public string? ChannelId { get; set; } = ChannelId;
    public string? WeeklyChannelId { get; set; } = WeeklyChannelId;

    /// <summary>Weekly competitions may use their own channel; everything else (and weekly, if unset) uses ChannelId.</summary>
    public string? ChannelFor(string kind) => kind == "weekly" ? WeeklyChannelId ?? ChannelId : ChannelId;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApplicationId) && !string.IsNullOrWhiteSpace(PublicKey)
                           && !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(GuildId);

    static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static DiscordOptions From(IConfiguration c) => new(
        N(c["PinBot:Discord:ApplicationId"]), N(c["PinBot:Discord:PublicKey"]), N(c["PinBot:Discord:BotToken"]),
        N(c["PinBot:Discord:GuildId"]), N(c["PinBot:Discord:ChannelId"]), N(c["PinBot:PublicUrl"])?.TrimEnd('/'),
        c.GetValue("PinBot:Discord:RunBackground", true), N(c["PinBot:Discord:WeeklyChannelId"]));
}

public sealed record DiscordMember(string UserId, string DisplayName, bool IsBot);

/// <summary>The few Discord REST calls PinBot makes. Faked in tests.</summary>
public interface IDiscordApi
{
    Task<string> PostMessageAsync(string channelId, JsonObject message, CancellationToken ct = default);
    /// <summary>The direct-message channel with a member (for messages only they should see).</summary>
    Task<string> OpenDmAsync(string userId, CancellationToken ct = default);
    Task EditMessageAsync(string channelId, string messageId, JsonObject message, CancellationToken ct = default);
    /// <summary>Pins or unpins a message (needs the Pin Messages permission in that channel).</summary>
    Task SetPinnedAsync(string channelId, string messageId, bool pinned, CancellationToken ct = default);
    Task<IReadOnlyList<DiscordMember>> ListMembersAsync(string guildId, CancellationToken ct = default);
    Task RegisterGuildCommandsAsync(string applicationId, string guildId, JsonArray commands, CancellationToken ct = default);
}

public sealed class DiscordRestException(HttpStatusCode status, string body)
    : Exception($"Discord API returned {(int)status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Discord REST v10 with a bot token. Honors 429 rate limits (retry_after), up to three attempts.</summary>
public sealed class DiscordRestApi : IDiscordApi
{
    readonly HttpClient _http;

    public DiscordRestApi(DiscordOptions options)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://discord.com/api/v10/"), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", options.BotToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/pinbot, 1.0)");
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var resp = await _http.SendAsync(request(), ct);
            if (resp.StatusCode != HttpStatusCode.TooManyRequests || attempt == 3)
            {
                if (resp.IsSuccessStatusCode) return resp;
                throw new DiscordRestException(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            }
            var body = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            var wait = body?["retry_after"]?.GetValue<double>() ?? 1.0;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0.1, 30)), ct);
        }
    }

    public async Task<string> PostMessageAsync(string channelId, JsonObject message, CancellationToken ct = default)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages")
            { Content = JsonContent.Create(message) }, ct);
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        return body!["id"]!.GetValue<string>();
    }

    public async Task<string> OpenDmAsync(string userId, CancellationToken ct = default)
    {
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "users/@me/channels")
            { Content = JsonContent.Create(new JsonObject { ["recipient_id"] = userId }) }, ct);
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        return body!["id"]!.GetValue<string>();
    }

    public async Task EditMessageAsync(string channelId, string messageId, JsonObject message, CancellationToken ct = default) =>
        await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}")
            { Content = JsonContent.Create(message) }, ct);

    public async Task SetPinnedAsync(string channelId, string messageId, bool pinned, CancellationToken ct = default) =>
        await SendAsync(() => new HttpRequestMessage(pinned ? HttpMethod.Put : HttpMethod.Delete, $"channels/{channelId}/messages/pins/{messageId}"), ct);

    /// <summary>Full member list (needs the privileged Server Members intent). Display name = server nickname, else global name.</summary>
    public async Task<IReadOnlyList<DiscordMember>> ListMembersAsync(string guildId, CancellationToken ct = default)
    {
        var members = new List<DiscordMember>();
        string after = "0";
        while (true)
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"guilds/{guildId}/members?limit=1000&after={after}"), ct);
            var page = await resp.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: ct) ?? [];
            foreach (var m in page)
            {
                var user = m!["user"]!;
                members.Add(new DiscordMember(
                    user["id"]!.GetValue<string>(),
                    m["nick"]?.GetValue<string>() ?? user["global_name"]?.GetValue<string>() ?? user["username"]!.GetValue<string>(),
                    user["bot"]?.GetValue<bool>() ?? false));
            }
            if (page.Count < 1000) return members;
            after = members[^1].UserId;
        }
    }

    public async Task RegisterGuildCommandsAsync(string applicationId, string guildId, JsonArray commands, CancellationToken ct = default) =>
        await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"applications/{applicationId}/guilds/{guildId}/commands")
            { Content = JsonContent.Create(commands) }, ct);
}
