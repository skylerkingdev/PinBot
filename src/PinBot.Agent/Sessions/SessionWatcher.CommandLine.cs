using System.Management;
using System.Runtime.InteropServices;

namespace PinBot.Agent.Sessions;

/// <summary>Which table a VPX process runs, from its command line.</summary>
public sealed partial class SessionWatcher
{
    static string? QueryCommandLine(int pid)
    {
        try
        {
            using var q = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var o in q.Get()) using (o) return o["CommandLine"] as string;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>First .vpx argument, e.g. <c>VPinballX64.exe -minimized -play "C:\Tables\AFM.vpx"</c>.</summary>
    public static string? TableFromCommandLine(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        foreach (var arg in SplitArgs(cmd))
            if (arg.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase))
                return Path.IsPathRooted(arg) ? Path.GetFullPath(arg) : arg;
        return null;
    }

    /// <summary>
    /// The table as a full path. A relative one (a frontend starting VPX in its own folder) is looked for in
    /// <paramref name="baseDirs"/> (VPX folders, then table folders), then by file name among the known tables if only one
    /// matches. Left as it is when nothing fits.
    /// </summary>
    internal static string? ResolveTable(string? table, IEnumerable<string> baseDirs, IEnumerable<string> knownTables)
    {
        if (table is null || Path.IsPathRooted(table)) return table;
        foreach (var dir in baseDirs)
        {
            var full = Path.GetFullPath(Path.Combine(dir, table));
            if (File.Exists(full)) return full;
        }
        var byName = knownTables.Where(t => Path.GetFileName(t).Equals(Path.GetFileName(table), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return byName.Count == 1 ? byName[0] : table;
    }

    internal static IEnumerable<string> SplitArgs(string cmd)
    {
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var c in cmd)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }
}
