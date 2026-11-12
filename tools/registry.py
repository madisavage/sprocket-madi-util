"""Keeps the mod manager's registry entries (registry/packages/madisavage/*.json) in step with the code.

Run: python3 tools/registry.py           writes the entries
     python3 tools/registry.py --check   lists stale entries and exits 1 if any (tools/release.sh runs this)

For each plugin project (a folder whose .cs files carry [BepInPlugin]) it:
- creates an entry if there is none, with placeholders for the description and tags to fill in by hand;
- sets its dependencies from the plugin's own hard [BepInDependency] on this repo's plugins, as ">=<Version>"
  (Directory.Build.props), since every plugin requires its dependencies at its own version or newer; a "when" already
  written for a dependency is kept, else it's "*" (every version of this package).
Everything else in an entry (description, tags, category, recommendations...) is left as written.
Entries with no plugin project (eraExpansion's data package) only get their dependencies' "when" filled in.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGES = ROOT / "registry" / "packages" / "madisavage"
OWNER = "madisavage"

PLUGIN = re.compile(r'\[BepInPlugin\(\s*"([^"]+)"\s*,\s*"([^"]+)"')
# Hard dependencies only: a soft one passes DependencyFlags.SoftDependency instead of a version
DEPENDENCY = re.compile(r'\[BepInDependency\(\s*"([^"]+)"\s*,\s*">="')


def kebab(name):
    return re.sub(r"(?<!^)(?=[A-Z])", "-", name).lower()


def version():
    text = (ROOT / "Directory.Build.props").read_text()
    return re.search(r"<Version>(.*?)</Version>", text).group(1)


def plugins():
    """Each plugin project: (folder, GUID, plugin name, hard dependency GUIDs)."""
    found = []
    for csproj in sorted(ROOT.glob("*/*.csproj")):
        folder = csproj.parent
        if folder.name == "tests":
            continue
        source = "\n".join(p.read_text() for p in folder.glob("*.cs"))
        if match := PLUGIN.search(source):
            found.append((folder, match.group(1), match.group(2), DEPENDENCY.findall(source)))
    return found


def stub(name, dll):
    template = json.loads((PACKAGES / "stabilizer.json").read_text())
    template.update({
        "id": f"{OWNER}.{kebab(name)}",
        "name": name,
        "display_name": {"en": re.sub(r"(?<!^)(?=[A-Z])", " ", name[0].upper() + name[1:])},
        "description": {"en": "TODO: one or two sentences for the mod manager."},
        "tags": [],
        "recommendations": [],
    })
    template["release"]["assets"]["include"] = [dll]
    return template


def main():
    check = "--check" in sys.argv
    v = version()
    entries = {p: json.loads(p.read_text()) for p in sorted(PACKAGES.glob("*.json"))}
    by_name = {e["name"]: p for p, e in entries.items()}
    projects = plugins()
    package_of = {guid: (entries[by_name[name]]["id"] if name in by_name else f"{OWNER}.{kebab(name)}") for _, guid, name, _ in projects}

    wanted = {}
    for folder, guid, name, deps in projects:
        path = by_name.get(name, PACKAGES / f"{kebab(name)}.json")
        entry = json.loads(json.dumps(entries.get(path) or stub(name, f"{folder.name}.dll")))
        # A "when" written by hand (from which of this package's versions it depends) is kept
        when = {d["id"]: d["when"] for d in entry.get("dependencies", []) if "when" in d}
        ours = [{"id": package_of[d], "version": f">={v}", "when": when.get(package_of[d], "*")} for d in deps if d in package_of]
        others = [d for d in entry.get("dependencies", []) if d["id"] not in package_of.values()]
        entry["dependencies"] = ours + others
        wanted[path] = entry
    for path, entry in entries.items():
        wanted.setdefault(path, json.loads(json.dumps(entry)))
    for entry in wanted.values():
        for dep in entry.get("dependencies", []):
            dep.setdefault("when", "*")

    stale = []
    for path, entry in wanted.items():
        text = json.dumps(entry, indent=4, ensure_ascii=False) + "\n"
        if not path.exists() or path.read_text() != text:
            stale.append(path)
            if not check:
                path.write_text(text)
    for path in stale:
        new = path not in entries
        print(f"{'stale' if check else 'wrote'} {path.relative_to(ROOT)}{' (new: fill in description and tags)' if new else ''}")
    if check and stale:
        sys.exit(1)


if __name__ == "__main__":
    main()
