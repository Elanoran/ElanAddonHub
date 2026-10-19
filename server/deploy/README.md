# Deploying and operating the Lodge server (Ubuntu, systemd)

Layout: source checkout `/opt/lodge-src`, build `/opt/lodge`, data `/var/lib/lodge`, config `/etc/lodge/lodge.env`,
backups `/var/backups/lodge`. Reverse proxy: see [reverse-proxy.md](reverse-proxy.md).

## One-time install (also updates the tools below)

```bash
sudo git clone https://github.com/Elanoran/ElanAddonHub /opt/lodge-src
sudo /opt/lodge-src/server/deploy/install.sh
```

`install.sh` is safe to run again. It installs `lodge-admin`, `lodge-backup`, `lodge-update`, the `lodge` unit and the
daily `lodge-backup.timer` (04:15 plus up to 20 minutes random delay). systemd drop-ins in
`/etc/systemd/system/lodge.service.d/` are never touched.

**.NET:** the build needs the **.NET 10 SDK** (`dotnet-sdk-10.0`; Lodge 2.26 and older: .NET 8). `install.sh` installs it with apt
when it is missing - Ubuntu 26.04 has it in its own archive; on a release that doesn't, add the .NET backports
(`sudo add-apt-repository ppa:dotnet/backports`) or Microsoft's feed first, otherwise `install.sh` stops with that message before
changing anything. The result is **self-contained** (`/opt/lodge/lodge`, one file): the service needs no .NET runtime on the
machine, so removing or upgrading the system's .NET never affects the running Lodge - only the next build. The SDK is picked by
`server/global.json` (any 10.0.x), which is why `install.sh` builds from `server/`.

## Starting a lodge: the owner link (2.12)

A fresh install has no Guild Master. At the end of `install.sh` it prints an **OWNER LINK** (once, to the console only; valid 1 hour, works once):

```bash
sudo git clone https://github.com/Elanoran/ElanAddonHub /opt/lodge-src
sudo /opt/lodge-src/server/deploy/install.sh        # prints the owner link at the end
```

Set `LODGE_PUBLIC_URL=https://your.site/lodge` in `/etc/lodge/lodge.env` first (then `sudo systemctl restart lodge`) so the link carries your address; if you forgot, set it
and run `sudo lodge-admin claim-owner` for a new one. In the Outpost: Lodge tab, "Create a lodge" shows these steps; paste the link into the join box, type the name you want, Join.
You are the Guild Master (Settings, Guild Master: lodge name, welcome text, ranks, invites). No code is made for you.

```bash
sudo lodge-admin claim-owner                 # a new owner link (refused when the lodge has an owner)
sudo lodge-admin recover-owner Elan          # locked out of every PC? a link that adds a new device for the owner Elan
```

Everyone else is invited from the app (Lodge sidebar, Invite) or with `sudo lodge-admin invite <name>`. `lodge-admin add` (permanent codes) still works for old setups but is no longer the way in.

## Invites, devices and codes (2.10): `lodge-admin`

```bash
sudo lodge-admin invite Bob                 # a one-time link for a new person (member); prints it ONCE, on stdout, send it privately
sudo lodge-admin invite Bob officer --days 2
sudo lodge-admin invite --guest --uses 3    # a guest link three people can use
sudo lodge-admin invites                    # pending / used / expired / revoked (never the secrets)
sudo lodge-admin revoke-invite <id>
sudo lodge-admin devices [name]             # each person's signed-in devices
sudo lodge-admin revoke-device <id>         # signed out and disconnected within 15 seconds
sudo lodge-admin retire-code Bob            # Bob's old permanent code stops working; his devices keep working (refused if he has none, --force overrides)
```

Set `LODGE_PUBLIC_URL=https://your.site/lodge` in `/etc/lodge/lodge.env` so the printed link carries your address
(`elanshub://lodge/join#your.site/lodge|<invite>`). The link goes to stdout and the warning to stderr, so `lodge-admin invite Bob > link.txt`
keeps only the link. An invite works once and lives 7 days by default (`LODGE_INVITE_HOURS`, or `--days 1-30` / `--hours 1-720`). Opening it in
the Outpost trades it for a key that belongs to that PC. Names may contain any letters. The same actions are in the Outpost: Settings, Guild Master
(owner and officers) and Settings, Lodge, Your devices (everyone).

Data: `/var/lib/lodge/invites` and `/var/lib/lodge/devices` (`lodge:lodge 0640`, only hashes; changed under `codes.lock` like the codes file, included
in the backups). `install.sh` creates them.

