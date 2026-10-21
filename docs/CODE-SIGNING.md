# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

For developers and anyone checking what they install. Once the SignPath Foundation application is approved, every
released `pinbot.exe` is Authenticode-signed and Windows shows SignPath Foundation as its publisher; until then releases
are not Windows-signed (they are always signed for auto-update, see below). Only `pinbot.exe` built from
this repository is signed; the server runs in Docker and is not.

## How a release is signed

1. A push to `master` starts the release workflow (`.github/workflows/release.yml`). GitHub-hosted runners build
   `pinbot.exe` from this repository's sources with `publish.ps1`.
2. The workflow sends the unsigned `pinbot.exe` to SignPath. A maintainer approves the signing request by hand; nothing
   is signed without that approval.
3. The workflow checks the Authenticode signature, then writes the update manifest (`pinbot.json`) for the signed
   file with the project's own release key. VPins install a new `pinbot.exe` only when that manifest's signature matches
   the key built into the copy they have (see [SERVER.md](SERVER.md#releases-and-vpin-auto-update)).
4. The signed `pinbot.exe` is published as a GitHub Release.

## Team

- Committers and reviewers: [skylerkingdev](https://github.com/skylerkingdev)
- Approvers: [skylerkingdev](https://github.com/skylerkingdev)

Changes from anyone else come as pull requests and are reviewed by a committer before they are merged. Everyone on the
team uses multi-factor authentication for GitHub and SignPath.

## Privacy

`pinbot.exe` sends data only to the PinBot server it was paired with: the address in the pairing line you paste
during setup, run by your Discord community. It sends the VPin's name (the Windows PC name unless you give another),
its table list and ROM names, the high scores and initials read from the games during competitions, whether a game is
being played, which competitions it is ready for, and its version. It downloads updates from that same server. It sends nothing to the PinBot developers or anyone else, and has no telemetry.
Unpairing or uninstalling ([VPIN.md](VPIN.md)) stops it.

The server then posts competitions and results to your Discord server; see [SERVER.md](SERVER.md).

## SignPath setup (maintainers)

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

The workflow expects project slug `PinBot` and signing policy slug `release-signing`, and two secrets in the `release`
environment: `SIGNPATH_API_TOKEN` (a SignPath CI user with submitter rights) and `SIGNPATH_ORGANIZATION_ID`. Without
the token, releases still ship, unsigned by Authenticode.
