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

## Tests

```bash
sudo bash server/tests/lodge_ops_test.sh      # backup, rotation, restore, update, rollback (temp folders, fake systemctl/curl)
```
