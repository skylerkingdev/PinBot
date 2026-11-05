using Dapper;
using Npgsql;
using PinBot.Contracts;

namespace PinBot.Server;

/// <summary>
/// Table updates (docs/TABLE-UPDATES.md): which reported table files are new or carry a new version. Only real changes
/// count: a re-save, a settings or script tweak with the same version, a removed table, or a cabinet's first report
/// are silent.
/// </summary>
public static class TableUpdates
{
    public const int MaxField = 200;

    public sealed record Change(TableFile File, string Kind, string? OldVersion)
    {
        public override string ToString() =>
            $"{File.Name ?? File.Table} {(Kind == "added" ? $"added ({File.Version ?? "no version"})" : $"{OldVersion} -> {File.Version}")}";
    }

    /// <summary>
    /// What one report contained: how many files, whether it was the cabinet's first (silent), what changed, and how many
    /// open questions were withdrawn because their table is no longer on the cabinet.
    /// </summary>
    public sealed record Recorded(int Files, bool Baseline, IReadOnlyList<Change> Changes, int Withdrawn = 0);

    /// <summary>
    /// Compares a cabinet's new list of table files with its previous one. A file that is gone and another that
    /// appeared for the same game (same ROM, or same table name when there is no ROM) is the same table renamed, as
    /// happens when file names carry the version: an update if its version changed, nothing if not.
    /// </summary>
    public static List<Change> Detect(IReadOnlyCollection<TableFile> previous, IReadOnlyCollection<TableFile> now, bool baseline)
    {
        if (baseline) return [];
        var before = previous.ToDictionary(f => f.Table, StringComparer.OrdinalIgnoreCase);
        var nowNames = now.Select(f => f.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = previous.Where(f => !nowNames.Contains(f.Table)).ToList();
        var changes = new List<Change>();
        foreach (var f in now)
        {
            if (before.TryGetValue(f.Table, out var old))
            {
                if (NewVersion(old.Version, f.Version)) changes.Add(new(f, "updated", old.Version));
                continue;
            }
            var renamedFrom = gone.FirstOrDefault(g => SameGame(g, f));
            if (renamedFrom is null) { changes.Add(new(f, "added", null)); continue; }
            gone.Remove(renamedFrom);
            if (NewVersion(renamedFrom.Version, f.Version)) changes.Add(new(f, "updated", renamedFrom.Version));
        }
        return changes;
    }

    /// <summary>A version is new only when the table states one and it differs (a table without a version can't say).</summary>
    static bool NewVersion(string? old, string? now) =>
        now is not null && !string.Equals(old?.Trim(), now.Trim(), StringComparison.OrdinalIgnoreCase);

    static bool SameGame(TableFile a, TableFile b) =>
        a.RomId is not null && b.RomId is not null
            ? string.Equals(a.RomId, b.RomId, StringComparison.OrdinalIgnoreCase)
            : a.Name is not null && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Text from a cabinet, made safe to store: trimmed, capped, empty is null.</summary>
    static string? F(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > MaxField ? s[..MaxField] : s;
    }

    public static TableFile Sanitize(TableFile f) => f with
    {
        Table = F(f.Table) ?? "",
        RomId = F(f.RomId) is { } rom ? Competitions.NormalizeRom(rom) : null,
        Name = F(f.Name), Version = F(f.Version), Authors = F(f.Authors),
        ScriptHash = F(f.ScriptHash) is { Length: 64 } h && h.All(char.IsAsciiHexDigit) ? h.ToLowerInvariant() : null,
        BackglassName = F(f.BackglassName), BackglassAuthor = F(f.BackglassAuthor),
        BackglassDmdType = f.BackglassDmdType is >= 0 and <= 9 ? f.BackglassDmdType : null,
    };

    /// <summary>
    /// Stores the cabinet's reported files and records what changed, inside the inventory's transaction. A newer update
    /// of a table replaces one still waiting for its member's answer.
    /// </summary>
    public static async Task<Recorded> RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string cabinet, IEnumerable<TableFile> reported, DateTime now)
    {
        var files = reported.Select(Sanitize).Where(f => f.Table.Length > 0)
            .GroupBy(f => f.Table, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var baseline = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT files_reported_at_utc FROM cabinet WHERE id = @cabinet", new { cabinet }, tx) is null;
        var previous = (await conn.QueryAsync<FileRow>("SELECT * FROM table_file WHERE cabinet_id = @cabinet", new { cabinet }, tx))
            .Select(r => r.ToFile()).ToList();
        var changes = Detect(previous, files, baseline);

        await conn.ExecuteAsync("DELETE FROM table_file WHERE cabinet_id = @cabinet", new { cabinet }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO table_file (cabinet_id, table_name, rom_id, name, version, authors, script_hash,
                                    backglass_name, backglass_author, backglass_dmd, alt_color, alt_sound)
            VALUES (@cabinet, @Table, @RomId, @Name, @Version, @Authors, @ScriptHash,
                    @BackglassName, @BackglassAuthor, @BackglassDmdType, @AltColor, @AltSound)
            """, files.Select(f => Params(cabinet, f)), tx);
        await conn.ExecuteAsync("UPDATE cabinet SET files_reported_at_utc = @now WHERE id = @cabinet", new { cabinet, now }, tx);

        // a table taken off the cabinet before its member answered: nothing to share any more (a file renamed for the
        // same game keeps its question)
        var open = await conn.QueryAsync<(long Id, string TableName, string? RomId, string? Name)>("""
            SELECT id, table_name, rom_id, name FROM table_update
            WHERE cabinet_id = @cabinet AND state IN ('pending', 'asked', 'undeliverable')
            """, new { cabinet }, tx);
        var gone = open.Where(o => !files.Any(f => string.Equals(f.Table, o.TableName, StringComparison.OrdinalIgnoreCase)
                                                   || SameGame(new TableFile(o.TableName, o.RomId, o.Name, null, null, null), f)))
            .Select(o => o.Id).ToArray();
        if (gone.Length > 0)
            await conn.ExecuteAsync("UPDATE table_update SET state = 'removed', decided_at_utc = @now WHERE id = ANY(@gone)", new { now, gone }, tx);

        foreach (var c in changes)
        {
            await conn.ExecuteAsync("""
                UPDATE table_update SET state = 'superseded', decided_at_utc = @now
                WHERE cabinet_id = @cabinet AND state IN ('pending', 'asked') AND (table_name = @table OR (rom_id IS NOT NULL AND rom_id = @rom))
                """, new { cabinet, table = c.File.Table, rom = c.File.RomId, now }, tx);
            var p = Params(cabinet, c.File);
            p.Add("kind", c.Kind);
            p.Add("old", c.OldVersion);
            p.Add("now", now);
            await conn.ExecuteAsync("""
                INSERT INTO table_update (cabinet_id, table_name, kind, old_version, rom_id, name, version, authors, script_hash,
                                          backglass_name, backglass_author, backglass_dmd, alt_color, alt_sound, detected_at_utc, owner_user_id)
                SELECT @cabinet, @Table, @kind, @old, @RomId, @Name, @Version, @Authors, @ScriptHash,
                       @BackglassName, @BackglassAuthor, @BackglassDmdType, @AltColor, @AltSound, @now, paired_by
                FROM cabinet WHERE id = @cabinet
                """, p, tx);
        }
        return new(files.Count, baseline, changes, gone.Length);
    }

    static DynamicParameters Params(string cabinet, TableFile f)
    {
        var p = new DynamicParameters(f);
        p.Add("cabinet", cabinet);
        return p;
    }

    sealed record FileRow(string CabinetId, string TableName, string? RomId, string? Name, string? Version, string? Authors, string? ScriptHash,
        string? BackglassName, string? BackglassAuthor, int? BackglassDmd, bool AltColor, bool AltSound)
    {
        public TableFile ToFile() => new(TableName, RomId, Name, Version, Authors, ScriptHash, BackglassName, BackglassAuthor, BackglassDmd, AltColor, AltSound);
    }
}
