# NVRAM safety

PinBot changes a table's NVRAM only for a competition, and only with this procedure. Code:
`src/PinBot.Agent/Nvram/NvramCoordinator.cs` (lifecycle) and `src/PinBot.Agent/Scoring/CompetitionImage.cs`
(competition file).

## Lifecycle

```text
Preparing --> BackedUp --> Ready --> PendingRestore --> Restoring --> Restored
    |             |                                         |
    +-> Abandoned (live file untouched; retried)            +-> NeedsRecovery (both copies kept)
Refused: this ROM cannot get a verified competition file (nothing touched)
```

Each competition gets its own journal folder in `%LOCALAPPDATA%\PinBot\nvram\<time>-<competition>-<rom>-<id>\`. It holds:
- `entry.json`: state, hashes and history
- `original.nv`: the read-only backup
- `competition-final.nv`: the competition's last state, kept for reference

## Rules

1. **Journal before change.** Every step is recorded (temp file, flushed to disk, then renamed) before the live file changes.
2. **Verified backup first.** The player's `.nv` is copied, re-read, compared by SHA-256, and marked read-only. A backup is never overwritten or deleted by PinBot.
3. **Whole-file swaps only.** A new live file is written to `<rom>.nv.pinbot-tmp` in the same folder, flushed, verified, then renamed over `<rom>.nv`. At every instant the live file is one complete file: the original or the competition file.
4. **Never a file in use.** A competition file is prepared or restored only while no running table can be using it: each running VPX uses only its own ROM's `.nv`, so other files are handled during play (a competition started in Discord is ready within seconds even while another table runs). A VPX whose table or ROM is unknown, or one PinBot has not looked at yet, keeps every file off limits, and after a table closes its scores are left to settle for 3 s more. Windows is asked afresh immediately before each rename. PinMAME loads NVRAM seconds after VPX starts, while the check-to-rename gap is milliseconds, so a launch cannot interleave. If the table starts, the temp file is deleted and the step retried after it closes. PinBot never blocks or ends a game.
5. **Restore byte-for-byte.** At the end, the backup is re-verified against its journaled hash, written back the same way, and the live file's hash is checked against it. A backup that doesn't verify stops everything (NeedsRecovery) and nothing is overwritten.
6. **One owner per file.** A second competition on the same `.nv` waits until the first is restored, so its backup is the player's own file, never another competition's.
7. **Absent original: not prepared.** A ROM with no `.nv` yet has nothing to protect and no verified base for a competition file. Play it once first.
8. **Single process.** `watch` and `uninstall` take a per-journal lock. A busy lock skips the pass, and the next tick retries.

## The competition file

It is the player's own `.nv` with only the main high-score values replaced by a nonzero descending ladder, e.g. 50,000 / 40,000 / … / 10,000:
- Checksums the map defines over changed bytes are recomputed.
- The result is decoded and must show the ladder, the same initials, and unchanged Buy-In and mode-champion entries.
- Only score and checksum bytes may differ from the original.

Settings, audits and everything else stay as they were. The file is kept for the whole competition, with no reset between games.

A ladder rather than zeros: on a real VPin, an all-zero Baywatch table asked for initials 6 times in one game; the ladder asked once. Coverage: a verified image builds for all 316 reference ROMs that store initials. ROMs without initials are refused.

## Recovery after a crash or power loss

On startup and every tick, unfinished journal entries are reconciled from the live file's content.

| Interrupted in | Live file is | Result |
|---|---|---|
| Preparing | untouched | Abandoned; prepared again |
| BackedUp | the original (possibly played since) | swap retried; a changed file restarts preparation from it |
| BackedUp | the competition file (possibly played since) | Ready: the swap had completed |
| BackedUp | neither, by hash and content | **NeedsRecovery** |
| Restoring | the original | Restored |
| Restoring | anything else, with the backup verified | restore retried |
| any | backup missing or changed | **NeedsRecovery**; nothing overwritten |
| — | `entry.json` unreadable | **NeedsRecovery**; that folder is left alone |

"By content" means: a competition file's main table holds ladder values, while the player's file holds their own (initials, score) pairs.

Every row is covered by tests, including a simulated crash at each of 8 steps and a tampered backup (`tests/PinBot.Agent.Tests/CoordinatorTests.cs`).

## Manual recovery

`pinbot status` shows any `NEEDS RECOVERY` entry with its folder. `original.nv` in that folder is the verified original; `entry.json` records its SHA-256 as `OriginalSha256`. With no table running, copy it over the live `.nv` to restore the player's file by hand.

## Known limits

- Detection relies on the process name `VPinball*`. Another emulator writing the same `.nv` would not be seen.
- Changes to settings or audits made during a competition are not merged back. The original is restored as it was.
- VPin Studio competitions must be switched off on the same VPin; its resets would fight PinBot's.

## Platform rules

Some ROMs check their high-score table and reset it to factory values when it looks wrong. Each rule below was found
on a real VPin, and the competition file follows it:

- **Stern SAM:** every high-score entry carries a checksum16 over its bytes from the initials to the next entry, less
  2 bytes, stored in the last two (0xFFFF minus the byte sum, little-endian). It holds on all 21 SAM maps across 120
  real NVRAM files (545 entries). PinBot derives it for the maps that list none (AC/DC, Avengers, Batman Dark Knight,
  Metallica 1.80h, Spider-Man, Transformers, TRON, Walking Dead).
- **Capcom:** the 77-byte high-score block (initials and scores) is kept twice more at ROM-specific places. PinBot finds
  both copies in the player's file and writes the same bytes into them.
- **Gottlieb System 3 / Premier** (Street Fighter II, Super Mario Bros, Class of 1812, Rescue 911, Wipe Out): the
  table is kept three times, 128 or 256 bytes apart, and all three are written.
- **Gottlieb System 80B:** each score has a 4-bit checksum (the low-nibble sum of its initials and score cells, mod
  16), and the top entry is copied into the data at offsets 0, 59 and 118. PinBot writes both. Only the "8-digit A"
  and "8-digit B" layouts (Genesis, Hollywood Heat, Monte Carlo, …) are offered; other 80B layouts are not.
- **Williams System 11** (Dr. Dude, Whirlwind): the ROM raises a laddered Champion, so the Champion stays the
  player's own and the ladder fills the places below it.
- **Data East:** Hook 5.01 and Star Wars 1.07 reset with other undocumented bytes changed, so they are not offered.
- **Sega Whitestar:** long names (South Park, 10 characters) end at a `[` marker after a repeat of the last letter
  (`ABCC[[[[[[` is ABC).

Backup copies are looked for on every platform. A file whose copies cannot be found, or whose checksums do not follow
the rule, gets no competition file.

## A ROM that rejects its competition file

A game that started on a competition file and ended with every previous high score gone means the ROM rejected that
file. The VPin then:

- keeps the evidence in `%LOCALAPPDATA%\PinBot\diagnostics\<rom>-<time>\`: `competition-file.nv` (what the ROM
  loaded), `after-game.nv` (what it left) and `original.nv` (the player's own file);
- records the ROM as rejected in `state.json` and drops its competitions, so the player's own file is restored once no
  table is running (even with the server unreachable);
- re-sends its inventory at once with that table not eligible ("this ROM reset its high scores on a competition file").
  The server stops sending that game's competitions there, the pick list no longer counts the VPin for it, and running
  and future cards for the game show **⚠️ Can't run on <VPin>** with that reason.

`pinbot status` lists rejected ROMs with their evidence folders. A rejection is lifted only by a newer release that
lists the ROM in `RejectedRom.FixedInThisRelease` (`src/PinBot.Agent/LocalState.cs`) alongside its fix; should the fix
not hold, the ROM is rejected again after one game. The game that showed the reset never counts (a reset never
qualifies), and other VPins are unaffected.

## Emulator self-test

`tools/PinBot.SelfTest` (`pinbot-selftest.exe`, run on a VPin with VPX closed) boots every competition-eligible ROM
in PinMAME (libpinmame, no VPX) three times on scratch copies: the player's own file (control), a naive ladder with no
checksum or copy fixes, and PinBot's competition file. Each run powers on, waits, inserts coins, presses Start and waits
again. PASS = the ROM reset the naive file (so it checks its scores) and kept PinBot's; OK = it kept both (play proves
it); FAIL = it reset PinBot's file (the files are kept). The VPin's real NVRAM, ROMs and settings are only read.

On 100 ROMs the first run gave 51 PASS, 40 OK and 9 FAIL; the platform rules above cover all nine.
