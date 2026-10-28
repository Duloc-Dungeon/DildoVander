#!/usr/bin/env bash
# Requires repository administration permission; private rulesets require Pro.
set -euo pipefail
cd "$(dirname "$0")/../.."
repo='Duloc-Dungeon/DildoVander'
github() {
  local router="${HOME}/.local/share/github-account-routing/github_accounts.py"
  if [[ -f "$router" ]]; then
    python "$router" gh "$@"
  else
    gh "$@"
  fi
}
if [[ "${1:-}" != '--apply' ]]; then
  echo "Target: $repo"
  cat .github/repository-settings.json .github/main-ruleset.json .github/development-ruleset.json
  echo 'Apply with an administrator identity: bash scripts/ci/apply-repository-settings.sh --apply'
  exit 0
fi
# Read errors must stop before attempting a write with incomplete state.
declare -A rule_ids
for branch in main development; do
  rules=$(github api --paginate "repos/$repo/rulesets" --jq ".[] | select(.name == \"$branch quality gate\") | .id")
  if [[ "$rules" == *$'\n'* ]]; then
    echo "Multiple $branch quality gate rulesets exist; reconcile them manually." >&2
    exit 1
  fi
  rule_ids[$branch]="$rules"
done
github api --method PATCH "repos/$repo" --input .github/repository-settings.json \
  --jq '{delete_branch_on_merge,allow_squash_merge,allow_merge_commit,allow_rebase_merge,allow_update_branch}'
for branch in main development; do
  if [[ -z "${rule_ids[$branch]}" ]]; then
    github api --method POST "repos/$repo/rulesets" --input ".github/$branch-ruleset.json" --jq '{id,name,enforcement}'
  else
    github api --method PUT "repos/$repo/rulesets/${rule_ids[$branch]}" --input ".github/$branch-ruleset.json" --jq '{id,name,enforcement}'
  fi
done
