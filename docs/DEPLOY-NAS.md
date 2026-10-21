# Running the PinBot server

For the community admin: run the PinBot server on a Synology NAS (DS220+ or similar, DSM 7.2+) or any other always-on
Docker host. About 30 minutes. Then [connect it to Discord](https://github.com/skylerkingdev/PinBot/blob/master/docs/DISCORD-SETUP.md).

The result:
- five always-on containers: the PinBot server, its PostgreSQL database, Tailscale (a public HTTPS address with
  **no router ports opened**), the updater (installs new PinBot releases by itself) and a nightly database backup;
- nothing to build and nothing to maintain.

Cost: $0.

## 1. One-time accounts and settings

1. **Synology only, Container Manager:** DSM → Package Center → install *Container Manager* (DSM 7.2 or later).
2. **Tailscale**, a free account at https://login.tailscale.com. In the admin console:
   - **DNS:** turn on *MagicDNS*, then turn on *HTTPS Certificates*.
   - **Access controls:** the policy must allow Funnel. New tailnets usually contain this already; add it if missing:

     ```json
     "nodeAttrs": [ { "target": ["autogroup:member"], "attr": ["funnel"] } ]
     ```

   - **Settings → Keys → Generate auth key.** Set *Reusable* off, *Ephemeral* **off** (an ephemeral device is deleted
     when the NAS reboots, and the public address changes), and *Tags* off (tagged devices are not covered by the
     Funnel rule above). Copy the key.

## 2. Prepare the files on your PC

1. Download `pinbot-deploy.zip` from the [latest release](https://github.com/skylerkingdev/PinBot/releases/latest) and
   extract it. You get a `deploy` folder (and these guides).
2. Create the secrets file: open the extracted `deploy` folder in Explorer, **Shift + right-click** an empty spot →
   **Open PowerShell window here**, and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File make-env.ps1
   ```

   It asks for the Tailscale key and writes `.env` with a random database password, using the line endings Docker
   needs. Keep `.env` private. (To write it by hand instead, follow `.env.example` and save it with Unix (LF) line
   endings.)

## 3. Start it

### On a Synology NAS

1. **File Station:** inside the `docker` shared folder, create a folder `pinbot` and upload `deploy` into it. The
   result is `/volume1/docker/pinbot/deploy/…`, including `deploy/.env`.
2. Still in File Station, create the empty folders `data`, `data/postgres`, `data/tailscale`, `data/updater` and
   `backups` inside `deploy/`. Synology's Docker does not create missing folders; without them it stops with
   "Bind mount failed".
3. Container Manager → **Project** → **Create**: name `pinbot`, path `/volume1/docker/pinbot/deploy`, then
   *Use existing docker-compose.yml*.
4. Click **Build** / **Start**. The first start downloads the images (a few minutes). After that, all five containers
   (`db`, `server`, `updater`, `tailscale`, `backup`) restart automatically, including after a NAS reboot.

### On any other Docker host

Copy the `deploy` folder (with `.env`) to the host, then in that folder:

```text
mkdir -p data/postgres data/tailscale data/updater backups
docker compose up -d
```

The updater needs the Docker socket (`/var/run/docker.sock`) and the folder mounted as it is in `docker-compose.yml`.

## 4. Check it

- **On your LAN:** `http://<NAS-IP>:5080/healthz` answers `{"status":"ok", …}`.
- **Public address:** the Tailscale admin console → *Machines* lists a machine named `pinbot`. Its address is
  `https://pinbot.<your-tailnet>.ts.net`. From a phone **on mobile data**, `https://pinbot.<your-tailnet>.ts.net/healthz`
  must answer too. That is the address Discord and remote VPins use.
- **Disable key expiry, once:** Tailscale admin console → *Machines* → `pinbot` → **⋯** → **Disable key expiry**.
  Otherwise the device must sign in again after 180 days and the public address silently stops working.

Next: [connect it to Discord](https://github.com/skylerkingdev/PinBot/blob/master/docs/DISCORD-SETUP.md). Once that is
done, members connect their VPins with `/pinbot pair`.

## Updates, backups, moving

- **Updates are automatic.** The `updater` container checks GitHub every hour and installs each new signed release
  once it has been out 48 hours (a release the maintainer withdraws within that time never reaches you; only a server
  set up during those 48 hours starts with the newest release straight away). It checks the release's signature,
  switches the server over, and if the new server does not start it goes back to the previous one and skips that
  release. Your info channel says when PinBot was updated and what changed. VPins update themselves from your server
  afterwards, when no table is running. Its log: Container Manager → **Container** → `pinbot-updater-1` → **Log**.
  Database, Tailscale and backups are never touched; the database is kept in `deploy/data/postgres`.
- **Changed `docker-compose.yml`** (rare, announced with the release): copy the new one over the old (keep `.env` and
  `data`), then Container Manager → project → **Action → Build** (or `docker compose up -d`).
- **Backups:** `deploy/backups/pinbot-YYYYMMDD.dump`, one per day, 14 days kept. Include the folder in Hyper Backup if
  you use it. To restore one, open a terminal on the `backup` container (Container Manager → **Container** →
  `pinbot-backup-1` → **Action → Open terminal**, or `docker exec -it pinbot-backup-1 sh`) and run:

  ```text
  pg_restore -h db -U pinbot -d pinbot --clean /backups/pinbot-YYYYMMDD.dump
  ```

- **Moving to another host:** copy `deploy/` including `data/` (or restore a dump) and start the same compose file.
  VPins keep working if the public address stays the same; otherwise they pair once more.

## Optional: the admin API

For scripts on your LAN only, off unless you set `PINBOT_ADMIN_TOKEN` in `.env` to a long random string. The public
address never forwards it. Discord does everything day to day; the API can also remove a VPin and group ROM revisions
([reference](https://github.com/skylerkingdev/PinBot/blob/master/docs/SERVER.md#admin-api-optional)).

```powershell
Invoke-RestMethod -Method Post http://<NAS-IP>:5080/api/v1/admin/pairing-codes -Headers @{Authorization='Bearer <PINBOT_ADMIN_TOKEN>'}
```
