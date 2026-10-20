using Dapper;
using Npgsql;

namespace PinBot.Server;

/// <summary>PostgreSQL access via Npgsql + Dapper. The schema is applied idempotently at startup.</summary>
public sealed class Db : IAsyncDisposable
{
    readonly NpgsqlDataSource _source;

    static Db() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public Db(string connectionString) => _source = NpgsqlDataSource.Create(connectionString);

    public ValueTask<NpgsqlConnection> OpenAsync() => _source.OpenConnectionAsync();

    public async Task MigrateAsync()
    {
        using var stream = typeof(Db).Assembly.GetManifestResourceStream("PinBot.Server.Schema.sql")
                           ?? throw new InvalidOperationException("Schema.sql not embedded");
        var sql = await new StreamReader(stream).ReadToEndAsync();
        await using var conn = await OpenAsync();
        // serialize concurrent starts of several server instances
        await conn.ExecuteAsync("SELECT pg_advisory_lock(7268843)");
        try { await conn.ExecuteAsync(sql); }
        finally { await conn.ExecuteAsync("SELECT pg_advisory_unlock(7268843)"); }
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
