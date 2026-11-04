using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinBot.Server.Vps;

/// <summary>One file entry of the Virtual Pinball Spreadsheet: a table, backglass, ROM, colorization or alt sound.</summary>
public sealed record VpsFile(string Id, string? Version, IReadOnlyList<string> Authors, IReadOnlyList<string> Features,
    string? Comment, string? Url, string? ImgUrl);

public sealed record VpsGame(string Id, string Name, string? Manufacturer, int? Year, string? ImgUrl,
    IReadOnlyList<VpsFile> Tables, IReadOnlyList<VpsFile> Backglasses, IReadOnlyList<VpsFile> Roms,
    IReadOnlyList<VpsFile> AltColors, IReadOnlyList<VpsFile> AltSounds)
{
    /// <summary>"24 (Stern 2009)", the way table files are usually named.</summary>
    public string Title => Manufacturer is { } m && Year is { } y ? $"{Name} ({m} {y})" : Name;

    public string PageUrl => $"{VpsIndex.Site}/tables?game={Uri.EscapeDataString(Id)}";
    public string TablePageUrl(VpsFile table) => $"{PageUrl}&fileType=tables&fileId={Uri.EscapeDataString(table.Id)}";
}

/// <summary>What PinBot could identify on VPS for one table file. Anything not identified for certain is null.</summary>
public sealed record VpsMatch(VpsGame Game, VpsFile? Table, VpsFile? Backglass, VpsFile? Rom, VpsFile? AltColor, VpsFile? AltSound);

/// <summary>
/// The parts of the VPS database PinBot uses (docs/TABLE-UPDATES.md), parsed defensively: unknown fields are ignored,
/// a malformed entry is skipped rather than failing the whole catalog, and only https links are kept.
/// </summary>
public sealed class VpsIndex
{
    public const string Site = "https://virtualpinballspreadsheet.github.io";
    const int MaxText = 300;

    public IReadOnlyList<VpsGame> Games { get; }
    public long LastUpdated { get; }
    readonly Dictionary<string, List<VpsGame>> _byRom = new(StringComparer.OrdinalIgnoreCase);

    public VpsIndex(IReadOnlyList<VpsGame> games, long lastUpdated)
    {
        Games = games;
        LastUpdated = lastUpdated;
        foreach (var g in games)
            foreach (var rom in g.Roms.Select(r => r.Version).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                (_byRom.TryGetValue(rom, out var list) ? list : _byRom[rom] = []).Add(g);
    }

    // ---------------- parsing the VPS download ----------------

    /// <summary>Parses vpsdb.json (an array of games). Throws <see cref="JsonException"/> only if it isn't such an array.</summary>
    public static VpsIndex Parse(Stream json, long lastUpdated)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("the VPS database is not a list of games");
        var games = new List<VpsGame>();
        foreach (var g in doc.RootElement.EnumerateArray())
            if (Game(g) is { } game) games.Add(game);
        return new(games, lastUpdated);
    }

