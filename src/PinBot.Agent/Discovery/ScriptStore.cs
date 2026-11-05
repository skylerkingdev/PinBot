using System.IO.Compression;
using System.Text;

namespace PinBot.Agent.Discovery;

/// <summary>
/// The scripts of this VPin's tables, by hash, compressed in PinBot's own folder: when a table's script changes, the
/// old one is here to compare with, so only the changed lines are ever shared (docs/TABLE-UPDATES.md, script fixes).
/// Never sent anywhere whole. Losing it only means one change can't be shared.
/// </summary>
public static class ScriptStore
{
    static string Dir => Path.Combine(LocalState.Dir, "scripts");

    static string PathOf(string hash) => Path.Combine(Dir, hash + ".vbs.gz");

    public static void Save(string? hash, string script)
    {
        if (hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) return;
        var path = PathOf(hash);
        try
        {
            if (File.Exists(path)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return; }
            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            using (var gz = new GZipStream(File.Create(tmp), CompressionLevel.SmallestSize))
                gz.Write(Encoding.Latin1.GetBytes(script));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string? Load(string? hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) return null;
        try
        {
            using var gz = new GZipStream(File.OpenRead(PathOf(hash)), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            return Encoding.Latin1.GetString(ms.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>Drops scripts no table uses now (<paramref name="current"/>) once they're older than <paramref name="unused"/>.</summary>
    public static void Prune(IReadOnlySet<string> current, TimeSpan unused)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir, "*.vbs.gz"))
                if (!current.Contains(Path.GetFileName(f)[..^".vbs.gz".Length]) && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > unused)
                    File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
