# How releases are signed

For developers and anyone checking what they install.

Every release is built from this repository by GitHub Actions and signed with the project's own release key. PinBot
servers and VPins install an update only when its signature matches the key built into the copy they already have, so
nobody else (not even someone who takes over the download server) can push an update to them.

`pinbot.exe` is **not signed with a Windows code-signing certificate**. That's why Windows warns the first time you run
it (*unknown publisher* or *Windows protected your PC*): choose **More info → Run anyway**. Updates after that install
themselves without a warning. The server runs in Docker and is not Windows-signed either.

## How a release is signed

1. A push to `master` starts the release workflow (`.github/workflows/release.yml`), after the same tests as CI pass.
   GitHub-hosted runners build `pinbot.exe` and the server images from this repository's sources. No secret is present
   while anything builds: the builds get only the committed public key (`keys/update-signing.pub`).
2. The workflow writes the update manifest for `pinbot.exe` (`pinbot.json`: its SHA-256 and size) and the server
   release description (`server-release.json`: both images by digest), and signs them with the release key. The key is
   a secret of the protected `release` environment, only on `master`, and exists only during those two steps, which
   check it against the committed public key.
3. Everything is published as a GitHub Release. VPins update from their server; servers' updaters install the release
   (see [SERVER.md](SERVER.md#releases-and-vpin-auto-update)).

To check a download yourself: its SHA-256 is in the release's `pinbot.json`.

## Team

- Committers and reviewers: [skylerkingdev](https://github.com/skylerkingdev)
- Release key holder: [skylerkingdev](https://github.com/skylerkingdev)

Changes from anyone else come as pull requests and are reviewed by a committer before they are merged. Everyone on the
team uses multi-factor authentication for GitHub.

## Privacy

`pinbot.exe` sends data only to the PinBot server it was paired with: the address in the pairing line you paste
during setup, run by your Discord community. It sends the VPin's name (the Windows PC name unless you give another),
its table list and ROM names, the high scores and initials read from the games during competitions, whether a game is
being played, which competitions it is ready for, and its version. It downloads updates from that same server. It sends nothing to the PinBot developers or anyone else, and has no telemetry.
Unpairing or uninstalling ([VPIN.md](VPIN.md)) stops it.

The server then posts competitions and results to your Discord server; see [SERVER.md](SERVER.md).

## Windows signing (optional, maintainers)

The release workflow can also Authenticode-sign `pinbot.exe` through [SignPath](https://about.signpath.io), before
the update manifest is written. It is off: it runs only when the `release` environment has the secrets
`SIGNPATH_API_TOKEN` (a SignPath CI user with submitter rights) and `SIGNPATH_ORGANIZATION_ID`, with project slug
`PinBot` and signing policy slug `release-signing`. A maintainer then approves each release in SignPath.

Artifact configuration (the workflow uploads `pinbot.exe` with `actions/upload-artifact`, which zips it):

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="pinbot.exe" product-name="PinBot" product-version="0.0.1">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

`product-version` is the agent's `<Version>` (`src/PinBot.Agent/PinBot.Agent.csproj`), the same in every build; the
release number is separate. Change both together.
