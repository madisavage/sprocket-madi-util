#!/usr/bin/env bash
# Opens a pull request on the mod manager registry with this repo's entries (registry/packages/madisavage/).
#
# Run: tools/registry-pr.sh [--dry-run]
#
# Clones the registry branch of furryaxw/SprocketModManager into build/registry-pr, copies the entries over
# packages/madisavage/, and if anything changed commits it on a branch registry-<Version>, pushes it to your
# fork (madisavage/SprocketModManager) and opens the PR against the registry branch.
# Needs git, python3 and the GitHub CLI (gh, logged in with `gh auth login`).
# --dry-run shows what would change, but commits, pushes and opens nothing.
set -euo pipefail

cd "$(dirname "$0")/.."
upstream=furryaxw/SprocketModManager
fork=madisavage/SprocketModManager
base=registry
dry_run=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run) dry_run=true ;;
        *) echo "Unknown option $1" >&2; exit 1 ;;
    esac
    shift
done

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
python3 tools/registry.py --check || { echo "Registry entries are out of date: run python3 tools/registry.py first." >&2; exit 1; }
grep -l '"TODO:' registry/packages/madisavage/*.json && { echo "Fill in the TODO descriptions above first." >&2; exit 1; }

# A fresh clone each run, so nothing left over from the last PR leaks in
work=build/registry-pr
rm -rf "$work"
git clone --quiet --single-branch --branch "$base" "https://github.com/$upstream.git" "$work"
branch="registry-$version"
git -C "$work" switch --quiet -c "$branch"

# Ours replace theirs, except translations the registry added (e.g. "zh") that ours lack are kept
stale_translations=$(python3 - registry/packages/madisavage "$work/packages/madisavage" <<'EOF'
import json, sys
from pathlib import Path
ours, theirs = Path(sys.argv[1]), Path(sys.argv[2])
for path in sorted(ours.glob("*.json")):
    entry = json.loads(path.read_text())
    target = theirs / path.name
    if target.exists():
        old = json.loads(target.read_text())
        for field in ("display_name", "description"):
            mine, kept = entry.get(field, {}), old.get(field, {})
            for lang, text in kept.items():
                if lang not in mine:
                    mine[lang] = text
                    if kept.get("en") != mine.get("en"):
                        print(f"{path.name} {field}.{lang}")
    target.write_text(json.dumps(entry, indent=4, ensure_ascii=False) + "\n")
EOF
)
git -C "$work" add -A packages/madisavage

if git -C "$work" diff --cached --quiet; then
    echo "The registry already has these entries; nothing to do."
    exit 0
fi
changes=$(git -C "$work" diff --cached --name-status)
echo "Changes against $upstream@$base:"
echo "$changes"
[[ -z "$stale_translations" ]] || printf 'Kept translations whose English text changed (may be stale):\n%s\n' "$stale_translations"
$dry_run && { git -C "$work" diff --cached --stat; echo "Dry run: nothing committed, pushed or opened."; exit 0; }

command -v gh >/dev/null || { echo "gh (GitHub CLI) is not installed." >&2; exit 1; }
if gh pr list -R "$upstream" --head "$branch" --state open --json url -q '.[].url' | grep -q .; then
    echo "A PR from $branch is already open; close it or bump <Version> first." >&2
    exit 1
fi

git -C "$work" commit --quiet -m "madisavage: registry entries for $version"
git -C "$work" push --quiet --force "git@github.com:$fork.git" "$branch"
body="Registry entries for madisavage/sprocket-madi-util $version.

\`\`\`
$changes
\`\`\`"
[[ -z "$stale_translations" ]] || body+="

These translations were kept, but their English text changed, so they may need updating:
$(sed 's/^/- /' <<< "$stale_translations")"
gh pr create -R "$upstream" --base "$base" --head "${fork%%/*}:$branch" \
    --title "madisavage: registry entries for $version" --body "$body"
