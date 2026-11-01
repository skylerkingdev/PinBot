using System.Text.Json;
using PinBot.Agent.Scoring;
using Xunit.Abstractions;

namespace PinBot.Agent.Tests;

/// <summary>
/// Decodes real PinMAME .nv files and compares with the expected output published by francisdb/pinmame-nvram
/// (an independent implementation over the same memory maps).
/// </summary>
public class GoldenTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(TestData.FixtureDir, "*.nv").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_matches_reference_decoder(string file)
    {
        var mismatches = Compare(Path.Combine(TestData.FixtureDir, file));
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// Opt-in: set PINBOT_NVRAM_CORPUS to a checkout of francisdb/pinmame-nvram's testdata folder
    /// to check every fixture (~500 ROMs) the same way.
    /// </summary>
    [Fact]
    public void Full_reference_corpus_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("PINBOT_NVRAM_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            output.WriteLine("PINBOT_NVRAM_CORPUS not set; skipped");
            return;
        }
        var failures = new List<string>();
        int files = 0, entries = 0;
        foreach (var nv in Directory.GetFiles(dir, "*.nv").Order())
        {
            if (!File.Exists(nv + ".json")) continue;
            files++;
            var m = Compare(nv, e => entries += e);
            failures.AddRange(m);
        }
        output.WriteLine($"{files} files, {entries} entries compared, {failures.Count} mismatches");
        foreach (var f in failures.Take(200)) output.WriteLine(f);
        Assert.Empty(failures);
    }

    static List<string> Compare(string nvPath, Action<int>? countEntries = null)
    {
        var name = Path.GetFileName(nvPath);
        var rom = TestData.RomOf(name);
        var problems = new List<string>();
        using var expected = JsonDocument.Parse(File.ReadAllBytes(nvPath + ".json"));
        var root = expected.RootElement;
        if (!root.TryGetProperty("high_scores", out _)) return problems; // reference has nothing to compare

        var map = TestData.Maps.TryLoad(rom, out var why);
        if (map is null)
            return Section(root, "high_scores").Any() ? [$"{name}: map not loaded: {why}"] : []; // e.g. rapidfir: empty table
        var bytes = File.ReadAllBytes(nvPath);
        IReadOnlyList<ScoreEntry> actual;
        try { actual = map.Decode(bytes); }
        catch (Exception ex) { return [$"{name}: decode failed: {ex.Message}"]; }

        var exp = Section(root, "high_scores").Concat(Section(root, "mode_champions")).ToList();
        countEntries?.Invoke(exp.Count);
        if (exp.Count != actual.Count) problems.Add($"{name}: {actual.Count} entries decoded, reference has {exp.Count}");
        for (int i = 0; i < Math.Min(exp.Count, actual.Count); i++)
        {
            var (e, a) = (exp[i], actual[i]);
            var label = e.GetProperty("label").GetString();
            if (label != a.Label) problems.Add($"{name}[{i}]: label '{a.Label}' vs '{label}'");
            var ei = e.TryGetProperty("initials", out var ie) ? Unescape(ie.GetProperty("value").GetString()) : null;
            if (ei != a.RawInitials) problems.Add($"{name}[{i}] {label}: initials '{a.RawInitials}' vs '{ei}'");
            if (e.TryGetProperty("score", out var se))
            {
                var v = se.GetProperty("value");
                var ok = v.ValueKind == JsonValueKind.Number ? a.Score == v.GetInt64() : a.ScoreText == v.GetString();
                if (!ok) problems.Add($"{name}[{i}] {label}: score {a.Score?.ToString() ?? a.ScoreText} vs {v}");
            }
            else if (a.Score != null) problems.Add($"{name}[{i}] {label}: decoded score {a.Score} but reference has none");
        }

        // Checksums: the set of failing regions must match the reference exactly.
        // older reference files list checksums under checksum8/checksum16, newer ones under validation.checksum
        var validation = root.TryGetProperty("validation", out var val) && val.TryGetProperty("checksum", out var vc) ? vc.EnumerateArray() : [];
        var expectedBad = new[] { "checksum8", "checksum16" }
            .SelectMany(k => root.TryGetProperty(k, out var arr) ? arr.EnumerateArray() : [])
            .Concat(validation)
            .Where(c => c.TryGetProperty("checksum_mismatch_expected", out _))
            .Select(c => c.GetProperty("label").GetString()!).ToHashSet();
        var actualBad = map.VerifyChecksums(bytes, scoreRegionsOnly: false).ToHashSet();
        if (!expectedBad.SetEquals(actualBad))
            problems.Add($"{name}: failing checksums [{string.Join(", ", actualBad)}] vs reference [{string.Join(", ", expectedBad)}]");
        return problems;
    }

    /// <summary>The reference writes bytes above 0x80 as literal "\u00XX" text; we decode them as Latin-1 characters.</summary>
    static string? Unescape(string? s) => s is null ? null
        : System.Text.RegularExpressions.Regex.Replace(s, @"\\u([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    static IEnumerable<JsonElement> Section(JsonElement root, string key) =>
        root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : [];
}
