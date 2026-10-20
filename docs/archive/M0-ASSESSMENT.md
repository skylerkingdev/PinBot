# M0 assessment — read-only score proof

Status 2026-10-12: **go/no-go gate passed on the real cabinet.**

**M1 (competition NVRAM): gate passed on the real cabinet (Baywatch and Attack from Mars).**

**M2 (portable backend): gate passed on the laptop.** See [SERVER.md](../SERVER.md).
- **28 tests against real PostgreSQL**, each on a throwaway database:
  - single-use and expiring pairing codes, revoked cabinets
  - 12 concurrent starts for one game create exactly one competition
  - overlapping competitions are rejected; different games coexist
  - variant identities: ROM revisions are grouped only when curated, and each revision maps to its own NVRAM file
  - deadline judged by session exit, with the delivery window for late uploads
  - duplicate, concurrent and lost-acknowledgement retries never double-count; a reused id is refused
  - unknown and ambiguous initials are stored but never counted
  - best per member across initials and two cabinets, with shared tie ranks
  - accepted scores keep their member after a nickname change
  - everything survives a server restart
- **Real-process rehearsal** (server + PostgreSQL + agent over HTTP on the fake cabinet):
  - pair → inventory → server competition reaches the agent → NVRAM prepared
  - game → queued on disk → uploaded → accepted → leaderboard #1
  - competition ended on the server → agent restored the original byte-for-byte
- **Fixed:** concurrent starts could abort one request with a PostgreSQL deadlock; creation is now serialized per game.
- Design and recovery table: [NVRAM-SAFETY.md](../NVRAM-SAFETY.md).
- Verified competition images build for all 316 reference ROMs that store initials.
- A simulated crash at each of 8 steps always ends with the original restored byte-for-byte. Corrupted journals, tampered backups, locked files, games starting mid-step, overlapping competitions on one file, late join, early end and cancellation each either recover or stop with both copies kept.
- A rehearsal with the real exe ran the whole lifecycle: prepare, a game played during the competition (reported as COUNTS), end, idle restore. The restored file's hash matched the original's.
- **Cabinet run 1 (Baywatch, Sega, no checksums), 2026-10-12: passed.**

  | Step | Result |
  |---|---|
  | Prepare | Backup verified (8,238 bytes); ladder 60,000 → 10,000 in place |
  | Session 1 (VPX opened directly, table unknown) | Two new scores |
  | Session 2 (via Popper) | Started from session 1's state; SKY 573,319,220 reported as `COUNTS` |
  | End | Idle restore |
  | Check | The next session's starting hash equalled the owner's manual backup (`7bf35b8a…`): byte-for-byte |

  Fixed from this run: a session launched without a `.vpx` on the command line was wrongly disqualified. The NVRAM baseline is what makes a score trustworthy, not the table name.

  Baywatch initials-screen quirk, **confirmed to be ROM behavior** (photographed): the mini-leaderboard column (positions 1–6) blanks when the second letter is typed.
  - It happens when the initials being typed match an entry the player already holds more than once.
  - It is drawn by the ROM from its entry working copy at `0x06E0`–`0x0752`.
  - **Controlled test:** a normal, non-competition file differing from a "no blanking" file only in #2's initials (JEK → SKY, 3 bytes) reproduced it. PinBot is not involved.
  - Scores and initials were stored and read correctly every time.
  - The ROM also keeps copies of the newest top score at `0x16D5` and `0x16F3`. It rewrites both at every game end, so the competition file need not touch them.

  Possible later tweak: seed the ladder with neutral placeholder initials instead of the previous holders'. That would widen the competition file's writes to the initials bytes, so only do it if players find the starting table confusing.
- **Cabinet run 2 (Attack from Mars, WPC, checksums over the high-score table), 2026-10-12: passed.**
  - The ROM accepted the recomputed checksums: attract mode showed the ladder under the existing initials, with no factory reset.
  - A real game set GC SKY 2,150,328,210 on the ladder table; the report showed `COUNTS`.
  - After the idle restore, `fc /b` against the owner's manual backup reported no differences.
  - Two earlier sessions quit before game over and correctly reported nothing new.
  - Lesson: the restore happens up to ~25 s after `comp end` (15 s idle + tick). Comparing earlier shows the competition file, not a failure.

