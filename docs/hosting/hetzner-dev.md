# Hetzner Dev Hosting

This profile hosts a lean OWS backend for dev/testing while Unreal Engine runs locally.
It is an add-on deployment configuration only; it does not change the base OWS API code.

## What Runs

- OWSPublicAPI
- OWSCharacterPersistence
- OWSInstanceManagement
- OWSGlobalData
- OWSChat
- OWSParty
- Postgres
- RabbitMQ

The profile intentionally omits ELK, OWSGuild, OWSActionHouse, OWSManagement, and UE dedicated servers.

## Recommended Host

Use a 4 GB Hetzner cloud server for the first pass. Add a 2 GB swapfile on small hosts:

```bash
sudo fallocate -l 2G /swapfile
sudo chmod 600 /swapfile
sudo mkswap /swapfile
sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

## Current Dev Server

SSH from Windows:

```powershell
ssh -F NUL -i $env:USERPROFILE\.ssh\id_ed25519_codex_20260509 root@87.99.150.89
```

The repo is checked out at:

```bash
cd /opt/owsfork/src
```

Check the running stack:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml ps
```

## Deploy

From `src/`:

```bash
cp .env.hetzner-dev.example .env.hetzner-dev
```

Edit `.env.hetzner-dev` and set a unique Postgres password. Keep
`DATABASE_CONNECTION_STRING` in sync with `DATABASE_PASSWORD`.

RabbitMQ uses the existing repo config at `.docker/rabbitmq/rabbitmq.conf`, which
defaults to `dev` / `test`. The broker ports are bound to `127.0.0.1` on the host
and are only used internally by OWS services in this profile.

Start the lean stack:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml up --build -d
```

Check status and logs:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml ps
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml logs -f owspublicapi
```

Apply project database updates as needed:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml exec -T database \
  psql -U postgres openworldserver < ../Databases/Postgres/SamsaraUpdates/AddSamsaraCharacterInitialPersistentData_pg.sql
```

## Updating The Server

The server should usually follow `origin/main`:

```bash
cd /opt/owsfork/src
git fetch origin
git pull --ff-only origin main
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml up --build -d
```

Keep `.env.hetzner-dev` local to the server. Do not commit it.

> **CAUTION — `--build` also recreates the `database` container.** The `database`
> service has a `build:` context, so `up --build` rebuilds and **recreates** it, which
> severs every service's live Postgres connection pool. On reconnect, any service still
> holding a **stale DB password** (e.g. `.env.hetzner-dev` was rotated but that container
> was never recreated) fails with `Npgsql 28P01: password authentication failed for user
> "postgres"` — which surfaces to players as **"login service unavailable"** from
> `owspublicapi`. This is a latent trap: a long-running container keeps working on its old
> pooled credentials until something forces a reconnect.
>
> Safer patterns:
> - **App-code change only (no DB image change):** rebuild just that service and don't touch
>   the DB — `... up -d --no-deps --build <service>`.
> - **After any DB restart/recreate:** force every app service to reconnect with the current
>   env — `... up -d --no-deps --force-recreate owspublicapi owscharacterpersistence owsinstancemanagement owsglobaldata owschat owsparty`.
> - **Verify no drift first:** run `scripts/hetzner/check-db-password-drift.sh` (compares each
>   running container's DB password to `.env.hetzner-dev`). A green `docker compose exec
>   database psql -U postgres` does NOT prove network creds are valid — that path uses local
>   trust auth and never checks the password.

## Troubleshooting

### "Login service unavailable" / `owspublicapi` returns HTTP 500

Check its logs for a Postgres auth error:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml logs --tail=40 owspublicapi | grep -i 28P01
```