**After updating to 2.10** (`sudo lodge-update`): nothing breaks - every personal code and the shared guest code keep working. Then, at your own pace:
1. make new invites instead of sending codes: `sudo lodge-admin invite <name> [role]` (or the Guild Master page);
2. people who still use a code get a quiet "Upgrade this device" in the Outpost (Lodge tab); their code keeps working until you retire it;
3. when `sudo lodge-admin devices <name>` shows all of someone's PCs, retire the old code: `sudo lodge-admin retire-code <name>` (Guild Master page: "Retire code").
Older Outposts can only use codes: don't retire a code whose owner still runs one.


## Forgetting a person's data (2.22): `lodge-admin purge`

```bash
sudo lodge-admin purge Bob --atlas-only   # take everything Bob put into the Atlas back out (bad data); Bob stays a member
sudo lodge-admin purge Bob                # forget everything stored about a person who is no longer a member
sudo lodge-admin remove Bob               # removes the codes AND queues the full purge
```

The command writes a small request file into `purge.d/` next to the codes file (`/var/lib/lodge/purge.d`); the running server picks it up within 15 seconds, does the same as the Guild Master page (Settings > Guild Master > Members > Revert Atlas) or `member.leave`, and deletes the file. The server must be running; a request waits there until it is. `purge Bob` without `--atlas-only` is refused while Bob still has a code. What goes and what stays (messages stay, shown as "(former member)"; uploaded files stay without a name; events stay) is listed in `SECURITY.md` under 2.22.0. Backups keep the old data until they rotate out (`LODGE_BACKUP_KEEP`, 14 by default).
## Updating: `lodge-update`

```bash
sudo lodge-update            # instead of git pull + install.sh
sudo lodge-update --reset    # only when upstream history was rewritten (asks first; --yes skips the question)
```

It records the current commit, runs `git fetch`, fast-forwards (never merges), takes a backup (`lodge-backup`; if the
backup fails nothing is changed), runs `install.sh` and checks `http://127.0.0.1:<port><pathbase>/health` (port and base
from `lodge.env`). If the new version does not come up healthy the checkout goes back to the old commit,
`install.sh` runs again and the command exits non-zero with `ROLLED BACK`. It prints `old -> new` server version
(read from the source, because `/health` only shows the version to a logged-in invite code, which the script never uses).
A rollback restores the code only; if a new version changed data, restore it with `lodge-backup restore`.
Before changing anything it checks that the SDK the new version builds with is installed or available from apt (exit 4 with a
clear message if not). It keeps a copy of the running build (`/opt/lodge.before-update`) until the end: if even the old commit
can't be rebuilt (say an OS upgrade removed its SDK), that copy is put back and restarted as it was.

## Backups: `lodge-backup`

```bash
sudo lodge-backup                       # one archive now
sudo lodge-backup list
sudo lodge-backup restore lodge-20261010-0415.tar.gz     # asks for 'yes'
systemctl list-timers lodge-backup.timer
journalctl -u lodge-backup -n 30
```

Each archive `/var/backups/lodge/lodge-YYYYmmdd-HHMM.tar.gz` holds the whole data folder (codes, profiles, pins, chat
history, uploaded files) except `*.part` / `*.tmp`. The codes file is archived while holding the `codes.lock` flock, so a
`lodge-admin` or Guild Master change is never captured half-written. The newest `LODGE_BACKUP_KEEP` (14) are kept.
`LODGE_BACKUP_FILES=0` leaves out the uploaded `files/` folder (they expire after `LODGE_FILE_DAYS` anyway).
The backup folder is `root:root 0700` and archives are `0600`: **they contain the invite codes**. Copy them off the box
only to somewhere equally private.

`restore` asks for confirmation, stops the service, moves the current data to `/var/lib/lodge.before-restore-<time>`
(kept until you delete it - it also contains codes), extracts the archive, sets owner `lodge:lodge`, folders `0750`,
files `0640` (`codes.lock` `0660`), starts the service and checks `/health`.

## Shared files (2.14): the Files page

Officers and the owner keep named files for the whole lodge (a UI pack, a guide, a build): `data/slots/`, each slot with its newest 3 versions. Everybody
can see and save them (guests too); only officers and the owner upload, replace (= a new version), roll back or delete. File types that can
run something on Windows (programs, scripts, shortcuts, installers, disk images, help files and the like: one list shared with the Outpost)
and zips that contain them are refused, here and in chat. An old server simply doesn't show the page.

