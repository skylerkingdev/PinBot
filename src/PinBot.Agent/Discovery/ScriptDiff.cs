using System.Text;

namespace PinBot.Agent.Discovery;

/// <summary>
/// Line diffs of table scripts, for sharing a script fix (docs/TABLE-UPDATES.md): what changed, as a unified diff with
/// a few lines of context, and applying one to exactly the script it was made from. Scripts are compared with line
/// endings normalized to \n, as their hash is.
/// </summary>
public static class ScriptDiff
{
    public const int Context = 3;
    /// <summary>A fix is small: more changed lines than this is a new version of the table, not a fix.</summary>
    public const int MaxChangedLines = 400;

    public static string[] Lines(string script) => script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>The unified diff from <paramref name="before"/> to <paramref name="after"/>; null when equal or too large.</summary>
    public static string? Make(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var ops = Edits(a, b, MaxChangedLines);
        if (ops is null || ops.All(o => o.Kind == ' ')) return null;
        var sb = new StringBuilder();
        // group the edits into hunks: changes closer than 2 × context share one
        int i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ') { i++; continue; }
            int start = Math.Max(0, i - Context);
            while (start < i && ops[start].Kind != ' ') start++;
            int end = i;
            while (true)
            {
                while (end < ops.Count && ops[end].Kind != ' ') end++;
                int run = 0;
                while (end + run < ops.Count && ops[end + run].Kind == ' ') run++;
                if (end + run < ops.Count && run <= 2 * Context) { end += run; continue; }
                end = Math.Min(ops.Count, end + Math.Min(run, Context));
                break;
            }
            int oldStart = ops[start].A, newStart = ops[start].B;
            int oldCount = ops.Skip(start).Take(end - start).Count(o => o.Kind != '+');
            int newCount = ops.Skip(start).Take(end - start).Count(o => o.Kind != '-');
            sb.Append($"@@ -{oldStart + 1},{oldCount} +{newStart + 1},{newCount} @@\n");
            for (int k = start; k < end; k++)
                sb.Append(ops[k].Kind).Append(ops[k].Kind == '+' ? b[ops[k].B] : a[ops[k].A]).Append('\n');
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="diff"/> applied to <paramref name="script"/>, or null when the script isn't the one it was made
    /// from (a context or removed line differs) or the diff is malformed. Returns text with \r\n line endings.
    /// </summary>
    public static string? Apply(string script, string diff)
    {
        var a = Lines(script);
        var output = new List<string>(a.Length + 64);
        int pos = 0; // next line of a to copy
        var lines = diff.Split('\n');
        int li = 0;
        while (li < lines.Length)
        {
            var header = lines[li];
            if (header.Length == 0) { li++; continue; }
            if (!header.StartsWith("@@ -")) return null;
            var parts = header.Split(' ');
            if (parts.Length < 4 || !TryRange(parts[1][1..], out var oldStart, out var oldCount) || !TryRange(parts[2][1..], out _, out var newCount)) return null;
            oldStart -= 1;
            if (oldStart < pos || oldStart > a.Length) return null;
            output.AddRange(a[pos..oldStart]);
            pos = oldStart;
            li++;
            int seenOld = 0, seenNew = 0;
            while (li < lines.Length && (seenOld < oldCount || seenNew < newCount))
            {
                var l = lines[li];
                if (l.Length == 0) return null;
                var text = l[1..];
                switch (l[0])
                {
                    case ' ':
                        if (pos >= a.Length || a[pos] != text) return null;
                        output.Add(text); pos++; seenOld++; seenNew++;
                        break;
                    case '-':
                        if (pos >= a.Length || a[pos] != text) return null;
                        pos++; seenOld++;
                        break;
                    case '+':
                        output.Add(text); seenNew++;
                        break;
                    default: return null;
                }
                li++;
            }
            if (seenOld != oldCount || seenNew != newCount) return null;
        }
        output.AddRange(a[pos..]);
        return string.Join("\r\n", output);
    }

    static bool TryRange(string s, out int start, out int count)
    {
        var p = s.Split(',');
        count = 1;
        return int.TryParse(p[0], out start) && (p.Length == 1 || int.TryParse(p[1], out count));
    }

    /// <summary>The lines a diff adds (for the risky-code check and the post's line count).</summary>
    public static IEnumerable<string> Added(string diff) =>
        diff.Split('\n').Where(l => l.StartsWith('+')).Select(l => l[1..]);

    public static int Changed(string diff) => diff.Split('\n').Count(l => l.Length > 0 && l[0] is '+' or '-');

    readonly record struct Op(char Kind, int A, int B);

    /// <summary>Myers' diff (keeping only each step's live diagonals): null when more than <paramref name="maxD"/> lines change.</summary>
    static List<Op>? Edits(string[] a, string[] b, int maxD)
    {
        int n = a.Length, m = b.Length, off = maxD + 1;
        var v = new int[2 * maxD + 3];
        var trace = new List<int[]>();
        for (int d = 0; d <= maxD; d++)
        {
            trace.Add(v[(off - d - 1)..(off + d + 2)]); // v before this step, diagonals -d-1..d+1
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, a.Length, b.Length, d);
            }
        }
        return null;
    }

    static List<Op> Backtrack(List<int[]> trace, int n, int m, int dEnd)
    {
        var ops = new List<Op>();
        int x = n, y = m;
        for (int d = dEnd; d > 0; d--)
        {
            var v = trace[d]; // diagonals -d-1..d+1 at index k + d + 1
            int k = x - y;
            int V(int kk) => v[kk + d + 1];
            int prevK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            int prevX = V(prevK), prevY = prevX - prevK;
            while (x > prevX && y > prevY) { x--; y--; ops.Add(new(' ', x, y)); }
            if (prevK == k + 1) { y--; ops.Add(new('+', x, y)); } // down: an inserted line
            else { x--; ops.Add(new('-', x, y)); }                 // right: a removed line
        }
        while (x > 0 && y > 0) { x--; y--; ops.Add(new(' ', x, y)); }
        ops.Reverse();
        return ops;
    }
}
