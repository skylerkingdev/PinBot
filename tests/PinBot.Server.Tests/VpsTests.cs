using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PinBot.Server.Discord;
using PinBot.Server.Vps;

namespace PinBot.Server.Tests;

/// <summary>A hand-made catalog shaped like the VPS database (vpsdb.json): ids, names and links are made up.</summary>
static class VpsSample
{
    public const string Json = """
        [
          { "id": "G24", "name": "24", "manufacturer": "Stern", "year": 2009, "imgUrl": "https://img.example/24.webp",
            "unknownField": { "anything": [1, 2] },
            "tableFiles": [
              { "id": "T241", "version": "2.4.1", "authors": ["RobbyKingPin", "32assassin", "Freneticamnesic"], "tableFormat": "VPX",
                "features": ["VR", "Hybrid"], "urls": [ { "url": "https://vpuniverse.example/files/24-v241/" } ], "imgUrl": "https://img.example/t241.webp" },
              { "id": "T40", "version": "4.0", "authors": ["Bigus1", "32assassin"], "tableFormat": "VPX", "urls": [ { "url": "https://vpforums.example/24-40" } ] },
              { "id": "TFP", "version": "2.4.1", "authors": ["RobbyKingPin"], "tableFormat": "FP", "urls": [ { "url": "https://fp.example/24" } ] },
              { "id": "TBAD", "version": "9", "authors": ["x"], "tableFormat": "VPX", "urls": [ { "url": "javascript:alert(1)" }, { "url": "http://insecure.example" } ] },
              "not an object",
              { "version": "no id" }
            ],
            "b2sFiles": [
              { "id": "B1", "version": "1.0.0", "authors": ["HauntFreaks"], "features": ["2Screens", "FullDMD"], "urls": [ { "url": "https://vpuniverse.example/b2s-hf" } ] },
              { "id": "B2", "version": "1.0", "authors": ["Wildman"], "urls": [ { "url": "https://vpuniverse.example/b2s-w" } ] }
            ],
            "romFiles": [ { "id": "R1", "version": "twenty4_150", "urls": [ { "url": "https://vpuniverse.example/rom" } ] } ],
            "altColorFiles": [ { "id": "C1", "version": "1.0", "authors": ["Colorist"], "urls": [ { "url": "https://vpuniverse.example/color" } ] } ]
          },
          { "id": "GTF", "name": "Transformers (Pro)", "manufacturer": "Stern", "year": 2011,
            "tableFiles": [
              { "id": "TF1", "version": "1.1", "authors": ["SG1bsoN", "HauntFreaks", "JPSalas"], "tableFormat": "VPX" },
              { "id": "TF2", "version": "1.1", "authors": ["UnclePaulie", "HauntFreaks"], "tableFormat": "VPX" }
            ],
            "b2sFiles": [
              { "id": "TB1", "authors": ["HauntFreaks"], "features": ["FullDMD"] },
              { "id": "TB2", "authors": ["HauntFreaks"], "features": ["2Screens"] },
              { "id": "TB3", "authors": ["HauntFreaks"], "features": ["FullDMD", "3Screens"] }
            ],
            "romFiles": [ { "id": "TR", "version": "tf_180" } ]
          },
          { "id": "GJP", "name": "JP's Transformers", "manufacturer": "Original", "year": 2018,
            "tableFiles": [ { "id": "JP1", "version": "1.0", "authors": ["JPSalas"], "tableFormat": "VPX" } ],
            "romFiles": [ { "id": "JR", "version": "tf_180" } ] },
          { "id": "GEM", "name": "Old EM", "manufacturer": "Gottlieb", "year": 1965, "tableFiles": [ { "id": "E1", "version": "1.0", "authors": ["Someone"] } ] },
          { "name": "a game without an id" }
        ]
        """;

    public static VpsIndex Index(long lastUpdated = 1) => VpsIndex.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Json)), lastUpdated);

    /// <summary>The sample repeated under new ids: a catalog big enough to pass the size check.</summary>
    public static string Big(int games)
    {
        var one = JsonDocument.Parse(Json).RootElement[0].GetRawText();
        return "[" + string.Join(",", Enumerable.Range(0, games).Select(i => one.Replace("\"G24\"", $"\"G{i}\""))) + "]";
    }
}

public class VpsIndexTests
{
    [Fact]
    public void Malformed_entries_unsafe_links_and_other_formats_are_dropped()
    {
        var index = VpsSample.Index();
        Assert.Equal(4, index.Games.Count); // the game without an id is skipped
        var g24 = index.Games[0];
        Assert.Equal(["T241", "T40", "TBAD"], g24.Tables.Select(t => t.Id)); // FP table, a string and an id-less entry dropped
        Assert.Null(g24.Tables.Single(t => t.Id == "TBAD").Url); // javascript: and http: links are not kept
        Assert.Equal("24 (Stern 2009)", g24.Title);
        Assert.Throws<JsonException>(() => VpsIndex.Parse(new MemoryStream("{\"not\":\"a list\"}"u8.ToArray()), 1));
    }