Downloads of chat files and shared files have one budget per person and hour (guests per address): `LODGE_DOWNLOADS_PER_HOUR` (600) and
`LODGE_DOWNLOAD_MB_PER_HOUR` (2048). Past it the server answers 429 for a while; normal use never gets near it.

After `sudo lodge-update` (the new version arrives with it; nothing else needs to be done) the settings below can go into `/etc/lodge/lodge.env`
(then `sudo systemctl restart lodge`); defaults in brackets, see `lodge.env.example`:

| Setting | Meaning |
|---|---|
| `LODGE_FILES_ENABLED` (1) | `0` switches the feature off: not advertised, uploads and downloads answer 403, the files stay on disk |
| `LODGE_FILES_MAX_MB` (50) | largest single file |
| `LODGE_FILES_TOTAL_MB` (500) | all kept versions of all slots together; a full lodge refuses uploads until something is deleted |
| `LODGE_FILES_UPLOADS_PER_10MIN` (20) | per person |

The reverse proxy must allow request bodies of at least `LODGE_FILES_MAX_MB` (nginx: `client_max_body_size 60m;` in the lodge location) - the same place the chat
upload limit is set. Backups include `slots/` (`LODGE_BACKUP_FILES=0` leaves it out together with `files/`).

## Realm status (2.20): the Battle.net API client

The Outpost can show small dots at the top, one per WoW Forever realm, green when up and red when down. The Lodge server asks the Blizzard Game Data API and pushes the answer to every connected member; the Outposts never hold a key.

1. Sign in at https://develop.battle.net with the Battle.net account that should own the key, open **API Access** and **Create client** (name: your lodge; redirect URL: any, e.g. `https://localhost`; service URL and intended use: a small community tool). Copy the **Client ID** and **Client Secret**.
2. In `/etc/lodge/lodge.env` (or wherever your env file lives, mode 600) set `LODGE_BNET_CLIENT_ID=...` and `LODGE_BNET_CLIENT_SECRET=...`, optionally `LODGE_BNET_REGION` (`eu` default), then `sudo systemctl restart lodge`.
3. Look at the journal: `journalctl -u lodge | grep "Realm status"`. The first poll prints, per namespace, how many connected realms exist and how many look like Forever realms (names only). If none is found the feature stays quiet (`available: false`, "Forever realms not found in the Blizzard API"), is polled every 15 minutes, and the Outpost shows nothing; the namespace to use can then be forced with `LODGE_REALMS_NAMESPACES`, and the realms to show chosen with `LODGE_REALMS_MATCH` (the journal then lists every realm name Blizzard returned). Beta realms are usually not in the public API; they appear after launch.

The feature is advertised only when both the id and the secret are set. The secret is never logged, sent to a client or put in an error text. One poll per interval (`LODGE_REALMS_POLL_SECONDS`, 60-900, default 120) for the whole lodge, with a backoff on errors and on HTTP 429.
## Tests

```bash
sudo bash server/tests/lodge_ops_test.sh      # backup, rotation, restore, update, rollback (temp folders, fake systemctl/curl)
```

## The Classic baseline, hosted once (2.17)

The Outpost's Atlas can show where things drop, who sells them and what quests give them from the public cMaNGOS classic-db (GPL-3.0). Instead of every member downloading it from GitHub, the lodge can host it once: an officer or the owner clicks **Share my Classic data with the Lodge** (Outpost > Settings > Lodge > Classic data; or answers "Yes" to the quiet offer after their own download), the server stores the original upstream files unchanged in one bundle, and every member's Outpost fetches it from the lodge automatically (a setting lets them opt out). Guests download it from the public source as before. The bundle is about 15 MB and lives in `data/datasets/` (inside `data/`, so `lodge-backup` includes it); the server never opens or runs it. After `sudo lodge-update` nothing else needs to be done; the settings are below. Allow request bodies of `LODGE_CLASSIC_MAX_MB` in the reverse proxy (the default of 40 MB is below the 50 MB the shared files already need).
Badges and the ladder: the Outpost's Lodge tab has a Ladder page and every profile a strip of badges, earned just by sharing (Atlas observations, flight times, files, calendar events, characters, time in the lodge...). It is worked out from data the lodge already holds plus a few small tallies in `data/badges.json` (included in `lodge-backup`); nothing new is collected from the game, guests have no badges, and members can hide themselves from the ladder. The Guild Master can switch it off in Outpost > Settings > Guild Master, and `LODGE_BADGES_ENABLED=0` turns it off for good (see the settings below).

## The Armory (2.16)

