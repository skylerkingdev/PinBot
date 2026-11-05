# Table updates

Status (2026-11-04): build steps 1–5 are implemented (see [Build order](#build-order)). The later phase is design
only.

When a member installs or updates a table on their VPin, PinBot can tell the community: who updated what, to which
version, with links to where the table, backglass and ROM come from. Agreed 2026-11-04.

A VPin Studio "Table Updated" card shows only the game name and a picture. A member who follows it lands on the VPS
game page, which lists every version of the table (Transformers has several), and can't tell which one to grab to
match. PinBot's post names **the exact entries the member is running** and links each one straight to its author's
download page, so anyone can get the same setup without searching.

## What the post shows

One Discord embed per table:

```
┃ Transformers (Pro) · Stern 2011
┃ Skyler updated 2.0.1 → 2.4.1
┃ VPX 2.4.1 by RobbyKingPin, SG1bsoN, HauntFreaks +15 · MOD · VR · Hybrid · FSS
┃
┃ Get Skyler's setup
┃ 🎰 Table      v2.4.1 · VPS uyJ5RDRCu6                         → vpuniverse page for that exact version
┃ 🖼️ Backglass  HauntFreaks v1.0.0 · Full DMD · VPS uX_JRoKZIR   → vpuniverse page for that backglass
┃ 💾 ROM        tf_180                                          → its download page
┃ 🌈 Colorization  Redrum 2.3                                   → (only if Skyler has colorization installed)
┃
┃ [picture of that table version, from VPS]
┃ Links go to the authors' own pages · All versions on VPS →
```

- **Each row links the author's download page** for that exact entry, not the VPS game page. One click gets the
  right file.
- **Each row shows the VPS file ID** (`uyJ5RDRCu6`), the same ID VPS prints under each card. Anyone browsing VPS can
  match it.
- **The version and the change** (`2.0.1 → 2.4.1`) come from the .vpx itself and the last inventory, so they show
  even without VPS.
- **A row PinBot isn't sure of says so:** "Table: version 2.4.1 by RobbyKingPin…, not identified on VPS". It never
  shows a guess as if it were a fact.
- **Extras** (colorization, alt sound, PuP pack) appear only when the member has them installed (the agent sees
  `pinmame/altcolor/<rom>`, `pinmame/altsound/<rom>`, or the table's PuP pack folder), and only with a link when
  matched or confirmed.
- **"All versions on VPS"** links the game page as a last resort, at the bottom.

## Rules

- **Nothing is hosted.** PinBot never stores, copies or relays table, backglass or ROM files. It posts links only.
- **Nothing is public without a click.** A member chooses each time, or chooses once to always share.
- **The Virtual Pinball Spreadsheet (VPS) is optional.** It makes the posts better (links, the exact VPS entry). It
  is never needed for anything to work. See [If VPS is unavailable](#if-vps-is-unavailable).
- **Only real changes are announced.** A re-save, a settings or POV tweak, or the first inventory after pairing posts
  nothing.
- **A question never outlives its table.** If the table is removed from the VPin, or changed again, before the member
  answers, the question is withdrawn. Once nothing in the DM is left to answer, PinBot edits it to say why and
  removes the buttons. A newer change gets its own DM.

## How it fits together

```
VPin (agent)                               Server                                          Discord
-----------------------------------------  ----------------------------------------------  -----------------------------
.vpx changes (FileSystemWatcher, exists)   compares with the last inventory
reads TableInfo from the .vpx: name,       -> new table, or the version string changed
  version, author, release date            -> table_update row (pending)
  (cached by size+mtime like the script)   matches against the VPS catalog, if one is loaded
inventory push carries those fields  ----> asks the member who paired the cabinet  ---------> DM with buttons
                                           shares, or remembers the choice        <---------- Share / Always / …
                                           posts in the updates channel            ---------> #table-updates
```

### Agent: read the table's own metadata

A .vpx is an OLE compound file (the agent already opens it with OpenMcdf for the script). The `TableInfo` storage
holds UTF-16 streams such as `TableName`, `AuthorName`, `TableVersion`, `ReleaseDate` and `TableDescription`. Read
those alongside the script, in the same pass, and cache them in `TableScriptCache` (keyed by size and mtime), so an
unchanged table is never reopened.

- **Backglass:** a `.directb2s` next to the table (same base name, as B2S looks for it) is XML whose header carries
  `Name`, `Author` and `DMDType`. Read just the header and cache it the same way.
- **Extras:** whether `pinmame/altcolor/<rom>`, `pinmame/altsound/<rom>` or a PuP pack folder exists.
- `InventoryTable` gets optional fields: `Name`, `Version`, `Authors`, `FileSize`, `Modified`, plus the backglass
  header and the extras. They are optional, so an old agent with a new server, or a new agent with an old server,
  keeps working.
- Tables **without a ROM** (EM and original tables) are left out of the inventory today. Updates should include
  them, so they are sent too, marked `not eligible: no ROM`. That doesn't change competitions.
- The agent **never contacts VPS**. It only reports facts about its own files.

### Server: detect an update

On each inventory push, compare each table with the cabinet's previous row:

| Previous            | Now                                         | Result                                    |
|---------------------|---------------------------------------------|-------------------------------------------|
| no rows (new pair)  | anything                                    | baseline only, silent                     |
| table not present   | present                                     | **added**                                 |
| version `2.3`       | version `2.4`                               | **updated**                               |
| same version        | different size/mtime                        | silent (re-save or tweak); a changed script hash is recorded for the later phase |
| no version string   | different size/mtime                        | silent (can't tell a real update)         |
| present             | gone                                        | silent (removals aren't announced)        |

Several changes inside ~10 minutes (a member installing a batch) become **one** DM with one row per table.

### Discord: ask, then share

- The DM goes to `cabinet.paired_by`, with buttons **[Share]**, **[Always share]**, **[Not this time]** and
  **[Don't ask again]**. A batch DM gets **[Share all]**.
- **The DM shows what PinBot matched, so the member can correct it in the same message:** "Table: v2.4.1 by
  RobbyKingPin, 32assassin +12 (VPS `QcOG228URt`)", and the same for the backglass. On a question about one table,
  dropdowns list that game's VPS table entries and backglasses (version, authors, features), plus "Not on VPS", with
  PinBot's match preselected. When PinBot is sure (one entry for the version and authors, and one backglass), the
  dropdowns wait behind a **[Wrong match?]** button. When it isn't, the row says "not identified: pick it below". On a
  question about several tables, a "Check a table's VPS entries" dropdown picks which table the entry dropdowns are
  for (Discord allows 5 rows of components), starting with the first one PinBot isn't sure of. A pick redraws the
  question. Share is never blocked: unidentified rows are posted as "not identified on VPS".
- **The choice is remembered** per cabinet, table file and version in `table_vps_choice`, so the same file is never
  asked about again.
- **Fixing a post after sharing:** `/pinbot updates` lists the member's shared posts in a dropdown. Picking one shows
  its entry dropdowns; a pick is remembered like one in the DM, and the post in the updates channel is edited to link
  the right entries. Only the member's own posts.
- **A post is completed for a day.** Members often add the backglass (or colorization, alt sound) a few minutes after
  the table. When that reaches PinBot within 24 hours of sharing, for the same file and version, the post is edited to
  show it, with the backglass matched on VPS again (a member's own pick still wins). After that, a post stays as it is.
- **Tables are told apart by file name.** Several tables of one game (VR, mods) can share a ROM, so the ROM never
  merges two files' updates. It only recognizes a renamed file: a file that is gone and one that appeared for the same
  game, whose open update follows the new name.
- New `member_pref(discord_user_id, share_updates)`: `ask` (default), `always`, `never`.
  `/pinbot updates share:<always|ask|never>` changes it.
- If a DM can't be delivered (DMs closed), the update stays unshared. It is never posted without a choice.
- The updates channel is set with `/pinbot-admin channels updates:#…`. **With no channel set, the feature is off**:
  no DMs and no posts.
- Pending updates expire after 7 days, so a late click on an old DM says "too old to share".

### Channels

Two new channel settings next to the existing challenges, weekly and info channels, both optional:

| Setting                                  | Channel type   | What goes there                                   | When       |
|------------------------------------------|----------------|---------------------------------------------------|------------|
| `/pinbot-admin channels updates:#…`      | text           | update posts ("Skyler updated 24 → 2.4.1")        | steps 1–5  |
| `/pinbot-admin channels setups:#…`       | text           | shared POVs (later: script fixes)                 | POV phase A |

- **Unset means off.** No updates channel means no update DMs or posts. Without a setups channel, POVs go to the
  updates channel; with neither, nothing is shared.
- **Later, a forum:** the setups setting can become a forum with one thread per game. Until then it's a text channel.
- **Why a forum for setups (later):** fixes and POVs are reference material people come back to. A forum gives each game one
  thread (created by PinBot the first time something is shared for it) that holds every fix, POV and settings file
  for that game, plus the discussion. Discord's own **Follow** on a thread notifies interested members, and forum
  tags mark POV, Settings, Script fix and Approved. PinBot's DM install offer still goes to every member whose cab it
  fits, whether they follow or not.
- **Links between them:** an update post links the game's thread when one exists ("3 fixes · 2 POVs for this table
  →").
- **Smaller servers:** both settings may point at the same text channel. In that case setups are posted as plain
  messages instead of forum threads.
- **Permissions:** PinBot needs Send Messages and Embed Links in the updates channel, plus Create Posts and Send
  Messages in Threads in the setups forum. The `/pinbot-admin channels` reply checks these and says what's missing.

## Matching with VPS

The match runs on the server against the catalog in memory, and only adds information:

1. **Game:** the table's ROM, looked up in the `romFiles[].version` index (`eatpm_l4` → game `3LNcE1yH`). For a table
   with no ROM, a normalized name match on `TableName` or the file name, accepted only when exactly one game matches.
0. **Member's choice first:** a remembered choice for this exact file wins over everything below.
1. **Game:** the table's ROM, looked up in the `romFiles[].version` index (`eatpm_l4` → game `3LNcE1yH`). For a table
   with no ROM, a normalized name match on `TableName` or the file name, accepted only when exactly one game matches.
2. **Table file:** within the game's `tableFiles` (VPX only), match the `version` string, then check that at least
   one author name appears in `AuthorName`. Exactly one → matched. Several or none → "pick one" in the DM.
   (Checked against today's data: Transformers 2.4.1 picks out exactly one of its six VPX entries, `uyJ5RDRCu6`. But
   three entries are "1.1" with overlapping author lists, so ties are real and the DM confirm is what resolves them.)
3. **ROM:** the `romFiles` entry for that ROM, with its links.
4. **Backglass:** the `.directb2s` header's `Author` matched against the game's `b2sFiles` authors, narrowed by Full
   DMD vs 2/3 screens from `DMDType`. Exactly one → matched. Otherwise "pick one" in the DM, or "backglass installed,
   not identified" in the post.
5. **Extras:** linked only when the game has exactly one entry of that kind, or the member picked one.

Every row is in one of three states (matched, confirmed by the member, not identified), and the post shows which.
Only matched or confirmed rows get a download link.

## If VPS is unavailable

PinBot must behave exactly as it does today when VPS is down, moved, slow, changed or gone for good.

**Where it can be touched.** Only one server background loop (`VpsCatalog`) ever makes a network call to VPS.
Competitions, results, leaderboards, pairing, the agent and the Discord commands never call VPS and never wait on it.

**How the catalog is fetched.**
- Every 6 hours, fetch `lastUpdated.json` (a few bytes). Download `vpsdb.json` (about 8 MB today) only when that
  changed.
- Hard limits: a 60 s timeout and a 50 MB size cap, so a hung or huge response is abandoned.
- After a failure, back off exponentially to at most 24 h. Failures are logged at warning level once per state change
  (working → failing → working), not on every attempt.
- The loop runs in its own `try/catch` with its own task. An exception there is logged and retried, and can't stop
  the other background loops (the same pattern as `DiscordBackground.Loop`, but a separate service).

**What's kept.**
- The download is parsed into a small index: games, VPX table files, ROMs and https links. Only the fields above are
  kept. Unknown fields are ignored and missing fields are tolerated per entry, so a bad entry is skipped, not fatal.
- The index is accepted only if it passes sanity checks: it parses, has at least 1,000 games, and isn't more than 20%
  smaller than the last good one. Otherwise the **last good index is kept**.
- The last good index is saved in Postgres (`vps_catalog`: one row with the compact JSON and its `lastUpdated`), so a
  restart doesn't need VPS. The server has no writable data folder, and the database already lives on the NAS.

**What happens without it.**

| Situation                                   | Behavior                                                        |
|---------------------------------------------|-----------------------------------------------------------------|
| VPS down, catalog loaded earlier            | matches use the last good index, which may be days old: fine    |
| VPS down, never loaded (fresh server)       | posts go out without links, using the table's own name/version  |
| VPS changes its format                      | sanity checks fail, last good index kept, one warning logged    |
| VPS gone for good                           | same as above, indefinitely; a manager can turn it off          |
| VPS returns links we don't trust            | only `https://` links are kept; the URL is wrapped in `<>`, no embeds |

**Off switches.** `PinBot__Vps__Url` (default: the GitHub raw URL) can point at a mirror, or be set empty to turn VPS
off completely, with no fetches and no links. The updates channel being unset turns the whole feature off.

**Tests.** Fake fetchers for: unreachable, timeout, 404, truncated JSON, valid JSON with the wrong shape, a tiny
catalog (sanity check), and a catalog larger than the cap. Each must leave updates posting without links, and
competitions untouched.

## Untrusted text

Table names, author names and VPS fields are data from strangers. Discord markdown and mentions are escaped,
`allowed_mentions` is empty (as in `ReleaseNotices`), and every field is truncated to a sane length.

## Build order

1. Agent: read `TableInfo` metadata, cache it, add the optional fields to the inventory and include no-ROM tables.
2. Server: detect updates (silent baseline, version comparison, batching) with the `table_update` and `member_pref`
   tables.
3. Discord: updates channel setting, DM with buttons, `/pinbot updates`, the channel post without links.
4. VPS catalog: the fetch loop, sanity checks, Postgres snapshot, and matching that adds the exact-entry links.
5. DM confirm: the dropdowns (one table, several tables, and "Wrong match?" when PinBot is sure), `table_vps_choice`,
   and fixing a shared post from `/pinbot updates`.

Steps 1–3 ship a complete feature with no VPS at all. Steps 4–5 make the posts point at exact entries. Shared POVs and
script fixes come after, as a [later phase](#later-phase-shared-povs-settings-and-script-fixes).

## Later phase: shared POVs, settings and script fixes

Not part of steps 1–5. A separate design once table updates are in use. Same goal: help everyone run the same setup.
It builds on step 4–5's exact identification of each member's table file. Everything shared here lives in the setups
forum, one thread per game (see [Channels](#channels)).

### POVs, phase A: sharing, the same way as table updates (built)

A POV is shared exactly like a table update: the VPin notices it, the member is asked in a DM, and only what they share
is posted. Status: built, and tested on a real VPin (2026-11-04: X-Files, cabinet mode, VPX 10.8).

- **What a POV is here:** VPX 10.8 keeps a table's view in the table's own `.ini` (next to the `.vpx`), section
  `[TableOverride]`, one set of `View…` keys per view mode: desktop (`ViewDT…`), cabinet (`ViewCab…`) and full single
  screen (`ViewFSS…`). The same file can also hold the member's other table options; only view keys are ever read.
- **Desktop vs cabinet:** a VPin runs in one mode (`BGSet` under `[Player]` in VPinballX.ini: 0 desktop, 1 cabinet,
  2 FSS). The agent reads only that mode's view keys, and the post says which mode the POV is for. A cabinet POV is
  never offered as a desktop one, and a VPin switching mode is not announced as a POV change.
- **Noticed:** the agent reads the view keys of each table's `.ini` (watching `.ini` files like `.directb2s` ones) and
  sends them with the table list: numbers only, at most 64 keys. A change with the same table version is a `pov` update.
  A new table or version is a table update instead (its POV comes with it). A POV changed again before the member
  answers replaces the earlier one. A POV change never replaces a table update.
- **Asked:** the same DM, settle time, buttons, 7-day expiry and closing. A POV DM is separate from a table DM, and
  POVs have their own setting, `/pinbot updates povs:<ask|always|never>`, so someone who always shares tables isn't
  posting every camera tweak.
- **Posted** in the setups channel, or the updates channel when unset: "Skyler shared a **cabinet** POV for this
  table", with the table version and its VPS entry (as in update posts), and the view keys as a code block to paste
  under `[TableOverride]` in the table's `.ini`. The `[Player]` section (screen size, player position: this VPin's own
  setup) is never sent.
- **Found in testing:** VPX saves only the view settings that differ from the table's own view. X-Files' cabinet POV
  was 9 keys (`ViewCabHOfs`, `ViewCabMode`, `ViewCabRotation`, `ViewCabScaleX/Y/Z`, `ViewCabVOfs`,
  `ViewCabWindowBot`, `ViewCabWindowTop`), no FOV or player position. So a shared POV is a set of changes on top of
  the table's built-in view, which is why it's only meant for the same table version. The post tells members to
  delete their own `ViewCab…` lines before pasting, or leftovers mix in.
- **`ViewCabMode = 2` is VPX's window mode,** whose `WindowTop`/`WindowBot` describe the physical playfield screen:
  the settings most likely to look wrong on a cabinet of another size.
- **Not yet:** the sharer's screen size and orientation in the post (more useful than first thought, see above), and
  a legacy `.pov` file next to a table.

### POVs, phase B: installing a shared POV (built)

Status: built, not yet tested on two real VPins.

1. **"Use this POV"** is a button on every POV post. Pressing it answers privately (only the clicker sees it), after
   checking each of their VPins: the same game (the table's own name, or its ROM), the **same table version**, and the
   **same view mode**. Otherwise it says why: "has v2.4.1: this POV was made for v2.0", "plays in desktop mode: this is
   a cabinet POV", "already has this view", or "No VPin is linked to you".
2. **"Install on <VPin>"** queues the install (`setup_install`) and wakes that VPin's waiting connection: the same
   long poll that brings competitions, so nothing new is opened on the member's network. The answer tells the
   member's VPin how many installs wait (`Setups`); it fetches them from `agent/setups`.
3. **The VPin installs it when no table is running**, and checks everything again itself (the server is never trusted
   to write anything but view settings): the table is still there, its view mode matches, and every line is
   `View<mode>… = number`, or the whole install is refused. It copies the table's `.ini` to PinBot's own
   `setup-backups` folder, removes that mode's `View…` lines under `[TableOverride]` (a shared POV holds only what
   differed from the table's view, see phase A), writes the shared ones, and keeps the file's encoding and line ends.
   Everything else in the file stays.
4. **It reports back** with the view before and after. The server records the new view as the table's known view, so
   the VPin's next table list isn't taken for the member's own POV change (no "share your POV?" for what they just
   installed).
5. **The member gets a DM:** "Installed Skyler's cabinet POV for 24 v2.0 on Den", with an **Undo** button. Undo
   queues putting back the view the VPin reported before the install, the same way. An install not picked up within
   7 days is dropped, and the member is told.

- **Generic by kind:** `setup_install.kind` is `pov` today; script fixes reuse the same queue, checks, report, DM and
  Undo (next section).
- **Competitions:** camera settings only, so a POV is always fine during a competition. Other table options (which on
  some tables set difficulty or ball count) are never read, shared or written.
- **Not yet:** comparing the sharer's playfield screen with the member's for window-mode cabinet POVs; subscribing to
  a member's POVs; a legacy `.pov` file.

### Script fixes (`.vbs`): offered, shown, and installed only on the member's accept

**A `.vbs` is a program.** VPX runs it with the user's full rights (files, shell, network). So PinBot never pushes a
script silently or to everyone. A fix is offered to each member it fits, shown in full, and installed only when that
member clicks **[Install]**, with these limits:

- **Exact base only.** A fix is a patch from one script (hash A) to another (hash B). It's offered only to cabs whose
  current script is exactly A. After patching, the agent checks the result is exactly B, or it writes nothing. What
  gets installed is byte-for-byte what was shown.
- **Small and readable.** The DM shows the whole diff. Fixes over ~200 changed lines can't be installed automatically;
  they're notify-only.
- **Risky code blocks automatic install.** The **agent** scans the added lines itself, so a compromised server can't
  skip the check. These words block the one-click install: `CreateObject`, `GetObject`, `Execute`, `ExecuteGlobal`,
  `Eval`, `WScript`, `Shell`, `FileSystemObject`, `XMLHTTP`, `ADODB`, `Run`/`Exec` on a non-VPX object, and new file
  or URL paths. Such fixes become notify-only, with the reason shown. (VPX's own `ExecuteGlobal GetTextFile(...)` line
  for `controller.vbs` is already in the base and isn't "added".)
- **Who can post installable fixes:** members with a **Table fixer** role (set by managers), or any member's fix once
  a manager approves it. Everyone else's fixes are notify-only.
- **Written as a sidecar, never into the .vpx.** The agent writes `<table>.vbs` next to the table (VPX prefers it over
  the embedded script). The 200 MB .vpx is never modified. An existing sidecar is backed up first.
  **[Undo]** in the DM, or `/pinbot updates undo`, puts back exactly what was there.
- **A stale sidecar is caught.** A sidecar overrides the table's own script, so if the member later installs a new
  version of the table, an old PinBot-installed fix would silently replace the new script. The agent remembers which
  base each fix was installed over. When the .vpx's embedded script no longer matches that base, it renames the sidecar
  to `<table>.vbs.pinbot-off` and tells the member.
- **Timing:** only when no table is running, and never on a game with a running competition. It's queued and
  installed when the competition ends, and the member is told so.
- **Members can also decline and install by hand.** Notify-only fixes link to the post and diff as before.

- **Fixes live in two places.** Some come as a sidecar `.vbs`. Many are edits to the script **inside the .vpx**
  (VPin Studio's "Edit Script" does this, and so does the VPX editor). The agent already extracts the script from
  either place (`TableScript.Read`), so it treats them the same way: it caches a **hash of the script text** next to
  the ROM facts, so an edit is noticed whichever way it was made.
- **Matching a fix to a table:** a posted fix records the hash of the script it was made from. A member is told about
  it only when their script hash matches that base, which means "this fix is for exactly your script", not just the
  same table version.
- **Sharing your own edit:** when a table's script hash changes but its version doesn't, the Share DM can offer
  "Share your script change": the diff from the previous script, with both hashes, posted for others to install (under
  the limits above) or apply by hand. To produce the diff, the agent keeps the previous script text of each table
  locally.
- **Competitions:** if a table's script hash changes (sidecar or embedded) while a competition on that game is
  running, scores from that cab after the change are marked for a manager's review rather than silently counted,
  since scripts can change tilt, ball save or scoring.

### Worked example: fast flips for 24 (checked 2026-11-04)

The real files: `24 (Stern 2009) v.2.4.1.vpx` from vpuniverse and a fast-flips version of its script.

- **The table's own info** reads cleanly from the .vpx: `TableName` "24 (Stern 2009)", `TableVersion` "2.4.1",
  `AuthorName` "freneticamnesic, 32assassin, DGrimmReaper, RobbyKingPin, …". The script is embedded (no sidecar).
- **VPS match:** ROM `twenty4_150` → game `P15WeTaO`. Version 2.4.1 is one of seven VPX entries, `QcOG228URt`
  (RobbyKingPin, 32assassin, Freneticamnesic). VPS writes "Freneticamnesic" with a capital F, so author matching must
  ignore case. The four backglasses need the `.directb2s` header or a pick in the DM.
- **The fix:** 12 lines added, 1 removed, in three places:
  1. `InitVpmFFlipsSAM` moves from before `Controller.Run` to the end of `Table1_Init`, once the ROM is running.
  2. `cSingleLFlip` / `cSingleRFlip = 0` are added, since the table has no staged flippers.
  3. An `On Error Resume Next` / `On Error Goto 0` pair is wrapped around one DMD setting, so it can't stop the table
     from starting.
- **The offer:** base `ea02029219c3447d…` → result `ce7a865d455e2113…` (SHA-256 of the script text, line endings
  normalized). None of the risky words are in the added lines, so a Table fixer's post would get **[Install]**. A cab
  on 24 v2.4.1 with the untouched script would receive it as a `24 (Stern 2009) v.2.4.1.vbs` sidecar.
- **Competitions:** fast flips changes how the flippers respond, so it's held while a competition on 24 is running.
- **The download also carried two `.ini` files**: a "Legends Cabinet" POV with `[TableOverride]` and `[Player]` camera
  values, and VR settings. Both also have a `[TableOption]` section, which includes **`Outlane Difficulty`**. This is
  exactly the competition-relevant key settings sync must skip or hold.

## Open questions

- **Settled:** the VPS site links a single file as `/tables?game=<game>&fileType=tables&fileId=<file>`, the format its
  own export uses. The post's title links there, and "All versions on VPS" links `/tables?game=<game>`.
- **Settled:** Discord dropdowns hold at most 25 options, so a game lists its first 24 VPS entries plus "Not on VPS".
- **Partly settled:** `.directb2s` headers carry `Author` (HauntFreaks on both real files checked) and `DMDType` 3 for
  full DMD. 24's HauntFreaks backglass matches exactly one VPS entry. Check more of the VPin's real files.
- **Found while building:** 197 of VPS's 673 ROM names belong to more than one game (mods and originals reuse ROMs).
  Those games are told apart by name, ignoring parentheses ("Transformers (Pro)" = "Transformers (Stern 2011)"), and
  failing that by which game lists the table's version.
- A backglass changed on its own is noticed by a watcher on `*.directb2s` next to the tables. Folders the agent doesn't
  watch are picked up at the next start.
