namespace PinBot.Agent.Scoring;

/// <summary>Result of building a competition NVRAM image from a player's own .nv bytes.</summary>
public sealed record CompetitionImage(byte[]? Bytes, IReadOnlyList<long> Ladder, string? Problem)
{
    public bool Ok => Bytes != null;
}

public sealed partial class RomMap
{
    static readonly long[] LadderSteps = [10_000, 1_000, 100, 10, 1];

    /// <summary>
    /// Copy of <paramref name="original"/> with only the main high-score values replaced by a small, nonzero,
    /// descending ladder (e.g. 60,000 ... 10,000), then checksums covering changed bytes recomputed.
    /// Initials, buy-in tables, mode champions, settings and audits are untouched. A ladder (not zeros) keeps the
    /// ROM's table normal: an all-zero table made the cabinet's Baywatch ask for initials six times in one game.
    /// The result is verified by decoding it; any doubt returns a Problem instead of bytes.
    /// </summary>
    public CompetitionImage BuildCompetitionImage(byte[] original)
    {
        if (!HasInitials) return Fail("ROM stores no initials");
        if (CompetitionBlocker is { } blocker) return Fail(blocker);
        var before = HighScoreReader.Extract(this, original);
        if (before.Status != ExtractStatus.Ok) return Fail($"original NVRAM not readable: {before.Status} {before.Detail}");

        var main = HighScores.Where(d => d.Section == "high_scores" && d.Score != null && !KeepsChampion(d.Label)).ToList();
        var (block, mirrors) = HighScoreMirrors(original, main);
        if (Platform.StartsWith("gottlieb-system3", StringComparison.Ordinal) && mirrors.Count != 2)
            return Fail("Gottlieb System 3 keeps two backup copies of its high-score table, and they were not found in this NVRAM");
        if (Platform == "capcom" && mirrors.Count == 0)
            return Fail("Capcom keeps backup copies of its high-score table, and none were found in this NVRAM");
        if (HasNibbleChecksums && mirrors.Count == 0)
            return Fail("System 80B keeps its top score in triplicate, and the three copies were not found in this NVRAM");
        if (HasNibbleChecksums && main.Select((e, i) => NibbleChecksum(original, e) != (original[NibbleChecksumBase + i] & 0xF)).Any(bad => bad))
            return Fail("this NVRAM's high-score checksums do not follow the known System 80B rule");
        foreach (var step in LadderSteps)
        {
            var ladder = Enumerable.Range(0, main.Count).Select(i => (main.Count - i) * step).ToList();
            var image = (byte[])original.Clone();
            bool encodable = true;
            for (int i = 0; i < main.Count && encodable; i++)
                encodable = TryEncodeScore(image, main[i].Score!, ladder[i]);
            if (!encodable) continue;
            foreach (var m in mirrors)
                for (int k = 0; k < block.Length; k++) // System 80B NVRAM is 4-bit: copy the low nibbles, keep the high ones
                    image[m + k] = IsSystem80B ? (byte)((image[m + k] & 0xF0) | (image[block.Start + k] & 0x0F)) : image[block.Start + k];
            if (HasNibbleChecksums)
                for (int i = 0; i < main.Count; i++)
                    image[NibbleChecksumBase + i] = (byte)((image[NibbleChecksumBase + i] & 0xF0) | NibbleChecksum(image, main[i]));
            FixChecksums(image, original);
            var problem = Verify(original, image, main, ladder, block, mirrors);
            return problem is null ? new(image, ladder, null) : Fail(problem);
        }
        return Fail("main score fields cannot hold a ladder value");
    }

