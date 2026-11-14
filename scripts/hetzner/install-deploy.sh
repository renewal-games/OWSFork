#!/usr/bin/env bash
# Install or update the CI deploy target on this box, as root:
#   install-deploy.sh <staging|production> <ci-deploy-key.pub>
# It creates the ows-deploy user (key login only, docker group), installs deploy-release.sh as
# /usr/local/sbin/ows-deploy-release, writes /etc/default/ows-deploy for the environment, creates the state
# folder /var/lib/ows-deploy, lets ows-deploy read src/.env.hetzner-dev, and installs the CI public key in
# ~ows-deploy/.ssh/authorized_keys restricted to that one command. Then log ows-deploy in to GHCR
# (docs/hosting/hetzner-dev.md, Continuous Deployment). Run it again after deploy-release.sh changes.
set -euo pipefail

die() { echo "!! $*" >&2; exit 2; }

[ "$#" -eq 2 ] || die "usage: $0 <staging|production> <ci-deploy-key.pub>"
environment="$1"
pubkey_file="$2"
[[ "$environment" =~ ^(staging|production)$ ]] || die "the environment is staging or production, not '$environment'"
[ "$(id -u)" -eq 0 ] || die "run as root"

repo="$(cd "$(dirname "$0")/../.." && pwd)"
[ "$repo" = /opt/owsfork ] || die "run it from the checkout at /opt/owsfork (deploy-release.sh deploys from there), not $repo"
user=ows-deploy
command=/usr/local/sbin/ows-deploy-release
settings=/etc/default/ows-deploy
state=/var/lib/ows-deploy
env_file="$repo/src/.env.hetzner-dev"
registry=ghcr.io/renewal-games/owsfork

[ -f "$env_file" ] || die "$env_file is missing"
command -v flock >/dev/null || die "flock is missing (util-linux)"
compose_help="$(docker compose up --help)" || die "docker compose is not installed"
[[ "$compose_help" == *--no-build* && "$compose_help" == *--pull* ]] \
  || die "this docker compose has no 'up --no-build --pull'; update Docker Compose"

# Exactly one ed25519 public key, with no options of its own: this script adds the restrictions.
key="$(grep -Ev '^[[:space:]]*(#|$)' "$pubkey_file")" || die "$pubkey_file has no key"
[ "$(printf '%s\n' "$key" | wc -l)" -eq 1 ] || die "$pubkey_file must hold exactly one public key"
read -r key_type key_blob _ <<<"$key"
[ "$key_type" = ssh-ed25519 ] || die "the CI deploy key must be a bare ssh-ed25519 public key, not '$key_type'"
ssh-keygen -l -f "$pubkey_file" >/dev/null || die "$pubkey_file is not a valid public key"

if ! id "$user" >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash --comment "OWSFork CI deploys" "$user"
fi
home="$(getent passwd "$user" | cut -d: -f6)"
# No password can match '*', and sshd does not treat it as a locked account, so only the key logs in.
usermod -p '*' "$user"
usermod -aG docker "$user"

# The CI deploy key must be its own key: refuse one that already opens another account here.
for f in /root/.ssh/authorized_keys /home/*/.ssh/authorized_keys; do
  [ -f "$f" ] && [ "$f" != "$home/.ssh/authorized_keys" ] || continue
  if grep -qF "$key_blob" "$f"; then die "this key is already in $f; generate a separate key for CI"; fi
done

install -m 755 -o root -g root "$repo/scripts/hetzner/deploy-release.sh" "$command"

if [ -f "$settings" ]; then
  # shellcheck disable=SC1090
  current="$(. "$settings" && echo "${OWS_DEPLOY_ENVIRONMENT:-}")"
  [ "$current" = "$environment" ] || die "$settings says this box deploys '$current'; remove it first to change that"
else
  cat > "$settings" <<EOF
# Read by $command (scripts/hetzner/deploy-release.sh). Written by scripts/hetzner/install-deploy.sh.
OWS_DEPLOY_ENVIRONMENT=$environment
OWS_DEPLOY_REGISTRY=$registry
# Seconds every app service gets to answer /health after a deploy.
OWS_DEPLOY_HEALTH_TIMEOUT=180
EOF
fi
chown root:root "$settings"
chmod 644 "$settings"

install -d -m 750 -o "$user" -g "$user" "$state"

chgrp "$user" "$env_file"
chmod 640 "$env_file"

# Root owns the key file, so the deploy user cannot change its own restrictions.
install -d -m 755 -o root -g root "$home/.ssh"
printf 'restrict,command="%s" %s\n' "$command" "$key" > "$home/.ssh/authorized_keys.tmp"
chmod 644 "$home/.ssh/authorized_keys.tmp"
mv "$home/.ssh/authorized_keys.tmp" "$home/.ssh/authorized_keys"

for f in "$env_file" "$repo/src/docker-compose.hetzner-dev.yml" "$repo/scripts/hetzner/check-db-password-drift.sh"; do
  runuser -u "$user" -- test -r "$f" || die "$user cannot read $f"
done

echo "Deploy target for '$environment' installed: $command, key in $home/.ssh/authorized_keys."
echo "Next, as root:"
echo "  su - $user -c 'docker login ghcr.io -u <github user> --password-stdin'   # paste a read:packages token"
echo "DEPLOY_KNOWN_HOSTS for the GitHub '$environment' environment is this line, prefixed with DEPLOY_HOST"
echo "(written [host]:port when SSH is not on port 22):"
echo "  $(cut -d' ' -f1,2 /etc/ssh/ssh_host_ed25519_key.pub)"