    [Fact]
    public void Exact_table_backglass_and_rom_are_found_for_24()
    {
        var m = VpsSample.Index().Match("twenty4_150", "24 (Stern 2009)", "24 (Stern 2009) v.2.4.1", "2.4.1",
            "freneticamnesic, 32assassin, DGrimmReaper, RobbyKingPin", "HauntFreaks", 3, altColor: true, altSound: false)!;
        Assert.Equal("G24", m.Game.Id);
        Assert.Equal("T241", m.Table?.Id);   // author case differs ("Freneticamnesic"): still the same author
        Assert.Equal("B1", m.Backglass?.Id);
        Assert.Equal("R1", m.Rom?.Id);
        Assert.Equal("C1", m.AltColor?.Id);  // the only colorization, and the member has one installed
        Assert.Null(m.AltSound);
        Assert.Equal("https://virtualpinballspreadsheet.github.io/tables?game=G24&fileType=tables&fileId=T241", m.Game.TablePageUrl(m.Table!));
    }

    [Fact]
    public void Ties_are_left_unidentified_rather_than_guessed()
    {
        // two VPX entries are "1.1" with HauntFreaks among the authors; three HauntFreaks backglasses, two of them full DMD
        var m = VpsSample.Index().Match("tf_180", "Transformers", "Transformers", "v1.1", "HauntFreaks", "HauntFreaks", 3, false, false)!;
        Assert.Equal("GTF", m.Game.Id);
        Assert.Null(m.Table);
        Assert.Null(m.Backglass);
        // the author list tells them apart
        Assert.Equal("TF2", VpsSample.Index().Match("tf_180", null, "x", "1.1", "UnclePaulie, HauntFreaks", null, null, false, false)!.Table?.Id);
    }

    [Fact]
    public void A_rom_shared_by_several_games_is_settled_by_name_or_by_version()
    {
        var index = VpsSample.Index();
        // "Transformers (Stern 2011)" and VPS's "Transformers (Pro)" are the same name once the parentheses are ignored
        Assert.Equal("GTF", index.Match("tf_180", "Transformers (Stern 2011)", "Transformers", "9.9", null, null, null, false, false)?.Game.Id);
        Assert.Equal("GJP", index.Match("tf_180", "JP's Transformers", "JPs Transformers", "1.0", null, null, null, false, false)?.Game.Id);
        // no name to go by: the only game that lists this version
        Assert.Equal("JP1", index.Match("tf_180", null, "tf mod", "1.0", "JPSalas", null, null, false, false)?.Table?.Id);
        Assert.Null(index.Match("tf_180", null, "tf mod", "7.7", null, null, null, false, false)); // nothing to go by
    }

    [Fact]
    public void A_table_without_a_rom_is_found_by_its_name_and_unknown_ones_are_not()
    {
        var index = VpsSample.Index();
        Assert.Equal("E1", index.Match(null, "Old EM (Gottlieb 1965)", "Old EM", "1.0", null, null, null, false, false)?.Table?.Id);
        Assert.Null(index.Match(null, "Something Else", "Something Else", "1.0", null, null, null, false, false));
        Assert.Null(index.Match("nope_l1", "Unknown", "Unknown", "1", null, null, null, false, false));
    }

    [Fact]
    public void The_stored_copy_round_trips()
    {
        var index = VpsSample.Index(42);
        var back = VpsIndex.FromJson(index.ToJson(), 42);
        Assert.Equal(42, back.LastUpdated);
        Assert.Equal("T241", back.Match("twenty4_150", null, "24", "2.4.1", "RobbyKingPin", null, null, false, false)?.Table?.Id);
    }

    static TableUpdateRow Row(string? version = "2.4.1", string? authors = "freneticamnesic, RobbyKingPin", string? b2sAuthor = "HauntFreaks") =>
        new(1, "cab", "24 (Stern 2009) v.2.4.1", "updated", "2.0", "twenty4_150", "24 (Stern 2009)", version, authors,
            "24 (Stern 2009)", b2sAuthor, 3, true, false, DateTime.UtcNow, "approved", "111", null, null);

