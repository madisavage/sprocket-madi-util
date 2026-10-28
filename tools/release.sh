#!/usr/bin/env bash
# Builds every plugin against the local game files and publishes a GitHub release.
#
# Run: tools/release.sh [--dry-run] [--game RANGE]
#
# The tag is <Version> from Directory.Build.props, so tag and plugin versions always match.
# The assets are the DLLs the registry packages list (registry/packages/*/*.json), plus MadiLib.xml.
# Needs dotnet, git, python3 and the GitHub CLI (gh, logged in with `gh auth login`).
# --dry-run builds and lists the assets, but tags and publishes nothing.
# --game sets the game versions the release supports, in the mod manager's range syntax
# (e.g. ">=0.2.55.5", "0.2.55.x"); default: the local game's version as a.b.c.x.
# It goes in the release body as the mod manager's sp-compat comment.
set -euo pipefail

cd "$(dirname "$0")/.."
dry_run=false
game_range=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run) dry_run=true ;;
        --game) game_range="${2:?--game needs a range}"; shift ;;
        *) echo "Unknown option $1" >&2; exit 1 ;;
    esac
    shift
done

# The game version is the 4-part version string Unity keeps in globalgamemanagers
game_root="${SPROCKET_GAME_ROOT:-..}"
game_version=$(grep -aoE '[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' "$game_root/Sprocket_Data/globalgamemanagers" | head -n 1 || true)
if [[ -z "$game_range" ]]; then
    [[ -n "$game_version" ]] || { echo "Couldn't read the game version from $game_root/Sprocket_Data/globalgamemanagers; pass --game." >&2; exit 1; }
    game_range="${game_version%.*}.x"
fi

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
# The registry's version_pattern: a suffix needs a hyphen, as 26.10.27-a
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    echo "Version '$version' in Directory.Build.props doesn't match the registry's version pattern (e.g. 26.10.27 or 26.10.27-a)." >&2
    exit 1
fi

if ! $dry_run; then
    command -v gh >/dev/null || { echo "gh (GitHub CLI) is not installed." >&2; exit 1; }
    [[ "$(git branch --show-current)" == main ]] || { echo "Release from main." >&2; exit 1; }
    [[ -z "$(git status --porcelain)" ]] || { echo "Commit or stash your changes first." >&2; exit 1; }
    git fetch --tags --quiet origin
    [[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || { echo "main isn't the same as origin/main; push or pull first." >&2; exit 1; }
    if git rev-parse -q --verify "refs/tags/$version" >/dev/null; then
        echo "Tag $version already exists; bump <Version> in Directory.Build.props." >&2
        exit 1
    fi
fi

echo "Building $version..."
dotnet build sprocket-madi.sln -c Release -p:SkipModDeploy=true -nologo -v q

assets=()
for dll in $(python3 -c '
import glob, json
for path in sorted(glob.glob("registry/packages/*/*.json")):
    for name in json.load(open(path))["release"]["assets"]["include"]:
        print(name)
'); do
    project=${dll%.dll}
    built="$project/bin/Release/net6.0/$dll"
    [[ -f "$built" ]] || { echo "Missing $built" >&2; exit 1; }
    assets+=("$built")
done
assets+=("MadiLib/bin/Release/net6.0/MadiLib.xml")

echo "Built against game $game_version; supports $game_range."
printf 'Assets:\n'
printf '  %s\n' "${assets[@]}"
$dry_run && { echo "Dry run: nothing tagged or published."; exit 0; }

git tag -a "$version" -m "$version"
git push origin "$version"
# Read by the mod manager; GitHub hides HTML comments. --generate-notes appends the changes below it
compat="<!-- sp-compat {\"hamish.sprocket\": [\"$game_range\"]} -->"
gh release create "$version" "${assets[@]}" --title "$version" --notes "$compat
Built against Sprocket $game_version." --generate-notes
echo "Released $version."
