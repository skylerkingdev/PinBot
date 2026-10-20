using PinBot.Agent.Discovery;
using System.Text.Json;
using PinBot.Agent.Scoring;

namespace PinBot.Agent.Sessions;

/// <summary>What a session leaves behind: the console report and one line in sessions.jsonl.</summary>
public sealed partial class SessionWatcher
{
    static void Print(Session s, DateTime exited, IReadOnlyList<string> changed, List<RomOutcome> outcomes, List<string> sessionProblems,
        Dictionary<string, (string Id, bool Counts, string Text)?> verdicts)
    {
        Log.Out();
        Log.Out($"=== Session {s.Id}  {s.Exe} pid {s.Pid}");
        Log.Out($"    table    {s.TablePath ?? "(unknown)"}");
        Log.Out($"    started  {s.StartedUtc:yyyy-MM-dd HH:mm:ss}Z{(s.StartObserved ? "" : " (before agent start)")}   exited {exited:yyyy-MM-dd HH:mm:ss}Z   ({exited - s.StartedUtc:hh\\:mm\\:ss})");
        if (changed.Count == 0)
        {
            Log.Out("    no NVRAM file changed (no game finished, a non-ROM table, or NVRAM is outside the watched folders)");
            if (s.TablePath != null && TableScript.Read(s.TablePath).Script is { } script && TableScript.FindRoms(script).Count > 0
                && !TableScript.CallsControllerStop(script))
                Log.Warn("    table script never calls Controller.Stop: PinMAME may not save NVRAM when this table exits");
        }
        foreach (var o in outcomes)
        {
            Log.Out($"    nvram    {o.NvPath}  rom={o.Rom}");
            if (o.Diff is { } d)
            {
                foreach (var (b, a) in d.ChangedSlots)
                    Log.Out($"      slot  {a.ShortLabel ?? a.Label,-14} {b?.Brief ?? "(none)"}  ->  {a.Brief}");
                if (d.NewEntries.Count == 0) Log.Out("      no new high-score entries");
            }
            foreach (var p in o.Problems) Log.Warn($"      {p}");
            if (o.Diff is { NewEntries.Count: > 0 } diff)
            {
                bool qualified = o.Trusted && sessionProblems.Count == 0;
                foreach (var e in diff.NewEntries)
                {
                    var verdict = !qualified ? "NOT QUALIFIED"
                        : e.Initials is null ? "VALID BUT UNATTRIBUTABLE (no initials)"
                        : "QUALIFIED";
                    var line = $"      NEW   {e.Initials ?? "(none)",-6} {e.Score:N0}   [{e.Label}]  rom={o.Rom}  {verdict}";
                    if (qualified && e.Initials != null) Log.Good(line); else Log.Warn(line);
                }
                if (verdicts.GetValueOrDefault(o.NvPath) is { } v)
                {
                    if (v.Counts && qualified) Log.Good($"      competition {v.Id}: COUNTS ({v.Text})");
                    else Log.Warn($"      competition {v.Id}: does NOT count - {(v.Counts ? "session not trustworthy" : v.Text)}");
                    if (diff.NewEntries.GroupBy(e => (e.Initials, e.Score)).Any(g => g.Count() > 1))
                        Log.Warn("      the same score was stored more than once: this ROM may be asking for initials repeatedly");
                }
            }
        }
        foreach (var p in sessionProblems) Log.Warn($"    session: {p}");
        Log.Out();
    }

    /// <summary>One JSON line per session in sessions.jsonl, to trace any result back to what was seen (in pinbot report).</summary>
    static void AppendJournal(Session s, DateTime exited, List<RomOutcome> outcomes, List<string> sessionProblems,
        Dictionary<string, (string Id, bool Counts, string Text)?> verdicts)
    {
        var record = new
        {
            sessionId = s.Id, exe = s.Exe, table = s.TablePath, startedUtc = s.StartedUtc, exitedUtc = exited,
            startObserved = s.StartObserved, sessionProblems,
            nvram = outcomes.Select(o => new
            {
                o.Rom, o.NvPath, o.Problems,
                beforeSha256 = o.Before?.Sha256, afterSha256 = o.After?.Sha256,
                newEntries = o.Diff?.NewEntries.Select(e => new { e.Label, e.Initials, e.Score }),
                qualified = o.Trusted && sessionProblems.Count == 0 && o.Diff is { NewEntries.Count: > 0 },
                competitionId = verdicts.GetValueOrDefault(o.NvPath)?.Id,
                countsForCompetition = verdicts.GetValueOrDefault(o.NvPath) is { Counts: true }
                                       && o.Trusted && sessionProblems.Count == 0 && o.Diff is { NewEntries.Count: > 0 },
            }),
            parser = MemoryMaps.ParserVersion,
        };
        try
        {
            Directory.CreateDirectory(LocalState.Dir);
            File.AppendAllText(Path.Combine(LocalState.Dir, "sessions.jsonl"), JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        catch (IOException ex) { Log.Warn($"could not append sessions.jsonl: {ex.Message}"); }
    }
}