    [Fact]
    public void Post_links_each_identified_entry_and_says_what_isnt()
    {
        var row = Row();
        var post = TableUpdatePublisher.Post(row, TableUpdatePublisher.Match(VpsSample.Index(), row));
        var embed = post["embeds"]![0]!;
        Assert.Equal("24 (Stern 2009)", embed["title"]!.GetValue<string>());
        Assert.EndsWith("fileId=T241", embed["url"]!.GetValue<string>());
        Assert.Equal("https://img.example/t241.webp", embed["image"]!["url"]!.GetValue<string>());
        var setup = embed["fields"]![0]!["value"]!.GetValue<string>();
        Assert.Contains("🎰 Table: [v2.4.1 by RobbyKingPin, 32assassin, Freneticamnesic](https://vpuniverse.example/files/24-v241/) · VPS `T241`", setup);
        Assert.Contains("🖼️ Backglass: [HauntFreaks v1.0.0 · full DMD](https://vpuniverse.example/b2s-hf) · VPS `B1`", setup);
        Assert.Contains("💾 ROM: [`twenty4_150`](https://vpuniverse.example/rom)", setup);
        Assert.Contains("🌈 Colorization: [Colorist v1.0](https://vpuniverse.example/color)", setup);
        Assert.Contains("[All versions on VPS](https://virtualpinballspreadsheet.github.io/tables?game=G24)", setup);

        var unknown = Row(version: "2.9", b2sAuthor: "Somebody");
        var vague = TableUpdatePublisher.Post(unknown, TableUpdatePublisher.Match(VpsSample.Index(), unknown))["embeds"]![0]!["fields"]![0]!["value"]!.GetValue<string>();
        Assert.Contains("🎰 Table: v2.9 · file `24 (Stern 2009) v.2.4.1` · not identified on VPS", vague);
        Assert.Contains("🖼️ Backglass by Somebody · full DMD · not identified on VPS", vague);
    }

    [Fact]
    public void Without_vps_the_post_has_no_links_and_claims_nothing()
    {
        var setup = TableUpdatePublisher.Post(Row())["embeds"]![0]!["fields"]![0]!["value"]!.GetValue<string>();
        Assert.DoesNotContain("](", setup);
        Assert.DoesNotContain("VPS", setup);
        Assert.Contains("🎰 Table: v2.4.1 · file `24 (Stern 2009) v.2.4.1`", setup);
    }
}

/// <summary>A VPS that answers as told: a version, a database, or a failure.</summary>
public sealed class FakeVpsSource : IVpsSource
{
    public long Version { get; set; } = 1;
    public string? Database { get; set; } = VpsSample.Big(1200);
    public Exception? Fail { get; set; }
    public int Downloads { get; private set; }

    public Task<long> LastUpdatedAsync(CancellationToken ct) => Fail is { } f ? Task.FromException<long>(f) : Task.FromResult(Version);

    public Task<Stream> DatabaseAsync(CancellationToken ct)
    {
        Downloads++;
        return Fail is { } f ? Task.FromException<Stream>(f) : Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Database!)));
    }
}

public class VpsCatalogTests
{
    static async Task<(TestServer S, FakeVpsSource Source)> StartAsync()
    {
        var source = new FakeVpsSource();
        var s = await TestServer.StartAsync(services => services.AddSingleton<IVpsSource>(source));
        return (s, source);
    }

    [Fact]
    public async Task Catalog_downloads_once_per_version_and_survives_a_restart_without_vps()
    {
        var (s, source) = await StartAsync();
        await using var _ = s;
        var catalog = s.Service<VpsCatalog>();
        await catalog.RefreshAsync(default);
        Assert.Equal(1200, catalog.Current!.Games.Count);
        await catalog.RefreshAsync(default);
        Assert.Equal(1, source.Downloads); // same version: not downloaded again

        source.Fail = new HttpRequestException("VPS is down");
        await s.RestartAsync();
        var restarted = s.Service<VpsCatalog>();
        await Assert.ThrowsAsync<HttpRequestException>(() => restarted.RefreshAsync(default));
        Assert.Equal(1200, restarted.Current!.Games.Count); // the stored copy is in use
    }

    [Fact]
    public async Task Broken_tiny_or_shrunken_downloads_keep_the_last_good_catalog()
    {
        var (s, source) = await StartAsync();
        await using var _ = s;
        var catalog = s.Service<VpsCatalog>();
        source.Database = VpsSample.Big(2000);
        await catalog.RefreshAsync(default);

        // truncated, not a list, far too small, and more than 20% smaller than the catalog in use
        foreach (var bad in new[] { "{\"truncated\": ", "{\"an\":\"object\"}", VpsSample.Json, VpsSample.Big(1500) })
        {
            source.Version++;
            source.Database = bad;
            await Assert.ThrowsAnyAsync<Exception>(() => catalog.RefreshAsync(default));
            Assert.Equal(2000, catalog.Current!.Games.Count);
        }
        Assert.Equal(2000, await s.CountAsync("SELECT games::bigint FROM vps_catalog"));

        source.Version++;
        source.Database = VpsSample.Big(1700); // a normal shrink (entries merged or removed) is accepted
        await catalog.RefreshAsync(default);
        Assert.Equal(1700, catalog.Current!.Games.Count);
    }

    [Fact]
    public async Task With_vps_never_reachable_there_is_no_catalog_and_nothing_else_cares()
    {
        var (s, source) = await StartAsync();
        await using var _ = s;
        source.Fail = new TaskCanceledException("timeout");
        var catalog = s.Service<VpsCatalog>();
        await Assert.ThrowsAsync<TaskCanceledException>(() => catalog.RefreshAsync(default));
        Assert.Null(catalog.Current);
        Assert.Null(TableUpdatePublisher.Match(catalog.Current, new TableUpdateRow(1, "c", "t", "added", null, "twenty4_150", null, "1", null,
            null, null, null, false, false, DateTime.UtcNow, "approved", null, null, null)));
    }
}
