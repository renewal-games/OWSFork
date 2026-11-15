#!/usr/bin/env bash
# Fails when an ecosystem listed in .github/dependabot.yml has had no Dependabot run in the
# last <days> days. dependabot.yml alone does nothing on a fork: GitHub leaves version updates
# off on forks until they are enabled under Settings > Advanced Security > Dependabot version
# updates. It also pauses them on repositories where Dependabot PRs go untouched for 90 days.
# Neither state produces an error anywhere, so this check is the only thing that notices.
#
# Usage: check-dependabot-version-updates.sh [dependabot.yml] [days]
# Needs gh (authenticated, with actions: read on the repository) and jq. The repository comes
# from GITHUB_REPOSITORY, or from the current gh checkout when that is unset.
# RUN_NAMES_FILE, when set, is read instead of the API (one run name per line), for testing.
set -euo pipefail

config="${1:-.github/dependabot.yml}"
days="${2:-8}"

# Dependabot names its runs "<package manager> in <directory> ...", and the package manager is
# spelled with underscores (github-actions -> github_actions).
mapfile -t ecosystems < <(sed -n 's/^[[:space:]]*-[[:space:]]*package-ecosystem:[[:space:]]*"\{0,1\}\([A-Za-z0-9_-]*\)"\{0,1\}.*$/\1/p' "$config" | tr '-' '_' | sort -u)
if [[ ${#ecosystems[@]} -eq 0 ]]; then
  echo "::error::No package-ecosystem entries found in $config."
  exit 1
fi

since=$(date -u -d "$days days ago" +%Y-%m-%dT%H:%M:%SZ)

if [[ -n "${RUN_NAMES_FILE:-}" ]]; then
  names=$(cat "$RUN_NAMES_FILE")
else
  repo="${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}"
  # Dependabot jobs run as the dynamic "Dependabot Updates" workflow.
  names=$(gh api -X GET "repos/$repo/actions/runs" --paginate \
    -f event=dynamic -f created=">=$since" -f per_page=100 \
    --jq '.workflow_runs[] | select(.actor.login == "dependabot[bot]") | .name')
fi

missing=()
for ecosystem in "${ecosystems[@]}"; do
  if grep -q "^$ecosystem in " <<< "$names"; then
    echo "$ecosystem: Dependabot ran since $since."
  else
    missing+=("$ecosystem")
  fi
done

if [[ ${#missing[@]} -eq 0 ]]; then
  exit 0
fi

for ecosystem in "${missing[@]}"; do
  echo "::error title=Dependabot not running::No $ecosystem Dependabot run since $since, although $config schedules one weekly."
done
echo "Enable Dependabot version updates under Settings > Advanced Security, or resume them under Insights > Dependency graph > Dependabot if GitHub paused them."
exit 1
