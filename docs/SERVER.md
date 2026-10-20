# PinBot server

One portable ASP.NET Core process with PostgreSQL. It runs `/pinbot` in Discord (docs/DISCORD-SETUP.md) and serves the VPin agents.

## Run

Requirements: the .NET 10 runtime (ASP.NET Core) and PostgreSQL 13+ (the `btree_gist` extension ships with standard PostgreSQL). The schema is created on startup.

| Setting (environment variable) | Meaning |
|---|---|
| `ConnectionStrings__PinBot` | `Host=…;Database=pinbot;Username=…;Password=…` |
| `PinBot__AdminToken` | Optional: turns on the admin API with this bearer token; off when unset |
| `PinBot__UploadGraceMinutes` | Minutes after a deadline in which results of games finished in time must arrive (default 5); then the standings are final |
| `ASPNETCORE_URLS` | e.g. `http://0.0.0.0:5080` |

Put TLS in front with a reverse proxy, or use the host's HTTPS. Agents warn when pairing over plain HTTP.

```bash
dotnet run --project src/PinBot.Server
```

## Rules the server enforces

- **One competition per game at a time.** A PostgreSQL exclusion constraint over the playing window, plus a per-game lock so concurrent starts fail cleanly with 409, never with a deadlock.
- **A ROM revision is its own game unless explicitly grouped** (`PUT /admin/rom-aliases`). The server never groups by ROM name prefix.
- **A result counts when:**
  - the session exited at or after the start and strictly before the (possibly early) end;
  - it arrived before the end plus the delivery window;
  - its ROM belongs to the competition's game;
  - its initials match exactly one member.

  Unknown or ambiguous initials are stored as `unattributed` and not counted.
- **Exactly once:** results deduplicate on `eventId`. A replay returns the original outcome; an id reused with different content is rejected.
- **Accepted scores keep their member.** A later nickname change does not move them.
- **Leaderboard:** best accepted score per member. Ties share a rank (1, 1, 3).
- **Status is computed from the clock** (`scheduled`, `active`, `finalizing`, `complete`, `cancelled`). No scheduler is needed for deadlines.

## Agent API (VPin credential as Bearer token)

| | |
|---|---|
| `POST /api/v1/pair/redeem` | `{code, cabinetName}` → `{cabinetId, credential}`; single-use 15-minute code, rate-limited |
| `PUT /api/v1/agent/inventory` | full table list; replaces the previous one |
| `GET /api/v1/agent/competitions` | competitions this VPin can take part in, up to the end of their delivery window |
| `POST /api/v1/agent/results` | batch of result events → final outcome per event |

## Admin API (optional; `Authorization: Bearer <PinBot__AdminToken>`)

Off unless `PinBot__AdminToken` is set, and not forwarded by the public address. Discord covers day-to-day use; this is for
scripts, tests, and the two things Discord has no command for: revoking a VPin and grouping ROMs.

| | |
|---|---|
| `POST /api/v1/admin/pairing-codes` | new pairing code |
| `POST /api/v1/admin/cabinets/{id}/revoke` | revoke a VPin credential |
| `GET /api/v1/admin/games` | games with an eligible table on at least one VPin |
| `PUT /api/v1/admin/rom-aliases` | `{romToGame: {"afm_113": "attack-from-mars", …}}` (curated grouping) |
| `PUT /api/v1/admin/members` | `{members: [{discordUserId, displayName, isBot}]}`; tags like `Name \| [SKY,AVK]` |
| `GET/POST /api/v1/admin/competitions` | list / start `{game, hours, kind?, startsAtUtc?}` → 201, 409 (overlap) or 422 (not eligible) |
| `POST /api/v1/admin/competitions/{id}/end` | end early |
| `GET /api/v1/admin/competitions/{id}/leaderboard` | ranking |

## VPin side

```bash
pinbot pair https://pinbot.example.org ABCDE-FGHJK --name "Living room"
```

```bash
pinbot watch
```

When paired, `watch` does the following:
- Sends the table inventory.
- Pulls competitions every 60 s and caches them, so NVRAM is still prepared and restored offline.
- Writes each counting score to `%LOCALAPPDATA%\PinBot\outbox` before uploading it, and retries with backoff (up to 15 min) until the server gives a final answer.

The credential is stored encrypted with Windows DPAPI for the current user.

## One ROM revision is one game

A game is one exact ROM revision: revisions of the same machine can differ in rules and scoring, so they are never
compared on one leaderboard. When VPins have different revisions of a machine, `/pinbot games` lists each with its
ROM in the title (e.g. `Cirqus Voltaire (Bally 1997) · cv_14` and `· cv_20h`); to compete together, put the VPins on
the same ROM. A deliberate exception can still be made with `PUT /api/v1/admin/rom-aliases`.

## Releases and VPin auto-update

Releases are made by GitHub (`.github/workflows/release.yml`): every push to `master` that changes `src/`, `deploy/`,
the maps or the signing tool becomes one once the CI checks pass. The workflow
- builds `pinbot.exe` with the release key's public key and a release number (UTC `yyyyMMddHHmmss`) inside, and writes
  `pinbot.json` (version, size, SHA-256, ECDSA P-256 signature) with the private key, the secret `UPDATE_SIGNING_KEY`
  of the protected `release` environment (only `master` can use it);
- builds the server image (that `pinbot.exe` inside, served at `/download/pinbot.exe` and `/download/pinbot.json`) and
  the updater image for amd64 and arm64, and pushes both to ghcr.io;
- publishes a GitHub Release with `pinbot.exe`, `pinbot.json`, `pinbot-deploy.zip` (a new server's setup files) and
  `server-release.json`: both images by digest, signed with the same key.

Each server's `updater` container (`src/PinBot.Updater`) checks the releases every hour. `early` servers (the
maintainer's) install a release at once; `stable` servers (the default) once it has been out 48 hours, so **deleting a
release on GitHub within 48 hours stops it** from reaching them. The updater verifies the signature against the key
built into it, pulls the images by digest, swaps the server, and checks `/healthz` reports the new release; if not, it
goes back to the previous image and never tries that release again. It never installs an older release. The server
posts in the info channel when it was updated (the lines starting `Players:` in the release's commit messages), went
back, or skipped a release.

`publish.ps1` still builds the same release locally (creating `keys\` on its first run) for testing.

A VPin running a release checks the manifest when it starts and whenever the server announces a release (the waiting
connection carries the release number). When the server offers a newer release signed with the
same key, and no table is running, it downloads it, checks size, hash and signature, and starts it in update
mode: the new exe stops the running one, installs itself in `%LOCALAPPDATA%\PinBot` and starts minimized. Pairing,
competitions, journals and queued results are kept. Older or differently signed releases are never installed, so a
compromised server or network can withhold updates but cannot push code. All communities' VPins and servers trust
the maintainer's release key. Without the private key no further release reaches existing VPins; each would need
one manual install of a build with a new key. Development builds (no key) never update themselves.

Exception: a **coin play / free play build** of a ROM is the same game as the ROM itself (e.g. `cv_20hc` and `cv_20h`,
`ww_lh6c` and `ww_lh6`, `tom_14hb` and `tom_14h`): PinMAME names them identically apart from "Coin Play"/"Free Play"
and they share one memory map. The list is generated from the pinned maps (`tools/PinBot.Variants`, written to
`src/PinBot.Server/rom-variants.json`; re-run it after updating the maps) and applied at every server start.
