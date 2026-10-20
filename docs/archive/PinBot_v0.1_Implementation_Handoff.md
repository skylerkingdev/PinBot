# PinBot — v0.1 Implementation Specification & Handoff

> **Automatic virtual pinball competitions through Discord.**
>
> **Status:** Approved product requirements; implementation not started.  
> **Scope:** Private Discord community first; one portable deployment per community if publicly released later.  
> **Design priorities, in order:** **Simplicity · Reliability · Elegance · Minimalism.**

## 0. Instructions for the implementation session

Treat this document as the functional baseline, **not** permission to build every component immediately. Start with **M0, a read-only local feasibility prototype**. Do not mutate users' NVRAM, create hosting resources, register a Discord app, or implement extra features until the relevant stage is reached.

Distinguish in this document:
- **LOCKED** = explicitly chosen by the project owner.
- **RECOMMENDED** = sensible, minimal implementation default; may change with prototype evidence.
- **UNRESOLVED / VALIDATE** = do not pretend this has been proven.

Favor deletion of unnecessary architecture over abstractions and “future-proofing.” The target is a finished, low-maintenance community utility, **not** a managed SaaS platform, general pinball frontend, tournament marketplace, or extensible plugin host.

**One-line acceptance goal:** Install the agent once, pair the cabinet once, play normally, and let PinBot manage qualifying competitions through Discord without ongoing library maintenance.

---

## 1. Product definition

**PinBot** is a small virtual-pinball competition system with two deliverables:

1. **PinBot Agent** — Windows cabinet-side app: discover VPX/ROM relationships; observe VPX sessions; protect personal NVRAM; extract newly qualifying high-score entries after VPX exits; queue and submit results. Runs unobtrusively and independently of any frontend.
2. **PinBot Server** — Portable HTTP service containing the competition engine, PostgreSQL persistence, and **one** Discord application integration. It handles cabinet pairing, automatic inventory aggregation, weekly and one-off competitions, player attribution, standings, and a leaderboard message per competition.

### Supported in v0.1 — LOCKED

- Windows VPX + VPinMAME **ROM-based** tables. No requirement for PinUP Popper, vPinOps, or VPinFE.
- Scores checked **only after the associated VPX session exits**. Score source is **qualifying ROM high-score entries**; not every game and not every score.
- An eligible session must **exit before the competition deadline**. Sessions exiting at or after the deadline do **not** qualify, even if play started before the deadline.
- One competition-specific NVRAM state per **cabinet + actual ROM/NVRAM path + competition**; **retain** and reuse that state throughout the competition, with **no resets between sessions**.
- Original personal NVRAM preserved and restored after competition completion, once its ROM is idle. Restoration is **best effort** and must not block launches or gameplay.
- Multiple distinct Discord players can play on one cabinet; one player can use multiple cabinets. Attribute results by ROM initials matched to Discord **server-member nickname tags**, e.g. `Player | [SKY,AVK,BMS]`.
- Unknown, missing, and ambiguous initials **do not count**. Never guess cabinet owner identity.
- Player leaderboard shows **best accepted score per Discord member**.
- Related ROM revisions of the same physical game may compete together **when independently decoded and mapped correctly**. Record the exact ROM revision on every result.
- **At most one active competition per canonical game**, including related ROM revisions. Different games may have concurrent competitions.
- **Any Discord server member may start a one-off competition.** No per-competition player opt-in.
- **Weekly tournament** automatically selects an eligible installed game; one-off competitions may coexist if they use different games.
- Newly installed eligible tables can **late join** an already running competition after successful agent discovery and preparation; no historical scores.
- Hosting is **platform-agnostic**: a portable server/container for both self-hosting and a potentially free-tier cloud deployment. No dependence on the owner's public hosted service.
- Offline buffering, idempotent submission, recoverability, and reliable NVRAM preservation matter more than strict launch enforcement.

### Excluded in v0.1 — LOCKED / intentionally deferred

- Pinball FX / FX3, non-ROM/scripted tables, live scoring, capture after every ball/game, scoring below ROM leaderboard thresholds.
- Frontend integration or management, DOF/DOFLinx management, overlays, capture media, pinball table installers.
- Per-cabinet Discord bots, per-game player selection, opt-in workflow, website/dashboard, public user accounts.
- Competitive anti-cheat, formal tournament brackets, cross-guild competitions, public SaaS tenancy, statistics platform, theme/plugin system.
- Hard preventing table launches on expired competition NVRAM.

