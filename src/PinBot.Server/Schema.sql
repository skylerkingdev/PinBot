-- PinBot server schema, v1. Applied at startup; every statement is idempotent.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE IF NOT EXISTS cabinet (
    id               text PRIMARY KEY,
    name             text NOT NULL,
    credential_hash  text NOT NULL UNIQUE,
    paired_at_utc    timestamptz NOT NULL,
    last_seen_at_utc timestamptz,
    revoked_at_utc   timestamptz
);

-- short-lived, single-use; only the hash is stored
CREATE TABLE IF NOT EXISTS pairing_code (
    code_hash      text PRIMARY KEY,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    used_at_utc    timestamptz,
    cabinet_id     text REFERENCES cabinet (id)
);

-- each cabinet's latest reported table inventory (replaced wholesale on every report)
CREATE TABLE IF NOT EXISTS installation (
    cabinet_id      text NOT NULL REFERENCES cabinet (id),
    table_name      text NOT NULL,
    rom_id          text NOT NULL,
    eligible        boolean NOT NULL,
    reason          text,
    reported_at_utc timestamptz NOT NULL,
    PRIMARY KEY (cabinet_id, table_name)
);

-- curated, explicitly validated grouping of ROM revisions into one game; a ROM not listed is its own game
CREATE TABLE IF NOT EXISTS rom_alias (
    rom_id            text PRIMARY KEY,
    canonical_game_id text NOT NULL
);

CREATE TABLE IF NOT EXISTS competition (
    id                 bigserial PRIMARY KEY,
    canonical_game_id  text NOT NULL,
    title              text NOT NULL,
    kind               text NOT NULL CHECK (kind IN ('oneoff', 'weekly')),
    created_by         text NOT NULL,
    starts_at_utc      timestamptz NOT NULL,
    ends_at_utc        timestamptz NOT NULL,
    ended_early_at_utc timestamptz,
    cancelled_at_utc   timestamptz,
    created_at_utc     timestamptz NOT NULL,
    CHECK (ends_at_utc > starts_at_utc),
    -- effective playing window; an early end shortens it
    span tstzrange GENERATED ALWAYS AS
        (tstzrange(starts_at_utc, LEAST(ends_at_utc, COALESCE(ended_early_at_utc, ends_at_utc)), '[)')) STORED,
    -- at most one competition per game at any moment, enforced by the database even under concurrent requests
    CONSTRAINT one_competition_per_game EXCLUDE USING gist (canonical_game_id WITH =, span WITH &&)
        WHERE (cancelled_at_utc IS NULL)
);

-- derived from Discord nickname tags such as "Player | [SKY,AVK]"; replaced wholesale on each refresh
CREATE TABLE IF NOT EXISTS member_initial (
    discord_user_id text NOT NULL,
    initials        text NOT NULL,
    display_name    text NOT NULL,
    observed_at_utc timestamptz NOT NULL,
    PRIMARY KEY (discord_user_id, initials)
);

