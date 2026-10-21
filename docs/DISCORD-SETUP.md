# Connecting PinBot to Discord

For the community admin: create the Discord app and connect it to your PinBot server. Do this after
[running the server](https://github.com/skylerkingdev/PinBot/blob/master/docs/DEPLOY-NAS.md). About 15 minutes; you
need *Manage Server* rights in your Discord server.

## 1. Create the application (Discord Developer Portal)

1. Open https://discord.com/developers/applications → **New Application** → name it `PinBot` → create.
2. On **General Information**, copy the **Application ID** and the **Public Key**.
3. **Bot** tab:
   - **Reset Token** → copy the **bot token**. It is shown once; keep it secret.
   - Turn **Public Bot** off, so only you can add it to servers.
   - Under *Privileged Gateway Intents*, turn **Server Members Intent** on. PinBot reads initials from server
     nicknames such as `Alex | [ALX]` in the member list.
4. **OAuth2 → URL Generator**:
   - Scopes: tick **bot** and **applications.commands**.
   - Bot permissions: **View Channels**, **Send Messages**, **Embed Links**, **Read Message History** and
     **Pin Messages** (to keep each leaderboard and the guide pinned; without it PinBot works but pins nothing).

   Open the generated URL, choose your Discord server, and **Authorize**.

## 2. Get the server and channel ids (Discord app)

1. User Settings → **Advanced** → turn on **Developer Mode**.
2. Right-click your Discord server's icon → **Copy Server ID**.
3. Right-click the channel for challenge leaderboards → **Copy Channel ID**. Optionally, do the same for a separate
   weekly channel. If a channel is private, give the PinBot role access to it.

## 3. Add the values to the PinBot server's settings

Edit `deploy/.env` (on a Synology NAS, DSM's **Text Editor** keeps its line endings intact) and set:

```
PINBOT_PUBLIC_URL=https://pinbot.<your-tailnet>.ts.net
DISCORD_APPLICATION_ID=<application id>
DISCORD_PUBLIC_KEY=<public key>
DISCORD_BOT_TOKEN=<bot token>
DISCORD_GUILD_ID=<server id>
DISCORD_CHANNEL_ID=<challenges channel id>
DISCORD_WEEKLY_CHANNEL_ID=<weekly channel id, optional>
```

Then apply it: on a Synology NAS, Container Manager → **Project** → `pinbot` → **Action → Build**; on another Docker
host, `docker compose up -d` in the `deploy` folder. This restarts the server with the new settings; nothing is
compiled.

## 4. Point Discord at PinBot

Developer Portal → your application → **General Information** → **Interactions Endpoint URL**:

```
https://pinbot.<your-tailnet>.ts.net/api/v1/discord/interactions
```

**Save.** Discord sends a signed test request first. Saving only succeeds if the server is running with the right
public key, which makes it a good end-to-end check.

## 5. Choose the info channel

In your Discord server, run:

```
/pinbot-admin channels info:#pinbot-info
```

with a channel of your own (make it read-only for members). PinBot keeps a pinned guide there, announces each newly
connected VPin, and says when PinBot was updated and what changed. Without an info channel there is no guide and no
update news. Run `/pinbot-admin channels` without options any time to see where PinBot posts.

## 6. Try it

Type `/pinbot` and start a short challenge: `/pinbot start game:<type part of a name> length:1 hour`. Its leaderboard
appears in the challenges channel within seconds. What players see: [PLAYING.md](https://github.com/skylerkingdev/PinBot/blob/master/docs/PLAYING.md).

## Admin commands

Only server managers see `/pinbot-admin`; both replies are visible only to you.

| Command | What it does |
|---|---|
| `/pinbot-admin channels [challenges] [weekly] [info]` | Where PinBot posts: challenge leaderboards, weekly leaderboards, and the info channel. Applies to new competitions at once and is kept across restarts; the channel ids in `.env` only seed a new server. Without options it shows the current channels. |
| `/pinbot-admin weekly enabled:<True/False> [day] [time] [timezone]` | The automatic weekly competition. The first time, give all three, e.g. `enabled:True day:Monday time:19:00 timezone:Eastern (US/Canada)`. Turning it on starts a weekly **right away**, running until the next slot (or the one after, if that is less than 2 days away); then a new game every week. A schedule change while one runs applies after it ends. |

Server managers may also end any challenge (`/pinbot end`) and start more than 2 at a time.

## Troubleshooting

- **Saving the endpoint URL fails:** the server isn't reachable, or the public key in `.env` doesn't match. Open
  `https://…ts.net/healthz` (it should start with `{"status":"ok"`), then check the `server` container's log.
- **Scores show as unattributed, or the log says "enable Server Members Intent":** turn the intent on (step 1.3).
  Nobody's initials can be read without it.
- **No leaderboard message:** check `/pinbot-admin channels`, and that the bot can view and post in that channel.
- **No update news or guide:** set the info channel (step 5).