**Publishing intent:** Free/noncommercial community utility. Potential later self/cloud hosting packages. **Open-source licensing and redistribution permissions are not yet decided**; do not infer a chosen license.

---

## 2. Architecture and boundaries

```text
  Windows cabinet 1      Windows cabinet 2       ...
   ┌─────────────┐        ┌─────────────┐
   │ PinBot Agent│        │ PinBot Agent│
   │ VPX observer│        │ VPX observer│
   │ NV journal  │        │ NV journal  │
   │ score parser│        │ score parser│
   │ retry queue │        │ retry queue │
   └──────┬──────┘        └──────┬──────┘
          └───────── HTTPS ──────┘
                        │
                ┌───────▼────────┐
                │ PinBot Server  │
                │ REST + Discord │
                │ scheduling     │
                │ score standings│
                └───────┬────────┘
                        │
                  PostgreSQL
                        │
                  Discord API
        (one bot; interactive commands and messages)
```

**Agent owns:** local filesystem detection, actual ROM/NV path, session observation, safe file changes, parser orchestration, durable local queue and journal. No inbound internet ports, Discord tokens, or knowledge of Discord commands.

**Server owns:** guild/cabinet pairing and authentication; received inventories; canonical game IDs and competition conflicts; time boundaries; score attribution and ranking; one-off and weekly scheduling; Discord displays. It never directly touches cabinet files.

**Discord is a UI, not a database or transport between cabinets.** All competition truth lives on the server. Cabinets communicate only with the server using HTTPS. Bot and API are one deployable server process.

**Portability:** use a conventional ASP.NET Core HTTP server and PostgreSQL. No host-specific SDK in business logic, and no always-on Gateway bot requirement. HTTP-based Discord interactions permit a request-driven cloud service. An ordinary container can run on a VPS or home host.

**RECOMMENDED stack:** current supported .NET LTS; C# for agent and ASP.NET Core Minimal API server; PostgreSQL centrally; lightweight local SQLite for agent queue/journal **if it materially simplifies durability**, otherwise atomic local files; Docker for server; one C# solution. Do not introduce message brokers, Redis, Kubernetes, ORMs beyond what is useful, or separate worker services.

---

## 3. Identity and game catalog

Keep these identifiers distinct:

| Identifier | Meaning | Rule |
|---|---|---|
| `cabinet_id` | Machine paired to server | Persistent, revocable; not a human identity |
| `discord_user_id` | Individual player | Permanent ranking key, independent of display name |
| `initials` | Initials recorded by ROM | Normalized; match uniquely to member tags |
| `table_installation_id` | Cabinet-local VPX table | Used for discovery and diagnostics |
| `rom_id` | Exact PinMAME ROM revision | Determines NV location/decoder, e.g. `afm_113b` |
| `canonical_game_id` | Shared game identity | Conflict key, e.g. Attack from Mars across compatible revisions |
| `competition_id` | Immutable competition identity | Owns submissions and local NV state |
| `session_id` | Local observed VPX run | Correlates baseline, exit, extraction and retries |

### Discovery — automatic, never manually imported

1. Scan plausible existing VPX table directories and installations at agent startup; accept an optional one-time path override **only when discovery fails**.
2. Find `.vpx` installations and determine their associated ROM when reliable. Some references may be embedded in table scripts and may be dynamically resolved: **do not assume every file can be parsed correctly by a text grep**.
3. Learn or confirm a ROM association from an observed successful launch when the launch makes this unambiguous. Do not treat guesses as verified associations.
4. Determine the **actual** VPinMAME NVRAM location, which can vary by installation/layout. Do not assume one global `nvram/` folder.
5. Check whether the configured score decoder supports this exact ROM revision. If not, list it as detected but **competition-ineligible**.
6. Sync a small inventory of supported games and exact ROM versions to the server at startup, after library changes, and at modest intervals. Do not do expensive recursive scans during gameplay.
7. Agent skips conflicting/ambiguous configurations and records a concise diagnostic rather than requesting constant user setup.

The server lists **eligible** games available on at least one cabinet; it does not require everyone to own the same tables. For weekly random selection, exclude conflicts and unsupported games, avoid immediate repeats when possible, and skip/announce if the pool is empty.

### Compatible ROM revisions

- Canonical identity must be **explicitly validated**, e.g. via a small tested ROM alias/mapping dataset. Never group games solely because ROM filenames share a prefix.
- Each revision must have a supported, tested extractor and verified NVRAM preparation behavior on its local cabinet.
- Scores are compared on a **friendly trust basis**, with no guarantee that all ROM settings/difficulties and VPX recreations are equally challenging. Store exact revision with every submission.
- **One active competition per canonical game** (not merely per exact ROM revision); if weekly Attack from Mars is active, another related Attack from Mars revision may not start a separate one-off competition.

