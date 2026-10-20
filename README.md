# PinBot

Automated virtual pinball high-score competitions for your Discord community.

Turn on your VPin and play. If a competition is running on that game, your score shows up on the Discord
leaderboard. Nobody signs up or joins: PinBot reads the high scores from the game itself and matches your initials
to your Discord nickname.

Automatic from end to end:

- **Joining:** nobody signs up. Your initials in your Discord nickname, e.g. `Name | [ABC]`, are enough.
- **Scores:** read from the game after each play. No screenshots, no forms.
- **Setup:** paste one line on the VPin. PinBot finds Visual Pinball X and your tables by itself.
- **Updates:** the server and every VPin install new signed releases by themselves. Nothing to maintain.

Your own high scores are backed up before a competition and restored byte-for-byte after it
([how](docs/NVRAM-SAFETY.md)). PinBot uses about 20 MB of memory and sleeps while a table runs.

A VPin is any Windows PC with Visual Pinball X: desktop, laptop or cabinet. PinBot works with ROM-based tables.
The PinBot server runs in Docker and is hosting-agnostic. Self-host it on your own hardware with no hosting fees, or
deploy it to a compatible cloud provider.

## Setup

1. **Server:** [run it on a Synology NAS](docs/DEPLOY-NAS.md) or any Docker host.
2. **Discord:** [connect the bot to your server](docs/DISCORD-SETUP.md) (about 15 minutes).
3. **VPins:** each player does this once:
   1. In Discord, run `/pinbot pair`. You get a download link and a one-time pairing line.
   2. Run `pinbot.exe` on the VPin. If Windows warns about an unknown publisher: **More info → Run anyway**.
   3. Paste the line and press Enter. PinBot installs itself and starts with Windows.
   4. Add your initials to your Discord nickname, e.g. `Name | [ABC]`.

## Commands

| Command | |
|---|---|
| `/pinbot start` | Start a competition on a game (1 hour to 7 days) |
| `/pinbot status` | Running competitions, leaders and VPins |
| `/pinbot games` | Games you can start a competition on |
| `/pinbot guest` | Let a guest score with their own initials for 24 hours |
| `/pinbot end` | End a competition early |
| `/pinbot pair` | Connect a VPin |
| `/pinbot-admin weekly` | Automatic weekly competition |
| `/pinbot-admin channels` | Where PinBot posts |

## Documentation

- [On the VPin](docs/VPIN.md): commands, tables, files, uninstalling
- [Server](docs/SERVER.md): configuration, API, releases
- [NVRAM safety](docs/NVRAM-SAFETY.md)
- [Security](SECURITY.md)

## Development

Requires the .NET 10 SDK. More in [DEVELOPMENT.md](docs/DEVELOPMENT.md).

```bash
git submodule update --init
```

```bash
dotnet test
```

Every push to `master` that passes CI becomes a signed release
([how](docs/SERVER.md#releases-and-vpin-auto-update)).

## License

MIT. Game memory maps from [Pinball Memory Maps](https://github.com/tomlogic/pinball-memory-maps) (ODbL);
see [NOTICE.md](NOTICE.md).
