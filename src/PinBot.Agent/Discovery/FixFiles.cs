using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PinBot.Agent.Discovery;

/// <summary>
/// Script fixes another member shared, installed on this VPin on its member's accept (docs/TABLE-UPDATES.md). A fix is
/// written as a sidecar .vbs next to the table (VPX prefers it over the script inside the .vpx), never into the .vpx.
/// It installs only on exactly the script it was made from and only if the result is exactly the shared one; added
/// lines that create objects, run programs or touch files are refused. A sidecar PinBot wrote is moved aside when the
/// table itself is replaced, so an old fix never overrides a new table's script.
/// </summary>
public static partial class FixFiles
{
    /// <summary>What PinBot wrote for an install: to undo it, and to notice when the table is replaced under it.</summary>
    public sealed record Installed(long InstallId, string Sidecar, string? Backup, string? EmbeddedHash, string Target);

    static string StatePath => Path.Combine(LocalState.Dir, "fixes.json");
    static string BackupDir => Path.Combine(LocalState.Dir, "setup-backups");

    static List<Installed> Load()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<List<Installed>>(File.ReadAllText(StatePath)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    static void Save(List<Installed> list)
    {
        Directory.CreateDirectory(LocalState.Dir);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list));
        File.Move(tmp, StatePath, overwrite: true);
    }

    [GeneratedRegex(@"\b(CreateObject|GetObject|WScript|Shell|ShellExecute|Execute|ExecuteGlobal|Eval|FileSystemObject|DeleteFile|DeleteFolder|CopyFile|CopyFolder|MoveFile|MoveFolder|CreateTextFile|OpenTextFile|CreateFolder|RegWrite|RegDelete|XMLHTTP|ServerXMLHTTP|WinHttp|ADODB|Environment|Exec)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Risky();

    /// <summary>The first added line that does something a table fix shouldn't need (outside comments), or null.</summary>
    public static string? RiskyLine(string diff) =>
        ScriptDiff.Added(diff).Select(l => l.Split('\'')[0]).FirstOrDefault(l => Risky().IsMatch(l))?.Trim();

    /// <summary>
    /// Installs the fix: returns the script hash before and after, or throws <see cref="InvalidDataException"/> with
    /// the reason it refused.
    /// </summary>
    public static (string Previous, string Now) Install(string vpxPath, long installId, string diff, string? baseHash, string? target)
    {
        var (script, _) = TableScript.Read(vpxPath);
        var current = TableMetadata.HashScript(script);
        if (script is null || current is null) throw new InvalidDataException("this VPin can't read the table's script");
        if (target is not null && current == target) throw new InvalidDataException("this VPin already has this fix");
        if (current != baseHash) throw new InvalidDataException("this VPin's script for the table isn't the one the fix was made from");
        if (RiskyLine(diff) is { } risky) throw new InvalidDataException($"the fix adds a line PinBot doesn't install automatically: {risky}");
        var result = ScriptDiff.Apply(script, diff) ?? throw new InvalidDataException("the fix doesn't apply to this VPin's script");
        if (TableMetadata.HashScript(result) != target) throw new InvalidDataException("the fixed script wouldn't match the shared one");

        var sidecar = Path.ChangeExtension(vpxPath, ".vbs");
        string? backup = null;
        if (File.Exists(sidecar))
        {
            Directory.CreateDirectory(BackupDir);
            backup = Path.Combine(BackupDir, $"{Path.GetFileNameWithoutExtension(sidecar)}-{installId}.vbs");
            File.Copy(sidecar, backup, overwrite: true);
        }
        Write(sidecar, result);
        var list = Load();
        list.RemoveAll(i => i.Sidecar.Equals(sidecar, StringComparison.OrdinalIgnoreCase));
        list.Add(new(installId, sidecar, backup, TableMetadata.HashScript(TableScript.ReadEmbedded(vpxPath).Script), target!));
        Save(list);
        return (current, target!);
    }

    /// <summary>Takes back install <paramref name="installId"/>: the earlier sidecar back, or none. Returns the script hash now.</summary>
    public static string? Undo(string vpxPath, long installId)
    {
        var list = Load();
        var rec = list.FirstOrDefault(i => i.InstallId == installId) ?? throw new InvalidDataException("this VPin has no record of that fix (already undone?)");
        var (script, _) = TableScript.Read(vpxPath);
        if (TableMetadata.HashScript(script) != rec.Target) throw new InvalidDataException("the table's script changed since the fix was installed: not undone");
        if (rec.Backup is not null && File.Exists(rec.Backup)) File.Copy(rec.Backup, rec.Sidecar, overwrite: true);
        else File.Delete(rec.Sidecar);
        list.Remove(rec);
        Save(list);
        return TableMetadata.HashScript(TableScript.Read(vpxPath).Script);
    }

    /// <summary>
    /// When the table a fix was installed on was replaced (its own script changed), the fix's sidecar would override
    /// the new script: moved aside (kept, renamed). Returns a note for the log, or null.
    /// </summary>
    public static string? MoveAsideIfStale(string vpxPath)
    {
        var sidecar = Path.ChangeExtension(vpxPath, ".vbs");
        var list = Load();
        var rec = list.FirstOrDefault(i => i.Sidecar.Equals(sidecar, StringComparison.OrdinalIgnoreCase));
        if (rec is null) return null;
        if (!File.Exists(sidecar)) { list.Remove(rec); Save(list); return null; }
        var embedded = TableMetadata.HashScript(TableScript.ReadEmbedded(vpxPath).Script);
        if (embedded is null || embedded == rec.EmbeddedHash) return null;
        var aside = sidecar + ".pinbot-old";
        File.Move(sidecar, aside, overwrite: true);
        list.Remove(rec);
        Save(list);
        return $"{Path.GetFileName(vpxPath)} was replaced: the fix PinBot installed for its old script was moved aside ({Path.GetFileName(aside)})";
    }

    static void Write(string path, string script)
    {
        var tmp = path + ".pinbot-tmp";
        File.WriteAllText(tmp, script, Encoding.Latin1);
        File.Move(tmp, path, overwrite: true);
    }
}
