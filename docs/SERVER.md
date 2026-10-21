# Server reference

For developers and scripters: how the PinBot server works, its settings, its APIs, and how releases reach servers and
VPins. To run a server for your community, see [DEPLOY-NAS.md](DEPLOY-NAS.md); to connect it to Discord,
[DISCORD-SETUP.md](DISCORD-SETUP.md).

One portable ASP.NET Core process with PostgreSQL. It answers `/pinbot` in Discord and serves the VPin agents.

## Run

Requirements: the .NET 10 runtime (ASP.NET Core) and PostgreSQL 13+ (the `btree_gist` extension ships with standard
PostgreSQL). The schema is created on startup.

```text
dotnet run --project src/PinBot.Server
```

| Setting (environment variable) | Meaning |
|---|---|
| `ConnectionStrings__PinBot` | `Host=…;Database=pinbot;Username=…;Password=…` |
| `ASPNETCORE_URLS` | e.g. `http://0.0.0.0:5080` |
| `PinBot__PublicUrl` | The public HTTPS address (pairing lines, links, the Discord endpoint) |
| `PinBot__Discord__ApplicationId`, `__PublicKey`, `__BotToken`, `__GuildId` | The Discord app and server; without them the server runs without Discord |
| `PinBot__Discord__ChannelId`, `__WeeklyChannelId` | First challenges and weekly channels (afterwards set with `/pinbot-admin channels`) |
| `PinBot__UploadGraceMinutes` | Minutes after a deadline in which results of games finished in time must arrive (default 5); then the standings are final |
| `PinBot__PairingCodeMinutes` | How long a pairing code works (default 15) |
| `PinBot__MaxClockSkewMinutes` | How far in the future a VPin's clock may put a game's end (default 5) |
| `PinBot__AdminToken` | Optional: turns on the admin API with this bearer token; off when unset |
| `PinBot__UpdaterState` | The updater's state file, read for the info channel's "skipped release" notices |

In `deploy/docker-compose.yml` these come from `deploy/.env` under shorter names (`PINBOT_PUBLIC_URL`,
`DISCORD_BOT_TOKEN`, `PINBOT_ADMIN_TOKEN`, …).

Put TLS in front with a reverse proxy, or use the host's HTTPS. Agents warn when pairing over plain HTTP. `/healthz`
answers `{"status":"ok","release":<release number or null>}` once the server is ready.

## Rules the server enforces

- **One competition per game at a time.** A PostgreSQL exclusion constraint over the playing window, plus a per-game
  lock so concurrent starts fail cleanly with 409, never with a deadlock.
- **A result counts when:**
  - the session exited at or after the start and strictly before the (possibly early) end;
  - it arrived before the end plus the delivery window;
  - its ROM belongs to the competition's game, and the sending VPin lists a table with that ROM;
  - its initials match exactly one member, or a guest registered with `/pinbot guest`.

  Unknown or ambiguous initials are stored as `unattributed` and counted later if exactly one member or guest claims
  them before the competition is final.
- **Exactly once:** results deduplicate on `eventId`. A replay returns the original outcome; an id reused with
  different content is rejected.
- **Accepted scores keep their member.** A later nickname change does not move them.
- **Leaderboard:** best accepted score per member. Ties share a rank (1, 1, 3).
- **Status is computed from the clock** (`scheduled`, `active`, `finalizing`, `complete`, `cancelled`). No scheduler is
  needed for deadlines.

## One ROM revision is one game

A game is one exact ROM revision: revisions of the same machine can differ in rules and scoring, so they are never
compared on one leaderboard. When VPins have different revisions of a machine, `/pinbot games` lists each with its ROM
in the title (e.g. `Cirqus Voltaire (Bally 1997) · cv_14` and `· cv_20h`); to compete together, put the VPins on the
same ROM. A deliberate exception can be made with `PUT /api/v1/admin/rom-aliases`.

Exception: a **coin play / free play build** of a ROM is the same game as the ROM itself (e.g. `cv_20hc` and `cv_20h`,
`ww_lh6c` and `ww_lh6`, `tom_14hb` and `tom_14h`): PinMAME names them identically apart from "Coin Play"/"Free Play" and
they share one memory map. The list is generated from the pinned maps (`tools/PinBot.Variants`, written to
`src/PinBot.Server/rom-variants.json`; the weekly maps workflow re-runs it) and applied at every server start.

## Agent API (the VPin's credential as Bearer token)

