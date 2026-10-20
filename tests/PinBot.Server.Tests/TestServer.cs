using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PinBot.Contracts;
using PinBot.Server;

namespace PinBot.Server.Tests;

public sealed class FakeClock(DateTime start) : TimeProvider
{
    public DateTime UtcNow { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// The real server (HTTP pipeline, PostgreSQL) on a throwaway database. Needs a local PostgreSQL: connection string
/// in PINBOT_TEST_PG, or the repo's git-ignored .pgdev file.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    public const string AdminToken = "test-admin-token";
    public FakeClock Clock { get; } = new(new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc));
    public HttpClient Admin { get; private set; } = null!;
    public string ConnectionString { get; }

    readonly string _adminCs;
    readonly string _database;
    WebApplicationFactory<Program> _factory = null!;

    TestServer(string adminCs, string database)
    {
        _adminCs = adminCs;
        _database = database;
        ConnectionString = new NpgsqlConnectionStringBuilder(adminCs) { Database = database, Pooling = false }.ConnectionString;
    }

    public static async Task<TestServer> StartAsync()
    {
        var adminCs = AdminConnectionString();
        var db = "pinbot_t_" + Guid.NewGuid().ToString("N")[..10];
        await using (var conn = new NpgsqlConnection(adminCs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {db}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var s = new TestServer(adminCs, db);
        s.Boot();
        return s;
    }

    /// <summary>Set before Boot to run with Discord configured against <see cref="Discord"/> (a fake).</summary>
    public FakeDiscord? Discord { get; private set; }
    public Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters? DiscordKey { get; private set; }

    void Boot()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PinBot", ConnectionString);
            b.UseSetting("PinBot:AdminToken", AdminToken);
            if (Discord != null)
            {
                b.UseSetting("PinBot:Discord:ApplicationId", "app1");
                b.UseSetting("PinBot:Discord:PublicKey", Convert.ToHexString(DiscordKey!.GeneratePublicKey().GetEncoded()));
                b.UseSetting("PinBot:Discord:BotToken", "bot-token");
                b.UseSetting("PinBot:Discord:GuildId", "guild1");
                b.UseSetting("PinBot:Discord:ChannelId", "chan1");
                b.UseSetting("PinBot:Discord:WeeklyChannelId", "chanW");
                b.UseSetting("PinBot:Discord:RunBackground", "false");
                b.UseSetting("PinBot:PublicUrl", "https://pinbot.example.ts.net");
                b.UseSetting("PinBot:Weekly:Day", "Monday");
                b.UseSetting("PinBot:Weekly:Time", "19:00");
                b.UseSetting("PinBot:Weekly:TimeZone", "UTC");
            }
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                if (Discord != null) s.AddSingleton<PinBot.Server.Discord.IDiscordApi>(Discord);
            });
        });
        Admin = Client(AdminToken);
    }

    public static async Task<TestServer> StartWithDiscordAsync()
    {
        var s = await StartAsync();
        s.Discord = new FakeDiscord();
        s.DiscordKey = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(new Org.BouncyCastle.Security.SecureRandom());
        await s.RestartAsync();
        return s;
    }

    public T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    /// <summary>Stops this server instance and starts a new one on the same database.</summary>
    public async Task RestartAsync()
    {
        await _factory.DisposeAsync();
        Boot();
    }

    public HttpClient Client(string? bearer = null)
    {
        var c = _factory.CreateClient();
        if (bearer != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    /// <summary>Pairs a new cabinet and returns a client carrying its credential.</summary>
    public async Task<(string Id, HttpClient Client, string Credential)> PairCabinetAsync(string name, params InventoryTable[] tables)
    {
        var code = await (await Admin.PostAsync($"{ApiV1.Prefix}/admin/pairing-codes", null)).Content.ReadFromJsonAsync<CreatePairingCodeResponse>();
        var pr = await Client().PostAsJsonAsync($"{ApiV1.Prefix}/pair/redeem", new PairRedeemRequest(code!.Code, name));
        pr.EnsureSuccessStatusCode();
        var paired = (await pr.Content.ReadFromJsonAsync<PairRedeemResponse>())!;
        var client = Client(paired.Credential);
        if (tables.Length > 0)
            (await client.PutAsJsonAsync($"{ApiV1.Prefix}/agent/inventory", new InventoryRequest(tables, "test"))).EnsureSuccessStatusCode();
        return (paired.CabinetId, client, paired.Credential);
    }

    public static InventoryTable Table(string rom, string? name = null) => new(name ?? rom + ".vpx", rom, true, null);

    public async Task<CompetitionView> StartCompetitionAsync(string game, double hours = 24)
    {
        var r = await Admin.PostAsJsonAsync($"{ApiV1.Prefix}/admin/competitions", new CreateCompetitionRequest(game, hours, null, "tester", null));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<CompetitionView>())!;
    }

    public async Task SetMembersAsync(params (string Id, string Name)[] members) =>
        (await Admin.PutAsJsonAsync($"{ApiV1.Prefix}/admin/members",
            new MembersRequest(members.Select(m => new Member(m.Id, m.Name, false)).ToList()))).EnsureSuccessStatusCode();

    public async Task<List<LeaderboardRow>> LeaderboardAsync(long id) =>
        (await Admin.GetFromJsonAsync<List<LeaderboardRow>>($"{ApiV1.Prefix}/admin/competitions/{id}/leaderboard"))!;

    public static async Task<List<ResultOutcome>> SubmitAsync(HttpClient cabinet, params ResultEvent[] events)
    {
        var r = await cabinet.PostAsJsonAsync($"{ApiV1.Prefix}/agent/results", new ResultsRequest(events));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ResultsResponse>())!.Outcomes.ToList();
    }

    public ResultEvent Event(long competitionId, string initials, long score, string rom = "afm_113b", TimeSpan? exitedAgo = null) =>
        new(Guid.NewGuid(), competitionId, "s-" + Guid.NewGuid().ToString("N")[..8], rom, initials, score,
            Clock.UtcNow - (exitedAgo ?? TimeSpan.FromMinutes(1)), "test");

    public async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await using var conn = new NpgsqlConnection(_adminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static string AdminConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("PINBOT_TEST_PG");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, ".pgdev");
            if (File.Exists(f)) return File.ReadAllText(f).Trim();
        }
        throw new InvalidOperationException("PostgreSQL for tests not configured: set PINBOT_TEST_PG or create .pgdev in the repo root");
    }
}
