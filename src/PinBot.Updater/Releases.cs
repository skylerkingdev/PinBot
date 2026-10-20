using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PinBot.Contracts;

namespace PinBot.Updater;

/// <summary>A published release as the updater sees it: its signed description and when GitHub published it.</summary>
public sealed record Published(ServerRelease Release, DateTime PublishedUtc);

/// <summary>A release this server will not install again, and why (a failed start, a bad signature).</summary>
public sealed record Refused(long Version, string Reason, DateTime AtUtc);

/// <summary>
/// Which release a server should run. "early" servers (the maintainer's) take a release as soon as it is published;
/// "stable" servers take one only once it has been out for the delay (48 h): a release deleted on GitHub within that
/// time never reaches them. Never older than what runs (no downgrades), never one this server refused before.
/// </summary>
public static class Ring
{
    public static Published? Choose(IEnumerable<Published> releases, bool early, TimeSpan delay, DateTime nowUtc, long current, IEnumerable<Refused> refused)
    {
        var skip = refused.Select(r => r.Version).ToHashSet();
        var candidates = releases.Where(p => p.Release.Version > current && !skip.Contains(p.Release.Version))
            .OrderByDescending(p => p.Release.Version).ToList();
        if (early) return candidates.FirstOrDefault();
        var settled = candidates.FirstOrDefault(p => nowUtc - p.PublishedUtc >= delay);
        // a brand-new server (nothing installed yet) starts with the newest release rather than wait 48 hours for nothing
        return settled ?? (current == 0 ? candidates.FirstOrDefault() : null);
    }
}

/// <summary>Reads the releases of the PinBot repository on GitHub: each carries server-release.json as an asset.</summary>
public sealed class GitHubReleases(HttpClient http, string repo, string? token)
{
    sealed record Asset(string Name, string Url);
    sealed record Rel(bool Draft, bool Prerelease, [property: JsonPropertyName("published_at")] DateTime? PublishedAt, List<Asset> Assets);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<Published>> ListAsync(CancellationToken ct)
    {
        var list = await Send<List<Rel>>($"https://api.github.com/repos/{repo}/releases?per_page=20", "application/vnd.github+json", ct) ?? [];
        var result = new List<Published>();
        foreach (var r in list.Where(r => !r.Draft && !r.Prerelease && r.PublishedAt is not null))
        {
            if (r.Assets.FirstOrDefault(a => a.Name == "server-release.json") is not { } asset) continue;
            try
            {
                if (await Send<ServerRelease>(asset.Url, "application/octet-stream", ct) is { } release)
                    result.Add(new Published(release, DateTime.SpecifyKind(r.PublishedAt!.Value, DateTimeKind.Utc)));
            }
            catch (JsonException) { } // not a release this updater understands: skipped
        }
        return result;
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