    static VpsGame? Game(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object || Str(g, "id") is not { } id || Str(g, "name") is not { } name) return null;
        int? year = g.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yy) ? yy : null;
        return new(id, name, Str(g, "manufacturer"), year, Link(Str(g, "imgUrl")),
            Files(g, "tableFiles", f => Str(f, "tableFormat") is null or "VPX"),
            Files(g, "b2sFiles"), Files(g, "romFiles"), Files(g, "altColorFiles"), Files(g, "altSoundFiles"));
    }

    static List<VpsFile> Files(JsonElement g, string property, Func<JsonElement, bool>? keep = null)
    {
        var list = new List<VpsFile>();
        if (!g.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var f in arr.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object || Str(f, "id") is not { } id || (keep != null && !keep(f))) continue;
            string? url = null;
            if (f.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                url = urls.EnumerateArray().Select(u => u.ValueKind == JsonValueKind.Object ? Link(Str(u, "url")) : null).FirstOrDefault(u => u != null);
            url ??= Link(Str(f, "url"));
            list.Add(new(id, Str(f, "version"), Strs(f, "authors"), Strs(f, "features"), Str(f, "comment"), url, Link(Str(f, "imgUrl"))));
        }
        return list;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s
            ? (s.Length > MaxText ? s[..MaxText] : s) : null;

    static List<string> Strs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim())
                .Where(s => s.Length is > 0 and <= MaxText).Take(40).ToList()
            : [];

    /// <summary>Only absolute https links: anything else (javascript:, http:, relative) is dropped.</summary>
    internal static string? Link(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && url.Length <= 500 ? u.AbsoluteUri : null;

    // ---------------- the compact copy kept in the database ----------------

    static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(Games, Compact);

    public static VpsIndex FromJson(string json, long lastUpdated) =>
        new(JsonSerializer.Deserialize<List<VpsGame>>(json, Compact) ?? [], lastUpdated);

    // ---------------- matching a table file ----------------

    /// <summary>
    /// The VPS game of a table (by its ROM, else by its own name), and the entries that are certainly the member's.
    /// Several equally good candidates mean none: a post never presents a guess as the member's file.
    /// </summary>
    public VpsMatch? Match(string? rom, string? tableName, string? fileName, string? version, string? authors,
        string? backglassAuthor, int? backglassDmd, bool altColor, bool altSound)
    {
        var tableAuthors = Names(authors);
        // same version; then the entry sharing the most authors with the table's own list, when exactly one does
        VpsFile? TableIn(VpsGame g)
        {
            var sameVersion = g.Tables.Where(t => SameVersion(t.Version, version)).ToList();
            return tableAuthors.Count == 0 ? One(sameVersion) : Best(sameVersion, t => t.Authors.Count(a => tableAuthors.Contains(Norm(a))));
        }
        var game = FindGame(rom, tableName, fileName, TableIn);
        if (game is null) return null;
        var table = TableIn(game);
        VpsFile? b2s = null;
        if (Names(backglassAuthor) is { Count: > 0 } b2sAuthors)
        {
            var byAuthor = game.Backglasses.Where(b => b.Authors.Any(a => b2sAuthors.Contains(Norm(a)))).ToList();
            b2s = One(byAuthor);
            if (b2s is null && byAuthor.Count > 1 && backglassDmd == 3)
                b2s = One(byAuthor.Where(b => b.Features.Any(f => f.Equals("FullDMD", StringComparison.OrdinalIgnoreCase))));
        }
        var romFile = rom is null ? null
            : game.Roms.Where(r => string.Equals(r.Version, rom, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Url is null).FirstOrDefault();
        return new(game, table, b2s, romFile, altColor ? One(game.AltColors) : null, altSound ? One(game.AltSounds) : null);
    }

    /// <summary>
    /// The game: the only one with this ROM; else, among the games sharing the ROM (mods and originals reuse ROMs), the
    /// only one whose name matches the table's, or failing that the only one listing this table version. Without a
    /// ROM, the only game whose name matches.
    /// </summary>
    VpsGame? FindGame(string? rom, string? tableName, string? fileName, Func<VpsGame, VpsFile?> tableIn)
    {
        var names = new[] { tableName, fileName }.OfType<string>().ToList();
        var full = names.Select(Norm).Where(n => n.Length > 0).ToHashSet();
        var core = names.Select(Core).Where(n => n.Length > 0).ToHashSet();
        bool Named(VpsGame g) => full.Contains(Norm(g.Title)) || full.Contains(Norm(g.Name)) || core.Contains(Core(g.Name));
        if (rom is not null && _byRom.TryGetValue(rom, out var byRom))
            return byRom.Count == 1 ? byRom[0] : One(byRom.Where(Named)) ?? One(byRom.Where(g => tableIn(g) != null));
        return One(Games.Where(Named));
    }

    /// <summary>The name without anything in parentheses: "Transformers (Pro)" and "Transformers (Stern 2011)" -> "transformers".</summary>
    static string Core(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        int depth = 0;
        foreach (var c in s)
        {
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (depth == 0) sb.Append(c);
        }
        return Norm(sb.ToString());
    }

    /// <summary>The one item with the highest score above zero, or null when none scores or several share the top.</summary>
    static T? Best<T>(IEnumerable<T> items, Func<T, int> score) where T : class
    {
        var top = items.Select(i => (Item: i, Score: score(i))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(2).ToList();
        return top.Count == 1 || (top.Count == 2 && top[0].Score > top[1].Score) ? top[0].Item : null;
    }

    static T? One<T>(IEnumerable<T> items) where T : class
    {
        T? found = null;
        foreach (var i in items)
        {
            if (found != null) return null;
            found = i;
        }
        return found;
    }

    /// <summary>"v2.4.1", "2.4.1 " and "2.4.1" are the same version.</summary>
    static bool SameVersion(string? a, string? b) =>
        a is not null && b is not null && string.Equals(V(a), V(b), StringComparison.OrdinalIgnoreCase);

    static string V(string v) => v.Trim().TrimStart('v', 'V').Trim();

    /// <summary>Lower-case letters and digits only: "Freneticamnesic" and "freneticamnesic " are one author.</summary>
    static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>"freneticamnesic, 32assassin & RobbyKingPin" -> the normalized names.</summary>
    static HashSet<string> Names(string? list) =>
        list is null ? []
            : list.Split([',', '&', '/', ';', '+'], StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(p => p.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
                .Select(Norm).Where(n => n.Length > 0).ToHashSet();
}
