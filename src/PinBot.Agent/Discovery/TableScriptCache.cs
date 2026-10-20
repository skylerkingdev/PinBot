using System.Collections.Concurrent;
using System.Text.Json;

namespace PinBot.Agent.Discovery;

/// <summary>What a table's script says: the ROM names it uses, its NVRAM offset, and how the script was read.</summary>
public sealed record ScriptFacts(List<string> Roms, int NvOffset, string Source);

/// <summary>
/// Remembers each table's <see cref="ScriptFacts"/> by file size and modification time, so PinBot opens a .vpx only
/// when it is new or changed: after the first run, startup reads no table files at all. A damaged cache file is ignored.
/// </summary>
public sealed class TableScriptCache(string path)
{
    public static TableScriptCache Default { get; } = new(Path.Combine(LocalState.Dir, "tables-cache.json"));

    sealed record Entry(long Length, long Modified, ScriptFacts Facts);

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
        // a sidecar .vbs (preferred by VPX) counts too: either file changing means the script must be read again
        var info = new FileInfo(vpxPath);
        var vbs = new FileInfo(Path.ChangeExtension(vpxPath, ".vbs"));
        long length = (info.Exists ? info.Length : -1) + (vbs.Exists ? (vbs.Length + 1) << 40 : 0);
        long modified = Math.Max(info.Exists ? info.LastWriteTimeUtc.Ticks : 0, vbs.Exists ? vbs.LastWriteTimeUtc.Ticks : 0);
        if (Entries.TryGetValue(vpxPath, out var e) && e.Length == length && e.Modified == modified) return e.Facts;
        var facts = read();
        Entries[vpxPath] = new Entry(length, modified, facts);
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
