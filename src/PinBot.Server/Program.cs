using System.Threading.RateLimiting;
using PinBot.Server;
using PinBot.Server.Discord;

// Container health probe (the runtime image has no curl): `dotnet PinBot.Server.dll --healthcheck`
if (args.Contains("--healthcheck"))
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var port = Environment.GetEnvironmentVariable("PINBOT_HEALTH_PORT") ?? "8080";
        return (await http.GetAsync($"http://localhost:{port}/healthz")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);

// Configuration (env vars): ConnectionStrings__PinBot, PinBot__UploadGraceMinutes, optional PinBot__AdminToken
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => ServerOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => new Db(sp.GetRequiredService<IConfiguration>().GetConnectionString("PinBot")
    ?? throw new InvalidOperationException("ConnectionStrings:PinBot is not configured")));
// Discord: env vars PinBot__Discord__ApplicationId / PublicKey / BotToken / GuildId / ChannelId, PinBot__PublicUrl,
// Without them the server runs without Discord (agent API only). The weekly schedule and channels are set from Discord (/pinbot-admin).
builder.Services.AddSingleton(sp => DiscordOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IDiscordApi>(sp => new DiscordRestApi(sp.GetRequiredService<DiscordOptions>()));
builder.Services.AddSingleton<LeaderboardPublisher>();
builder.Services.AddSingleton<InfoPublisher>();
builder.Services.AddSingleton<CompetitionChanges>();
builder.Services.AddSingleton<MemberSync>();
builder.Services.AddSingleton<WeeklyScheduler>();
builder.Services.AddHostedService<DiscordBackground>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("pairing", ctx => RateLimitPartition.GetFixedWindowLimiter(
        Security.ClientAddress(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
await app.Services.GetRequiredService<Db>().MigrateAsync();
await using (var conn = await app.Services.GetRequiredService<Db>().OpenAsync())
{
    await RomVariants.ApplyAsync(conn, app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime); // coin/free play builds are one game
    await Settings.ApplyChannelsAsync(conn, app.Services.GetRequiredService<PinBot.Server.Discord.DiscordOptions>()); // set from Discord
}

app.UseRateLimiter();
// the release lets the updater confirm a new server really runs (null: a development build)
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", release = AgentDownload.LatestRelease() }));
// the cabinet agent, built into the image by publish.ps1, so /pinbot pair can link to it
app.MapGet(AgentDownload.Route, () => File.Exists(AgentDownload.Path)
    ? Results.File(AgentDownload.Path, "application/vnd.microsoft.portable-executable", "pinbot.exe")
    : Results.NotFound());
app.MapGet(AgentDownload.ManifestRoute, () => File.Exists(AgentDownload.ManifestPath)
    ? Results.File(AgentDownload.ManifestPath, "application/json")
    : Results.NotFound());
app.MapAgentApi();
if (app.Services.GetRequiredService<ServerOptions>().AdminToken is not null) app.MapAdminApi(); // off unless a token is set
app.MapDiscordInteractions();
app.Run();
return 0;

public partial class Program;
