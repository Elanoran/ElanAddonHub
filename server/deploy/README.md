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
can see and save them (guests too); only officers and the owner upload, replace (= a new version), roll back or delete. Programs and scripts
(`.exe .dll .bat .cmd .ps1 .js .msi .lnk ...`) and zips that contain them are refused. An old server simply doesn't show the page.

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

## Tests

```bash
sudo bash server/tests/lodge_ops_test.sh      # backup, rotation, restore, update, rollback (temp folders, fake systemctl/curl)
```

## The Armory (2.16)

Members can choose characters to share with the lodge (Outpost > Settings > Lodge > Armory, off until they turn it on): what the character wears, its stats, talents, professions and hunter pets - never bags, bank, gold or mail. Everybody in the lodge can browse them on the Outpost's Characters page ("Lodge" switch). Guests (the shared code) neither share nor browse. Unticking a character removes it; a member who leaves or is removed takes theirs along. The data lives in `data/armory.json` (a few KB per character; inside `data/`, so `lodge-backup` includes it). After `sudo lodge-update` nothing else needs to be done; the only setting (in `/etc/lodge/lodge.env`, then `sudo systemctl restart lodge`) is `LODGE_ARMORY_ENABLED` (see the table below).

## The Atlas pool (2.15)

Members who run Elan's Toolkit pool what it saw in game - which mobs dropped what (and how often), vendor stock, who found which item and where, mob spawn
points, item details the game client revealed - and everybody in the lodge gets the totals in the Outpost's Atlas. The server keeps one record set per member
and shows the sum, so a member re-sending data never counts twice. Guests can read, not share; every member can switch sharing off in the Outpost
(Settings, Lodge). Characters' names are never sent (finds show the member's lodge name). The data lives in `data/atlas.json` (a few MB; inside `data/`, so
`lodge-backup` includes it). After `sudo lodge-update` nothing else needs to be done; the only setting (in `/etc/lodge/lodge.env`, then
`sudo systemctl restart lodge`):

| Setting | Meaning |
|---|---|
| `LODGE_ARMORY_ENABLED` (1) | `0` switches the Armory off: not advertised, `armory.put` answers `disabled`, the other messages answer an error, the data stays on disk |
| `LODGE_ATLAS_ENABLED` (1) | `0` switches the feature off: not advertised, `atlas.share` / `atlas.get` answer `disabled`, the data stays on disk |
