using System.Text.Json;
using System.Text.RegularExpressions;

// Generates src/PinBot.Server/rom-variants.json from the pinned memory maps: ROMs that are the same game as another
// ROM except for coin play / free play. Run after updating the maps submodule:
//   dotnet run --project tools/PinBot.Variants -- vendor/pinball-memory-maps src/PinBot.Server/rom-variants.json
// A pair is listed only when PinMAME names both ROMs identically apart from "Coin Play" / "Free Play" (romnames.json,
// e.g. cv_20hc "Cirqus Voltaire (2.0H Coin Play)" and cv_20h "Cirqus Voltaire (2.0H)") AND both use the same memory map
// (the same NVRAM layout). Any other difference, a version number included, keeps them separate games.
if (args is not [var maps, var output]) { Console.Error.WriteLine("usage: PinBot.Variants MAPS_DIR OUTPUT.json"); return 1; }
static Dictionary<string, string> Read(string path) => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.EnumerateObject()
    .Where(p => !p.Name.StartsWith('_') && p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);
var titles = Read(Path.Combine(maps, "romnames.json"));
var mapOf = Read(Path.Combine(maps, "index.json"));
var byTitle = titles.Where(t => mapOf.ContainsKey(t.Key)).GroupBy(t => t.Value).ToDictionary(g => g.Key, g => g.Select(t => t.Key).ToList());
var play = new Regex(@"\s*\b(coin|free) play\b\s*", RegexOptions.IgnoreCase);
var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
int ambiguous = 0;
foreach (var (rom, title) in titles.Where(t => mapOf.ContainsKey(t.Key)))
{
    var m = Regex.Match(title, @"^(?<name>.*?)\s*\((?<par>[^()]*)\)\s*$");
    if (!m.Success || !play.IsMatch(m.Groups["par"].Value)) continue;
    var basePar = play.Replace(m.Groups["par"].Value, " ").Trim();
    var baseTitle = basePar.Length > 0 ? $"{m.Groups["name"].Value} ({basePar})" : m.Groups["name"].Value;
    var bases = byTitle.GetValueOrDefault(baseTitle, []).Where(b => b != rom && mapOf[b] == mapOf[rom]).ToList();
    if (bases.Count == 1) variants[rom] = bases[0];
    else if (bases.Count > 1) ambiguous++;
}
File.WriteAllText(output, JsonSerializer.Serialize(variants, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"{variants.Count} coin/free play variants written; {ambiguous} ambiguous left out");
return 0;
