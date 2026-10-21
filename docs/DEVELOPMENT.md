# Development

For developers: building, testing and releasing PinBot. How the parts fit together: [SERVER.md](SERVER.md).

## Build and test

Requires Windows (the VPin agent is a Windows program) and the .NET 10 SDK. The memory maps are a pinned git submodule.

```text
git submodule update --init
dotnet test
```

The server tests run the real server against PostgreSQL. Point them at any PostgreSQL 13+ you can create databases on,
with an admin connection string in the environment variable `PINBOT_TEST_PG` or in a file named `.pgdev` at the
repository root (git-ignored), e.g. `Host=localhost;Username=postgres;Password=…;Database=postgres`. Each test creates
and drops its own database. CI uses the PostgreSQL that GitHub's Windows runners ship.

To check all ~500 reference NVRAM files, set `PINBOT_NVRAM_CORPUS` to the `testdata` folder of a
[francisdb/pinmame-nvram](https://github.com/francisdb/pinmame-nvram) checkout.

`publish.ps1` builds `pinbot.exe` and `pinbot.json` in `dist/`, signed with a local test key that real VPins don't
accept.

## Layout

- `src/PinBot.Agent`: the VPin agent. `Scoring/` decodes memory maps and diffs NVRAM, `Discovery/` finds VPX,
  tables, ROMs and NVRAM, `Sessions/` watches VPX, `Nvram/` prepares and restores competition files.
- `src/PinBot.Server`: the server and Discord bot. `src/PinBot.Updater`: keeps a server on the latest release.
- `src/PinBot.Contracts`: what the agent, server and updater exchange, and release signing.
- `tools/`: the signing tool, the variant list generator, and an emulator self-test.
- `tests/`: `Fixtures/nvram` holds 20 real `.nv` files from the reference corpus.

## Releasing

Push to `master`. A push that changes what ships (`src/`, `deploy/`, the maps, `publish.ps1`, the signing tool,
`NOTICE.md` or the setup guides) becomes a signed release once the checks pass; servers install it and their VPins
follow. Other pushes (docs, tests) make no release. To stop a bad release, delete it on GitHub within 48 hours.

A line starting `Players:` in a commit message is posted in every server's info channel when that release arrives, e.g.
`Players: /pinbot guest lets a friend's scores count for 24 hours.` Only the maintainer's and the maps workflow's
commits are used.

The weekly memory maps workflow merges and releases map updates that only add ROMs; any other map change opens a pull
request. Dependabot opens monthly pull requests for the pinned GitHub Actions and base images.