**M1 gate passed:** original restored byte-for-byte on real cabinet NVRAM, with and without checksums. Every fault-injection case also restores or stops with both copies kept.

## Cabinet results (2026-10-12)

**Setup**
- VPX: `C:\vPinball\VisualPinball`, launched normally.
- VPinMAME: found from its COM registration; NVRAM path from its `nvram_directory` setting.
- `VPMAlias.txt`: 56 aliases.

**`scan`**
- 172 tables, of which 100 eligible after the two rule fixes below.
- No ambiguous NVRAM paths.
- Ambiguous script ROMs, flagged rather than guessed: Twilight Zone (3 ROMs) and Haunted House (2).
- Unsupported, correctly: EM tables, original tables, and 7 unmapped revisions (e.g. `im_185ve`, `potc_600as`).

**`watch`** (7 sessions, 6 machines, 5 platforms, every report matching what was played)

| Table | Platform | Result |
|---|---|---|
| Attack from Mars | WPC | New 2nd place SKY 577,697,520; the 3 shifted slots were not counted as new |
| Attack from Mars (again) | WPC | New 4th place, blank initials, as the player actually entered them (bytes `20 20 20`) |
| Metallica | Stern SAM | Quit early: `.nv` rewritten, correctly nothing new |
| Lethal Weapon 3 | Data East | No high score: correctly nothing new |
| Whirlwind | Williams System 11 | New 4th place SKY 5,355,250 |
| Xenon | Bally AS-2518-35 | New high score 122,830; this ROM stores no initials |
| Twilight Zone | WPC | Script lists 3 ROMs; the session showed it writes `tz_94h.nv`, and that was learned |

Process observation, waiting for the file to settle, learning associations and the read-only guarantee all behaved as designed.

**Fixes from the cabinet test** (now in the build)
- **Eligibility needs evidence:** either an existing `.nv` file or an observed session. Without this, an EM table whose name happened to match a real ROM (`matahari`) showed as eligible.
- **ROMs that store no initials are listed but ineligible.** That is 20 of the 120 tables (pre-1983 Bally, Williams and Stern, e.g. Xenon, Eight Ball Deluxe, Gorgar). Their scores can never be attributed. `watch` labels such scores "VALID BUT UNATTRIBUTABLE".
- **Blank initials:** players can deliberately enter blanks. Such scores are read correctly and never counted. `read` shows the raw bytes for blank-looking initials.

## What was verified

| Check | Evidence |
|---|---|
| Decoder correct on real NVRAM | 516 reference `.nv` files (all ~500 ROMs in francisdb/pinmame-nvram) decode to the same 2,440 high-score/mode-champion entries, and the same set of failing checksums, as an independent implementation. 17 of them are committed as fixtures across 9 platforms (WPC, Stern SAM and Whitestar, Data East, Williams System 6/7/11, Gottlieb System 80B, Bally AS-2518-35). Includes both ROMs found on this laptop: `avs_170c`, `flash_l1`. |
| New-entry detection | Tests on real `afm_113b` NVRAM: insertion that shifts slots → exactly one new entry; two games in one session; a player beating their own entry; mode champion changes reported but not qualifying; wholesale replacement flagged as a reset. |
| Failure handling | Unsupported ROM, missing file, exclusively locked file, truncated file, checksum mismatch over score bytes, NVRAM created during the session, ROM different from what the script claimed, ambiguous NVRAM location, ambiguous script ROM, no ROM, missing session identity. |
| Never modifies NVRAM | Reads share Read/Write/Delete so PinMAME can always write; a test holds a writer open during reads and verifies bytes and mtime are unchanged. No write path to `.nv` files exists in the agent. |
| Process observation | End-to-end on this laptop with a fake `VPinballX64.exe` (renamed `cmd.exe`) carrying a real `-play "...vpx"` command line and a simulated PinMAME write: correct table/ROM/NVRAM, QUALIFIED report, association learned, ambiguous NVRAM path resolved by observation, repeated sessions report only their own scores, no-game sessions report no change, and both "VPX already running at agent start" and "two VPX processes at once" are reported NOT QUALIFIED. |

59 automated tests pass in about 1 s.

## Extraction approach and licensing

