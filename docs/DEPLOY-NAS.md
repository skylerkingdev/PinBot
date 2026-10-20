# Deploying the PinBot server on a Synology NAS (DS220+, DSM 7.2+)

The result:
- three always-on containers: PinBot server, PostgreSQL, and Tailscale (a public HTTPS address with **no router ports opened**);
- a nightly database backup on the NAS's own storage.

Cost: $0.

## 1. One-time accounts and settings (you)

1. **Container Manager:** DSM → Package Center → install *Container Manager*. It requires DSM 7.2 or later.
2. **Tailscale**, a free account at https://login.tailscale.com. In the admin console:
   - **DNS:** turn on *MagicDNS*, then turn on *HTTPS Certificates*.
   - **Access controls:** the policy must allow Funnel. New tailnets usually contain this already; add it if missing:

     ```json
     "nodeAttrs": [ { "target": ["autogroup:member"], "attr": ["funnel"] } ]
     ```

   - **Settings → Keys → Generate auth key.** Set *Reusable* off, *Ephemeral* **off** (an ephemeral device is deleted when the NAS reboots, and the public address changes), and *Tags* off (tagged devices are not covered by the Funnel rule above). Copy the key.

## 2. Copy the files to the NAS

1. Download `pinbot-deploy.zip` from the latest release on the PinBot GitHub page and extract it on your PC. You get
   `deploy/` (and these guides). It is uploaded to `/volume1/docker/pinbot` in step 3. (Container Manager creates the
   `docker` shared folder.) Nothing is built on the NAS: it runs the signed release images published with each release.
2. Create the secrets file **on your PC**, in the extracted `deploy` folder, before uploading:

   ```bash
   powershell -ExecutionPolicy Bypass -File make-env.ps1
   ```

   It asks for the Tailscale key and writes `.env` with a random database password, using clean line endings. Upload `.env` into `deploy/` on the NAS with the other files, and keep it private.

   To write `.env` by hand instead, follow `.env.example` and save it with Unix (LF) line endings.

## 3. Start it

1. **File Station:** inside the `docker` shared folder, create a folder `pinbot` and upload `deploy` into it. The result is `/volume1/docker/pinbot/deploy/…`, including `deploy/.env`.
2. Still in File Station, create the empty folders `data`, `data/postgres`, `data/tailscale`, `data/updater` and `backups` inside `deploy/`. Synology's Docker does not create missing bind-mount folders; without them it stops with "Bind mount failed".
3. Container Manager → **Project** → **Create**.
4. Name `pinbot`, path `/volume1/docker/pinbot/deploy`, then *Use existing docker-compose.yml*.
5. Click **Build** / **Start**.

The first start downloads the images (a few minutes). After that, all five containers (`db`, `server`, `updater`, `tailscale`, `backup`) restart automatically, including after a NAS reboot.

## 4. Check it

- **On your LAN:** `http://<NAS-IP>:5080/healthz` returns `{"status":"ok"}`.
- **Public address:** the Tailscale admin console → *Machines* lists a machine named `pinbot`. Its address is `https://pinbot.<your-tailnet>.ts.net`. From a phone **on mobile data**, `https://pinbot.<your-tailnet>.ts.net/healthz` must also return `{"status":"ok"}`. That is the address Discord and remote VPins use.
- **Disable key expiry, once:** Tailscale admin console → *Machines* → `pinbot` → **⋯** → **Disable key expiry**. Otherwise the device must re-authenticate after 180 days and the public address silently stops working.
- **Admin API (optional, LAN only):** off unless you set `PINBOT_ADMIN_TOKEN` in `.env` (any long random string). Funnel deliberately does not expose it. Discord does everything day to day; the API is for scripts, e.g.:

  ```bash
  powershell -Command "Invoke-RestMethod -Method Post http://<NAS-IP>:5080/api/v1/admin/pairing-codes -Headers @{Authorization='Bearer <PINBOT_ADMIN_TOKEN>'}"
  ```

## 5. Pair a VPin

Once Discord is set up, anyone pairs their own VPin: `/pinbot pair` in Discord, download `pinbot.exe` from the link, double-click it and paste the line (see [VPIN.md](VPIN.md)). Without Discord, on the VPin with the code from step 4:

```bash
pinbot pair https://pinbot.<your-tailnet>.ts.net ABCDE-FGHJK --name "VPin"
```

## Updating, backups, moving

- **Updates are automatic.** The `updater` container checks GitHub every hour and installs each new signed release
  once it has been out 48 hours (a release the maintainer withdraws within that time never reaches you). It checks the
  release's signature, switches the server over, and if the new server does not start it goes back to the previous one
  and skips that release. Your info channel says when PinBot was updated, and what changed for players. VPins update
  themselves from your server afterwards, when no table is running. Its log: Container Manager → **Container** →
  `pinbot-updater-1` → **Log**. `PINBOT_UPDATE_RING=early` in `.env` installs releases at once (the maintainer's own
  server). Database, Tailscale and backups are never touched; the database is kept in `deploy/data/postgres`.
- **Changed `docker-compose.yml`** (rare, announced with the release): copy the new one over the old (keep `.env` and
  `data`), then Container Manager → project → **Action → Build**.
- **Backups:** `deploy/backups/pinbot-YYYYMMDD.dump`, one per day, 14 days kept. Include the folder in Hyper Backup if you use it. To restore into a fresh database:

  ```bash
  pg_restore -h <host> -U pinbot -d pinbot --clean pinbot-YYYYMMDD.dump
  ```

- **Moving to another host** (another NAS, a cloud container): copy `deploy/` including `data/` (or restore a dump) and start the same compose file. VPins keep working if they were paired to the same Funnel name, or re-pair once.