    /// <summary>
    /// Capcom keeps two backup copies of its whole high-score block (initials and scores) and resets the table to
    /// factory values when they disagree (seen on Big Bang Bar), so the ladder goes into every copy. The copies sit at
    /// ROM-specific places, so they are found in the cabinet's own file: exact duplicates of the block elsewhere.
    /// Verified on Airborne, Big Bang Bar and Breakshot (two copies each; in played files they hold the real scores).
    /// The emulator self-test (2026-10-13) found the same on other platforms: Gottlieb System 3 keeps three copies (the
    /// ROM restored the player's own table from the two untouched ones: Street Fighter II, Super Mario Bros, Class of
    /// 1812, Rescue 911, Wipe Out) and Data East's Hook keeps two. So copies are looked for on every platform; a table
    /// without exact duplicates (most) is written once, as before.
    /// </summary>
    ((int Start, int Length) Block, List<int> Mirrors) HighScoreMirrors(byte[] nv, List<EntryDef> main)
    {
        if (HasNibbleChecksums) return TopScoreTriplicate(nv, main[0]);
        var offs = main.SelectMany(d => (d.Initials?.FileOffsets ?? []).Concat(d.Score!.FileOffsets)).ToList();
        var block = (Start: offs.Min(), Length: offs.Max() - offs.Min() + 1);
        var span = nv.AsSpan(block.Start, block.Length);
        if (span.IndexOfAnyExcept(span[0]) < 0) return (block, []); // uniform (blank) block: copies cannot be told apart
        var mirrors = new List<int>();
        for (int o = 0; o + span.Length <= nv.Length; o++)
            if (o != block.Start && nv.AsSpan(o, span.Length).SequenceEqual(span)) mirrors.Add(o);
        return (block, mirrors);
    }

    /// <summary>
    /// System 80B keeps 59 bytes of data in triplicate (offsets 0, 59, 118; majority vote), and that data holds a copy
    /// of the top high-score entry, initials and score: the high score to date. With only the table changed, Genesis
    /// rebuilt the table from it (the old champion back on top, the rest cleared). The copies are found in the
    /// cabinet's file as the three matches of the top entry's nibbles, 59 bytes apart; anything else finds none.
    /// </summary>
    ((int Start, int Length) Block, List<int> Mirrors) TopScoreTriplicate(byte[] nv, EntryDef top)
    {
        const int Copy = 59;
        var offs = (top.Initials?.FileOffsets ?? []).Concat(top.Score!.FileOffsets).ToList();
        var block = (Start: offs.Min(), Length: offs.Max() - offs.Min() + 1);
        bool Same(int o) => Enumerable.Range(0, block.Length).All(k => (nv[o + k] & 0xF) == (nv[block.Start + k] & 0xF));
        if (Enumerable.Range(0, block.Length).All(k => (nv[block.Start + k] & 0xF) == (nv[block.Start] & 0xF))) return (block, []);
        var hits = Enumerable.Range(0, 3 * Copy - block.Length + 1).Where(Same).ToList();
        return hits.Count == 3 && hits[1] - hits[0] == Copy && hits[2] - hits[1] == Copy ? (block, hits) : (block, []);
    }

    static CompetitionImage Fail(string why) => new(null, [], why);

    /// <summary>
    /// For the emulator self-test only: the ladder written into the score fields and nothing else (no checksums, no
    /// backup copies). A ROM that resets its table on this file is shown to check its scores, so a competition file
    /// it keeps is proven; a ROM that keeps this one does not check at power-on, and the test cannot judge it.
    /// </summary>
    public byte[]? BuildNaiveImage(byte[] original)
    {
        var main = HighScores.Where(d => d.Section == "high_scores" && d.Score != null).ToList();
        foreach (var step in LadderSteps)
        {
            var image = (byte[])original.Clone();
            if (Enumerable.Range(0, main.Count).All(i => TryEncodeScore(image, main[i].Score!, (main.Count - i) * step))) return image;
        }
        return null;
    }

    /// <summary>
    /// Gottlieb System 80B keeps a 4-bit checksum per high score at offsets 242-246 (per the maps' notes; Genesis
    /// rejected a ladder written without it). The formula, derived from the cabinet's own Genesis files before and after
    /// the ROM rewrote its table: the low-nibble sum of the entry's initials and score cells, mod 16. It holds on every
    /// sample file of the two layouts below (25 entries); other 80B layouts are not confirmed and stay unoffered.
    /// </summary>
    static readonly string[] NibbleChecksumMaps = ["80b-8digit-A-10KB.map.json", "80b-8digit-B-10KB.map.json"];
    const int NibbleChecksumBase = 242;

