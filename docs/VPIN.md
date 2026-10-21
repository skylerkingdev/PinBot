# On your VPin

For VPin owners: installing PinBot, what it does on your PC, its commands, and removing it. How competitions work in
Discord: [PLAYING.md](PLAYING.md).

## Install

In your Discord server, run `/pinbot pair`. Only you see the reply: a download link for `pinbot.exe` and a one-time
line like `https://pinbot.example.ts.net/pair/ABCDE-FGHJK` (it expires after 15 minutes). Run `pinbot.exe` on the VPin.
If Windows warns about it (*unknown publisher* or *Windows protected your PC*), choose **More info → Run anyway**. Then
paste the line and press Enter, and give the VPin the name others will see (or press Enter to keep the PC's name).

PinBot installs itself in `%LOCALAPPDATA%\PinBot`, starts minimized, and starts whenever you log in to Windows. Open
**PinBot** from the Start menu to see its status.

## What it does

- After each game it reads the ROM's high scores and sends new ones to your community's PinBot server.
- When a competition starts, it prepares that game's high-score table the next time that table isn't running: your own
  table is backed up and verified, and the competition starts from a low score ladder. At the end your own table is put
  back exactly as it was. Details: [NVRAM-SAFETY.md](NVRAM-SAFETY.md).
- It updates itself from the PinBot server: signed releases only, and only when no table is running.
- If it crashes it restarts itself. After 3 crashes within 10 minutes it stops trying until you next log in to Windows,
  and `pinbot report` collects what happened.

It stays out of the way: below-normal priority in Windows' efficiency mode, about 15–20 MB of memory, asleep while a
table runs. When idle it checks for VPX every 5 seconds and keeps one quiet connection to the server. New or changed
tables are noticed through Windows file notifications, not by scanning.

## Commands

Run these in a Command Prompt with the installed copy, e.g. `"%LOCALAPPDATA%\PinBot\pinbot.exe" status`.

| Command | What it does |
|---|---|
| `pinbot status` | Pairing, whether PinBot runs and starts with Windows, its release, competitions |
| `pinbot scan --vpx DIR --tables DIR` | Point PinBot at VPX and your tables, if it found none (remembered) |
| `pinbot report` | Logs and state in one zip on the desktop, to attach to a bug report (never your server credential) |
| `pinbot pair LINE` | Pair again with the line from `/pinbot pair` (or `pinbot pair URL CODE [--name NAME]`) |
| `pinbot watch` | Show each game's high scores in this window as they happen. Close the minimized PinBot window first: only one runs at a time |
| `pinbot read FILE.nv` | Decode a ROM's high scores |
| `pinbot diff BEFORE.nv AFTER.nv` | Compare two NVRAM files |
| `pinbot uninstall` | Put back your own high scores, unpair, and stop starting with Windows |
| `pinbot help` | All commands |

Running a different `pinbot.exe` by double-clicking it replaces the installed copy.

`watch` marks each new entry `QUALIFIED` or `NOT QUALIFIED` with the reason, and during a competition
`competition <id>: COUNTS` or why it doesn't.

## Troubleshooting

- **My score didn't show up:** see [PLAYING.md](PLAYING.md#my-score-didnt-show-up).
- **PinBot found no tables:** `pinbot scan --vpx "C:\Visual Pinball" --tables "D:\Tables"` with your own folders.
- **A table is listed as unsupported:** its exact ROM revision has no memory map yet (see below).
- **Anything else:** run `pinbot report` and attach the zip from your desktop to a
  [bug report](https://github.com/skylerkingdev/PinBot/issues/new/choose).

## Uninstall

Close Visual Pinball, then run `"%LOCALAPPDATA%\PinBot\pinbot.exe" uninstall`. It puts back your own high scores,
unpairs, and removes PinBot from Windows startup and the Start menu. It refuses (and changes nothing) if a high-score
table could not be put back. To remove everything, delete the folder `%LOCALAPPDATA%\PinBot` afterwards. Your VPin stays
listed on the PinBot server until an admin removes it.

## Tables

`pinbot scan` lists how each table's ROM was found:

| Source | Meaning |
|---|---|
| `Observed` | A real game wrote this ROM's `.nv` file: verified |
| `Script` | One `cGameName` in the table script: verified once played |
| `Ambiguous` | The script names several ROMs, or the same `.nv` exists in several folders |
| `Unknown` | No ROM in the script (original table, or the ROM is chosen at run time) |

A ROM is **unsupported** when no memory map exists for that exact revision. It is listed, never guessed.

### Hardware families proven in real competitions

Each family below has run a full competition on a real VPin: the competition table was prepared, the game's scores
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

Other ROMs with a memory map also work. If a ROM ever wipes its high-score table during a competition, PinBot stops
offering that ROM on that VPin, puts the player's own scores back, and keeps the files needed to fix it (see
`pinbot status`).

Cirqus Voltaire only asks for initials once a score beats its 5th place, which on the factory table is 30,000,000. A
competition starts from a low ladder, so it asks like any other table.

## Files

Everything lives in `%LOCALAPPDATA%\PinBot` (set `PINBOT_HOME` to use another folder):

- `pinbot.exe`: the installed copy
- `nvram\`: one folder per competition with the verified backup of your own high scores
- `outbox\`: scores waiting to reach the server
- `logs\`: the newest 14 days, at most 5 MB a day
- `sessions.jsonl`: one line per game PinBot watched
- `state.json`: pairing, path overrides, and which ROM and NVRAM file each table uses
- `diagnostics\`: files kept when a ROM rejected a competition table
- `update\`, `crashes.txt`, `tables-cache.json`: PinBot's own bookkeeping
