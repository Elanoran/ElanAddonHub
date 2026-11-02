# A Lodge with Docker

The same Lodge server as the normal install ([../deploy/README.md](../deploy/README.md)), as a container image. Pick the
one you are comfortable with: the normal install updates itself with `sudo lodge-update` (with a backup and a rollback),
the Docker one with a new image.

What you need: a Linux machine with Docker and Docker Compose, and a web server with HTTPS in front of the Lodge (nginx,
Caddy, Apache, Nginx Proxy Manager) exactly as for the normal install: [../deploy/reverse-proxy.md](../deploy/reverse-proxy.md).
Voice and chat use the same web connection: there are no other ports to open.

> Run it on a VM or a real machine. Docker inside a system container (LXC) works for trying it out, but can break after
> updates of the host.

## Start

1. Make a folder for the Lodge and put two files in it:
   - [`docker-compose.yml`](docker-compose.yml) from this folder;
   - `lodge.env`: your settings, the same `LODGE_*` lines as `/etc/lodge/lodge.env` of a normal install
     ([../deploy/lodge.env.example](../deploy/lodge.env.example)). At least:
     ```
     LODGE_PUBLIC_URL=https://your.site/lodge
     ```
     Leave out `LODGE_URLS` and `LODGE_DATA`: the image sets them (it listens on 8080, its data is `/data`).
2. Start it:
   ```bash
   docker compose up -d
   ```
3. Make the owner link (whoever opens it first becomes the Guild Master; it works once, for an hour):
   ```bash
   docker compose exec lodge lodge-admin claim-owner
   ```
4. Point your web server at `127.0.0.1:5280` under the path `/lodge`, as in the reverse proxy guide.

## Safe by default

The Compose file is set up so the Lodge exposes nothing but what your web server forwards:

- **Only this machine can reach it** (`127.0.0.1:5280`). Docker opens published ports past the host's firewall (ufw), so
  never change that to `5280:8080` or `0.0.0.0` - put your web server in front instead.
- **It knows who your visitors are.** Only the Docker network's gateway (your host, where the web server runs) may pass on
  the visitor's address (`LODGE_TRUSTED_PROXIES`); without it every member would share one address for the Lodge's
  lockouts and rate limits. If your web server runs in another container on the Lodge's network, trust that subnet instead.
- **Locked down.** It runs as an ordinary user, with no Linux capabilities, a read-only system (only `/data`, `/backups`
  and a small `/tmp` are writable), and limits on memory and processes.
- **No secrets in the image.** Your codes, keys and tokens live only in `lodge.env` and the `lodge-data` volume on your
  machine. Keep `lodge.env` readable by you only (`chmod 600 lodge.env`): it can hold the admin token and Battle.net secret.
- Anyone who may run `docker` on the machine can read the Lodge's settings and data (`docker inspect`, `docker exec`):
  give the `docker` group only to people you would give root.
- The image is rebuilt every week on a fresh base, so security fixes arrive with `docker compose pull`.

## Every day

| | |
|---|---|
| Logs | `docker compose logs -f lodge` |
| Codes, invites, members | `docker compose exec lodge lodge-admin ...` (the same commands as `sudo lodge-admin ...`) |
| Status | `docker compose ps` (it reports *healthy* when the Lodge answers) |
| Settings | edit `lodge.env`, then `docker compose up -d` |

## Update

```bash
docker compose exec lodge lodge-backup
docker compose pull && docker compose up -d
```

The Outpost knows a Lodge runs in Docker and shows this command when your server is behind. To go back, put the previous
version in `docker-compose.yml` (`image: ghcr.io/elanoran/lodge:2.37.1` instead of `:latest`) and `docker compose up -d`.

## Backups

Everything the Lodge keeps (codes, chat, files, the Cairn key) is in the `lodge-data` volume. **Backups contain the invite
codes**: keep copies private.

```bash
docker compose exec lodge lodge-backup            # one archive in the lodge-backups volume
docker compose exec lodge lodge-backup list
```

Restore (the Lodge must be stopped; the current data is archived first, as `lodge-before-restore-<time>.tar.gz`):

```bash
docker compose stop lodge
docker compose run --rm --entrypoint lodge-backup lodge restore lodge-20261102-0415.tar.gz
docker compose up -d
```

Copy archives out of the volume with `docker compose cp lodge:/backups/<archive> .`, and into it the same way the other
direction. There is no daily timer in the image: run `lodge-backup` from the host's cron, for example
`15 4 * * * cd /srv/lodge && docker compose exec -T lodge lodge-backup`.

## Moving a Lodge into Docker

On the old server make a backup (`sudo lodge-backup`), copy the archive next to your `docker-compose.yml`, then:

```bash
docker compose up -d && docker compose stop lodge
docker compose cp lodge-20261102-0415.tar.gz lodge:/backups/
docker compose run --rm --entrypoint lodge-backup lodge restore lodge-20261102-0415.tar.gz
docker compose up -d
```

Copy your settings from `/etc/lodge/lodge.env` into `lodge.env` (without `LODGE_URLS` and `LODGE_DATA`), and point your
web server at the new address. Members keep their codes and devices.

## Building the image yourself

From a checkout of this repository:

```bash
docker build -f server/docker/Dockerfile -t elans-lodge .
```

and use `image: elans-lodge` in `docker-compose.yml`.
