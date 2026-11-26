#!/usr/bin/env bash
# Deploy one release of the OWS app services on this box. It is the forced command of the CI deploy key:
# scripts/hetzner/install-deploy.sh installs it as /usr/local/sbin/ows-deploy-release and restricts the key
# to it (docs/hosting/hetzner-dev.md, Continuous Deployment).
#
# Input is exactly "<environment> <sha>": SSH_ORIGINAL_COMMAND when run through the key, else the two
# arguments (root, by hand). <environment> must be the one /etc/default/ows-deploy names for this box, and
# <sha> a full 40-character lowercase commit SHA whose images CI pushed to GHCR. Anything else is refused.
#
# It pulls <registry>/<service>:linux-<sha> for the six app services and recreates them with --no-deps, so the
# database container is never recreated. Then it runs check-db-password-drift.sh and asks /health in every app
# service. If either fails it rolls back to the last deployed SHA's images, pulled and never rebuilt. It never
# runs migrations and never moves the git checkout.
#
# State per environment is in /var/lib/ows-deploy/<environment>/: `current` holds the last SHA that passed its
# checks, `history` one line per run. The last output line is for the Deploy workflow:
#   ows-deploy: previous=<sha|none> checkout=<sha|unknown> result=<deployed|failed|rolled_back|rollback_failed>
# Exit: 0 deployed, 1 failed with nothing changed, 2 refused, 3 rolled back, 4 rollback failed.
set -euo pipefail

# The services check-db-password-drift.sh checks. owsmanagement (the console) is not deployed here.
APP_SERVICES=(owspublicapi owscharacterpersistence owsinstancemanagement owsglobaldata owschat owsparty)
SETTINGS=/etc/default/ows-deploy
REPO_DIR=/opt/owsfork
SRC_DIR="$REPO_DIR/src"
ENV_FILE=.env.hetzner-dev
COMPOSE_FILE=docker-compose.hetzner-dev.yml
DRIFT_CHECK="$REPO_DIR/scripts/hetzner/check-db-password-drift.sh"
STATE_ROOT=/var/lib/ows-deploy

refuse() { echo "ows-deploy: refused: $*" >&2; exit 2; }
log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*"; }

if [ -n "${SSH_ORIGINAL_COMMAND+x}" ]; then
  [ "$#" -eq 0 ] || refuse "arguments and SSH_ORIGINAL_COMMAND together"
  request="$SSH_ORIGINAL_COMMAND"
else
  request="$*"
fi
if ! [[ "$request" =~ ^(staging|production)\ ([0-9a-f]{40})$ ]]; then
  refuse "expected '<staging|production> <40-character lowercase hex SHA>'"
fi
environment="${BASH_REMATCH[1]}"
sha="${BASH_REMATCH[2]}"

[ -r "$SETTINGS" ] || refuse "$SETTINGS is missing; run scripts/hetzner/install-deploy.sh"
# shellcheck disable=SC1090
. "$SETTINGS"
[ "$environment" = "${OWS_DEPLOY_ENVIRONMENT:-}" ] \
  || refuse "this box deploys '${OWS_DEPLOY_ENVIRONMENT:-unset}', not '$environment'"
registry="${OWS_DEPLOY_REGISTRY:-}"
[[ "$registry" =~ ^[a-z0-9.-]+(/[a-z0-9._-]+)+$ ]] || refuse "OWS_DEPLOY_REGISTRY in $SETTINGS is not a registry path"
health_timeout="${OWS_DEPLOY_HEALTH_TIMEOUT:-180}"
[[ "$health_timeout" =~ ^[1-9][0-9]*$ ]] || refuse "OWS_DEPLOY_HEALTH_TIMEOUT in $SETTINGS is not a number of seconds"

cd "$SRC_DIR"
[ -r "$ENV_FILE" ] || refuse "$SRC_DIR/$ENV_FILE is missing or unreadable"
[ -r "$COMPOSE_FILE" ] || refuse "$SRC_DIR/$COMPOSE_FILE is missing"
[ -r "$DRIFT_CHECK" ] || refuse "$DRIFT_CHECK is missing"

state_dir="$STATE_ROOT/$environment"
mkdir -p "$state_dir"
exec 9>"$STATE_ROOT/lock"
flock -w 900 9 || { log "Another deploy has held $STATE_ROOT/lock for 15 minutes; nothing was changed."; exit 1; }

previous=none
if [ -f "$state_dir/current" ]; then
  previous="$(cat "$state_dir/current")"
  [[ "$previous" =~ ^[0-9a-f]{40}$ ]] || { log "$state_dir/current does not hold a SHA; fix it by hand."; exit 1; }