| Candidate | Verdict |
|---|---|
| **tomlogic/pinball-memory-maps** (formerly pinmame-nvram-maps) | **Chosen.** JSON maps for 1,530 ROM names (391 maps), with high-score initials. ODbL 1.0 / DbCL 1.0 since Aug 2026, which permits use with attribution, including closed-source use. Share-alike applies only to modified maps; we use them unmodified as a pinned git submodule (`68c1b33`). |
| francisdb/pinmame-nvram (Rust, MIT) | Reference implementation and source of MIT test fixtures. Not embedded: calling Rust from .NET adds a native build for ~400 lines of decoding. |
| superhac/pinmame-score-parser (Python) | **Rejected:** no license (all rights reserved), and it would need a Python runtime. |
| PINemHi | **Rejected:** closed-source freeware with no stated license; it could only be run as an external process. |

The C# decoder (`Scoring/MemoryMaps.cs`, about 400 lines) implements only what score extraction needs:
- `high_scores` and `mode_champions`
- `bcd`/`int`/`ch` encodings, nibble/endian rules, `scale`/`offset`/`special_values`, `char_map`
- CPU-address → file-offset translation via platform files
- checksum8/16 verification

Attribution is in `NOTICE.md`, which ships next to the exe. The project's own license is still undecided.

## Open blockers and risks (need the real cabinet)

1. **Gate not yet run.** This laptop has no VPX/VPinMAME, only a VPin Studio client, so no real session has been observed.
2. **Where VPinMAME actually writes NVRAM.** Watched folders:
   - VPinMAME's registered folder and its `nvram_directory` registry setting
   - `<VPX>\VPinMAME\nvram` and `<VPX>\PinMAME\nvram`
   - per-table `pinmame\nvram` folders (VPX 10.8 folder-per-table layout)

   If a real session shows "no NVRAM file changed" after a finished game, the cabinet writes somewhere else. Send that log.
3. **NVRAM write timing.** PinMAME is expected to write NVRAM when emulation stops, before VPX exits. The agent waits for the folders to be quiet for 2 s (maximum 30 s). This must be confirmed on the cabinet.
4. **Elevation.** If VPX runs as administrator and PinBot does not, the VPX command line can't be read: scores are still found, but the session is flagged "missing session identity". Run both the same way.
5. **ROM identity from scripts is a hint only.** Scripts can pick ROMs dynamically. The table→ROM association is trusted only after a session is observed writing exactly one `.nv` file.
6. **A map existing ≠ ROM verified.** Coverage is broad, but competition eligibility should require at least one observed, correct extraction on a cabinet (see recommendation A).
7. **Secondary score tables are excluded (decided 2026-10-11).** A survey of every map's `high_scores` labels found that only Buy-In tables are secondary leaderboards (27 maps, e.g. AFM, TZ 9.4H, Twister). They are decoded and shown, but never qualify. Themed labels are main lists and still count: "Admiral", "#1 Jedi", the Simpsons' "Under-Achievers", Transformers' Autobot/Decepticon.
8. **Reset detection is a heuristic.** "Every previous high score vanished" is treated as a re-initialized NVRAM, and nothing qualifies. Re-entering exactly the same initials *and* score as an existing entry cannot be told apart from no change (practically never happens).
9. **Package size.** The self-contained exe is 68 MB (30 MB zipped). A framework-dependent build is about 2 MB if the cabinet has the .NET 8 runtime; decide at M5.

## Cabinet test (go/no-go)

1. Extract `pinbot-m0.zip`, run `pinbot scan`, and keep the output.
2. Run `pinbot watch`, then pick 3–5 supported tables from different eras and, for each:
   - launch and quit without finishing a game → expect "no NVRAM file changed";
   - play until you get a high score, enter initials, quit → expect `NEW <initials> <score> QUALIFIED` matching what the machine showed (take a photo of the in-game high-score screen);
   - repeat once → only the new score is reported.

   Launch at least one table from your usual frontend and one directly.
3. Send back `%LOCALAPPDATA%\PinBot\logs\*.log`, `sessions.jsonl`, `state.json`, the scan output and photos. Optionally include the played ROMs' `.nv` files: they contain only scores, settings and audits, and become regression fixtures.

**Pass** = every reported entry matches the machine; no false NEW entries; consistent across repeats.

## How VPin Studio does it (MIT; source reviewed 2026-10-11)

