# Security

## Reporting a problem

Please don't open a public issue for a security problem. Report it privately through GitHub:
**Security → Report a vulnerability** on this repository. Include what you found, how to reproduce it, and what an
attacker could do with it. You'll get an answer within a week; fixes go out as a new signed release.

Only the latest release is supported. VPins update themselves to it once their server has it.

## What matters most

- **VPin auto-update.** A VPin only runs a new `pinbot.exe` whose ECDSA P-256 signature matches the public key
  built into the copy it already has ([SERVER.md](docs/SERVER.md#releases-and-vpin-auto-update)). Anything that gets a
  VPin to run unsigned code is critical.
- **VPin files.** The agent changes NVRAM files only for a competition and puts the originals back
  ([NVRAM-SAFETY.md](docs/NVRAM-SAFETY.md)). The only other files it writes are a shared POV or script fix its owner
  pressed **Install** for (below). Anything that makes it write, delete or overwrite other files is critical.
- **Shared POVs and script fixes** ([SHARING.md](docs/SHARING.md)). A VPX script is a program that runs with the
  player's rights, so a script fix is the one place a member's VPin runs code another member wrote. It is never
  pushed: the member reads the changed lines in the post and installs it with a click. The VPin checks every install
  itself and doesn't trust the server: a POV must be only `View…` settings with numbers; a fix must apply to exactly
  the script it was made from and produce exactly the shared one (SHA-256), and its added lines may not create
  objects, run programs, or touch files, the registry or the network. It is written as a `.vbs` beside the table (the
  `.vpx` is never changed), never during a competition on that game, with the previous file kept for Undo. That word
  check is a safety net, not a sandbox: a fix that gets a script to do harm without those words would get past it,
  which is why the member reads it first. Anyone on the Discord server can share a fix; limiting that to a role is
  planned. Anything that gets a fix installed without its owner's click, on a different script, or outside the
  table's own `.vbs` is critical.
- **Server endpoints.** Discord interactions must carry a valid Ed25519 signature; VPin calls need the credential
  issued at pairing; pairing codes are single use, short-lived and rate-limited per client. The optional admin API is
  off unless `PINBOT_ADMIN_TOKEN` is set, needs that token, and is never forwarded by the public address.
- **Competition results.** Scoring runs on a friendly trust basis: a VPin owner can always edit their own NVRAM.
  Faking scores from your own VPin is out of scope; changing someone else's results is not. The server accepts a
  result only for a ROM in the sending VPin's own table list.

## Running your own server

- Keep `deploy/.env` private: it holds the database password, Tailscale key, Discord bot token and any admin token.
  If one leaks, rotate it (Discord developer portal, Tailscale admin console, a new random string for the others).
- The public address only forwards the paths listed in `deploy/tailscale/serve.json` (health, pairing, agent,
  Discord, downloads). Keep it that way: the admin API is meant for your LAN, and only when you set a token.
- The `updater` container is given the Docker socket so it can swap the server for a new release: that is full control
  of Docker on the host. Once running, it installs only images named by digest in a release signed with the release
  key, and only recreates PinBot's server and updater. The very first `docker compose up`, and each **Build** in
  Container Manager, fetch the `latest` images from ghcr.io as they are; `latest` only ever points at a published,
  signed release.

## Releases (maintainer)

- The release key signs every `pinbot.exe` and every server release. Its private half lives offline as a backup and as
  the secret `UPDATE_SIGNING_KEY` in the repository's `release` environment, which only `master` can use; the release
  workflow writes it only for its signing steps, never while code builds, and checks it against the committed public
  key `keys/update-signing.pub`. Whoever controls the GitHub account or that secret can push code to every PinBot server
  and VPin: keep two-factor authentication on, and rotate the key (one manual reinstall of every VPin and server) if it
  may have leaked.
- Never commit `keys/update-signing.pem`. If it is lost, VPins must be updated by hand with a `pinbot.exe` signed by
  a new key.
- Workflow actions are pinned by commit and base images by digest; Dependabot proposes updates as pull requests.
- A bad release can be stopped for stable servers by deleting it on GitHub within 48 hours of publishing (a server set
  up during those 48 hours starts with the newest release).
