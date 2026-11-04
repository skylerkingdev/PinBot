using System.Security.Cryptography;
using System.Text;
using System.Xml;
using OpenMcdf;

namespace PinBot.Agent.Discovery;

/// <summary>
/// What a table says about itself (the TableInfo the author filled in, in the VPX editor's Table Info) and a hash of the
/// script VPX runs for it. Used to tell the community when a member installs or updates a table; never for scoring.
/// </summary>
public sealed record TableMeta(string? Name, string? Version, string? Authors, string? ScriptHash);

/// <summary>The header of the .directb2s backglass next to a table: who made it and what kind it is.</summary>
/// <param name="DmdType">B2S DMDType: 1 none, 2 a DMD area on the backglass screen, 3 a separate full DMD</param>
public sealed record BackglassInfo(string? Name, string? Author, int? DmdType, bool Dual);

public static class TableMetadata
{
    const int MaxField = 200;

    /// <summary>
    /// The TableInfo storage of a .vpx holds the author's fields as UTF-16 streams (TableName, AuthorName, TableVersion…).
    /// Any field that is missing or unreadable is null; a table without TableInfo still gets its script hash.
    /// </summary>
    public static TableMeta Read(string vpxPath, string? script)
    {
        string? name = null, version = null, authors = null;
        try
        {
            using var root = RootStorage.OpenRead(vpxPath);
            if (root.TryOpenStorage("TableInfo", out var info))
            {
                name = ReadField(info, "TableName");
                version = ReadField(info, "TableVersion");
                authors = ReadField(info, "AuthorName");
            }
        }
        catch (Exception ex) when (ex is IOException or FileFormatException or UnauthorizedAccessException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
        {
            // only descriptive: an unreadable table is still listed, without its name and version
        }
        return new(name, version, authors, HashScript(script));
    }

    static string? ReadField(Storage info, string name)
    {
        if (!info.TryOpenStream(name, out var stream)) return null;
        using (stream)
        {
            var len = (int)Math.Min(stream.Length, MaxField * 2 * 4); // a sane cap; fields are short
            var buf = new byte[len];
            stream.ReadExactly(buf);
            return Clean(Encoding.Unicode.GetString(buf));
        }
    }

    /// <summary>Trimmed, control characters dropped, at most 200 characters; empty is null.</summary>
    internal static string? Clean(string? s)
    {
        if (s is null) return null;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsControl(c) ? ' ' : c);
        var t = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (t.Length > MaxField) t = t[..MaxField];
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// SHA-256 (hex) of the script text with line endings normalized to \n, so the same script copied between machines
    /// (or saved with other line endings) has the same hash. The script was read as Latin-1, so this hashes its bytes.
    /// </summary>
    public static string? HashScript(string? script) =>
        script is null ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.Latin1.GetBytes(script.Replace("\r\n", "\n").Replace('\r', '\n'))));
}

public static class Backglass
{
    /// <summary>B2S loads the backglass named like the table: "Table.vpx" -> "Table.directb2s".</summary>
    public static string PathFor(string vpxPath) => Path.ChangeExtension(vpxPath, ".directb2s");

    static readonly string[] HeaderElements = ["Name", "Author", "DMDType", "DualBackglass"];

    /// <summary>
    /// Reads only the header of a .directb2s (an XML file of tens of MB, mostly images): it stops at the first element
    /// that is not a simple header value. Null when there is no file or it is not a backglass.
    /// </summary>
    public static BackglassInfo? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true,
                MaxCharactersInDocument = 0,
            });
            if (!reader.ReadToFollowing("DirectB2SData")) return null;
            var values = new Dictionary<string, string?>();
            int depth = reader.Depth;
            while (reader.Read() && reader.Depth > depth)
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                // the header is a run of <X Value="..."/> elements; the first nested one (Animations, Images...) ends it
                if (!reader.IsEmptyElement) break;
                if (HeaderElements.Contains(reader.LocalName)) values[reader.LocalName] = reader.GetAttribute("Value");
                if (values.Count == HeaderElements.Length) break;
            }
            int? dmd = int.TryParse(values.GetValueOrDefault("DMDType"), out var d) ? d : null;
            return new(TableMetadata.Clean(values.GetValueOrDefault("Name")), TableMetadata.Clean(values.GetValueOrDefault("Author")),
                dmd, values.GetValueOrDefault("DualBackglass") == "1");
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
