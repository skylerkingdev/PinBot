using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace PinBot.Server;

/// <summary>One-time deployment settings (env vars such as PinBot__AdminToken, PinBot__UploadGraceMinutes).</summary>
public sealed record ServerOptions(string? AdminToken, TimeSpan UploadGrace, TimeSpan PairingCodeLifetime, TimeSpan MaxClockSkew)
{
    public static ServerOptions From(IConfiguration c) => new(
        AdminToken: string.IsNullOrWhiteSpace(c["PinBot:AdminToken"]) ? null : c["PinBot:AdminToken"],
        UploadGrace: TimeSpan.FromMinutes(c.GetValue("PinBot:UploadGraceMinutes", 5.0)),
        PairingCodeLifetime: TimeSpan.FromMinutes(c.GetValue("PinBot:PairingCodeMinutes", 15.0)),
        MaxClockSkew: TimeSpan.FromMinutes(c.GetValue("PinBot:MaxClockSkewMinutes", 5.0)));
}

public static class Security
{
    const string CodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789"; // no 0/O, 1/I/L, U

    public static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>10 characters (~49 bits), shown as XXXXX-XXXXX. Single use, expires in minutes, attempts rate-limited.</summary>
    public static string NewPairingCode()
    {
        var chars = Enumerable.Range(0, 10).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray();
        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    public static string NormalizeCode(string code) =>
        new(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>256-bit random cabinet credential; the server stores only its SHA-256.</summary>
    public static string NewCredential() => Base64Url(RandomNumberGenerator.GetBytes(32));

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    static string? Bearer(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..].Trim() : null;
    }

    /// <summary>Endpoint filter: resolves the calling cabinet from its credential, or 401.</summary>
    public static async ValueTask<object?> RequireCabinet(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var credential = Bearer(http);
        if (credential is null) return Results.Unauthorized();
        var db = http.RequestServices.GetRequiredService<Db>();
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        await using var conn = await db.OpenAsync();
        var id = await conn.ExecuteScalarAsync<string?>(
            "UPDATE cabinet SET last_seen_at_utc = @now WHERE credential_hash = @h AND revoked_at_utc IS NULL RETURNING id",
            new { now, h = Sha256(credential) });
        if (id is null) return Results.Unauthorized();
        http.Items["cabinet"] = id;
        return await next(ctx);
    }

    /// <summary>Endpoint filter for the optional admin API: the bearer token must match PinBot:AdminToken.</summary>
    public static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var options = ctx.HttpContext.RequestServices.GetRequiredService<ServerOptions>();
        var token = Bearer(ctx.HttpContext);
        if (options.AdminToken is null || token is null || !FixedTimeEquals(token, options.AdminToken))
            return Results.Unauthorized();
        return await next(ctx);
    }

    public static string CabinetId(this HttpContext ctx) => (string)ctx.Items["cabinet"]!;

    /// <summary>
    /// Who is calling, for rate limits. Public requests all arrive through the Tailscale proxy (one address), which
    /// appends the real client to X-Forwarded-For, so its last entry is the one to trust; anything before it was sent by
    /// the client and may be made up. Without the header (LAN), the connection's own address.
    /// </summary>
    public static string ClientAddress(HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        var last = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