If present, a container is on a stale DB password (see the CAUTION above). Fix by recreating
it with the current env (DB left alone):

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml up -d --no-deps --force-recreate owspublicapi
```

Then confirm: a request to `/api/Users/GetServerToConnectTo` returns 400 (business error), not
500 (DB error), and the logs show a clean start with no `28P01`.

### Idle zone servers never shut down / port 7778 reused

Zone (dedicated) server processes run on the **dev PC**, not this box; the launcher decides to
shut them down from the `mapinstances` rows in this DB. Symptoms of a leak: `mapinstances` is
empty (or has stale rows) while `SamsaraSagaServer.exe` processes keep running, and every new
instance is assigned the base port (`StartingInstancePort`, 7778) because the port is derived
from existing rows — so fresh servers collide on 7778 with a lingering one and die.

Inspect from this box:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml exec -T database \
  psql -U postgres -d openworldserver -c \
  "SELECT mapinstanceid, mapid, port, status, numberofreportedplayers, lastserveremptydate, lastupdatefromserver FROM mapinstances ORDER BY mapinstanceid;"
```

On the dev PC, list/kill orphaned processes (verify no player connections first — an idle
server's only established connection is outbound to the Party gRPC service on `:44364`):

```powershell
Get-CimInstance Win32_Process -Filter "Name='SamsaraSagaServer.exe'" | Select ProcessId, CommandLine
# after confirming zero inbound player connections:
Stop-Process -Id <pid> -Force
```

## Public Endpoints

By default the profile exposes:

- Public API: `http://SERVER_IP:44302`
- Global Data API: `http://SERVER_IP:44325`
- Chat gRPC: `SERVER_IP:50051`
- Party REST: `http://SERVER_IP:44306`
- Party gRPC: `SERVER_IP:44364`

Postgres, RabbitMQ, Character Persistence, and Instance Management are bound to
`127.0.0.1` on the host for admin/debug access and SSH tunnels only.

For public HTTPS, put Caddy or Nginx in front of these ports. Example Caddy shape:

```caddyfile
api.example.com {
	reverse_proxy 127.0.0.1:44302
}

chat.example.com {
	reverse_proxy h2c://127.0.0.1:50051
}

global-data.example.com {
	reverse_proxy 127.0.0.1:44325
}

party.example.com {
	reverse_proxy h2c://127.0.0.1:44364
}

party-api.example.com {
	reverse_proxy 127.0.0.1:44306
}
```

## Logging

This profile does not run ELK. It overrides hosted services toward console logging and
replaces the second Serilog sink with console output so services do not depend on Logstash.

Use Docker logs for hosted dev:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml logs -f
```

## Persistence And Backups

Postgres and RabbitMQ data are stored in named Docker volumes:

- `ows-hetzner-dev-postgres-data`
- `ows-hetzner-dev-rabbitmq-data`

Back up Postgres before destructive host changes:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml exec database \
  pg_dump -U postgres openworldserver > openworldserver.sql
```

Restore into a fresh initialized database:

```bash
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml exec -T database \
  psql -U postgres openworldserver < openworldserver.sql
```

## Smoke Tests

```bash
curl http://SERVER_IP:44302/api/system/status
curl http://SERVER_IP:44325/api/system/status
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml logs --tail=100 owschat
docker compose --env-file .env.hetzner-dev -f docker-compose.hetzner-dev.yml logs --tail=100 owsparty
```

Then test account creation, login, character persistence, chat connection, and party registration/invite flow from your local UE client.

## Local UE Instance Launcher

If UE dedicated server instances run on your PC, run OWSInstanceLauncher on your
PC too. Keep RabbitMQ and internal APIs private by using an SSH tunnel:

```bash
ssh -N \
  -L 56720:127.0.0.1:5672 \
  -L 18028:127.0.0.1:44328 \
  -L 18023:127.0.0.1:44323 \
  root@SERVER_IP
```

Point the local Instance Launcher at:

- RabbitMQ host: `127.0.0.1`
- RabbitMQ port: `56720`
- Instance Management URL: `http://127.0.0.1:18028/`
- Character Persistence URL: `http://127.0.0.1:18023/`
- Global Data URL: `http://SERVER_IP:44325/`
- Public API URL: `http://SERVER_IP:44302/`

For UE config while using the tunnel, use the same internal URLs for
Instance Management and Character Persistence.

On Windows, `scripts/hetzner/Start-OWSTunnelTray.cmd` starts a tray helper that
keeps the tunnel alive and lets you copy the UE and Instance Launcher settings.
