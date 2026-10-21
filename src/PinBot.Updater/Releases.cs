using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PinBot.Contracts;

namespace PinBot.Updater;

/// <summary>A published release as the updater lists it: its number (from the tag), when GitHub published it, and where
/// its signed description (server-release.json) can be downloaded.</summary>
public sealed record Published(long Version, DateTime PublishedUtc, string AssetUrl);

/// <summary>A release this server will not install again, and why (a failed start, a bad signature).</summary>
public sealed record Refused(long Version, string Reason, DateTime AtUtc);

/// <summary>
/// Which release a server should run. "early" servers (the maintainer's) take a release as soon as it is published;
/// "stable" servers take one only once it has been out for the delay (48 h): a release deleted on GitHub within that
/// time never reaches them. Never older than what runs (no downgrades), never one this server refused before.
/// </summary>
public static class Ring
{
    public static Published? Choose(IEnumerable<Published> releases, bool early, TimeSpan delay, DateTime nowUtc, long current, IEnumerable<Refused>? refused)
    {
        var skip = (refused ?? []).Select(r => r.Version).ToHashSet();
        var candidates = releases.Where(p => p.Version > current && !skip.Contains(p.Version))
            .OrderByDescending(p => p.Version).ToList();
        if (early) return candidates.FirstOrDefault();
        var settled = candidates.FirstOrDefault(p => nowUtc - p.PublishedUtc >= delay);
        // a brand-new server (nothing installed yet) starts with the newest release rather than wait 48 hours for nothing
        return settled ?? (current == 0 ? candidates.FirstOrDefault() : null);
    }

    /// <summary>"v20261021004511" -> 20261021004511; anything else is not a PinBot release tag.</summary>
    public static long? VersionOfTag(string? tag) =>
        tag is ['v', .. var digits] && digits.Length == 14 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// Reads the releases of the PinBot repository on GitHub. One listing per check, and one download only for the release
/// about to be installed, so an hourly check stays far inside GitHub's 60 requests an hour for anonymous callers.
/// </summary>
public sealed class GitHubReleases(HttpClient http, string repo, string? token)
{
    sealed record Asset(string Name, string Url);
    sealed record Rel([property: JsonPropertyName("tag_name")] string? TagName, bool Draft, bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTime? PublishedAt, List<Asset>? Assets);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<Published>> ListAsync(CancellationToken ct)
    {
        var list = await Send<List<Rel>>($"https://api.github.com/repos/{repo}/releases?per_page=30", "application/vnd.github+json", ct) ?? [];
        return list
            .Where(r => !r.Draft && !r.Prerelease && r.PublishedAt is not null)
            .Select(r => (Version: Ring.VersionOfTag(r.TagName), r.PublishedAt,
                          Asset: r.Assets?.FirstOrDefault(a => a.Name == "server-release.json")))
            .Where(x => x.Version is not null && x.Asset is not null)
            .Select(x => new Published(x.Version!.Value, DateTime.SpecifyKind(x.PublishedAt!.Value, DateTimeKind.Utc), x.Asset!.Url))
            .ToList();
    }

    /// <summary>The signed description of one release, or null when it cannot be read as one.</summary>
    public async Task<ServerRelease?> FetchAsync(Published p, CancellationToken ct)
    {
        try { return await Send<ServerRelease>(p.AssetUrl, "application/octet-stream", ct); }
        catch (JsonException) { return null; }
    }

    async Task<T?> Send<T>(string url, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("pinbot-updater", "1"));
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}
