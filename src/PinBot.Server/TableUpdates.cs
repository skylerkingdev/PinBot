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
        var changes = new List<Change>();
        foreach (var (f, old) in Pair(previous, now))
        {
            if (old is null) changes.Add(new(f, "added", null));
            else if (NewVersion(old.Version, f.Version)) changes.Add(new(f, "updated", old.Version));
        }
        return changes;
    }

    /// <summary>Each file now, with the file it was before: the same file name, else a file of the same game that is gone (renamed).</summary>
    static List<(TableFile Now, TableFile? Before)> Pair(IReadOnlyCollection<TableFile> previous, IReadOnlyCollection<TableFile> now)
    {
        var before = previous.ToDictionary(f => f.Table, StringComparer.OrdinalIgnoreCase);
        var nowNames = now.Select(f => f.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = previous.Where(f => !nowNames.Contains(f.Table)).ToList();
        var pairs = new List<(TableFile, TableFile?)>();
        foreach (var f in now)
        {
            if (before.TryGetValue(f.Table, out var old)) { pairs.Add((f, old)); continue; }
            // the same table name first, then the same ROM: several tables of one game can share a ROM
            var renamedFrom = gone.FirstOrDefault(g => g.Name is not null && string.Equals(g.Name, f.Name, StringComparison.OrdinalIgnoreCase) && SameGame(g, f))
                              ?? gone.FirstOrDefault(g => SameGame(g, f));
            if (renamedFrom is not null) gone.Remove(renamedFrom);
            pairs.Add((f, renamedFrom));
        }
        return pairs;
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
        var renamed = baseline ? [] : Pair(previous, files)
            .Where(p => p.Before is { } b && !string.Equals(b.Table, p.Now.Table, StringComparison.OrdinalIgnoreCase)).ToList();

        await conn.ExecuteAsync("DELETE FROM table_file WHERE cabinet_id = @cabinet", new { cabinet }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO table_file (cabinet_id, table_name, rom_id, name, version, authors, script_hash,
                                    backglass_name, backglass_author, backglass_dmd, alt_color, alt_sound)
            VALUES (@cabinet, @Table, @RomId, @Name, @Version, @Authors, @ScriptHash,
                    @BackglassName, @BackglassAuthor, @BackglassDmdType, @AltColor, @AltSound)
            """, files.Select(f => Params(cabinet, f)), tx);
        await conn.ExecuteAsync("UPDATE cabinet SET files_reported_at_utc = @now WHERE id = @cabinet", new { cabinet, now }, tx);

        // a renamed file keeps its open update (it follows the new name)
        foreach (var (f, b) in renamed)
            await conn.ExecuteAsync("""
                UPDATE table_update SET table_name = @to
                WHERE cabinet_id = @cabinet AND table_name = @from AND state IN ('pending', 'asked', 'approved', 'undeliverable')
                """, new { cabinet, from = b!.Table, to = f.Table }, tx);

        // a table taken off the cabinet before its member answered: nothing to share any more
        var open = await conn.QueryAsync<(long Id, string TableName)>("""
            SELECT id, table_name FROM table_update
            WHERE cabinet_id = @cabinet AND state IN ('pending', 'asked', 'undeliverable')
            """, new { cabinet }, tx);
        var gone = open.Where(o => !files.Any(f => string.Equals(f.Table, o.TableName, StringComparison.OrdinalIgnoreCase)))
            .Select(o => o.Id).ToArray();
        if (gone.Length > 0)
            await conn.ExecuteAsync("UPDATE table_update SET state = 'removed', decided_at_utc = @now WHERE id = ANY(@gone)", new { now, gone }, tx);

        foreach (var change in changes)
        {
            // the open update it replaces (a table read halfway through copying, or changed twice before the member
            // answered): the change is still relative to what the member last had, so "new" stays new and an update
            // keeps its first old version
            var c = change;
            var replaced = await conn.QueryFirstOrDefaultAsync<(string Kind, string? OldVersion)?>("""
                SELECT kind, old_version FROM table_update
                WHERE cabinet_id = @cabinet AND state IN ('pending', 'asked') AND table_name = @table
                ORDER BY id LIMIT 1
                """, new { cabinet, table = c.File.Table }, tx);
            if (replaced is { } r && (r.Kind == "added" || !string.Equals(r.OldVersion?.Trim(), c.File.Version?.Trim(), StringComparison.OrdinalIgnoreCase)))
                c = c with { Kind = r.Kind, OldVersion = r.Kind == "added" ? null : r.OldVersion };
            await conn.ExecuteAsync("""
                UPDATE table_update SET state = 'superseded', decided_at_utc = @now
                WHERE cabinet_id = @cabinet AND state IN ('pending', 'asked') AND table_name = @table
                """, new { cabinet, table = c.File.Table, now }, tx);
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
        // an update not posted yet describes the file as it is now: a backglass, colorization or alt sound copied in after
        // the table (installers copy the .vpx first) is part of the same setup
        await conn.ExecuteAsync("""
            UPDATE table_update u SET rom_id = f.rom_id, name = f.name, authors = f.authors, script_hash = f.script_hash,
                backglass_name = f.backglass_name, backglass_author = f.backglass_author, backglass_dmd = f.backglass_dmd,
                alt_color = f.alt_color, alt_sound = f.alt_sound
            FROM table_file f
            WHERE u.cabinet_id = @cabinet AND u.state IN ('pending', 'asked', 'approved')
              AND f.cabinet_id = u.cabinet_id AND f.table_name = u.table_name AND COALESCE(f.version, '') = COALESCE(u.version, '')
            """, new { cabinet }, tx);
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