---

## 4. Player attribution through Discord tags

**LOCKED convention:** Discord server-member nickname contains a bracketed initials list, e.g. `Blake | [BLK,MAK]`. Multiple sets of initials may map to one Discord account. Ignore tags of bot accounts, including legacy VPin Studio cabinet bots. Multiple users can play on one cabinet with no local selection step.

**Recommended parser for v0.1:** locate one bracketed list of comma-separated, three-character case-insensitive alphanumeric initials in the guild-specific nickname/display name; trim whitespace; store normalized uppercase initials and immutable Discord user ID. Verify against actual member examples before freezing parsing. No per-player registration UI.

- The same initials claimed by two human members are **ambiguous** and ignored for current submissions. Do not choose first match.
- Unknown initials are ignored, not posted as `Unknown` and not assigned to the machine owner.
- Once a score has been accepted, retain its `discord_user_id` even if that member later changes nickname/tags.
- Cache and periodically refresh mappings, with on-demand refresh for stale/ambiguous data when practical. Do not request the full member list on every score.
- The Discord app must have the relevant Guild Members access for full member listing. HTTP interactions by themselves **do not** provide ongoing member-update events; use REST periodic refresh rather than adding a permanently connected Gateway client for v0.1.
- If permissions are missing, surface an explicit setup diagnostic rather than attributing scores incorrectly.
- No `MESSAGE_CONTENT` intent is needed for slash commands; don't request extra permissions.

**Important:** A user's identity is `discord_user_id`, not `initials`. Scores entered under either `BLK` or `MAK` rank under that one person, across all cabinets.

---

## 5. Score detection and deadline rules

### Session flow — LOCKED

1. Agent observes VPX launch and establishes the exact ROM and current competition **if it has been successfully prepared**.
2. Agent keeps a durable baseline of normalized known high-score entries for that competition/ROM before this session is accepted.
3. User plays normally. No score polling, overlays, or input interception during play.
4. On **actual VPX session exit** (wait for PinMAME to finish writing/for the NV file to stabilize), parse supported high-score records from competition NVRAM.
5. Compare normalized records against the baseline; identify **new or improved qualifying entries**, including correct handling of reordering/overwritten slots. Do not accept old/default scores just because they appear in a file.
6. If VPX exit time is **before** the competition's `ends_at_utc`, prepare score submissions. If the session exits at/after the deadline, **do not count any extracted scores from that session**.
7. Resolve initials to exactly one Discord user. Ignore unrecognized or ambiguous initials.
8. Durably queue new results before networking; upload on availability. Server deduplicates by stable idempotency key, stores the accepted result, and keeps only each user's best score in the displayed leaderboard.
9. Update the local baseline/snapshot **only after a safe, durable local state transition**, so crash/retry cannot fabricate a new result.

### Explicit limitations

- Exit-only capture does **not** guarantee every completed game is seen; scores below ROM high-score thresholds can be lost.
- Multiple games in a single VPX launch can only contribute high-score entries still present at exit.
- A session ending after deadline is ineligible **even if a game began before deadline**. There is **no gameplay grace period**.
- An agent that started monitoring after a session was already in progress must **not claim** that its initial baseline or start state is trustworthy; mark that session unqualified if required information is missing.

### Offline arrivals vs gameplay deadline

Distinguish two timestamps: `session_exited_at_utc` (eligibility) and `received_at_utc` (upload). A result from a session that exited **before** the deadline can still upload after network recovery. **RECOMMENDED default:** hold competition results in `Finalizing` for a 24-hour *delivery* window, then publish final standings. This does **not** extend gameplay or permit post-deadline VPX sessions. Choose the delivery-window duration as a centrally configured default, not an extra option in each Discord form.

Local timestamps are not cryptographic proof; this is a trust-based private community. Use UTC, record available server-clock offset/last sync, validate obvious clock drift and reject impossible timestamps. Do not build anti-cheat.

### Score event example (illustrative DTO, not final wire schema)

```json
{
  "eventId": "8d3380a1-5f1e-476e-a764-d1a98ad96e59",
  "competitionId": "104",
  "cabinetId": "c-ab12",
  "sessionId": "s-8a51",
  "canonicalGameId": "attack-from-mars",
  "romId": "afm_113b",
  "initials": "SKY",
  "score": 428750000,
  "sessionExitedAtUtc": "2026-10-11T21:31:00Z",
  "extractorVersion": "prototype"
}
```