| Topic | VPin Studio | PinBot |
|---|---|---|
| Score reading | PINemHi (external exe), then tomlogic maps, then superhac's parser | tomlogic maps only, golden-tested |
| Secondary lists | Drops labels containing "BUY-IN"/"BUYIN" | Same rule (`\bbuy[\s-]?in\b`) |
| Table → ROM | Frontend DB field, else a script regex over raw `.vpx` bytes (heuristic, no OLE parsing) | OLE `GameData`/`CODE` or sidecar `.vbs`, confirmed by observing a session |
| ROM aliases | Resolves `VPMAlias.txt` (`alias,…,realrom`); NVRAM uses the real ROM name | **Adopted** |
| NVOffset | Tables sharing a ROM use `<rom> vN.nv` | **Adopted** (`NVOffset (n)` in script) |
| NVRAM folder | `nvram_directory` registry value, else `<VPinMAME>\nvram`; `<table>\pinmame\nvram` on standalone | Same, plus any of these that exist |
| Game end | Frontend exit hooks (curl calls injected into Popper scripts), plus an nvram file watcher held back while an emulator process runs | Process observation only; no frontend dependency |
| Competition reset | Back up to `.hsbckp`, then **delete** the `.nv` and copy in a pre-reset file from `syd711/nvrams` (MIT, 348 ROMs) | Not built (M1) |
| Restore after competition | **None automatic**: manual restore from backup | Required by our spec (M1) |

Lessons adopted:
- Tables without `Controller.Stop` may never save NVRAM, so `watch` now warns when a session changes nothing and the script lacks it.
- Read only after the emulator exits.
- Resolve aliases and NVOffset before choosing the `.nv` file.

