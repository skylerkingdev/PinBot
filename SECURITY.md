# Security

## Reporting a problem

Please don't open a public issue for a security problem. Report it privately through GitHub:
**Security → Report a vulnerability** on this repository. Include what you found, how to reproduce it, and what an
attacker could do with it. You'll get an answer within a week; fixes go out as a new signed release.

Only the latest release is supported. VPins update themselves to it once their server has it.

## What matters most

- **VPin auto-update.** A VPin only runs a new `pinbot.exe` whose ECDSA P-256 signature matches the public key
  built into the copy it already has (docs/SERVER.md). Anything that gets a VPin to run unsigned code is critical.
- **VPin files.** The agent changes NVRAM files only for a competition and puts the originals back
  (docs/NVRAM-SAFETY.md). Anything that makes it write, delete or overwrite other files is critical.
- **Server endpoints.** Discord interactions must carry a valid Ed25519 signature; VPin calls need the credential
  issued at pairing; pairing codes are single use, short-lived and rate-limited. The optional admin API is off unless
  `PINBOT_ADMIN_TOKEN` is set, needs that token, and is never forwarded by the public address.
- **Competition results.** Scoring runs on a friendly trust basis: a VPin owner can always edit their own NVRAM.
  Faking scores from your own VPin is out of scope; changing someone else's results is not. The server accepts a
  result only for a ROM in the sending VPin's own table list.

## Running your own server

- Keep `deploy/.env` private: it holds the database password, Tailscale key, Discord bot token and any admin token.
  If one leaks, rotate it (Discord developer portal, Tailscale admin console, a new random string for the others).
- The public address only forwards the paths listed in `deploy/tailscale/serve.json` (health, pairing, agent,
  Discord, downloads). Keep it that way: the admin API is meant for your LAN, and only when you set a token.
- The `updater` container is given the Docker socket so it can swap the server for a new release: that is full control
  of Docker on the host. It only ever pulls images named by digest in a release signed with the release key, and only
  recreates PinBot's server and updater.

## Releases (maintainer)

- The release key signs every `pinbot.exe` and every server release. It lives as the secret `UPDATE_SIGNING_KEY` in
  the repository's `release` environment, which only `master` can use, and offline as a backup. Whoever controls the
  GitHub account or that secret can push code to every PinBot server and VPin: keep two-factor authentication on, and
  rotate the key (one manual reinstall of every VPin and server) if it may have leaked.
- Never commit `keys/update-signing.pem`. If it is lost, VPins must be updated by hand with a `pinbot.exe` signed by
  a new key.
- A bad release can be stopped for stable servers by deleting it on GitHub within 48 hours of publishing.