Server resolves initials to a Discord user. Score value must support the valid integer range of supported ROMs; never use floating-point scoring. `eventId` is generated and persisted once per logical detected event, then replayed unchanged on retries.

---

## 6. Competition NVRAM lifecycle — paramount safety contract

**LOCKED:** A competition owns a **persistent temporary ROM score state**, not a freshly reset state every launch. The player's original NVRAM is restored when safe after the competition ends. The agent is *not* a launch gatekeeper.

### Logical states

```text
Discovered
    │
    ▼
PendingIdlePreparation
    │ verified original backup / absence recorded
    ▼
CompetitionReady  ──── reused through all eligible VPX sessions
    │
    │ end time reached, or event ended
    ▼
PendingIdleRestoration
    │ restore & verify
    ▼
Restored
```

Errors go to `NeedsRecovery`, **not** to repeated destructive attempts. A durable local journal records each intended transition and exact paths/checksums. Journal must be written before changes to the active NV path. File operations and SQLite/local state writes must be made durable as far as practical.

### Preparation

1. Confirm correct physical NVRAM path for this ROM. If path cannot be determined, do not prepare.
2. Confirm relevant VPX/PinMAME activity is idle; serialize against other operations on the same path.
3. Copy existing original `.nv` to protected immutable backup and verify bytes/checksum. If no original exists, record **original was absent**.
4. Create/initialize competition NV state using a **ROM-validated** reset or known-good initialization mechanism. Never assume deleting arbitrary `.nv` files is universally safe or that reset preserves ROM settings.
5. Place competition state at the location PinMAME will use, verify, journal ownership, and enter `CompetitionReady`.
6. Baseline the newly prepared leaderboard before the first eligible run.

### During the competition

- Reuse exactly the same competition NVRAM across VPX launches. **No per-session reset.**
- On each eligible exit, extract new qualifying high-score entries and preserve the competition file in place.
- Personal ROM scores/settings remain backed up; competition-time changes to audits/settings are not merged back.
- Late-joining cabinets do the preparation when idle, even if a competition has been running for several days.

### At expiration / cancellation

1. Competition deadline immediately stops eligibility of sessions that exit after the deadline.
2. Agent marks the temporary NV state `PendingIdleRestoration` when it learns the competition ended.
3. If VPX/PinMAME is active, wait; **never terminate or block the game**.
4. Once idle, restore the protected original file byte-for-byte and verify. If no original existed, remove the temporary competition file once safely idle.
5. Only after verification, journal `Restored` and retire backups per a conservative cleanup policy.
6. On boot/agent restart, reconcile incomplete journal operations before attempting additional modifications.

**Permitted harmless race:** If a user launches the table after the deadline but before restoration, the game may use competition NVRAM once more. It simply contributes **no competition scores**, and restoration waits for an idle moment. **Do not** introduce a frontend hook, game-launch blocker, mandatory service, or intrusive enforcement to eliminate this rare case.

### Data-safety invariants

- **Never intentionally overwrite, reset, or delete an unverified personal NVRAM backup.**
- **Never intentionally replace a ROM's NV file while its active emulator session may still be writing it.**
- No backup gets silently superseded by a later competition. If a new competition starts before the prior one has safely restored that ROM, defer preparation for the new competition. Different games may coexist, but serialize access by **actual file path**.
- If original NVRAM is absent, recovery must restore that absence, not manufacture fake original data.
- After crash/power loss, original bytes must be recoverable, or the agent must preserve both available copies and report a clear manual-recovery condition; **do not guess or repeatedly swap**.
- There is no hard launch lock. Observing idle is **best effort**; the prototype must expose how it mitigates the idle-check-to-launch race. If unsafe, skip/defer that operation rather than risking personal files.
- PinBot must never claim guaranteed isolation from process observation alone; no frontend integration means a launch race remains possible. Validate the acceptable risk with real VPX installations.

### Required failure tests

Original exists / absent; unsupported ROM reset; same ROM two table installations; different ROMs same cabinet; application killed after backup/before swap, during swap, during restore; power-off; file lock; concurrent table start; server unreachable; cabinet off for entire tournament; late join; two session exits in rapid succession; same ROM launched after deadline; corrupted journal; and inconsistent checksum.

---

## 7. Competition operations and Discord UX