fi
checkout="$(git -c safe.directory="$REPO_DIR" -C "$REPO_DIR" rev-parse HEAD 2>/dev/null || true)"
[[ "$checkout" =~ ^[0-9a-f]{40}$ ]] || checkout=unknown

dc() { docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"; }
image_of() { printf '%s/%s:linux-%s' "$registry" "$1" "$2"; }

# Point compose at a release: a SHA's GHCR images, or "local", the images the env file names, which is what a box
# ran before its first pipeline deploy. Exported values override the env file's REGISTRY, PLATFORM and TAG.
use_release() {
  if [ "$1" = local ]; then
    unset REGISTRY PLATFORM TAG
  else
    export REGISTRY="$registry" PLATFORM=linux TAG="$1"
  fi
}

pull_release() {
  local svc ref
  for svc in "${APP_SERVICES[@]}"; do
    ref="$(image_of "$svc" "$1")"
    if ! docker pull --quiet "$ref"; then
      docker image inspect "$ref" >/dev/null 2>&1 || { log "Could not pull $ref"; return 1; }
      log "Could not pull $ref; using the copy already on this box"
    fi
  done
}

start_release() {
  use_release "$1"
  dc up -d --no-deps --no-build --pull never "${APP_SERVICES[@]}"
}

# The same request as x-app-healthcheck in the compose file, so chat is asked on its port 80 too.
HEALTH_PROBE='exec 3<>/dev/tcp/127.0.0.1/80 && printf "GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -1 <&3 | grep -q " 200 "'

verify_release() {
  local release="$1" failed=0 svc cid image deadline
  log "Checking the database password of every app service"
  OWS_SRC_DIR="$SRC_DIR" bash "$DRIFT_CHECK" || failed=1

  log "Asking /health of every app service (up to ${health_timeout}s)"
  deadline=$((SECONDS + health_timeout))
  for svc in "${APP_SERVICES[@]}"; do
    cid="$(dc ps -q "$svc")"
    if [ -z "$cid" ]; then
      printf '%-26s %s\n' "$svc" "not running"; failed=1; continue
    fi
    image="$(docker inspect --format '{{.Config.Image}}' "$cid")"
    if [ "$release" != local ] && [ "$image" != "$(image_of "$svc" "$release")" ]; then
      printf '%-26s %s\n' "$svc" "runs $image"; failed=1; continue
    fi
    until timeout 15 docker exec "$cid" bash -c "$HEALTH_PROBE" >/dev/null 2>&1; do
      if [ "$SECONDS" -ge "$deadline" ]; then
        printf '%-26s %s\n' "$svc" "/health did not answer 200"; failed=1; continue 2
      fi
      sleep 5
    done
    printf '%-26s %s\n' "$svc" "healthy"
  done
  return "$failed"
}

# Keep the images of the release just deployed and of the one before it; remove older ones.
prune_images() {
  local svc ref
  for svc in "${APP_SERVICES[@]}"; do
    while read -r ref; do
      case "$ref" in
        *:"<none>"|*":linux-$sha"|*":linux-$previous") ;;
        *) docker image rm "$ref" >/dev/null 2>&1 || log "Could not remove $ref" ;;
      esac
    done < <(docker image ls --format '{{.Repository}}:{{.Tag}}' "$registry/$svc")
  done
}

finish() {
  printf '%s %s %s previous=%s result=%s\n' "$(date -u +%FT%TZ)" "$environment" "$sha" "$previous" "$1" \
    >> "$state_dir/history"
  echo "ows-deploy: previous=$previous checkout=$checkout result=$1"
  exit "$2"
}

log "Deploying $sha to $environment (last deployed: $previous, checkout: $checkout)"
if ! pull_release "$sha"; then
  log "The images of $sha are not all available; nothing was changed."
  finish failed 1
fi
if start_release "$sha" && verify_release "$sha"; then
  printf '%s\n' "$sha" > "$state_dir/current.tmp"
  mv "$state_dir/current.tmp" "$state_dir/current"
  prune_images
  log "Deployed $sha to $environment."
  finish deployed 0
fi

if [ "$previous" = "$sha" ]; then
  log "$sha failed its checks and is also the last deployed release; there is nothing older to roll back to."
  finish rollback_failed 4
fi
target="$previous"
[ "$previous" != none ] || target=local
log "$sha failed its checks; rolling back to $target"
if { [ "$target" = local ] || pull_release "$target"; } && start_release "$target" && verify_release "$target"; then
  log "Rolled back to $target. $sha is not deployed."
  finish rolled_back 3
fi
log "The rollback to $target failed its checks too; the app services need a hand."
finish rollback_failed 4
