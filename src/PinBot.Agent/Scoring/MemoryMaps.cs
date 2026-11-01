using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PinBot.Agent.Scoring;

/// <summary>
/// Read-only decoder for PinMAME .nv files using Pinball Memory Maps
/// (https://github.com/tomlogic/pinball-memory-maps, ODbL 1.0 / DbCL 1.0), _fileformat 0.7–0.8.
/// Only the high_scores and mode_champions sections are decoded. Semantics follow the map README
/// and the reference implementation francisdb/pinmame-nvram (MIT), and are verified against its test data.
/// </summary>
public sealed class MemoryMaps
{
    public const string ParserVersion = "pbmm-0.9/pinbot-0.0.2";

    /// <summary>Reads a file of the maps repository by its '/'-separated relative path; null if it does not exist.</summary>
    readonly Func<string, byte[]?> _read;
    readonly Dictionary<string, string> _index;
    readonly Dictionary<string, RomMap> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps from a folder (a checkout of the repository, or --maps).</summary>
    public MemoryMaps(string root) : this(rel =>
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }, Path.Combine(root, "index.json")) { }

    MemoryMaps(Func<string, byte[]?> read, string indexLocation)
    {
        _read = read;
        var index = read("index.json") ?? throw new FileNotFoundException($"Memory map index not found at {indexLocation}");
        using var doc = JsonDocument.Parse(index);
        _index = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.EnumerateObject())
            if (!p.Name.StartsWith('_') && p.Value.ValueKind == JsonValueKind.String)
                _index[p.Name] = p.Value.GetString()!;
    }

    /// <summary>Maps folder next to the executable (populated from the pinned submodule at build time).</summary>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "maps");

    const string ResourcePrefix = "pbmm/";

    /// <summary>The maps built into pinbot.exe (embedded resources "pbmm/&lt;relative path&gt;"), so it is a single file.</summary>
    public static MemoryMaps Embedded()
    {
        var asm = typeof(MemoryMaps).Assembly;
        // MSBuild may write the folder part with backslashes: index by the normalized name
        var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(n => n[ResourcePrefix.Length..].Replace('\\', '/'), StringComparer.Ordinal);
        return new(rel =>
        {
            if (!names.TryGetValue(rel, out var name)) return null;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }, "pinbot.exe (embedded)");
    }

    /// <summary>A maps folder next to the executable if there is one (development), otherwise the embedded maps.</summary>
    public static MemoryMaps Default() => File.Exists(Path.Combine(DefaultRoot, "index.json")) ? new(DefaultRoot) : Embedded();

    public int RomCount => _index.Count;

    public bool HasMap(string rom) => _index.ContainsKey(rom);

    /// <summary>Loads and validates the map for an exact ROM name. Returns null with a reason if unsupported.</summary>
    public RomMap? TryLoad(string rom, out string? reason)
    {
        lock (_cache) return TryLoadLocked(rom, out reason);
    }

    RomMap? TryLoadLocked(string rom, out string? reason)
    {
        reason = null;
        if (_cache.TryGetValue(rom, out var cached)) return cached;
        if (!_index.TryGetValue(rom, out var rel))
        {
            reason = "no memory map for this ROM revision";
            return null;
        }
        try
        {
            var map = RomMap.Load(rom, _read, rel);
            if (!map.HighScores.Any(e => e.Section == "high_scores"))
            {
                reason = "memory map has no main high-score table";
                return null;
            }
            _cache[rom] = map;
            return map;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            reason = $"memory map unusable: {ex.Message}";
            return null;
        }
    }
}

public enum Nibble { Both, Low, High }

/// <summary>One value descriptor (start/length/offsets/encoding...) already translated to .nv file offsets.</summary>
public sealed class Field
{
    public required string Encoding { get; init; }
    public required int[] FileOffsets { get; init; }  // one entry per stored byte (or nibble cell)
    public Nibble Nibble { get; init; }
    public bool LittleEndian { get; init; }
    public decimal Scale { get; init; } = 1;
    public long Offset { get; init; }
    public byte? Mask { get; init; }
    public bool NullTerminated { get; init; }
    public string? Default { get; init; }
    public Dictionary<string, JsonElement>? SpecialValues { get; init; }
}