**One Discord app per hosted PinBot installation**, not a bot per cabinet. For v0.1 one guild/community per deployment. Other communities can deploy their own PinBot Server. Don't build multi-tenant hosting now.

### First-run experience

1. Community administrator deploys the server, creates a Discord application, grants its required permissions, configures the Interactions Endpoint URL and token, and chooses the competition channel. This is **one-time infrastructure setup**, not repeated per cabinet.
2. Admin generates a **short-lived, single-use pairing link/code** through Discord; agent setup accepts the server URL and code (the link may carry both). There is no global PinBot service directory.
3. Agent registers a unique revocable cabinet credential and displays simple `Paired / Monitoring / Problem` status. No cabinet Discord token or router port forwarding.
4. Guild members retain their existing nickname tags. No separate player-registration command and no opting into individual competitions.

### Minimum v0.1 interactions

| Discord command (proposed names) | Behavior | Permission |
|---|---|---|
| `/pinbot start` | Search eligible game via autocomplete; choose duration; start a one-off immediately | **Any guild member** |
| `/pinbot status` | Active competitions and leaderboard links | Any guild member |
| `/pinbot end` | End an existing one-off early | Creator or moderator; proposed safe default |
| `/pinbot weekly` | Enable/disable weekly automatic scheduling, once | Guild admin |
| `/pinbot pair` | Generate cabinet pairing link | Guild admin/moderator; proposed safe default |

Commands are examples of final names, not a requirement for separate command prefixes. No web UI. Discord searchable autocomplete avoids full-library dropdown limits. Validate selected game **again on submission**, since another competition might have started during the menu interaction. Return a clear conflict rather than creating it.

**Competition creation:** Immutable ID; canonical game; creator; start/end timestamps; weekly vs one-off; active/no-conflict check. Allow simultaneous competitions for different games; reject same-game overlap. Store a separate edited leaderboard message per competition, not a flood of score posts.

**Weekly automation:** The same competition engine used for one-offs. A recurring scheduler selects a supported, installed, non-conflicting game and creates a timed competition without human action. A precise weekly start day/time/timezone is a one-time deployment configuration, **not yet chosen**. Skip cleanly if no eligible game exists.

**One-off guardrails — RECOMMENDED, not locked:** cap duration and/or number of concurrently created one-offs per user, so an open creation permission doesn't accidentally create a large number of NVRAM states. Favor minimal and easily explained rules. Do not add roles management or complicated permission screens.

**Discord engineering requirements:** Use signed HTTP interactions, verify Ed25519 signature and timestamp, respond/defer within Discord's required time limit, and use server-side idempotency on button/command retries. Guild member listing for nickname tags requires `GUILD_MEMBERS` privileged intent. Schedule a periodic member-map refresh; no long-lived Gateway client for v0.1. Keep Discord token and public key on server only. Never expose secret in logs or public channel. Respect Discord API rate limits.

---

## 8. Server state, scheduling, and score ranking

### Competition states

```text
Scheduled (if using weekly schedule) → Active → Finalizing → Complete
                                               ↳ cancelled / ended early
```

Keep state transitions deterministic and idempotent. `Finalizing` is the **offline delivery** period; the gameplay eligibility deadline is already closed. There is no paused tournament, extensions, bracket, manual approval queue, or per-member enrollment.

### Minimal data model (relational)

- `Cabinet(id, credential_hash, paired_at_utc, last_seen_at_utc, status)`
- `Installation(id, cabinet_id, table_path_fingerprint, canonical_game_id, rom_id, extractor_supported, last_reported_at_utc)`
- `MemberInitial(guild_id, discord_user_id, initials, observed_at_utc)` — derived cache from nicknames, with collision awareness
- `Competition(id, canonical_game_id, kind, created_by_discord_user_id, starts_at_utc, ends_at_utc, status, message_id, created_at_utc)`
- `Result(event_id UNIQUE, competition_id, cabinet_id, session_id, discord_user_id, initials, rom_id, score_integer, session_exited_at_utc, received_at_utc, parser_version)`
- `CabinetCompetition(cabinet_id, competition_id, rom_id, latest_state, last_contact_utc)` — optional lightweight server view; **local NVRAM journal remains agent-owned**
- `ServerConfig(guild_id, channel_id, weekly_enabled, schedule_config)` — minimal one-time configuration

Additional unique indexes: no duplicate event; no conflicting active competition per canonical game (enforce transactionally, not just Discord UI); credential/hash uniqueness. Don't store raw ROMs or NVRAM in the server.

