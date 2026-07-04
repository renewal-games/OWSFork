#!/usr/bin/env bash
# Detect OWS services running with a stale Postgres password.
#
# Long-running containers keep working on their old pooled DB credentials even after
# .env.hetzner-dev is rotated; the mismatch only surfaces when a reconnect is forced
# (e.g. the database container is recreated), showing up as
#   Npgsql 28P01: password authentication failed for user "postgres"
# and, for owspublicapi, as "login service unavailable".
#
# This compares each running container's DB-connection-string password to the value
# currently in .env.hetzner-dev. No secrets are printed. Exit 1 if any drift is found.
#
# Usage (from the repo `src/` dir on the dev server):
#   ./scripts/hetzner/check-db-password-drift.sh
set -euo pipefail

SRC_DIR="${OWS_SRC_DIR:-/opt/owsfork/src}"
ENV_FILE="${OWS_ENV_FILE:-.env.hetzner-dev}"
COMPOSE_FILE="${OWS_COMPOSE_FILE:-docker-compose.hetzner-dev.yml}"
SERVICES=(owspublicapi owscharacterpersistence owsinstancemanagement owsglobaldata owschat owsparty)

cd "$SRC_DIR"
DC="docker compose --env-file $ENV_FILE -f $COMPOSE_FILE"

if [ ! -f "$ENV_FILE" ]; then echo "!! $ENV_FILE not found in $SRC_DIR" >&2; exit 2; fi

FILEPW="$(grep -E '^DATABASE_PASSWORD=' "$ENV_FILE" | head -1 | cut -d= -f2- | tr -d '"')"
if [ -z "$FILEPW" ]; then echo "!! DATABASE_PASSWORD not set in $ENV_FILE" >&2; exit 2; fi

drift=0
for svc in "${SERVICES[@]}"; do
  cid="$($DC ps -q "$svc" 2>/dev/null || true)"
  if [ -z "$cid" ]; then printf '%-26s %s\n' "$svc" "not running"; continue; fi
  cpw="$(docker inspect "$cid" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null \
        | grep -i 'OWSDBConnectionString=' \
        | sed -n 's/.*[Pp]assword=\([^;"]*\).*/\1/p')"
  if [ "$cpw" = "$FILEPW" ]; then
    printf '%-26s %s\n' "$svc" "OK"
  else
    printf '%-26s %s\n' "$svc" "STALE (container pw len ${#cpw} != env len ${#FILEPW})"
    drift=1
  fi
done

if [ "$drift" -ne 0 ]; then
  echo
  echo "Drift found. Recreate the stale service(s) with the current env (DB left alone):"
  echo "  $DC up -d --no-deps --force-recreate <service>"
  exit 1
fi
echo
echo "All services match .env.hetzner-dev."