public sealed record EntryDef(string Section, string Label, string? ShortLabel, Field? Initials, Field? Score);

/// <summary>
/// A checksum over the file bytes Start..End (inclusive), stored at StoredAt: one byte (Bits 8), two bytes in the
/// platform's byte order (16), or the low nibble of one cell (4, on 4-bit NVRAM, summing the cells' low nibbles).
/// Complement: the stored value is the sum's bitwise complement (0xFF - sum), not the sum itself.
/// </summary>
public sealed record ChecksumRegion(string Label, int Start, int End, int StoredAt, int Bits, bool Complement)
{
    public IEnumerable<int> StoredBytes => Bits == 16 ? [StoredAt, StoredAt + 1] : [StoredAt];

    int Compute(byte[] nv)
    {
        int sum = 0;
        for (int b = Start; b <= End; b++) sum += Bits == 4 ? nv[b] & 0xF : nv[b];
        return (Complement ? ~sum : sum) & (Bits switch { 16 => 0xFFFF, 8 => 0xFF, _ => 0xF });
    }

    int Stored(byte[] nv, bool littleEndian) => Bits switch
    {
        16 => littleEndian ? nv[StoredAt] | nv[StoredAt + 1] << 8 : nv[StoredAt] << 8 | nv[StoredAt + 1],
        8 => nv[StoredAt],
        _ => nv[StoredAt] & 0xF,
    };

    public bool Ok(byte[] nv, bool littleEndian) => Compute(nv) == Stored(nv, littleEndian);

    public void Write(byte[] nv, bool littleEndian)
    {
        int v = Compute(nv);
        if (Bits == 16) (nv[StoredAt], nv[StoredAt + 1]) = littleEndian ? ((byte)v, (byte)(v >> 8)) : ((byte)(v >> 8), (byte)v);
        else if (Bits == 8) nv[StoredAt] = (byte)v;
        else nv[StoredAt] = (byte)((nv[StoredAt] & 0xF0) | v);
    }
}

/// <summary>A region the ROM keeps more than one copy of (file offset of each copy, same length): writes go to all.</summary>
public sealed record MirrorRegion(string Label, int[] Starts, int Length);

public sealed partial class RomMap
{
    public required string Rom { get; init; }
    public required string MapFile { get; init; }
    public required string Platform { get; init; }

    /// <summary>True when the main high-score table records initials (needed to attribute a score to a player).</summary>
    public bool HasInitials => HighScores.Any(e => e.Section == "high_scores" && e.Initials != null);
    public string? CharMap { get; init; }
    public required int NvramSize { get; init; }
    public required List<EntryDef> HighScores { get; init; }
    public required List<EntryDef> ModeChampions { get; init; }
    public required List<ChecksumRegion> Checksums { get; init; }
    /// <summary>Copies the map declares (validation.mirror, _fileformat 0.9); empty for older maps.</summary>
    public List<MirrorRegion> Mirrors { get; init; } = [];
    public bool LittleEndian { get; init; }

