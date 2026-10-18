# sprocket-madi

BepInEx 6 (IL2CPP) plugins for Sprocket, plus the library they share.

| Folder | Plugin | What it does |
| --- | --- | --- |
| `MadiLib/` | `MadiLib.dll` | Shared library: keybinds, the **MadiLib** Settings tab, HUD panels, editor panel controls, config helpers. Required by the others. |
| `madiUtil/` | `madiUtil.dll` | Designer sliders, a save keybind and a scenario era unlock toggle. |
| `stagedAmmo/` | `stagedAmmo.dll` | Ready racks, stowage, re-racking and autoloaders. |
| `subcaliber/` | `subcaliber.dll` | Discarding sabot rounds: a projectile narrower than the gun. |
| `composite/` | `composite.dll` | Pseudo-composite armour: stack selected faces into thin plates. |

## Install

Download the DLLs from the latest release and put them in `BepInEx/plugins`. Always include `MadiLib.dll`; add whichever plugins you want.

## MadiLib

Adds a **MadiLib** tab to the game's Settings screen, shown once any plugin uses it. Changes there save at once.

For plugin authors: `MadiLib.dll` has keybinds, Settings tab sections, HUD panels, the player's vehicle, editor panel controls (collapsible sections, dropdowns, live readouts), a per-object cache and per-key config. See the [API reference](docs/MadiLib.md); the same docs ship as `MadiLib.xml` for your editor.

## madiUtil

Utilities because file editing tanks on Linux is really annoying, you will not catch me navigating to `/home/madi/.steam/steam/steamapps/compatdata/1674170/pfx/drive_c/users/steamuser/Documents/My Games/Sprocket/Factions/Default/Blueprints/Vehicles`.

### Efficiency
Selecting a crew seat in the vehicle designer shows a **Base efficiency** slider (0–100, step 0.25) in the edit pane. The slider reads and writes `CrewSeatBlueprint.BaseEfficiency`.

### Laying drive torque
Selecting a laying drive shows **Elevation torque multiplier** and **Azimuth torque multiplier** sliders (0–100, step 0.25). They read and write `LayingDriveBlueprint.Elevation.TorqueMultiplier` and `LayingDriveBlueprint.Azimuth.TorqueMultiplier`.

### Save keybind
**Save vehicle** (default Ctrl+S) saves the vehicle open in the designer, the same as its save button. Rebind it on the MadiLib tab.

### Unlock scenario eras
**Unlock scenario eras** on the MadiLib tab toggles the `UnlockScenarioEras` setting, which was removed from the cheats menu. On, scenarios accept vehicles from any era. 

## stagedAmmo

Staged ammunition. The loader only takes shells from **ready racks**, racks close to the gun's breech. Racks farther away are **stowage**; over time, crew move shells from stowage into the ready racks.

- A rack is a ready rack for a gun when the loader path from breech to rack is at most **Ready rack distance** (default 1.5 m).
- The loader ignores stowage racks. Once ready racks run dry, the gun waits for restock.
- While the gun has a living loader who isn't loading it, they re-rack: one shell at a time into the closest ready rack with room, taken from the next matching rack farther out (ready or stowage). Ammo moves inward rack by rack. Each carry takes as long as the game's loader rules give for that shell: pick up, walk the crew path between the two racks, put down; divided by the loader's crew efficiency and scaled by **Re-rack time multiplier** (default 1). Loading pauses re-racking; it resumes where it left off.
- A box in the bottom-left corner shows, per gun on your vehicle, ready and stowed shells and re-rack progress. Toggle with **Show indicator**.

### Autoloaders

Tick **Autoloader** in an ammo rack's edit panel to make it an autoloader rack. The checkbox adds **[auto]** to the rack's name, so it travels with shared vehicle files; typing the tag yourself works too.

- A gun with any autoloader racks loads itself from those, with or without a loader. They are its ready racks; all its other racks are stowage, however close.
- The loader doesn't load an autoloaded gun. They only re-rack, from stowage into the autoloader racks, and don't pause for loading.
- **Reload speed** (0.1–10, shown once **Autoloader** is ticked) sets that rack's speed: 1 is a loader at 100% efficiency. It's saved in the name as `[auto x2]` for 2. **Speed multiplier** (default 1) scales every autoloader on top.
- The indicator marks autoloaded guns with **(autoloader)**.

Settings are on the MadiLib tab, and in `BepInEx/config/madi.stagedammo.cfg` under `[Staging]`, `[Autoloader]` and `[Display]`.

## subcaliber

Selecting a cannon in the vehicle designer adds a collapsible **Penetrator** section:

- **Projectile diameter** (1 mm to the caliber). Below the caliber, the shell is a sabot round.
- **Penetrator length** in calibers of its own diameter (1 to 30; the vanilla shells are 3).
- **Material**, a dropdown of the materials file.
- Readouts that update as you change them: **Sabot muzzle velocity**, **Sabot penetration** (at the muzzle) and **Penetrator / sabot mass**.

How it works:

- The cartridge stays full-bore: same charge, case and rack size.
- The sabot fills the bore around the penetrator, along its length, at **Sabot density** times steel's (default 0.35, about aluminium). Muzzle velocity comes from the game's interior ballistics with the full-bore charge pushing penetrator plus sabot. The sabot is discarded at the muzzle.
- In flight the penetrator drags at its own diameter and hits with its own diameter and mass. Penetration is the game's de Marre formula, so length and density help through mass only (as √mass). Long-rod (APFSDS) behaviour isn't modelled.

The armour view's penetration simulator also gets a **Penetrator** section with **Penetrator length** and **Material**. Set **Calibre** to the penetrator's diameter and **Penetration** as usual; These make very small changes to the penetration characteristics.

Materials are in `BepInEx/config/madi.subcaliber.materials.json`, written on first run as material name to density in kg/m³ (steel 7850, tungsten carbide 15600, tungsten heavy alloy 17600, depleted uranium 19100). Edit it and restart to add or change materials.

**Sabot density** is on the MadiLib tab, and in `BepInEx/config/madi.subcaliber.cfg` under `[Sabot]`. Per-shell settings are in the same file under `[Projectile diameter]`, `[Penetrator length]` and `[Penetrator material]`, keyed by the shell's projectile GUID. They don't travel with shared vehicle files. Changing the shell's caliber or propellant may give it a new GUID; set them again.

The game's own designer readouts and the gunner's sight range marks still use the game's full-bore steel round.

With Dynamic Gun Sight (`SprocketDynamicSight.dll`) installed, its range marks use the penetrator: the muzzle velocity it predicts before a shot is the sabot round's, and its drag uses the penetrator's diameter and mass. It caches rounds until the scene changes, so after changing a shell's penetrator settings, change scene before trusting the marks.

## composite

Pseudo-composite armour: a stack of thin plates. In a freeform compartment's edit panel, a collapsible **Composite** section:

- **Layers**: plates in the stack, counting the selected faces (2 to 50).
- **Layer thickness**: every plate's thickness in mm; the selected faces are set to it too.
- **Gap**: air between plates, in mm.
- **Axis**: **Face normals**, or **X**, **Y** or **Z** (default **Y**). Along an axis, every plate is the same shape moved along that world axis, such as a glacis stacked straight up; the gap is still measured square to the plates.
- **Stack inward**: off, new plates go out, the side the faces face; on, they go in.
- **Stack selected faces**: copies the selected faces (and their mirrors, with mirror on) into the stack, in the same compartment. One undo reverts it.

Copies of adjacent faces share corners, so a selected surface stacks as connected surfaces. Settings reset each session.

## Build

`Directory.Build.props` holds the shared settings. Building copies each plugin's DLL to `BepInEx/plugins`; pass `-p:SkipModDeploy=true` to only write to `bin/`. Build everything:

```powershell
dotnet build .\sprocket-madi.sln --configuration Release `
  -p:SprocketGameRoot="<Sprocket Location>"
```

`SprocketGameRoot` defaults to the `SPROCKET_GAME_ROOT` environment variable, then to the folder above this repo.

## Release

1. Set `<Version>` in `Directory.Build.props`. Every plugin's `[BepInPlugin]` version and DLL file version come from it (through a generated `ModInfo.Version`).
2. Build in Release.
3. Publish a GitHub release tagged with that version (SemVer, such as `26.10.12`; a leading `v` is fine) and upload every `bin/Release/net6.0/<name>.dll` file, plus `MadiLib/bin/Release/net6.0/MadiLib.xml` for plugin authors.

Every plugin shares the release tag as its version.

## Mod manager registry

`registry/mods/madisavage/` holds this repo's entries for the [SprocketModManager registry](https://github.com/furryaxw/SprocketModManager/blob/registry/CONTRIBUTING.en.md). They mirror the registry's own paths: copy them into `mods/madisavage/` on its `registry` branch and open a pull request. The registry reads versions and DLLs from this repo's releases, so only update the entries when their metadata changes. Dependencies on MadiLib accept any version (`"version": "*"`); the plugins themselves refuse to load against a MadiLib older than they are, so the entries don't change per release. If a MadiLib release breaks older plugins, add a dependency rule with a `when` for the plugin versions that need the new MadiLib.