Pre-reset files carry settings such as volume (vpin-studio#373), and deleting the `.nv` loses operator settings. Both support recommendation B.

**PinBot replaces only the local Discord competition function (decided 2026-10-11).** VPin Studio is a catch-all; PinBot is reliable because it does one thing. Other tools stay for everything else, but their competition features are switched off, so there is no coexistence logic. Migration needs to cover:
- Before the first PinBot competition, end any VPin Studio competitions, restore personal NVRAM from its `resources\backups\<rom>\*.hsbckp` (it never restores automatically), and turn off its competition features.
- M1 should warn when it is about to prepare a ROM that has VPin Studio `.hsbckp` backups. The live `.nv` may then be competition state rather than the player's personal scores, and must not be treated as "original" without confirmation.
- Scope stays as locked in the spec. iScored, subscription competitions, EM/VPReg/Future Pinball scores are not carried over.

## Recommendations (design changes for M1+)

- **A. Evidence-based eligibility (proposed).** Before M1 touches a table's NVRAM, it must hold one of these:
  - *observed*: a session was seen writing that exact `<rom>.nv`; or
  - *corroborated*: the script names exactly one ROM, and exactly one `<rom>.nv` exists in the watched folders.

  Most tables people actually play already have a `.nv` file, so they qualify without a launch. A single launch is needed only for:
  - tables never played (no `.nv` yet);
  - scripts that name several ROMs, or none;
  - a `.nv` present in several folders.

  Inventory reports those as "play once to enable". Late join works the same way: once the first play is observed, the table joins the running competition.
- **B. Simpler, safer competition NVRAM (M1).** Instead of a ROM-specific factory reset, build the competition file as a copy of the original with only the mapped high-score bytes cleared, then recompute the map's checksums. Settings, adjustments and audits stay identical, and the same maps and golden checksums validate the result. Still validate per ROM on the cabinet: a few ROMs may restore defaults if scores look invalid.
  **Do not zero the scores.** On the owner's cabinet, after a VPin Studio reset that sets all six Baywatch scores to 0, the first game asks for initials 6 times in a row before game over. **Confirmed on the cabinet (2026-10-12):** seed the cleared table with a small nonzero descending ladder instead.

| Starting table (same file, only the 6 score values differ) | Result after one game |
|---|---|
| VPin Studio reset: all scores 0 | 6 initials prompts; the empty slots end up `AAA 0` |
| Ladder 60,000 down to 10,000 | 1 prompt; the score is inserted at #1 and the ladder shifts down |

With the ladder, PinBot reported exactly one new entry. Other ROMs show the same zero-reset behaviour, so the ladder is the default for all competition files. Each ROM is still validated before use.
- **C. Keep exit-only capture.** NVRAM is written at emulation stop, so polling during play would add nothing.
- **D. Restoration race (M1).** The agent can detect whether any process holds the `.nv` file (`FileLocked` today) in addition to process checks. Use both, and defer on any doubt.

## Deployment (2026-10-12)

The server runs on the owner's Synology DS220+ (DSM 7.4.1, Container Manager) from `deploy/docker-compose.yml`:
- containers: server, PostgreSQL 16, Tailscale Funnel, nightly `pg_dump`;
- public HTTPS through Funnel; no router ports opened;
- NAS set up for 24/7 service: auto-restart after a power loss, no HDD hibernation.

Verified from outside the tailnet:

| Check | Result |
|---|---|
| `/healthz` | `ok` with a valid TLS certificate |
| Agent API without a credential | 401 |
| Pairing endpoint | reachable |
| Admin API from the internet | 404 (not routed by Funnel; LAN only on `:5080`) |

Two Synology-specific notes are in [DEPLOY-NAS.md](../DEPLOY-NAS.md): Synology does not create missing bind-mount folders, and `.env` must use LF line endings (written by `make-env.ps1`).

### First real competition through the deployed server (2026-10-12)

| Step | Result |
|---|---|
| Pair | The VPin redeemed a code over the public Funnel address |
| Inventory | 100 eligible tables sent to the NAS |
| Server start | Competition 1 (Attack from Mars, 2 h) reached the cabinet on its next sync; NVRAM backed up and the ladder put in place |
| Game | A real game set GC **SKY 1,033,239,240**: reported `COUNTS`, written to the outbox, uploaded, **accepted** |
| Leaderboard | NAS shows rank 1 for the tagged member |
| End | Ended on the server: the cabinet saw `finalizing` on its next sync and restored the original NVRAM byte-for-byte |

M2 is proven on the owner's real cabinet and NAS.

### M3: Discord on the deployed server (2026-10-12)

| Step | Result |
|---|---|
| Interactions endpoint | Discord accepted `https://pinbot.<your-tailnet>.ts.net/api/v1/discord/interactions` (signed PING); unsigned requests get 401 |
| `/pinbot status` | Lists running competitions and leaders (only the caller sees it) |
| Leaderboard cards | Posted by the bot in `#pinbot-challenges` (one-offs) and `#pinbot-weekly` (weekly), edited as results arrive |
| `/pinbot weekly` | First set on a Monday afternoon (Monday 19:00 Eastern): competition 2 (Jokerz!) started immediately and runs to the following Monday's slot |
| Real game | **SKY 1,957,310** on Jokerz!: `COUNTS`, uploaded, **accepted**, credited to the member tagged `Alex \| [SKY]` through member sync, and shown on the weekly card |

Deployment lesson: Container Manager's **Build** reused the existing `pinbot-server:local` image instead of compiling the new source, so the first M3 upload kept running M2 code. The compose file now sets `pull_policy: build`; a quick check that new code is live is `GET /api/v1/discord/interactions`: 405 means the M3 server is running, 404 means the old one.

M3 is proven on the owner's real cabinet, NAS and Discord server.

### Platform validation on the owner's cabinet (2026-10-12/13)

One competition per memory layout in the community's game list; each first game ran on the competition ladder,
counted and was accepted.

| Layout | Proven on | Fix needed first |
|---|---|---|
| Williams WPC (8K / 12K) | Attack from Mars, Twilight Zone, Cirqus Voltaire | none |
| Williams System 11 (48K / early 40K) | Jokerz!, High Speed | none |
| Data East v2 / v3 / v3b | Time Machine, Lethal Weapon 3, Baywatch | none |
| Stern SAM, 36-byte entries | AC/DC | per-entry checksums missing from the map: derived (verified on 545 entries) |
| Stern SAM, 32-byte entries, derived checksums | Transformers (both leaderboards) | same |
| Stern SAM, documented checksums | Star Trek | none |
| Stern-era Whitestar | Lord of the Rings | none |
| Sega-era Whitestar | Starship Troopers | none |
| Gottlieb System 3 | Street Fighter II | none |
| Capcom | Big Bang Bar | two backup copies of the high-score table: written too |
| Gottlieb System 80B (8-digit A/B) | Genesis | per-score nibble checksums and the triplicated top score: derived from the cabinet's own files |

Cirqus Voltaire "never asked for initials" because no game reached its 30,000,000 factory 5th place; with the ladder
it asks like any other. Signed auto-update proven: the cabinet moved between releases by itself after NAS-only installs.