    bool IsSystem80B => Platform.StartsWith("gottlieb-system80b", StringComparison.Ordinal);
    bool HasNibbleChecksums => IsSystem80B && NibbleChecksumMaps.Contains(Path.GetFileName(MapFile));

    /// <summary>Why this ROM cannot run competitions even though its scores can be read, or null.</summary>
    public string? CompetitionBlocker =>
        OverlapProblem is { } overlap ? overlap
        : IsSystem80B && !HasNibbleChecksums ? "high scores are protected by checksums PinBot cannot update yet (Gottlieb System 80B)"
        : RejectsLadder.Contains(Rom) ? "this ROM resets its high scores on a competition file (found by the emulator self-test); not offered until that is understood"
        : null;

    /// <summary>
    /// Why this map cannot be trusted to say whose score is whose, or null: a high score's initials or score share bytes
    /// with another slot's, so one of the two is read from the wrong place (Dracula's map once read the top score's
    /// initials from the Loop Champion's). Such a ROM's new scores are not trusted and it runs no competitions until the
    /// map is fixed.
    /// </summary>
    public string? OverlapProblem
    {
        get
        {
            var owner = new Dictionary<int, EntryDef>();
            foreach (var e in HighScores.Concat(ModeChampions))
                foreach (var o in new[] { e.Initials, e.Score }.SelectMany(f => f?.FileOffsets ?? []).Distinct())
                {
                    if (owner.TryGetValue(o, out var other) && other != e && (e.Section == "high_scores" || other.Section == "high_scores"))
                        return $"the memory map reads '{other.Label}' and '{e.Label}' from the same bytes, so a score could be credited to the wrong initials";
                    owner[o] = e;
                }
            return null;
        }
    }

    /// <summary>
    /// ROMs the emulator self-test (2026-10-13) showed resetting a correct-looking ladder to factory values, with other
    /// bytes changed alongside that no map describes (a check PinBot does not know yet). Data East's other games kept it.
    /// </summary>
    static readonly HashSet<string> RejectsLadder = new(StringComparer.OrdinalIgnoreCase) { "hook_501", "stwr_107" };

    /// <summary>
    /// Williams System 11 games with a Champion slot (Dr. Dude, Whirlwind) raised a laddered Champion to 4,040,000 in
    /// the emulator self-test and kept the rest of the ladder: the Champion stays the player's own, and the ladder fills
    /// the places below it.
    /// </summary>
    bool KeepsChampion(string label) => Platform.StartsWith("williams-system11", StringComparison.Ordinal) && label == "Champion";

    int NibbleChecksum(byte[] nv, EntryDef e) =>
        (e.Initials?.FileOffsets ?? []).Concat(e.Score!.FileOffsets).Sum(o => nv[o] & 0xF) & 0xF;

    /// <summary>Inverse of the bcd/int decoders for one field. False if the value is not exactly representable.</summary>
    bool TryEncodeScore(byte[] nv, Field f, long value)
    {
        if (f.SpecialValues?.ContainsKey(value.ToString()) == true) return false;
        decimal rawDec = (value - f.Offset) / f.Scale;
        if (rawDec < 0 || rawDec != decimal.Truncate(rawDec)) return false;
        long raw = (long)rawDec;
        var cells = f.FileOffsets;
        if (cells.Any(o => o >= nv.Length)) return false;

        if (f.Encoding == "bcd")
        {
            int digits = f.Nibble == Nibble.Both ? cells.Length * 2 : cells.Length;
            var s = raw.ToString().PadLeft(digits, '0');
            if (s.Length > digits) return false;
            // logical order is most-significant first; little-endian stores it reversed
            int Physical(int k) => f.LittleEndian ? cells[cells.Length - 1 - k] : cells[k];
            for (int k = 0; k < cells.Length; k++)
            {
                int off = Physical(k);
                nv[off] = f.Nibble switch
                {
                    Nibble.Both => (byte)(((s[2 * k] - '0') << 4) | (s[2 * k + 1] - '0')),
                    Nibble.Low => (byte)((nv[off] & 0xF0) | (s[k] - '0')),       // keep the unused high nibble
                    Nibble.High => (byte)((nv[off] & 0x0F) | ((s[k] - '0') << 4)), // keep the unused low nibble
                    _ => nv[off],
                };
            }
        }
        else if (f.Encoding == "int" && f.Nibble == Nibble.Both)
        {
            if (cells.Length < 8 && raw >= 1L << (8 * cells.Length)) return false;
            for (int k = 0; k < cells.Length; k++)
            {
                int shift = 8 * (f.LittleEndian ? k : cells.Length - 1 - k);
                nv[cells[k]] = (byte)(raw >> shift);
            }
        }
        else return false;

        long check = f.Encoding == "bcd" ? ReadBcd(nv, f) : ReadInt(nv, f);
        return check == raw;
    }

