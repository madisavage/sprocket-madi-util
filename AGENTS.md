# AGENTS.md

BepInEx 6 (IL2CPP) plugins for Sprocket, plus `MadiLib`, the library they share. `README.md` covers what each plugin does, and `contributing.md` has the rules for human contributors. This file is those rules for coding agents.

## Commits

- Don't commit, branch, tag or release unless the human asks. A human reviews every change before it goes in.
- Don't add `Co-Authored-By` lines or any other AI attribution to commits or PRs. The human is the author.
- `tools/release.sh` and version bumps in `Directory.Build.props` are for the maintainer only.

## Build and test

```sh
dotnet build sprocket-madi.sln -p:SkipModDeploy=true   # build without copying DLLs into BepInEx/plugins
dotnet test tests                                       # unit tests for pure logic (xunit, not in the solution)
```

The build references the game's interop assemblies. Set `SPROCKET_GAME_ROOT` if the repo isn't inside the game folder. A plain build copies each DLL to `BepInEx/plugins`, which changes the human's game install, so pass `-p:SkipModDeploy=true` unless they ask for a deploy.

Patches can't be unit tested. When you finish, say what needs checking in game. Don't claim something works in game unless the human checked it.

## Code style

Write code in the style the repo already has: simple and clear to anyone who programs, not clever or maximally idiomatic C#.

Some older code breaks these rules (e.g. `is not { } x` patterns, chained ternaries, long one-line LINQ). Don't copy it. When you change a function that breaks a rule, rewrite it to follow the rules, without changing behaviour. TODO item F28 tracks rewriting the rest.

- One check per line, with each case spelled out. No packing logic into pattern matching, chained `?:`, or long boolean expressions.
- Use plain names and plain words. Comments say *why* or *what a value means* (`// Drive blueprint, its speed limit property`), not what the next line does.
- Public MadiLib members get `/// <summary>` docs. They ship as `MadiLib.xml`, and `docs/MadiLib.md` should match them.
- Match the surrounding file's layout: block namespaces, a `Plugin : BasePlugin` entry point, nested `[HarmonyPatch]` classes.
- Keep pure logic (math, parsing, migration) free of game types where you can, so `tests/` can cover it. Add a test there for new logic of this kind.
- Wrap new per-frame hooks in `Profile.Measure`.
- Use `var` only when the type is named on the same line (`new List<Face>()`, `TryCast<PlateStructure>()`); otherwise write the type out. In IL2CPP code, `List` vs `Il2CppSystem...List` or a Unity alias matters, and `var` hides it.
- Hooks fail toward the game's own behaviour: catch at the hook, log once what the player will see (`"Couldn't set the HEAT jet's budget, the game's stands"`), and let the game's code run. In per-frame code, set a flag so it logs once, not every frame (`broken` in `highExplosive/Jet.cs`).
- No empty catches. When reading files, catch the specific exceptions with a `when` filter and log what was skipped (`armourMaterial/Main.cs`).
- Parse and format numbers with `CultureInfo.InvariantCulture`. Without it, players whose decimal mark is a comma get broken saves.
- Use `TryCast<T>()` on IL2CPP objects, never a C# cast or `as`.
- A Prefix that skips the original (`return false`) gets a one-line comment saying why.

Bad:

```csharp
internal static bool Allowed(float year, int? now) => !Plugin.Historical.Value || now is not int y || y >= year;
```

Good:

```csharp
internal static bool Allowed(float year, int? now)
{
    // Historical availability off: everything can be picked
    if (!Plugin.Historical.Value)
        return true;
    // Outside the designer: nothing is limited
    if (now == null)
        return true;
    return now.Value >= year;
}
```

## Save compatibility

This rule is non-negotiable.

- Settings live in the blueprint: in part descriptions (JSON), name tags such as `[stab]`, or fields the game already saves. Never in a separate file.
- A vehicle saved with the mods must still load without them. Players without the mods get the game's own behaviour.
- Old files must keep loading. If a saved format changes, read the old one and convert it, and add a test (see `tests/RoundsMigration.cs`).
- Limits such as the historical year restrictions only hide choices in the designer. A vehicle that already has something keeps it.

## Use MadiLib

Before writing a helper, check `MadiLib/` and `docs/MadiLib.md`. It has keybinds, Settings tab sections, HUD panels, editor panel controls (`EditorGui`), the player's vehicle, `ObjectCache`, `Introduced` (year limits), `TechFiles` and `Profile`. If something you write would be useful to more than one plugin, put it in MadiLib and document it.

## IL2CPP and Harmony gotchas

- Constructor patches fail. Patch a method the constructor calls, or a later one.
- Setting `FloatProperty.SoftMax`, or making Min equal Max, kills the slider.
- Ref-returned non-blittable structs (e.g. AI `ShortTargetInfo`) throw.
- Patch classes need a class-level `[HarmonyPatch]`.
- Unity types come in through `extern alias UnityCoreModule;`. Copy the `using` aliases from a neighbouring file.

## README

Every player-facing change updates `README.md` in the same change. Write it for players, not programmers. Say what it does and where it shows up in the game, the range and default, where it's saved, and what happens without the mod. Follow the existing sections, e.g. madiUtil's "Operating pressure".

## TODO.md

`TODO.md` holds the bug reports (`B1`, ...) and features (`F12`, ...). Each item is a checkbox line:

```markdown
- [ ] **F12. Short title** — files and symbols involved (`folder/File.cs` (`Method`)); game types and the interop DLL they're in
  - Today: what happens now.
  - Goal: what should happen.
  - Find out first: questions to answer in the code or the game before building.
  - Fix / rules: the plan, one bullet per case.
  - Keep: behaviour that must not change (usually save compatibility).
  - Open questions: decisions for the human.
  - Check: how to verify it in game.
```

- Treat line numbers and plans as stale. The items were written without building, so check each one against the code first.
- Ask the human about anything under **Open questions** before deciding it yourself.
- When you build an item, tick it (`[x]`) and add a first sub-bullet: `**Built (YYYY-MM-DD):** what was done, defaults, and what's not done`. Add "checked in game" only when the human says they checked it.
- Don't delete items. The maintainer removes finished ones.
- **Decided against** lists ideas that were rejected on purpose. Don't bring them back without asking.