CREATE TABLE IF NOT EXISTS result (
    event_id              uuid PRIMARY KEY,
    competition_id        bigint NOT NULL REFERENCES competition (id),
    cabinet_id            text NOT NULL REFERENCES cabinet (id),
    session_id            text NOT NULL,
    rom_id                text NOT NULL,
    initials              text NOT NULL,
    score                 bigint NOT NULL CHECK (score > 0),
    session_exited_at_utc timestamptz NOT NULL,
    received_at_utc       timestamptz NOT NULL,
    parser_version        text NOT NULL,
    payload_hash          text NOT NULL,
    status                text NOT NULL CHECK (status IN ('accepted', 'unattributed', 'rejected')),
    reason                text,
    -- fixed at acceptance: later nickname changes never move an accepted score
    discord_user_id       text,
    CHECK ((status = 'accepted') = (discord_user_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS result_accepted ON result (competition_id, discord_user_id) WHERE status = 'accepted';

-- v2 (Discord). Additive and idempotent.
ALTER TABLE competition ADD COLUMN IF NOT EXISTS created_by_name    text;
ALTER TABLE competition ADD COLUMN IF NOT EXISTS discord_channel_id text;
ALTER TABLE competition ADD COLUMN IF NOT EXISTS discord_message_id text;
-- hash of the last leaderboard content sent to Discord; the publisher edits the message only when it changes
ALTER TABLE competition ADD COLUMN IF NOT EXISTS published_hash     text;

-- one weekly competition per weekly slot, even across restarts or two server instances
CREATE UNIQUE INDEX IF NOT EXISTS weekly_slot ON competition (starts_at_utc) WHERE kind = 'weekly' AND cancelled_at_utc IS NULL;

-- small one-time settings changed from Discord (e.g. weekly on/off)
CREATE TABLE IF NOT EXISTS setting (
    key   text PRIMARY KEY,
    value text NOT NULL
);

-- v3: each member's best score as last announced in Discord (JSON {user: score}); the publisher compares against it
-- to tell members they were passed. NULL until the first publish pass, which records without announcing.
ALTER TABLE competition ADD COLUMN IF NOT EXISTS announced_bests text;

-- v4: when the "competition has ended" message was posted. Added once; competitions that had already ended then are
-- marked as announced so an upgrade never posts conclusions for old competitions.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'competition' AND column_name = 'end_announced_at_utc') THEN
        ALTER TABLE competition ADD COLUMN end_announced_at_utc timestamptz;
        UPDATE competition SET end_announced_at_utc = now()
        WHERE LEAST(ends_at_utc, COALESCE(ended_early_at_utc, ends_at_utc)) <= now();
    END IF;
END $$;

-- v5: anyone can request a pairing code from Discord; remember who, and which member paired each cabinet
ALTER TABLE pairing_code ADD COLUMN IF NOT EXISTS created_by      text;
ALTER TABLE cabinet      ADD COLUMN IF NOT EXISTS paired_by       text;
ALTER TABLE cabinet      ADD COLUMN IF NOT EXISTS paired_by_name  text;
ALTER TABLE pairing_code ADD COLUMN IF NOT EXISTS created_by_name text;

-- v6: close competitions as soon as no cabinet can still deliver a result. A cabinet reports when it has nothing left
-- to upload; a competition is final once every cabinet with the game did so after its deadline (or a manager closes it).
ALTER TABLE cabinet     ADD COLUMN IF NOT EXISTS caught_up_at_utc  timestamptz;
ALTER TABLE competition ADD COLUMN IF NOT EXISTS finalized_at_utc  timestamptz;

-- v7: the leaderboard message is pinned while a competition runs: NULL (not pinned), 'pinned', 'unpinned', or
-- 'denied' (the bot may not pin in that channel; not retried for this competition)
ALTER TABLE competition ADD COLUMN IF NOT EXISTS pin_state text;

-- v8: "cabinet connected" announcements in the info channel. Cabinets paired before this existed count as announced.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'cabinet' AND column_name = 'join_announced_at_utc') THEN
        ALTER TABLE cabinet ADD COLUMN join_announced_at_utc timestamptz;
        UPDATE cabinet SET join_announced_at_utc = now();
    END IF;
END $$;

-- v9: the game an installation's ROM belongs to, whatever its revision (sent by agents that know it). ROM revisions
-- of one game are linked through rom_alias so they compete as one game.
ALTER TABLE installation ADD COLUMN IF NOT EXISTS game_key text;

-- v10: only the same ROM revision is the same game (revisions can differ in rules and scoring). Undo the automatic
-- revision links v9 created: an alias whose ROM and target were reported as the same game. Manual aliases stay.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM setting WHERE key = 'schema_v10_revision_links_removed') THEN
        DELETE FROM rom_alias a
        WHERE EXISTS (SELECT 1 FROM installation i1 JOIN installation i2 ON i2.game_key = i1.game_key
                      WHERE i1.rom_id = a.rom_id AND i2.rom_id = a.canonical_game_id AND i1.game_key IS NOT NULL);
        INSERT INTO setting (key, value) VALUES ('schema_v10_revision_links_removed', 'true');
    END IF;
END $$;

-- v11: each cabinet's readiness for a competition (competition file in place, waiting and why, or refused and why),
-- shown live on the Discord card so players know when they can play
CREATE TABLE IF NOT EXISTS competition_cabinet (
    competition_id bigint NOT NULL REFERENCES competition (id),
    cabinet_id     text NOT NULL REFERENCES cabinet (id),
    state          text NOT NULL CHECK (state IN ('ready', 'waiting', 'refused')),
    detail         text,
    updated_at_utc timestamptz NOT NULL,
    PRIMARY KEY (competition_id, cabinet_id)
);

-- v12: the PinBot version each cabinet last reported with its inventory (a signed release number, or the build version
-- of a copy that cannot update itself), shown by /pinbot status
ALTER TABLE cabinet ADD COLUMN IF NOT EXISTS agent_version text;

-- v13: when the "10 minutes left" reminder was posted (once per competition)
ALTER TABLE competition ADD COLUMN IF NOT EXISTS warning_announced_at_utc timestamptz;

-- v14: since when a table has been running on the cabinet (NULL: none), shown as "in a game" in /pinbot status
-- and on cards
ALTER TABLE cabinet ADD COLUMN IF NOT EXISTS playing_since timestamptz;

-- v15: guests: initials a member vouches for, for 24 hours (players without a Discord account). The id is the same for
-- the same host and initials on every visit, so a returning guest is the same player.
CREATE TABLE IF NOT EXISTS guest (
    id             text PRIMARY KEY,
    initials       text NOT NULL,
    host_user_id   text NOT NULL,
    display_name   text NOT NULL,
    expires_at_utc timestamptz NOT NULL
);

-- v16: initials two or more members use in their tags ("clashes"), announced once to those members, privately, when the
-- nickname sync first sees them (members is their sorted Discord ids). Replaces telling them after a score was lost,
-- whose column is dropped.
CREATE TABLE IF NOT EXISTS initials_clash (
    initials        text NOT NULL,
    members         text NOT NULL,
    notified_at_utc timestamptz NOT NULL,
    PRIMARY KEY (initials, members)
);
ALTER TABLE result DROP COLUMN IF EXISTS owner_notified_at_utc;