    /// <summary>Recomputes every checksum region that contains a byte changed from the original.</summary>
    void FixChecksums(byte[] image, byte[] original)
    {
        foreach (var c in Checksums)
        {
            bool touched = false;
            for (int b = c.Start; b <= c.End && !touched; b++) touched = image[b] != original[b];
            if (!touched) continue;
            if (c.Is16)
            {
                int sum = 0;
                for (int b = c.Start; b <= c.End - 2; b++) sum += image[b];
                var v = (ushort)(0xFFFF - (sum & 0xFFFF));
                (image[c.End - 1], image[c.End]) = LittleEndian ? ((byte)v, (byte)(v >> 8)) : ((byte)(v >> 8), (byte)v);
            }
            else
            {
                int dataEnd = c.StoredAt is null ? c.End - 1 : c.End;
                int sum = 0;
                for (int b = c.Start; b <= dataEnd; b++) sum += image[b];
                image[c.StoredAt ?? c.End] = (byte)(0xFF - (sum & 0xFF));
            }
        }
    }

    /// <summary>
    /// The image must decode cleanly, carry exactly the ladder in the main table, leave every other decoded entry
    /// unchanged, and differ from the original only in main score bytes and checksum bytes.
    /// </summary>
    string? Verify(byte[] original, byte[] image, List<EntryDef> main, List<long> ladder, (int Start, int Length) block, List<int> mirrors)
    {
        int mask = IsSystem80B ? 0x0F : 0xFF;
        foreach (var m in mirrors)
            if (Enumerable.Range(0, block.Length).Any(k => (image[m + k] & mask) != (image[block.Start + k] & mask)))
                return "a high-score backup copy does not match the table";
        var after = HighScoreReader.Extract(this, image);
        if (after.Status != ExtractStatus.Ok) return $"competition image does not verify: {after.Status} {after.Detail}";
        var before = Decode(original);
        bool Laddered(ScoreEntry e) => e.Section == "high_scores" && e.Score != null && !KeepsChampion(e.Label);
        var mainScores = after.Entries.Where(Laddered).Select(e => e.Score!.Value).ToList();
        if (!mainScores.SequenceEqual(ladder)) return "competition image does not decode to the ladder";
        for (int i = 0; i < before.Count; i++)
        {
            var (b, a) = (before[i], after.Entries[i]);
            if (b.RawInitials != a.RawInitials) return $"initials changed in '{a.Label}'";
            if (!Laddered(b) && (b.Score != a.Score || b.ScoreText != a.ScoreText))
                return $"non-ladder entry changed: '{a.Label}'";
        }
        var scoreBytes = main.SelectMany(d => d.Score!.FileOffsets).ToList();
        var mirrored = scoreBytes.Where(o => o >= block.Start && o < block.Start + block.Length).ToList();
        var allowed = new HashSet<int>(scoreBytes.Concat(mirrors.SelectMany(m => mirrored.Select(o => o - block.Start + m))));
        if (HasNibbleChecksums) allowed.UnionWith(Enumerable.Range(NibbleChecksumBase, main.Count));
        foreach (var c in Checksums)
        {
            if (c.Is16) { allowed.Add(c.End - 1); allowed.Add(c.End); }
            else allowed.Add(c.StoredAt ?? c.End);
        }
        for (int b = 0; b < image.Length; b++)
            if (image[b] != original[b] && !allowed.Contains(b)) return $"unexpected byte change at offset {b}";
        return null;
    }
}
