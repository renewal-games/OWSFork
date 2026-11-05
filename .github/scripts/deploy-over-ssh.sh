#!/usr/bin/env bash
# Deploys one release through the box's forced command (scripts/hetzner/deploy-release.sh) and reports it in
# the job summary. The pipeline never runs migrations, so a release range that adds any, or a rollback over
# one, gets a warning annotation; so does a box whose checkout has a different compose file than the release.
#
# Environment: DEPLOY_ENVIRONMENT, DEPLOY_SHA, DEPLOY_HOST, DEPLOY_KNOWN_HOSTS, DEPLOY_SSH_KEY, and optionally
# DEPLOY_USER (default ows-deploy) and DEPLOY_SSH_PORT (default 22). Run it from a full-history checkout
# (fetch-depth: 0) so the release range can be diffed.
set -euo pipefail

environment="${DEPLOY_ENVIRONMENT:-}"
sha="${DEPLOY_SHA:-}"
host="${DEPLOY_HOST:-}"
user="${DEPLOY_USER:-ows-deploy}"
port="${DEPLOY_SSH_PORT:-22}"
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"

fail() { echo "::error title=Deploy to ${environment:-?}::$*"; exit 1; }
warn() { echo "::warning title=$1::$2"; printf -- '- **%s.** %s\n' "$1" "$2" >> "$summary"; }

[[ "$environment" =~ ^(staging|production)$ ]] || fail "DEPLOY_ENVIRONMENT must be staging or production"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || fail "DEPLOY_SHA must be a full 40-character lowercase commit SHA"
[[ "$host" =~ ^[A-Za-z0-9][A-Za-z0-9.:-]*$ ]] || fail "set the $environment environment variable DEPLOY_HOST to the box's hostname or IP"
[[ "$user" =~ ^[a-z_][a-z0-9_-]*$ ]] || fail "DEPLOY_USER '$user' is not a user name"
[[ "$port" =~ ^[1-9][0-9]{0,4}$ ]] && [ "$port" -le 65535 ] || fail "DEPLOY_SSH_PORT '$port' is not a port"
[ -n "${DEPLOY_KNOWN_HOSTS:-}" ] || fail "set the $environment environment variable DEPLOY_KNOWN_HOSTS to the box's host key line"
[ -n "${DEPLOY_SSH_KEY:-}" ] || fail "set the $environment environment secret DEPLOY_SSH_KEY"

work="$(mktemp -d "${RUNNER_TEMP:-/tmp}/deploy.XXXXXX")"
trap 'rm -rf "$work"' EXIT
(
  umask 077
  printf '%s\n' "$DEPLOY_SSH_KEY" | tr -d '\r' > "$work/key"
  printf '%s\n' "$DEPLOY_KNOWN_HOSTS" | tr -d '\r' > "$work/known_hosts"
)

echo "Deploying $sha to $environment ($user@$host)"
set +e
ssh -F /dev/null -i "$work/key" -p "$port" \
  -o IdentitiesOnly=yes -o IdentityAgent=none -o BatchMode=yes \
  -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$work/known_hosts" -o GlobalKnownHostsFile=/dev/null \
  -o ConnectTimeout=20 -o ServerAliveInterval=30 -o ServerAliveCountMax=6 \
  "$user@$host" "$environment $sha" 2>&1 | tee "$work/deploy.log"
status="${PIPESTATUS[0]}"
set -e

report="$(grep -E '^ows-deploy: previous=' "$work/deploy.log" | tail -n 1 || true)"
field() { sed -n "s/.* $1=\([^ ]*\).*/\1/p" <<<"$report"; }
previous="$(field previous)"
checkout="$(field checkout)"
result="$(field result)"

{
  echo "### Deploy to $environment: ${result:-no result}"
  echo
  echo "| | |"
  echo "| --- | --- |"
  echo "| Release | \`$sha\` |"
  echo "| Last deployed before | \`${previous:-unknown}\` |"
  echo "| Checkout on the box | \`${checkout:-unknown}\` |"
  echo
} >> "$summary"

have_commit() { [[ "$1" =~ ^[0-9a-f]{40}$ ]] && git cat-file -e "$1^{commit}" 2>/dev/null; }

# Migrations in <to> that <from> lacks: entries added to the Postgres manifest, and new .sql files beside it.
added_migrations() {
  {
    git diff --no-renames -U0 "$1" "$2" -- Databases/Postgres/migration-manifest.txt \
      | sed -n 's/^+\([^+#[:space:]].*\)$/\1/p'
    git diff --no-renames --name-only --diff-filter=A "$1" "$2" -- 'Databases/Postgres/*.sql' \
      | sed 's|^Databases/Postgres/||'
  } | sed 's/[[:space:]]*$//' | sort -u | paste -sd, - | sed 's/,/, /g'
}

check_migrations() {
  local adds drops
  if [ "$previous" = none ]; then
    warn "Migrations unknown" "No release was recorded on the box before this one, so the pipeline cannot tell which migrations it needs. Check schemaversions against Databases/Postgres/migration-manifest.txt by hand."
    return
  fi
  if ! have_commit "$previous"; then
    warn "Migrations unknown" "The last deployed SHA '${previous:-unknown}' is not in this repository's history, so the release range cannot be checked for migrations."
    return
  fi
  adds="$(added_migrations "$previous" "$sha")"
  drops="$(added_migrations "$sha" "$previous")"
  case "$result" in
    deployed)
      [ -z "$adds" ] || warn "Migrations not run" "${previous:0:12}..${sha:0:12} adds migrations the pipeline did not run: $adds. Run them by hand (docs/hosting/hetzner-dev.md, Database Migrations)."
      [ -z "$drops" ] || warn "Older release" "$sha is older than ${previous:0:12}; these migrations stay applied: $drops."
      ;;
    rolled_back|rollback_failed)
      [ -z "$adds" ] || warn "Rollback keeps migrations" "The rollback does not revert migrations. If any of these from ${previous:0:12}..${sha:0:12} were applied, they stay: $adds."
      ;;
  esac
}

check_compose() {
  local changed
  [ "$result" = deployed ] || return 0
  if ! have_commit "$checkout"; then
    warn "Checkout unknown" "The box's checkout is at '${checkout:-unknown}', which is not in this repository's history, so its compose file could not be compared with the release's."
    return
  fi
  changed="$(git diff --name-only "$checkout" "$sha" -- src/docker-compose.hetzner-dev.yml src/.env.hetzner-dev.example | paste -sd, - | sed 's/,/, /g')"
  [ -z "$changed" ] || warn "Compose file behind" "The box's checkout (${checkout:0:12}) differs from the release in $changed, and compose ran with the checkout's copy. Pull the checkout on the box and check .env.hetzner-dev."
}

if [ -n "$report" ] && [ "$result" != failed ]; then
  check_migrations
  check_compose
fi

case "$result:$status" in
  deployed:0)
    echo "Deployed $sha to $environment." ;;
  failed:*)
    fail "The images of $sha could not all be pulled; nothing was changed. See the log." ;;
  rolled_back:*)
    if [ "$previous" = none ]; then target="the images .env.hetzner-dev names"; else target="$previous"; fi
    fail "$sha failed its checks on $environment and was rolled back to $target. See the log." ;;
  rollback_failed:*)
    fail "$sha failed its checks on $environment and the rollback did not pass either: the app services need a hand. See the log." ;;
  *)
    fail "The box reported no result (ssh exit $status; 2 means it refused the request). See the log." ;;
esac
