# Connecting PinBot to Discord

One-time setup: about 15 minutes. You need *Manage Server* rights in your Discord server.

## 1. Create the application (Discord Developer Portal)

1. Open https://discord.com/developers/applications → **New Application** → name it `PinBot` → create.
2. On **General Information**, copy the **Application ID** and the **Public Key**.
3. **Bot** tab:
   - **Reset Token** → copy the **bot token**. It is shown once; keep it secret.
   - Turn **Public Bot** off, so only you can add it to servers.
   - Under *Privileged Gateway Intents*, turn **Server Members Intent** on. PinBot reads nickname tags such as `Alex | [ALX]` from the member list.
4. **OAuth2 → URL Generator**:
   - Scopes: tick **bot** and **applications.commands**.
   - Bot permissions: **View Channels**, **Send Messages**, **Embed Links**, **Read Message History**, and (optional) **Pin Messages** so the leaderboard stays pinned while a competition runs. Already invited? Give the PinBot role **Pin Messages** in the PinBot channels' permissions instead.

   Open the generated URL, choose your server, and **Authorize**.

## 2. Get the server and channel ids (Discord app)

1. User Settings → **Advanced** → turn on **Developer Mode**.
2. Right-click your server icon → **Copy Server ID**.
3. Right-click the channel for one-off competitions → **Copy Channel ID**. Optionally, right-click a separate channel for the weekly competition → **Copy Channel ID**. If a channel is private, give the PinBot role access to it.

## 3. Add the values on the NAS

Edit `docker/pinbot/deploy/.env` on the NAS. DSM's **Text Editor** package keeps the file's line endings intact. Add:

```
PINBOT_PUBLIC_URL=https://pinbot.<your-tailnet>.ts.net
DISCORD_APPLICATION_ID=<application id>
DISCORD_PUBLIC_KEY=<public key>
DISCORD_BOT_TOKEN=<bot token>
DISCORD_GUILD_ID=<server id>
DISCORD_CHANNEL_ID=<one-off competitions channel id>
DISCORD_WEEKLY_CHANNEL_ID=<weekly channel id, optional>
```

Then apply it: Container Manager → **Project** → `pinbot` → **Action → Build**. This recreates the server with the new settings; nothing is compiled.

## 4. Point Discord at PinBot (after the server is running)

Developer Portal → your application → **General Information** → **Interactions Endpoint URL**:

```
https://pinbot.<your-tailnet>.ts.net/api/v1/discord/interactions
```

**Save.** Discord sends a signed test request first. Saving only succeeds if the server is running with the right public key, which makes it a good end-to-end check.

## 5. Try it

In your server, type `/pinbot`:

| Command | Who | What |
|---|---|---|
| `/pinbot start game:<search> length:<1h…7 days>` | anyone (2 running at a time; server managers unlimited) | starts a competition (1, 2 or 4 hours, 1, 3 or 7 days; default 3 days); the leaderboard message appears in the channel |
| `/pinbot games` | anyone | lists every game you can start (only you see it) |
| `/pinbot help` | anyone | how PinBot works (only you see it) |
| `/pinbot-admin channels [challenges] [weekly] [info]` | server managers (only they see `/pinbot-admin`) | where PinBot posts: one-off leaderboards, weekly leaderboards, and its pinned guide with new-VPin announcements (make that one read-only for members). Applies to new competitions at once and is kept across restarts; without options it shows the current channels. The channel ids in the NAS configuration only seed a new server. PinBot needs Send Messages, Embed Links and Pin Messages in each. |
| `/pinbot status` | anyone | running competitions and leaders, and each VPin: online, and whether its PinBot is current, updating itself, or too old to update itself (only you see the reply) |
| `/pinbot end competition:<#>` | its creator or a server manager | ends it early; games already finished still count |
| `/pinbot pair` | anyone (3 an hour) | a one-time line and the pinbot.exe download link to connect your own VPin: desktop, laptop or cabinet (only you see it) |
| `/pinbot guest ABC [name]` | anyone | for 24 hours, scores entered as ABC count for your guest (someone without Discord), also games they already played; the host is pinged for their places |
| `/pinbot-admin weekly enabled:<True/False> [day] [time] [timezone]` | server managers | automatic weekly competition. The first time, include all three, e.g. `enabled:True day:Monday time:19:00 timezone:Eastern`. Turning it on starts a weekly **right away**, whatever the day, running until the next slot; then a new game every week. A schedule change while one runs applies after it ends. |

Players don't register. They put their initials in their **server nickname**, e.g. `Alex | [ALX]` or `Bob [BOB,BB2]`. The member list refreshes every 10 minutes. A score entered with initials nobody claims yet still counts once someone adds that tag, as long as the competition hasn't finished.

Within a second or two of `/pinbot start`, every online VPin has the competition (VPins keep a request open that the server answers at once), and the card shows **🟢 Ready on** / **⏳ Getting ready** (with why, e.g. a table is running) / **⚠️ Can't run on** per VPin while it runs (including a VPin where the game reset its high scores on a competition file; see NVRAM-SAFETY.md). A game started before a VPin was ready still counts if it beats that table's own high scores. Every new competition card carries a headline (**📣 New weekly competition: …** or **📣 New challenge from …**) so members' notifications say what started; nobody is pinged for it. The card lists every participant and is pinned while the competition runs (if PinBot may pin there); every message about a competition ends with a **leaderboard** link that jumps to its card. When a score changes the standings, PinBot posts in the competition's channel: **👑 … takes the lead** (a new leader, including the first score), **👑 … raises the top score** (the leader beats their own best), or **⚡ … takes 2nd place** (any better place, including a first entry). Only members who already have a score in that competition and were just overtaken are mentioned (pinged); PinBot never uses @everyone, @here or roles. A better score that changes no place posts nothing. Ten minutes before the deadline, **⏰ … ends in 10 minutes** reminds everyone that a game counts only if it is finished (VPX closed) by then, with the leader; nobody is pinged (not for competitions shorter than 30 minutes or ended early). When a competition ends, **🏁 … has ended!** lists the podium and mentions the top three.

Only games that **end** before the deadline count, and their results must reach the server within **5 minutes** of the deadline (they normally arrive within a minute). Then the standings are final and the **🏁 … has ended!** message is posted. A VPin that is offline at the deadline loses its last games: simple and predictable beats waiting.

## Troubleshooting

- **Saving the endpoint URL fails:** the server isn't reachable, or the public key in `.env` doesn't match. Check `https://…ts.net/healthz`, then the `server` container log.
- **Scores show as unattributed / the log says "enable Server Members Intent":** turn the intent on (step 1.3). Nobody's initials can be read without it.
- **No leaderboard message:** check `DISCORD_CHANNEL_ID`, and that the bot can view and post in that channel.
