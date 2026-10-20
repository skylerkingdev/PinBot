# Pinball FX: could PinBot support it? (research, 2026-10-14)

Question: does Pinball FX (Zen Studios, Steam app 2328760) expose, in a supported way, what PinBot needs for a
competition: a table id, a game end, the final score and who played (initials)? Only non-invasive sources were
considered (no memory reading, no decrypting saves, no network interception).

**Answer: not today.** Pinball FX publishes which table is played and when, but no score and no player identity.

| PinBot needs | Supported source | Status |
|---|---|---|
| Table id | DOFLinx hook (per-table mapping), save file name | available |
| Game start / end | `PLAY_MODE` / `MENU_MODE` events, save file timestamp | only "entered / left a table", no game over |
| Final score | none found | missing |
| Player initials | none (one score per account, by design) | missing |

## 1. Steam

- **No Steam leaderboards.** `https://steamcommunity.com/stats/2328760/leaderboards/?xml=1` answers "No public global
  leaderboards available". Zen runs its own cross-platform leaderboards (the game config points at
  `zencdn.blob.core.windows.net`); no public API was found.
- Steam has 45 achievements (unlocked or not) and Steam Cloud saves: nothing about scores.

## 2. Local save data

- `%LOCALAPPDATA%\PinballFX\Saved\SaveGames\<SteamID64>\TABLE_<id>_<mode>[_Normal].sav`, one file per table and mode
  (e.g. `TABLE_123_CLASSIC.sav`), also synced by Steam Cloud. `profile.sav` and `settings.sav` sit beside them.
- Table saves are Unreal Engine 4.27 `GVAS` files (`/Script/PinballFX.PFXTableSave`). Readable fields: `m_TableID`
  (e.g. 123), `m_GameID`, `m_Timestamp`, `m_Balls` (3), `m_UncompressedDataSize`.
- The table state is `m_TableData`: 38,800 bytes unpacking to 157,451, in no standard format (not zlib, gzip, zstd or
  LZ4 frame; every byte value occurs), i.e. compressed and possibly encrypted by Zen's own save system
  (`Saved\ZenSSType.ini`). Reading scores from it would mean reverse-engineering a proprietary format: ruled out.
- Usable without that: a table save changing means a game was played on table N, and when.
- `Mods\Cabinet\Settings.ini` in the install folder holds cabinet mode settings (`CabinetEnabled`).

## 3. Zen's DOFLinx interface (first party)

- Pinball FX's own settings have a **Cabinet → DOFLinx** tab. When enabled, the game loads `DOFLinxTrigger.dll` from its
  binaries folder (`PinballFX\Binaries\Win64`, next to `PinballFX-Win64-Shipping.exe`) and sends it table events.
  DOFLinx credits "Zen and freezy for PinballFx integration"; the game executable contains the tab's settings and
  `doflinxtrigger.dll`. Pinball M works the same way (`PinBallM-Win64-Shipping.exe`).
- What the events carry, from DOFLinx's 145 per-table mapping files (`FX\*.FX` in the DOFLinx repository):
  - which table (each table has its file; e.g. `FF_ROM=fx3_wms_afm` for Attack from Mars);
  - modes: `PLAY_MODE`, `MENU_MODE`, `INLANE_MODE`, `LAUNCHBALL`;
  - flippers, nudges, and every switch by name (e.g. `target_77_drop_target`, `sensor_61_left_ramp_enter`).
- No game over, score, initials or high-score event appears in any of the mapping files.
- **Open question:** the files only list events someone mapped to lights and solenoids; the hook may send more. The
  calling convention is in the DLL (not on the development laptop) and was not inspected.

## 4. Player identity

Pinball FX keeps one score per account per table and has no initials entry (long-standing; Zen and Steam forums). A
VPin is one Steam profile, so even with scores there would be no "who played". PinBot's initials model does not fit
unless every player has their own account, which a shared VPin does not.

## Next steps (non-invasive)

1. **Log the full event stream on the VPin:** enable the DOFLinx tab, turn on DOFLinx logging, play one complete
   game. Any game-over or score event passing through the hook shows up there. This settles the open question.
2. **Ask Zen** (they already work with freezy and DDH69) for "game started / game ended with final scores per player,
   plus the table id" through the existing hook.

Without one of these, Pinball FX support would need memory reading or cracking saves: against PinBot's reliability
pillar and good standing with Zen.

## Sources

- [DOFLinx docs: Enabling Pinball FX](https://github.com/DOFLinx/docs/blob/main/docs/getting-started/06_PinballFX.md)
- [DOFLinx FX table files](https://github.com/DOFLinx/DOFLinx/tree/main/FX)
- [DOFLinx 8.0 release (VPForums)](https://www.vpforums.org/index.php?showtopic=53394)
- [DOFLinx 8 + Pinball FX questions (VPForums)](https://www.vpforums.org/index.php?showtopic=53579)
- [Steamworks leaderboards (ISteamUserStats)](https://partner.steamgames.com/doc/api/ISteamUserStats)
- [VPin Studio](https://github.com/syd711/vpin-studio) (launches Pinball FX; reads no Pinball FX scores)
- [Zen Studios forum: "What about Highscores?"](https://forum.zenstudios.com/forum/zen-studios-games/pinball-fx3/7122-what-about-highscores)
- [Steam: "No local highscores?" (Pinball FX Classic)](https://steamcommunity.com/app/442120/discussions/0/1474222595301508263/)
- [Spesoft forums: PinballX and Pinball FX3 high score list](https://forums.gameex.com/forums/topic/24277-suggestion-pinballx-and-pinball-fx3-high-score-list/)
