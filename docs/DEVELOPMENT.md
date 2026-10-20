# Development

Requires the .NET 10 SDK. The memory maps are a pinned git submodule.

```bash
git submodule update --init
```

```bash
dotnet test
```

`publish.ps1` builds a release locally (`pinbot.exe` and the server package in `dist/`).

## Layout

- `src/PinBot.Agent`: the VPin agent. `Scoring/` decodes memory maps and diffs NVRAM, `Discovery/` finds VPX,
  tables, ROMs and NVRAM, `Sessions/` watches VPX, `Nvram/` prepares and restores competition files.
- `src/PinBot.Server`: the server and Discord bot. `src/PinBot.Updater`: keeps a server on the latest release.
- `tests/`: `Fixtures/nvram` holds 17 real `.nv` files across 9 hardware platforms.

To check all ~500 reference NVRAM files, set `PINBOT_NVRAM_CORPUS` to the `testdata` folder of a
[francisdb/pinmame-nvram](https://github.com/francisdb/pinmame-nvram) checkout.

## Releasing

Push to `master`. Once CI passes, GitHub builds, signs and publishes the release; servers install it and their
VPins follow. To stop a bad release, delete it on GitHub within 48 hours.

A line starting `Players:` in a commit message is posted in every server's info channel when that release arrives,
e.g. `Players: /pinbot guest lets a friend's scores count for 24 hours.`

The weekly memory maps workflow merges and releases map updates that only add ROMs; any other map change opens a pull
request.

How it works: [SERVER.md](SERVER.md#releases-and-vpin-auto-update).