Leaderboard query groups accepted results by `discord_user_id`, takes `MAX(score_integer)`, then ranks descending. Define ties consistently (e.g. same rank for tied scores; this is a reasonable default, not a locked product requirement).

### Proposed versioned API (implementation-level design, not frozen syntax)

```text
POST /api/v1/pair/redeem
PUT  /api/v1/agent/inventory
GET  /api/v1/agent/competitions
POST /api/v1/agent/results
POST /api/v1/agent/heartbeat      # only if needed
POST /api/v1/discord/interactions
POST /internal/tick               # authenticated, idempotent scheduler
GET  /healthz
```

Prefer straightforward periodic **pull** by agent over WebSocket subscriptions. Use JSON with explicit API version and strict input validation. Keep retry/backoff bounded and persistent. If the HTTP result is ambiguous, retry the same event ID until acknowledged, never invent a new one.

### Portable scheduler

Scheduled competition start/end/finalization should be implemented in an **idempotent server `Tick(nowUtc)` operation**. It can be called by an external host cron/HTTP trigger on scale-to-zero cloud hosting or by an ordinary host scheduler when self-hosting. No provider-specific scheduler inside the domain logic. Do not require a 24/7 server process merely to track deadlines.

### Security without building a security platform

- TLS for all agent/server communication and Discord endpoints.
- Short-lived one-use pairing code; durable per-cabinet revocable key/credential, stored securely and never in Discord public messages.
- Verify Discord signatures against the exact received raw request bytes.
- Server checks guild ID and command permissions, even if Discord UI disables a control.
- Rate limit pairing/one-off creation and validate bounded numeric scores and strings.
- Do not claim cryptographic anti-cheat: a trusted cabinet controls its local scores and timestamps.
- Minimal stored personal data: Discord member IDs/nicknames-as-needed, scores, cabinet IDs. No unnecessary profiling or telemetry.

---

## 9. Agent operation and recovery

**Startup:** read durable journal and pending results; discover VPX installation and current active process; reconcile local NVRAM ownership; synchronize known competitions; refresh table inventory if changed; enter quiet monitoring mode. Avoid modifying NV if a relevant ROM session is already active or context is uncertain.

**Session detection:** observe actual VPX process lifecycle rather than assuming VPinMAME is a separate executable. Capture executable/table/ROM relationship where possible; do not infer session end merely because a launcher/Steam wrapper exits. The first feasibility prototype should test the real cabinet's launch patterns. Avoid high-frequency CPU polling.

**Exit:** wait for the actual file to be stable and no writer to hold it; parse; compare against durable baseline; enqueue; mark exit time and snapshot; notify server opportunistically. Never delay returning to the frontend or game launch.

**Offline:** preserve local queue; keep playing; retry with backoff; never require server access for gameplay. Participation in *new* competitions may be delayed until the agent next receives them. Restore expired competition NVRAM safely using cached deadline/state when possible; reconcile with server later.

**Problems:** unsupported ROM, ambiguous mapping, NV file lock, backup checksum mismatch, Discord tag collision, score parser failure or offline server are explicit statuses. Prefer quietly skipping an unreliable score rather than publishing a fabricated one. Provide a small local status/log, not a full cabinet dashboard.

**Observability:** concise structured logs (session ID, ROM, comp ID, state transition, result event ID, error category), capped log retention, one diagnostic export if needed. No metrics backend or cloud telemetry.

---

## 10. Suggested repository scaffold

```text
pinbot/
├── PinBot.sln
├── README.md
├── src/
│   ├── PinBot.Contracts/      # shared DTOs/enums/validation
│   ├── PinBot.Agent/          # Windows background app
│   │   ├── Discovery/         # VPX paths, table/ROM identification
│   │   ├── Sessions/          # process observation and exit records
│   │   ├── Nvram/             # journal, backup, prep, restore
│   │   ├── Scoring/           # replaceable parser adapter
│   │   └── Sync/              # queue, pairing, HTTP
│   └── PinBot.Server/         # Minimal API, scoring, Discord, scheduling
│       ├── Competitions/
│       ├── Players/
│       ├── Discord/
│       └── Persistence/
├── tests/
│   ├── PinBot.Agent.Tests/
│   ├── PinBot.Server.Tests/
│   └── Fixtures/             # synthetic/sanitized .nv samples
├── deploy/
│   ├── Dockerfile
│   ├── docker-compose.yml    # PinBot server + Postgres
│   └── cloud-example/        # later, provider template only
└── docs/
    ├── SPEC.md
    ├── INSTALL.md
    ├── NVRAM-SAFETY.md
    └── OPERATIONS.md
```

