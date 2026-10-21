using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace PinBot.Server.Discord;

/// <summary>This server's release and its notes for people (release.json, written by the release workflow).</summary>
public sealed record ReleaseInfo(long Version, IReadOnlyList<string> Notes, string? Url)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ReleaseInfo? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ReleaseInfo>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "agent", "release.json");
}

/// <summary>A release the server's updater would not install, from the updater's state file (mounted read-only).</summary>
public sealed record RefusedRelease(long Version, string Reason, DateTime AtUtc);

/// <summary>
/// Tells the info channel when this server changed release: updated (with what changed for players), went back to an
/// earlier release, or skipped one that did not start. Once per change; a brand-new server says nothing about its first.
/// </summary>
public static class ReleaseNotices
{
    /// <summary>A notice to post and how to remember it was posted (run only after the post succeeded).</summary>
    public sealed record Due(JsonObject? Message, Func<NpgsqlConnection, Task> Remember);

    /// <summary>
    /// What to tell the info channel now. Each notice is remembered on its own, right after it is posted, so a failure
    /// part way never posts an earlier one twice; skipped releases are remembered one by one, so none is ever missed.
    /// A notice without a message (a brand-new server's first release) is only remembered.
    /// </summary>
    public static async Task<IReadOnlyList<Due>> DueAsync(NpgsqlConnection conn, ReleaseInfo? running, IReadOnlyList<RefusedRelease> refused)
    {
        var due = new List<Due>();
        if (running is null) return due; // a development build: nothing to announce
        var version = running.Version.ToString(CultureInfo.InvariantCulture);
        var announced = long.TryParse(await Settings.GetAsync(conn, Settings.ReleaseAnnounced), out var a) ? a : (long?)null;
        Task RememberRelease(NpgsqlConnection c) => Settings.SetAsync(c, Settings.ReleaseAnnounced, version);
        if (announced is null) due.Add(new(null, RememberRelease)); // first start with notices: remember silently
        else if (running.Version > announced) due.Add(new(Updated(running), RememberRelease));
        else if (running.Version < announced) due.Add(new(WentBack(running.Version, announced.Value), RememberRelease));

        var told = (await Settings.GetAsync(conn, Settings.ReleaseSkipAnnounced) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        foreach (var r in refused.Where(r => r.Version > running.Version).DistinctBy(r => r.Version).OrderBy(r => r.Version))
        {
            var key = r.Version.ToString(CultureInfo.InvariantCulture);
            if (told.Contains(key)) continue;
            async Task RememberSkip(NpgsqlConnection c)
            {
                var now = (await Settings.GetAsync(c, Settings.ReleaseSkipAnnounced) ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(key).Distinct()
                    .OrderByDescending(v => v, StringComparer.Ordinal).Take(50); // the newest 50 are plenty
                await Settings.SetAsync(c, Settings.ReleaseSkipAnnounced, string.Join(",", now));
            }
            due.Add(new(announced is null ? null : Skipped(r, running.Version), RememberSkip));
        }
        return due;
    }

    /// <summary>"20261020164000" -> "20 Oct 2026, 16:40 UTC".</summary>
    public static string When(long version) =>
        DateTime.TryParseExact(version.ToString(CultureInfo.InvariantCulture), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture) : version.ToString(CultureInfo.InvariantCulture);

    public static JsonObject Updated(ReleaseInfo r)
    {
        var sb = new StringBuilder($"🔄 **PinBot was updated** (release of {When(r.Version)}).");
        var notes = r.Notes.Where(n => !string.IsNullOrWhiteSpace(n)).Take(8).ToList();
        if (notes.Count == 0) sb.Append(" Fixes and improvements.");
        foreach (var n in notes) sb.Append($"\n• {n.Trim()}");
        if (r.Notes.Count > notes.Count) sb.Append("\n• …and more");
        sb.Append("\nVPins update themselves the next time no table is running: nothing to do.");
        if (r.Url is { Length: > 0 } url) sb.Append($" [Details](<{url}>)");
        return Plain(sb.ToString());
    }

    public static JsonObject WentBack(long running, long from) =>
        Plain($"↩️ **PinBot went back** to its release of {When(running)}: the newer one ({When(from)}) did not work on this server. " +
              "Competitions carry on as before.");

    public static JsonObject Skipped(RefusedRelease r, long running) =>
        Plain($"⏭️ The PinBot release of {When(r.Version)} was **skipped** on this server ({r.Reason.TrimEnd('.')}). " +
              $"It stays on the release of {When(running)}; competitions carry on as before.");

    static JsonObject Plain(string text) => new()
    {
        ["content"] = text,
        ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
    };

    /// <summary>The updater's refused releases (its state file, mounted read-only), or none.</summary>
    public static IReadOnlyList<RefusedRelease> ReadRefused(string? updaterState)
    {
        if (updaterState is null || !File.Exists(updaterState)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(updaterState));
            return doc.RootElement.TryGetProperty("refused", out var list)
                ? list.Deserialize<List<RefusedRelease>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }
}