Members can choose characters to share with the lodge (Outpost > Settings > Lodge > Armory, off until they turn it on): what the character wears, its stats, talents, professions and hunter pets - never bags, bank, gold or mail. Everybody in the lodge can browse them on the Outpost's Characters page ("Lodge" switch). Guests (the shared code) neither share nor browse. Unticking a character removes it; a member who leaves or is removed takes theirs along. The data lives in `data/armory.json` (a few KB per character; inside `data/`, so `lodge-backup` includes it). After `sudo lodge-update` nothing else needs to be done; the only setting (in `/etc/lodge/lodge.env`, then `sudo systemctl restart lodge`) is `LODGE_ARMORY_ENABLED` (see the table below).

## The Atlas pool (2.15)

**Joining the Cairn (2.28).** Nothing to set up on the server: the Guild Master switches it on in the Outpost (Settings > Guild Master >
The Cairn) with the Lodge's public address and, during the Cairn's beta, an invite code from the Cairn's operator. The server makes its own key
pair (`data/cairn-key.pem`, mode 600 - `lodge-backup` keeps it; keep your backups private), asks the Cairn, answers its check at
`<public address>/cairn/challenge` (so that path must reach the Lodge through your reverse proxy, which it does when everything under the
Lodge's path is forwarded), and contributes and receives once admitted. Optional settings: `LODGE_PUBLIC_URL` (the address suggested to the
Guild Master) and `LODGE_CAIRN_URL` (another, self-hosted Cairn). A Lodge set up the old way (`LODGE_CAIRN_ENABLED` + `LODGE_CAIRN_KEY`) keeps
working after `sudo lodge-update` and moves to its own key pair by itself (the log says "signs its requests with its own key pair now").

Members who run Elan's Toolkit pool what it saw in game - which mobs dropped what (and how often), vendor stock, who found which item and where, mob spawn
points, item details the game client revealed - and everybody in the lodge gets the totals in the Outpost's Atlas. The server keeps one record set per member
and shows the sum, so a member re-sending data never counts twice. Guests can read, not share; every member can switch sharing off in the Outpost
(Settings, Lodge). Characters' names are never sent (finds show the member's lodge name). The data lives in `data/atlas.json` (a few MB; inside `data/`, so
`lodge-backup` includes it). After `sudo lodge-update` nothing else needs to be done; the only setting (in `/etc/lodge/lodge.env`, then
`sudo systemctl restart lodge`):

| Setting | Meaning |
|---|---|
| `LODGE_BNET_CLIENT_ID` / `LODGE_BNET_CLIENT_SECRET` (unset) | The Battle.net API client for realm status (see "Realm status" below). Both set = the `realms` feature is advertised; either missing = off. The secret is never logged or sent to anyone |
| `LODGE_BNET_REGION` (eu) | `eu`, `us`, `kr` or `tw`: the API host and the namespaces probed |
| `LODGE_REALMS_POLL_SECONDS` (120) | 60 - 900. One poll for the whole lodge, whatever the member count |
| `LODGE_REALMS_NAMESPACES` (empty) | Comma separated; replaces the default `dynamic-<region>`, `dynamic-classic-<region>`, `dynamic-classic1x-<region>`, `dynamic-classicann-<region>` probe |
| `LODGE_REALMS_MATCH` (forever) | Comma separated words, realm names or slugs; a realm whose name, slug or category contains one of them is shown. When nothing matches, the journal lists the realm names Blizzard returned so you can pick them |
| `LODGE_BADGES_ENABLED` (1) | `0` switches badges and the ladder off for good: not advertised, every request answers `disabled`, the Guild Master's switch in the Outpost is greyed out, the data (`data/badges.json`) stays on disk. With `1` the Guild Master can still switch it off from Outpost > Settings > Guild Master |
| `LODGE_ARMORY_ENABLED` (1) | `0` switches the Armory off: not advertised, `armory.put` answers `disabled`, the other messages answer an error, the data stays on disk |
| `LODGE_ATLAS_ENABLED` (1) | `0` switches the feature off: not advertised, `atlas.share` / `atlas.get` answer `disabled`, the data stays on disk |
| `LODGE_CLASSIC_ENABLED` (1) | `0` switches the hosted Classic data off: not advertised, upload / download / delete answer 403 `disabled`, the stored bundle stays on disk (about 15 MB) |
| `LODGE_CLASSIC_MAX_MB` (40) | largest bundle accepted |
| `LODGE_CLASSIC_DOWNLOADS_PER_HOUR` (6) | per member |