Folder layout is **RECOMMENDED**, not a demand for needless subprojects. Keep contracts minimal and ensure the Agent remains usable entirely without the Server during M0.

### Suggested C# interface boundaries

```csharp
public interface ITableDiscovery
{
    Task<IReadOnlyList<TableInstallation>> ScanAsync(CancellationToken ct);
}

public interface ISessionObserver
{
    IAsyncEnumerable<VpxSessionEvent> ObserveAsync(CancellationToken ct);
}

public interface IHighScoreExtractor
{
    Task<ExtractionResult> ExtractAsync(string romId, string nvramPath, CancellationToken ct);
}

public interface INvramCoordinator
{
    Task<PrepareResult> PrepareAsync(CompetitionTarget target, CancellationToken ct);
    Task<RestoreResult> RestoreWhenSafeAsync(CompetitionTarget target, CancellationToken ct);
}
```

Use interfaces **only where they isolate real uncertain integration points**. No generalized plugin loader. For scoring, compare candidates before bundling: PINemHi, `superhac/pinmame-score-parser` (Python metadata-driven parser), and `francisdb/pinmame-nvram` (Rust/MIT). Check license, decoder coverage, confidence of initials parsing, packaging footprint, and test fixtures; choose the simplest viable integration. Do not ship external parsers or ROM data before license verification.

---

## 11. Milestones and mandatory gates

### M0 — READ-ONLY SCORE PROOF (first task; no network)

Build a single Windows executable that:
1. Discovers or accepts a one-time VPX/table folder location.
2. Watches actual VPX launches/exits; identifies `.vpx` table and ROM when reliably available.
3. Resolves the actual NV path; reads NVRAM **without modifying it**.
4. Decodes high-score entries for **three to five representative ROMs** using a tested candidate extractor.
5. Records normalized before/after records; prints exactly which score entries are new/changed, including initials and revision.
6. Outputs a concise diagnostic log and tests repeated exits, unsupported ROM, no file, file lock, and missing session identity.

**Go/no-go:** Correct, repeatable extraction from actual local cabinet sessions. If it fails, stop and reassess score collection. **Do not start Discord/backend yet.**

### M1 — SAFE COMPETITION NVRAM

Implement durable local journal, original backup/absence tracking, ROM-validated competition preparation, retained competition NVRAM, idle-only best-effort restoration, backup checksums, crash recovery. Add deliberate fault injection between file/journal transitions. **Gate:** demonstrated original restoration byte-for-byte or non-destructive flagged recovery under all tested failure modes.

### M2 — PORTABLE BACKEND

Implement minimal server + PostgreSQL, pairing, inventory, competition storage/conflicts, result ingestion, idempotency, UTC deadline semantics, best-per-player ranking. Test with simulated two-cabinet inputs and an offline delivery queue. **Gate:** retries, restarts, duplicate events, variant identities, and conflicting competitions are correct.

### M3 — DISCORD INTERFACE

Implement HTTP interactions/signature verification, guild nickname member synchronization, game autocomplete, `/pinbot start`, `/pinbot status`, `/pinbot end`, pairing, editable leaderboard, weekly rotation. **Gate:** organizer starts one-off in Discord; multiple tagged players on one cabinet automatically appear after scores are submitted; no account setup/opt-in per competition.

### M4 — REAL-WORLD BETA

At least two cabinets, shared and different tables, several Discord-tagged members, late table installation, one-off + weekly different games, internet outage, VPX crash, score extraction error, deadline crossed while game runs, restoration after expiration, system reboot, legacy NVRAM originally absent.

### M5 — PACKAGE & FREEZE

One agent installer or standalone distribution, one Docker image, a Compose sample including Postgres, a cloud deployment template if practical, concise recovery instructions, and a tagged release. **Then deliberately stop feature expansion.** No requirement to operate PinBot as a public hosted service.

---

## 12. Acceptance criteria for v0.1

