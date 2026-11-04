using System.Collections.Concurrent;
using System.Text.Json;

namespace PinBot.Agent.Discovery;

/// <summary>
/// What a table's files say: the ROM names its script uses, its NVRAM offset, how the script was read, and (for the
/// community's table-update posts) the table's own name, version and authors, its script hash, and its backglass.
/// </summary>
public sealed record ScriptFacts(List<string> Roms, int NvOffset, string Source, TableMeta? Meta = null, BackglassInfo? Backglass = null);

/// <summary>
/// Remembers each table's <see cref="ScriptFacts"/> by file size and modification time, so PinBot opens a .vpx only
/// when it is new or changed: after the first run, startup reads no table files at all. A damaged cache file is ignored.
/// </summary>
public sealed class TableScriptCache(string path)
{
    public static TableScriptCache Default { get; } = new(Path.Combine(LocalState.Dir, "tables-cache.json"));

    /// <summary>Bumped when <see cref="ScriptFacts"/> gains something read from the files: older entries are read again.</summary>
    internal const int CurrentFormat = 2;

    sealed record Entry(long Length, long Modified, ScriptFacts Facts, int Format = 0);

    ConcurrentDictionary<string, Entry>? _entries;
    bool _dirty;

    ConcurrentDictionary<string, Entry> Entries
    {
        get
        {
            if (_entries != null) return _entries;
            try
            {
                var loaded = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) : null;
                _entries = new(loaded ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _entries = new(StringComparer.OrdinalIgnoreCase); }
            return _entries;
        }
    }

    public ScriptFacts Get(string vpxPath, Func<ScriptFacts> read)
    {
        // a sidecar .vbs (preferred by VPX) and the .directb2s backglass count too: any of them changing means reading again
        var info = new FileInfo(vpxPath);
        var vbs = new FileInfo(Path.ChangeExtension(vpxPath, ".vbs"));
        var b2s = new FileInfo(Backglass.PathFor(vpxPath));
        long length = (info.Exists ? info.Length : -1) + (vbs.Exists ? (vbs.Length + 1) << 40 : 0) + (b2s.Exists ? (b2s.Length + 1) * 31 : 0);
        long modified = Math.Max(Math.Max(info.Exists ? info.LastWriteTimeUtc.Ticks : 0, vbs.Exists ? vbs.LastWriteTimeUtc.Ticks : 0),
            b2s.Exists ? b2s.LastWriteTimeUtc.Ticks : 0);
        if (Entries.TryGetValue(vpxPath, out var e) && e.Length == length && e.Modified == modified && e.Format == CurrentFormat) return e.Facts;
        var facts = read();
        Entries[vpxPath] = new Entry(length, modified, facts, CurrentFormat);
        _dirty = true;
        return facts;
    }

    /// <summary>Writes the cache if anything was added; tables that no longer exist are dropped.</summary>
    public void Save()
    {
        if (!_dirty || _entries is null) return;
        try
        {
            foreach (var gone in _entries.Keys.Where(p => !File.Exists(p)).ToList()) _entries.TryRemove(gone, out _);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, path, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // only a cache: read the tables next time
    }
}
