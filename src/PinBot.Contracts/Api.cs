namespace PinBot.Contracts;

/// <summary>Agent ↔ server wire format, API version 1. JSON with camelCase names; all times UTC.</summary>
public static class ApiV1
{
    public const string Prefix = "/api/v1";
}

public sealed record PairRedeemRequest(string Code, string CabinetName);

/// <summary>The credential is shown once; the server keeps only its hash.</summary>
public sealed record PairRedeemResponse(string CabinetId, string Credential);

public sealed record InventoryTable(string Table, string RomId, bool Eligible, string? Reason);

/// <summary>
/// One table file on the cabinet as it describes itself, with or without a ROM: what table-update posts are made of.
/// Name, Version and Authors are the table's own TableInfo; ScriptHash is the SHA-256 of the script VPX runs for it.
/// </summary>
public sealed record TableFile(
    string Table, string? RomId, string? Name, string? Version, string? Authors, string? ScriptHash,
    string? BackglassName = null, string? BackglassAuthor = null, int? BackglassDmdType = null,
    bool AltColor = false, bool AltSound = false,
    // the table's view (POV) for the VPin's view mode ("desktop", "cabinet", "fss"): "Key = value" lines of View… keys
    string? PovMode = null, string? Pov = null);

/// <summary>
/// Replaces the cabinet's whole inventory. <see cref="Files"/> (newer agents) lists every table file, including tables
/// without a ROM; servers that predate it ignore it, and a missing list leaves the server's copy as it was.
/// </summary>
public sealed record InventoryRequest(IReadOnlyList<InventoryTable> Tables, string AgentVersion, IReadOnlyList<TableFile>? Files = null);

public sealed record InventoryResponse(int Tables, int EligibleRoms);

/// <summary>
/// A competition this cabinet should know about: running, or ended but still inside the delivery window
/// (so the agent can still upload late-arriving results and restore NVRAM).
/// <see cref="RomIds"/> are the ROM revisions installed on this cabinet that belong to the competition's game.
/// </summary>
public sealed record AgentCompetition(
    long Id, string CanonicalGameId, string Title, IReadOnlyList<string> RomIds,
    DateTime StartsAtUtc, DateTime EndsAtUtc, string Status);

/// <param name="Version">changes whenever a competition starts or ends; pass it back as known= to wait for the next change</param>
/// <param name="LatestRelease">the signed release the server offers (0: none), so cabinets check for updates only when there is one</param>
/// <param name="Setups">installs waiting for this cabinet (shared POVs): fetch them from agent/setups when no table runs</param>
public sealed record AgentCompetitionsResponse(DateTime ServerTimeUtc, IReadOnlyList<AgentCompetition> Competitions, long Version = 0, long LatestRelease = 0,
    int Setups = 0);

/// <summary>Something a member chose to install on this VPin from another member's share. Kind "pov": <paramref name="Content"/>
/// is the view keys for <paramref name="Mode"/> ("" puts the table's own view back).</summary>
public sealed record SetupInstall(long Id, string Kind, string Table, string? Mode, string Content);

/// <summary>What the VPin did: "installed" or "refused" (with why), and the table's view before and after.</summary>
public sealed record SetupInstallResult(string State, string? Detail = null, string? Previous = null, string? Now = null);

/// <summary>A table started (the cabinet is busy, not offline) or no table runs any more.</summary>
public sealed record AgentActivity(bool Playing);

/// <summary>Where a cabinet stands with a competition: ready (competition file in place), waiting (with why), or refused.</summary>
public sealed record CompetitionState(long CompetitionId, string State, string? Detail);

public static class CompetitionStates
{
    public const string Ready = "ready", Waiting = "waiting", Refused = "refused";

    /// <summary>
    /// Inventory reason for a ROM that reset its high scores on a competition file on that cabinet: the card shows the
    /// cabinet as unable to run the game there, rather than leaving it out silently.
    /// </summary>
    public const string RomRejected = "this ROM reset its high scores on a competition file: not offered here until a PinBot update fixes it";
}

public sealed record CompetitionStatesRequest(IReadOnlyList<CompetitionState> States);

/// <summary>
/// One newly qualifying high-score entry. <see cref="EventId"/> is generated once per detected entry and replayed
/// unchanged on every retry; the server deduplicates on it.
/// </summary>
public sealed record ResultEvent(
    Guid EventId, long CompetitionId, string SessionId, string RomId,
    string Initials, long Score, DateTime SessionExitedAtUtc, string ParserVersion);

public sealed record ResultsRequest(IReadOnlyList<ResultEvent> Events);

public static class ResultStatus
{
    /// <summary>Counted for the leaderboard under one Discord member.</summary>
    public const string Accepted = "accepted";
    /// <summary>Valid, but the initials match no member or more than one: not counted.</summary>
    public const string Unattributed = "unattributed";
    /// <summary>Not valid for this competition (late, wrong game, bad data). Final.</summary>
    public const string Rejected = "rejected";
}

/// <summary>Every outcome is final: the agent removes the event from its outbox. Only transport errors are retried.</summary>
public sealed record ResultOutcome(Guid EventId, string Status, string? Reason);

public sealed record ResultsResponse(IReadOnlyList<ResultOutcome> Outcomes);