- [ ] Works with VPX launched directly, without PinUP Popper, vPinOps, VPinFE, or frontend scripting.
- [ ] Detects supported installed ROM-based tables automatically and synchronizes eligible inventory.
- [ ] A table installed mid-competition can join without player or operator opt-in.
- [ ] On VPX exit before deadline, newly qualifying high-score entries can be extracted and attributed correctly.
- [ ] VPX exit at/after deadline produces **no accepted results**, even if game began earlier.
- [ ] Scores that never enter supported ROM high-score records are **not promised**.
- [ ] Multiple players on one cabinet are separated through Discord tags; the same Discord user can score from another cabinet.
- [ ] Unknown/duplicate initials are ignored, never guessed.
- [ ] Replays, retries and restarts do not create duplicate accepted submissions.
- [ ] Results are ranked by **best score per Discord member**.
- [ ] Related validated ROM revisions share one canonical game competition; same-game overlap is rejected.
- [ ] Any server member can start a one-off from Discord; weekly rotation runs without regular administrator involvement.
- [ ] Competition NVRAM persists between VPX sessions; original data is safely restored after it ends.
- [ ] Post-deadline relaunches are **allowed**; no gameplay blocking, automatic termination, or frontend dependency.
- [ ] Agent/server outage doesn't stop pinball; pending results and recovery state survive process restart.
- [ ] Server runs as one portable application on self-hosted infrastructure or a cloud container; no provider lock-in in domain logic.
- [ ] No general dashboard, per-cabinet Discord bots, manual library editing, player-registration workflow, telemetry SaaS, or unnecessary plugins.

---

## 13. Known risks / explicitly unresolved questions

These are **technical experiments or deployment details**, not invitations to expand product scope:

1. **Actual table → ROM discovery:** Script-based `cGameName` references can be dynamic or stored in VPX containers. M0 must establish reliable discovery on real installations, with conservative unknown handling.
2. **Real NV location and write lifecycle:** Resolve installation-specific paths and observe when data is safe to read. Process exit alone isn't proof that an arbitrary path is idle.
3. **Score extractor choice and redistribution:** Parser coverage, initials support, and licenses require validation; don't assume 247 parser mappings mean 247 fully verified competition-ready machines.
4. **Safe NVRAM initialization:** Per-ROM approach must be verified; blanket reset/delete can affect settings or unsupported machine state.
5. **Race between idle check and game launch:** Cannot guarantee perfect safety without being a launcher; user accepts best-effort restoration and no blocking. Favor non-destructive skip/defer over complex interception.
6. **Discord full membership access:** Guild Members intent must be configured and may be subject to Discord approval requirements at public scale. Refresh mappings periodically via REST; avoid Gateway unless evidence forces it.
7. **Cloud free tier:** Select after server exists. Hosting fees, limits, scheduled invocations and sleeping cold-start behavior may change. Server portability is mandatory; *free forever* is not a guarantee.
8. **One-time defaults to choose at deployment:** weekly tournament timezone/start time; offline delivery grace (24h recommended); one-off duration choices/caps; log retention. Do **not** turn these into complicated per-user settings.
9. **Trust model:** No proof of game settings equality or tamper resistance. Private, friendly scoring is intentional.

---

## 14. Implementation-session kickoff prompt (copy/paste)

> We are implementing **PinBot**, a minimal frontend-independent VPX/VPinMAME virtual pinball competition system. Read the attached **PinBot v0.1 Implementation Specification & Handoff** and follow its locked scope and principles: **simplicity, reliability, elegance, minimalism**. Start with **M0 only**: scaffold a small .NET Windows console/worker prototype that can discover VPX sessions and their `.vpx`/ROM/NVRAM association, read supported high-score records from actual NVRAM **without writing anything**, and compare normalized high-score records before and after VPX exits. Include test fixtures and basic diagnostics. Explore existing parsers before choosing one, explicitly report feasibility gaps or unsupported ROMs, and do not scaffold Discord, hosting, NVRAM resets, a web dashboard, or generalized plugins yet. Confirm each milestone gate before moving to the next. Treat product choices marked RECOMMENDED/UNRESOLVED as proposals, not already proven facts.

---

## 15. Engineering references (verify at implementation time)

- Discord application commands and autocomplete: https://docs.discord.com/developers/docs/interactions/slash-commands
- Discord HTTP interactions, timing, signature verification: https://docs.discord.com/developers/interactions/receiving-and-responding
- Guild member listing and privileged intent: https://docs.discord.com/developers/resources/guild#list-guild-members
- VPin Studio competition behavior: https://github.com/syd711/vpin-studio/wiki/Competitions
- PINemHi high-score extraction: https://www.pinemhi.com/about.php
- Python metadata-driven parser: https://github.com/superhac/pinmame-score-parser
- Rust NVRAM parser: https://github.com/francisdb/pinmame-nvram
- Pinball memory maps: https://github.com/tomlogic/pinball-memory-maps

**Final principle:** *Automate the routine. Protect the important. Tolerate the harmless.*