    internal static RomMap Load(string rom, Func<string, byte[]?> read, string relativeMapPath)
    {
        byte[] Read(string rel) => read(rel) ?? throw new FileNotFoundException($"memory map file missing: {rel}");
        using var mapDoc = JsonDocument.Parse(Read(relativeMapPath.Replace('\\', '/')));
        var m = mapDoc.RootElement;

        var fileFormat = m.GetProperty("_fileformat").GetDouble();
        if (fileFormat < 0.7 || fileFormat >= 1.0)
            throw new InvalidDataException($"unsupported _fileformat {fileFormat.ToString(CultureInfo.InvariantCulture)}");

        var meta = m.GetProperty("_metadata");
        var platformName = meta.GetProperty("platform").GetString()!;
        string? charMap = meta.TryGetProperty("char_map", out var cm) ? cm.GetString() : null;

        using var platDoc = JsonDocument.Parse(Read($"platforms/{platformName}.json"));
        var plat = platDoc.RootElement;
        bool little = plat.TryGetProperty("endian", out var pe) && pe.GetString() == "little";
        JsonElement? nvram = null;
        foreach (var region in plat.GetProperty("memory_layout").EnumerateArray())
            if (region.GetProperty("type").GetString() == "nvram")
            {
                if (nvram != null) throw new InvalidDataException("platform has more than one nvram region");
                nvram = region;
            }
        if (nvram is not { } nv) throw new InvalidDataException("platform has no nvram region");
        long baseAddr = Num(nv.GetProperty("address"));
        int size = (int)Num(nv.GetProperty("size"));
        var platNibble = nv.TryGetProperty("nibble", out var pn) ? ParseNibble(pn.GetString()) : Nibble.Both;

        Field? ParseField(JsonElement d, long shift = 0)
        {
            var enc = d.GetProperty("encoding").GetString()!;
            int[] offsets;
            if (d.TryGetProperty("offsets", out var offs))
            {
                // a start next to offsets is their base (_fileformat 0.9); a repeated entry shifts that base
                long origin = d.TryGetProperty("start", out var st) ? Num(st) + shift
                    : shift == 0 ? 0 : throw new InvalidDataException("a repeated entry's offsets need a start to shift");
                offsets = offs.EnumerateArray().Select(o => ToFileOffset(origin + Num(o), baseAddr, size)).ToArray();
            }
            else
            {
                long start = Num(d.GetProperty("start")) + shift;
                long len = d.TryGetProperty("length", out var l) ? Num(l)
                         : d.TryGetProperty("end", out var e) ? Num(e) - start + 1 : 1;
                int first = ToFileOffset(start, baseAddr, size);
                ToFileOffset(start + len - 1, baseAddr, size);
                offsets = Enumerable.Range(first, (int)len).ToArray();
            }
            return new Field
            {
                Encoding = enc,
                FileOffsets = offsets,
                Nibble = d.TryGetProperty("nibble", out var n) ? ParseNibble(n.GetString()) : platNibble,
                LittleEndian = little,
                Scale = d.TryGetProperty("scale", out var s) ? s.GetDecimal() : 1m,
                Offset = d.TryGetProperty("offset", out var o2) ? (long)Num(o2) : 0,
                Mask = d.TryGetProperty("mask", out var mk) ? (byte)Num(mk) : null,
                NullTerminated = d.TryGetProperty("null", out var nu) && nu.GetString() is "truncate" or "terminate",
                Default = d.TryGetProperty("default", out var df) && df.ValueKind == JsonValueKind.String ? df.GetString() : null,
                SpecialValues = d.TryGetProperty("special_values", out var sv)
                    ? sv.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : null,
            };
        }

        List<EntryDef> Entries(string section)
        {
            var list = new List<EntryDef>();
            if (!m.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
                foreach (var (n, shift) in Repeats(e))
                {
                    var label = Numbered(e.TryGetProperty("label", out var lb) ? lb.GetString()! : "?", n)!;
                    list.Add(new EntryDef(
                        section == "high_scores" && IsSecondaryLeaderboard(label) ? SecondarySection : section,
                        label,
                        e.TryGetProperty("short_label", out var sl) ? Numbered(sl.GetString(), n) : null,
                        e.TryGetProperty("initials", out var i) ? ParseField(i, shift) : null,
                        e.TryGetProperty("score", out var sc) ? ParseField(sc, shift) : null));
                }
            return list;
        }

        // _fileformat 0.9 describes checksums and copies in _metadata.validation; its maps keep the older checksum8 and
        // checksum16 lists for a while, which are to be ignored there
        var checksums = new List<ChecksumRegion>();
        var mirrors = new List<MirrorRegion>();
        var validation = fileFormat >= 0.9 && meta.TryGetProperty("validation", out var v) ? v : default;
        if (fileFormat >= 0.9)
        {
            if (validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("checksum", out var arr))
                foreach (var c in arr.EnumerateArray())
                    foreach (var (n, shift) in Repeats(c))
                    {
                        string label = Numbered(c.TryGetProperty("label", out var lb) ? lb.GetString() : null, n) ?? "checksum";
                        int bits = c.TryGetProperty("bits", out var bt) ? (int)Num(bt) : 8;
                        if (bits is not (4 or 8 or 16)) throw new InvalidDataException($"checksum '{label}' has {bits} bits");
                        long start = Num(c.GetProperty("start")) + shift;
                        long end = c.TryGetProperty("end", out var e) ? Num(e) + shift
                                 : start + (c.TryGetProperty("length", out var l) ? Num(l) : 1) - 1;
                        // the checksum ends the range unless "checksum" says where it is
                        int width = bits == 16 ? 2 : 1;
                        bool apart = c.TryGetProperty("checksum", out var ca);
                        long stored = apart ? Num(ca) + shift : end - width + 1;
                        if (apart) ToFileOffset(stored + width - 1, baseAddr, size);
                        checksums.Add(new ChecksumRegion(label, ToFileOffset(start, baseAddr, size),
                            ToFileOffset(apart ? end : end - width, baseAddr, size), ToFileOffset(stored, baseAddr, size),
                            bits, c.GetProperty("complement").GetBoolean()));
                    }
            if (validation.ValueKind == JsonValueKind.Object && validation.TryGetProperty("mirror", out var mir))
                foreach (var d in mir.EnumerateArray())
                    foreach (var (n, shift) in Repeats(d))
                    {
                        long origin = (d.TryGetProperty("start", out var st) ? Num(st) : 0) + shift;
                        int length = (int)Num(d.GetProperty("length"));
                        var starts = d.GetProperty("offsets").EnumerateArray().Select(o => origin + Num(o)).ToList();
                        foreach (var s0 in starts) ToFileOffset(s0 + length - 1, baseAddr, size);
                        mirrors.Add(new MirrorRegion(Numbered(d.TryGetProperty("label", out var lb) ? lb.GetString() : null, n) ?? "mirror",
                            starts.Select(s0 => ToFileOffset(s0, baseAddr, size)).ToArray(), length));
                    }
        }
        else
            foreach (var (key, is16) in new[] { ("checksum8", false), ("checksum16", true) })
            {
                if (!m.TryGetProperty(key, out var arr)) continue;
                foreach (var c in arr.EnumerateArray())
                {
                    string label = c.TryGetProperty("label", out var lb) ? lb.GetString() ?? key : key;
                    int start = ToFileOffset(Num(c.GetProperty("start")), baseAddr, size);
                    int end = c.TryGetProperty("end", out var e) ? ToFileOffset(Num(e), baseAddr, size)
                            : start + (int)(c.TryGetProperty("length", out var l) ? Num(l) : 1) - 1;
                    int? storedAt = c.TryGetProperty("checksum", out var ca) ? ToFileOffset(Num(ca), baseAddr, size) : null;
                    int group = c.TryGetProperty("groupings", out var g) ? (int)Num(g) : end - start + 1;
                    if (group <= 0 || (end - start + 1) % group != 0)
                        throw new InvalidDataException($"{key} '{label}' groupings do not divide its range");
                    // checksum16: the range's last two bytes hold 0xFFFF minus the sum of the rest; checksum8: 0xFF minus
                    // the low byte of the sum, in the range's last byte or at "checksum"
                    for (int gs = start; gs <= end; gs += group)
                    {
                        int ge = gs + group - 1;
                        checksums.Add(is16 ? new ChecksumRegion(label, gs, ge - 2, ge - 1, 16, true)
                            : storedAt is { } at ? new ChecksumRegion(label, gs, ge, at, 8, true)
                            : new ChecksumRegion(label, gs, ge - 1, ge, 8, true));
                    }
                }
            }

        return new RomMap
        {
            Rom = rom,
            MapFile = relativeMapPath,
            Platform = platformName,
            CharMap = charMap,
            NvramSize = size,
            LittleEndian = little,
            HighScores = Entries("high_scores"),
            ModeChampions = Entries("mode_champions"),
            Checksums = platformName != "stern-sam" ? checksums : WithSamEntryChecksums(checksums, Entries("high_scores")),
            Mirrors = mirrors,
        };
    }

    /// <summary>Each copy of a descriptor with a "repeat" object (_fileformat 0.9): its 1-based number and address shift.</summary>
    static IEnumerable<(int N, long Shift)> Repeats(JsonElement d)
    {
        if (!d.TryGetProperty("repeat", out var r)) return [(1, 0)];
        long count = Num(r.GetProperty("count")), step = Num(r.GetProperty("step"));
        if (count is < 1 or > 1000) throw new InvalidDataException($"repeat count {count}");
        return Enumerable.Range(1, (int)count).Select(n => (n, (n - 1) * step));
    }

    static string? Numbered(string? label, int n) => label?.Replace("{#}", n.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Stern SAM protects every high-score entry with its own checksum16: the entry's bytes from the initials up to
    /// the next entry, less 2 bytes, with the checksum in the last two (0xFFFF minus the byte sum, little-endian). Half
    /// the SAM maps leave this out; without it a competition image would be rejected by the ROM, which then resets the
    /// table to factory defaults (seen on AC/DC). Verified on every SAM map against 120 real NVRAM files (545 entries,
    /// no exception), including the maps that do list their checksums. Entries a map already covers keep its checksum.
    /// </summary>
    static List<ChecksumRegion> WithSamEntryChecksums(List<ChecksumRegion> declared, List<EntryDef> highScores)
    {
        var starts = highScores.Where(e => e.Initials != null).Select(e => e.Initials!.FileOffsets.Min()).Order().ToList();
        if (starts.Count < 2) return declared;
        var stride = starts.Zip(starts.Skip(1), (a, b) => b - a).Min();
        if (stride < 8) return declared;
        var derived = starts.Select(s => new ChecksumRegion("SAM entry (derived)", s, s + stride - 5, s + stride - 4, 16, true));
        return [.. declared, .. derived.Where(d => !declared.Any(c => c.Start <= d.Start && d.Start <= c.End))];
    }

    /// <summary>Section name for high_scores entries that belong to a secondary leaderboard (never qualifying).</summary>
    public const string SecondarySection = "secondary_scores";

    /// <summary>
    /// Buy-In tables ("Buy-In #1", "BuyIn Score #2", "Buy-In Score #3"; 27 maps, e.g. AFM, TZ 9.4H, Twister) record
    /// scores from paid extra balls, not the main game. Every other high_scores label in the maps (themed names like
    /// "Admiral", "#1 Jedi", Simpsons' "Under-Achievers", Transformers' Autobot/Decepticon) is a main list.
    /// </summary>
    internal static bool IsSecondaryLeaderboard(string label) =>
        System.Text.RegularExpressions.Regex.IsMatch(label, @"\bbuy[\s-]?in\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static int ToFileOffset(long address, long baseAddr, int size)
    {
        long off = address - baseAddr;
        if (off < 0 || off >= size)
            throw new InvalidDataException($"address 0x{address:X} is outside the NVRAM region");
        return (int)off;
    }

    static Nibble ParseNibble(string? s) => s switch
    {
        "low" => Nibble.Low,
        "high" => Nibble.High,
        "both" or null => Nibble.Both,
        _ => throw new InvalidDataException($"unknown nibble '{s}'"),
    };

    /// <summary>Numbers may be JSON integers or "0x" hex / decimal strings.</summary>
    internal static long Num(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetInt64();
        var s = e.GetString() ?? throw new FormatException("null number");
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(s, CultureInfo.InvariantCulture);
    }

    // ---------------- decoding ----------------

    /// <summary>Decodes all high-score and mode-champion entries from the raw .nv bytes.</summary>
    public IReadOnlyList<ScoreEntry> Decode(byte[] nv)
    {
        var result = new List<ScoreEntry>(HighScores.Count + ModeChampions.Count);
        foreach (var def in HighScores.Concat(ModeChampions))
        {
            string? initials = def.Initials is { } i ? ReadChars(nv, i) : null;
            long? score = null;
            string? scoreText = null;
            if (def.Score is { } s)
            {
                long raw = s.Encoding switch
                {
                    "bcd" => ReadBcd(nv, s),
                    "int" => ReadInt(nv, s),
                    _ => throw new InvalidDataException($"unsupported score encoding '{s.Encoding}'"),
                };
                long display = ApplyScale(raw, s.Scale) + s.Offset;
                if (s.SpecialValues != null && s.SpecialValues.TryGetValue(display.ToString(CultureInfo.InvariantCulture), out var special))
                {
                    if (special.ValueKind == JsonValueKind.Number) score = special.GetInt64();
                    else scoreText = special.ToString();
                }
                else score = display;
            }
            bool isDefaultInitials = initials != null && def.Initials!.Default != null && initials == def.Initials.Default;
            result.Add(new ScoreEntry(def.Section, def.Label, def.ShortLabel, initials, score, scoreText, isDefaultInitials));
        }
        return result;
    }

    /// <summary>Labels of checksum regions that do not verify; by default only regions covering a score/initials byte.</summary>
    public IReadOnlyList<string> VerifyChecksums(byte[] nv, bool scoreRegionsOnly = true)
    {
        var scoreBytes = new HashSet<int>(HighScores.Concat(ModeChampions)
            .SelectMany(d => (d.Initials?.FileOffsets ?? []).Concat(d.Score?.FileOffsets ?? [])));
        var failures = new List<string>();
        foreach (var c in Checksums)
        {
            bool covers = !scoreRegionsOnly;
            for (int b = c.Start; b <= c.End && !covers; b++) covers = scoreBytes.Contains(b);
            if (!covers) continue;
            if (Math.Max(c.End, c.StoredBytes.Max()) >= nv.Length)
                throw new InvalidDataException($"file too small for checksum region '{c.Label}'");
            if (!c.Ok(nv, LittleEndian) && !failures.Contains(c.Label)) failures.Add(c.Label);
        }
        return failures;
    }

    static byte[] ReadRaw(byte[] nv, Field f)
    {
        var buf = new byte[f.FileOffsets.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            int off = f.FileOffsets[i];
            if (off >= nv.Length) throw new InvalidDataException($"file too small: needs offset {off}, has {nv.Length} bytes");
            buf[i] = nv[off];
        }
        return buf;
    }

    /// <summary>Combines 4-bit cells into bytes; an odd count gets a leading zero cell.</summary>
    internal static byte[] DeNibble(byte[] cells, Nibble nibble)
    {
        if (nibble == Nibble.Both) return cells;
        var src = cells.Length % 2 == 0 ? cells : [0, .. cells];
        var result = new byte[src.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            byte hi = src[2 * i], lo = src[2 * i + 1];
            result[i] = nibble == Nibble.Low
                ? (byte)(((hi & 0x0F) << 4) | (lo & 0x0F))
                : (byte)((hi & 0xF0) | ((lo & 0xF0) >> 4));
        }
        return result;
    }

    internal static long ReadBcd(byte[] nv, Field f)
    {
        var buf = ReadRaw(nv, f);
        if (f.LittleEndian) Array.Reverse(buf);
        buf = DeNibble(buf, f.Nibble);
        long v = 0;
        foreach (var b in buf)
        {
            // nibbles 0xA-0xF count as 0 (blank digits on some platforms)
            int hi = b >> 4, lo = b & 0x0F;
            v = checked(v * 100 + (hi > 9 ? 0 : hi) * 10 + (lo > 9 ? 0 : lo));
        }
        return v;
    }

    internal static long ReadInt(byte[] nv, Field f)
    {
        var buf = DeNibble(ReadRaw(nv, f), f.Nibble);
        if (buf.Length > 8) throw new InvalidDataException("int field longer than 8 bytes");
        ulong v = 0;
        if (f.LittleEndian) for (int i = buf.Length - 1; i >= 0; i--) v = (v << 8) | buf[i];
        else foreach (var b in buf) v = (v << 8) | b;
        return checked((long)v);
    }

    static long ApplyScale(long v, decimal scale) => scale == 1m ? v : (long)decimal.Truncate(v * scale);

    string ReadChars(byte[] nv, Field f)
    {
        if (f.Encoding != "ch") throw new InvalidDataException($"unsupported initials encoding '{f.Encoding}'");
        var buf = ReadRaw(nv, f);
        if (f.Nibble != Nibble.Both)
        {
            // nibble platforms store each character as two 4-bit cells; zero bytes are dropped
            var bytes = DeNibble(buf, f.Nibble).Where(b => b != 0).ToArray();
            return Encoding.Latin1.GetString(bytes);
        }
        if (f.Mask is { } mask) for (int i = 0; i < buf.Length; i++) buf[i] &= mask;
        var sb = new StringBuilder(buf.Length);
        if (CharMap != null)
        {
            foreach (var b in buf) sb.Append(b < CharMap.Length ? CharMap[b] : '?');
            return sb.ToString();
        }
        foreach (var b in buf)
        {
            if (f.NullTerminated && b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