| | |
|---|---|
| `POST /api/v1/pair/redeem` | `{code, cabinetName}` → `{cabinetId, credential}`; single-use code, rate-limited per client |
| `PUT /api/v1/agent/inventory` | full table list; replaces the previous one |
| `GET /api/v1/agent/competitions` | competitions this VPin can take part in, up to the end of their delivery window. With `known=<version>&wait=<seconds>` (at most 25) the server holds the request until competitions change |
| `PUT /api/v1/agent/activity` | a table started, or none runs any more (busy, not offline) |
| `PUT /api/v1/agent/competition-states` | which competitions this VPin is ready for (shown on the leaderboard card) |
| `POST /api/v1/agent/results` | batch of result events → final outcome per event |

On the VPin, the agent keeps the waiting request open, so a new competition arrives within seconds; if that connection
drops, it fetches every minute instead. It caches competitions, so high-score tables are still prepared and put back
while the server is unreachable. Each counting score is written to `%LOCALAPPDATA%\PinBot\outbox` before it is sent and
retried (backing off up to 2 minutes) until the server gives a final answer. The credential is stored encrypted with
Windows DPAPI for the current user.

## Admin API (optional)

`Authorization: Bearer <PinBot__AdminToken>`. Off unless `PinBot__AdminToken` is set, and not forwarded by the public
address. Discord covers day-to-day use; this is for scripts, tests, and the two things Discord has no command for:
removing a VPin and grouping ROMs.

| | |
|---|---|
| `POST /api/v1/admin/pairing-codes` | new pairing code |
| `POST /api/v1/admin/cabinets/{id}/revoke` | remove a VPin (its credential stops working) |
| `GET /api/v1/admin/games` | games with an eligible table on at least one VPin |
| `PUT /api/v1/admin/rom-aliases` | `{romToGame: {"afm_113": "attack-from-mars", …}}` (curated grouping) |
| `PUT /api/v1/admin/members` | `{members: [{discordUserId, displayName, isBot}]}`; initials like `Name \| [ABC,XYZ]` |
| `GET/POST /api/v1/admin/competitions` | list / start `{game, hours, kind?, startsAtUtc?}` → 201, 409 (overlap) or 422 (not eligible) |
| `POST /api/v1/admin/competitions/{id}/end` | end early |
| `GET /api/v1/admin/competitions/{id}/leaderboard` | ranking |

## Releases and VPin auto-update

Releases are made by GitHub (`.github/workflows/release.yml`) from every push to `master` that changes what ships
(`src/`, `deploy/`, the maps, `publish.ps1`, the signing tool, `NOTICE.md`, the setup guides), once the CI checks pass.
The workflow
- builds `pinbot.exe` with the release's public key (`keys/update-signing.pub`) and a release number (UTC
  `yyyyMMddHHmmss`) inside, then writes `pinbot.json` (version, size, SHA-256, ECDSA P-256 signature). The private key,
  the secret `UPDATE_SIGNING_KEY` of the protected `release` environment (only `master` can use it), is present only in
  the signing steps, never while code builds, and must match the committed public key;
- builds the server image (that `pinbot.exe` inside, served at `/download/pinbot.exe` and `/download/pinbot.json`) and
  the updater image for amd64 and arm64, and pushes them to ghcr.io under the release number;
- publishes a GitHub Release with `pinbot.exe`, `pinbot.json`, `pinbot-deploy.zip` (a new server's setup files) and
  `server-release.json` (both images by digest, signed with the same key), and only then moves the images' `latest`
  tags, which a brand-new server starts from.

Each server's `updater` container (`src/PinBot.Updater`) checks the releases every hour. `early` servers (the
maintainer's) install a release at once; `stable` servers (the default) once it has been out 48 hours, so **deleting a
release on GitHub within 48 hours stops it** from reaching them (a server set up during that time starts with the
newest release). The updater verifies the signature against the key built into it, pulls the images by digest, swaps
the server and waits (up to 30 minutes) for `/healthz` to report the new release. If the server stops instead, it goes
back to the previous image and never tries that release again; if the updater itself is interrupted half way, it puts
the previous server back when it next starts. It never installs an older release, keeps only the current and previous
images, and finally replaces itself with the release's updater. The server posts in the info channel when it was
updated (the lines starting `Players:` in the maintainer's commit messages), went back, or skipped a release.

A VPin running a release checks the manifest when it starts and whenever the server offers a newer release. When it
is signed with the same key, and no table is running, it downloads it, checks size, hash and signature, checks again
that no table has started, and starts it in update mode: the new exe stops the running one, installs itself in
`%LOCALAPPDATA%\PinBot` and starts minimized. Pairing, competitions, journals and queued results are kept. Older or
differently signed releases are never installed, so a compromised server or network can withhold updates but cannot
push code. Every community's VPins and servers trust the maintainer's release key; without it no further release
reaches existing VPins, and each would need one manual install of a build with a new key. Development builds (no key)
never update themselves.

`publish.ps1` builds `pinbot.exe` and `pinbot.json` locally for testing, with a local key that real VPins don't accept.
