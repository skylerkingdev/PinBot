# On the VPin

## Install

In Discord, run `/pinbot pair`. Only you see the reply: a download link for `pinbot.exe` and a one-time line like
`https://pinbot.example.ts.net/pair/ABCDE-FGHJK`. Run `pinbot.exe` on the VPin (Windows may say *unknown publisher*:
**More info → Run anyway**), paste the line and press Enter.

PinBot installs itself in `%LOCALAPPDATA%\PinBot`, starts minimized, and starts whenever you log in to Windows.
Open **PinBot** from the Start menu to see its status.

## What it does

- After each VPX session it reads the ROM's high scores and sends new ones to your server.
- When a competition starts, it prepares that game's NVRAM the next time no table is running: your original is
  backed up and verified, and the competition starts from a low score ladder. At the end your original is restored
  byte-for-byte. Details: [NVRAM-SAFETY.md](NVRAM-SAFETY.md).
- It updates itself from your server: signed releases only, and only when no table is running.
- If it crashes it restarts itself (up to 3 times in 10 minutes).

It stays out of the way: below-normal priority in efficiency mode, about 15–20 MB of memory, asleep while a table runs.
When idle it checks for VPX every 5 seconds and keeps one quiet connection to the server. New or changed tables are
noticed through Windows file notifications, not scanning.

## Commands

| Command | |
|---|---|
| `pinbot status` | Pairing, whether it runs and starts with Windows, competitions |
| `pinbot scan --vpx DIR --tables DIR` | Point PinBot at VPX and your tables, if it found none (remembered) |
| `pinbot watch` | What runs in the background, in a console: each changed NVRAM slot and new high score after VPX exits |
| `pinbot report` | Logs and state in one zip on the desktop, to send when asked |
| `pinbot read FILE.nv` | Decode a ROM's high scores |
| `pinbot diff BEFORE.nv AFTER.nv` | Compare two NVRAM files |
| `pinbot uninstall` | Restore your original high scores, unpair and stop starting with Windows |
| `pinbot help` | All commands |

The installed copy is `%LOCALAPPDATA%\PinBot\pinbot.exe`, e.g. `"%LOCALAPPDATA%\PinBot\pinbot.exe" uninstall`.
Running a newer `pinbot.exe` by hand replaces it.

`watch` marks each new entry `QUALIFIED` or `NOT QUALIFIED` with the reason, and during a competition
`competition <id>: COUNTS` or why it doesn't.

## Tables

`pinbot scan` lists how each table's ROM was found:

| Source | Meaning |
|---|---|
| `Observed` | A real session wrote this ROM's `.nv` file: verified |
| `Script` | One `cGameName` in the table script: verified once played |
| `Ambiguous` | The script names several ROMs, or the same `.nv` exists in several folders |
| `Unknown` | No ROM in the script (original table, or the ROM is chosen at run time) |

A ROM is **unsupported** when no memory map exists for that exact revision. It is listed, never guessed.

### Hardware families proven in real competitions

Each family below has run a full competition on a real VPin: the competition file was prepared, the game's scores
counted, and the player's own high scores were put back afterwards.

| Hardware family | Proven with |
|---|---|
| Williams WPC | Attack from Mars, Twilight Zone, Cirqus Voltaire |
| Williams System 11 | Jokerz!, High Speed |
| Data East | Time Machine, Lethal Weapon 3, Baywatch |
| Stern SAM | AC/DC, Transformers, Star Trek |
| Stern and Sega Whitestar | Lord of the Rings, Starship Troopers |
| Gottlieb System 3 | Street Fighter II |
| Gottlieb System 80B | Genesis |
| Capcom | Big Bang Bar |

Other ROMs with a memory map also work. If a ROM ever wipes its high-score table on a competition file, PinBot stops
offering that ROM on that VPin, puts the player's own scores back, and keeps the files needed to fix it (see
`pinbot status`).

Cirqus Voltaire only asks for initials once a score beats its 5th place, which on the factory table is 30,000,000. A
competition file starts from a low ladder, so it asks like any other table.

## Files

Everything lives in `%LOCALAPPDATA%\PinBot` (set `PINBOT_HOME` to use another folder):

- `logs\`: 14 days
- `sessions.jsonl`: one line per observed session
- `state.json`: path overrides and table → ROM/NVRAM associations confirmed by real sessions
