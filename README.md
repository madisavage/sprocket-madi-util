# sprocket-madi

BepInEx 6 (IL2CPP) plugins for Sprocket, plus the library they share.

| Folder | Plugin | What it does |
| --- | --- | --- |
| `MadiLib/` | `MadiLib.dll` | Shared library: keybinds, the **MadiLib** Settings tab, HUD panels, editor panel controls, config helpers. Required by the others. |
| `madiUtil/` | `madiUtil.dll` | Designer sliders, a save keybind and a scenario era unlock toggle. |
| `stagedAmmo/` | `stagedAmmo.dll` | Ready racks, stowage, re-racking and autoloaders. |
| `subcaliber/` | `subcaliber.dll` | Discarding sabot rounds: a projectile narrower than the gun, with long-rod penetration. |
| `composite/` | `composite.dll` | Pseudo-composite armour: stack thin plates behind selected faces, in add-ons that can each have their own material. |
| `stabilizer/` | `stabilizer.dll` | Naive gun stabilization for traverse motors and laying drives. |
| `armourMaterial/` | `armourMaterial.dll` | Pick any armour material for a plate structure. |

## Install

Download the DLLs from the latest release and put them in `BepInEx/plugins`. Always include `MadiLib.dll`; add whichever plugins you want.

## MadiLib

Adds a **MadiLib** tab to the game's Settings screen, shown once any plugin uses it. Changes there save at once.

**Scroll speed** (1–30, default 7) sets how far a mouse wheel step scrolls an open dropdown list, for every dropdown in the game. 7 matches the edit panel; the game's dropdowns use 0.05, a fraction of a pixel per step. It's on the MadiLib tab, and in `BepInEx/config/madi.madilib.cfg` under `[Dropdowns]`.

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

### Era and material editor
`tools/techedit.py` is a Qt app for the game's `StreamingAssets/Eras` and `StreamingAssets/Technology` folders. Run it with `uv run tools/techedit.py` (uv installs PySide6); pass the game folder if the repo isn't inside it.

- A timeline of eras (greyed when not playable) with every armour material under its invention date. Ctrl+wheel zooms; click to jump to the entry.
- **Eras**: create, copy, delete and edit file name, name, start date, playable and the medium/heavy vehicle masses.
  Below the form, every technology in the selected era (dated from its start up to the next era's) with its properties: edit file, type and date in place (a new date can move it to another era), edit, rename, add and remove properties, and copy or delete technologies.
- **Materials**: a sortable table of every tech with an `rhaFactor`. Edit cells in place; a blank optional cell removes the key. New copies the selected material.
- **Save all** (Ctrl+S) writes changed files. Deleting renames the file to `.json.bak`.

`uv run tools/techedit.py --check` round-trips every file through the writer.

### Penetration plot
`tools/penetration.py` plots real rounds' penetration against the game's de Marre, Lanz-Odermatt and subcaliber's blend of the two, and sweeps a rod's length in a 105 mm gun against the full-bore round. Run it with `uv run tools/penetration.py` (uv installs matplotlib), or pass a file name to save a PNG. The rounds' figures are rough; replace them in its `ROUNDS` list with sourced values. Keep its `BLEND` in step with `LongRod.BlendStart` and `BlendEnd`.

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
- **Sabot coverage**, the fraction of the penetrator's length the sabot covers (0.05 to 1, default 1; about 0.25 to 0.35 for a long rod's saddle sabot), and **Sabot density** relative to steel (0 to 1, default 0.35, about aluminium); they only matter below the caliber.
- **Material**, a dropdown of the materials file.
- Readouts that update as you change them: **Sabot muzzle velocity**, **Sabot penetration** (at the muzzle) and **Penetrator / sabot mass**.

How it works:

- The cartridge stays full-bore: same charge, case and rack size.
- The sabot fills the bore around the penetrator, along **Sabot coverage** of its length, at **Sabot density** times steel's. Muzzle velocity comes from the game's interior ballistics with the full-bore charge pushing penetrator plus sabot. The sabot is discarded at the muzzle.
- The charge has to push its own gas too: about a third of its mass moves at projectile speed, so light projectiles leave slower than the game's formula says. Charge mass is the case's length at bore width times **Propellant density** (default 800 kg/m³; 0 turns it off). Scaled so a full-bore 3-caliber projectile is unchanged; a light sabot round can lose 30–40% (a 25 mm, 3.5-caliber DU rod in a 105 mm gun with a 600 mm case keeps about 0.68 of the game's velocity).
- Every muzzle velocity is softly capped at **Max muzzle velocity** (default 2200 m/s): `v / (1 + (v/max)⁸)^⅛`. Rounds under about three quarters of it lose well under 1%; none pass it.
- In flight the penetrator drags at its own diameter and hits with its own diameter and mass. Penetration blends the game's de Marre formula into the Lanz-Odermatt long-rod equation by length (below).

The armour view's penetration simulator also gets a **Penetrator** section with **Penetrator length** and **Material**. Set **Calibre** to the penetrator's diameter and **Penetration** as usual; These make very small changes to the penetration characteristics.

Materials are in `BepInEx/config/madi.subcaliber.materials.json`, written on first run as material name to density in kg/m³ (steel 7850, tungsten carbide 15600, tungsten heavy alloy 17600, depleted uranium 19100). Edit it and restart to add or change materials.

Per-shell settings are in `BepInEx/config/madi.subcaliber.cfg` under `[Projectile diameter]`, `[Penetrator length]`, `[Penetrator material]`, `[Sabot coverage]` and `[Sabot density]`, keyed by the shell's projectile GUID. They don't travel with shared vehicle files. Changing the shell's caliber or propellant may give it a new GUID; set them again.

The game's own designer readouts and the gunner's sight range marks still use the game's full-bore steel round.

### Long rods

A subcaliber penetrator's penetration is a straight blend of the game's de Marre and the Lanz-Odermatt perforation equation by length: all de Marre at 0 calibers, half each at 4, all Lanz-Odermatt at 8 calibers or longer. De Marre gives small fast penetrators far too much (about 1.5x real APDS, 2–3x real APFSDS) and Lanz-Odermatt gives stubby ones too little (about 0.75x real APDS), so APDS-length rounds land between them (the L7's L52 APDS comes out about 360 mm against a real 300 or so); `tools/penetration.py` plots all three against real rounds. With Lanz-Odermatt, length, material and target hardness count, not only mass. A penetrator 8 calibers or longer is a long rod and spalls more.

- On impact, the game's penetrator constant is swapped for the one that makes its de Marre give the blended penetration at the impact speed. Obliquity, multiple plates, spall and damage are still the game's; through stacked plates, penetration falls off with speed as de Marre does.
- Below Lanz-Odermatt's minimum speed for the material, the game's penetration applies.
- The cannon panel's **Sabot penetration** readout shows the blended value.
- At a fixed speed the blend dips slightly from about 4 to 8 calibers, where de Marre's share falls faster than Lanz-Odermatt rises: at 2000 m/s a 25 mm DU rod loses up to about 60 mm there. Shorter rods also leave faster, which offsets some of it.
- **Spall multiplier** (0–3, default 1.25) scales each spall burst a long rod makes. The game's burst is a fixed number of fragments; the whole part of the multiplier repeats it (2 is two bursts), and the rest adds one burst of that fraction of the mass (1.25 is a normal burst plus a quarter-mass one). 0 is no spall. The armour view's simulator uses it too when its **Penetrator length** is 8 calibers or more.
- **Target hardness** is the Brinell hardness long rods are rated against (default 260, rolled armour).
- Coefficients per material are in `BepInEx/config/madi.subcaliber.longrod.json`, keyed by material name: `A`, `C0`, `C1` (tungsten and DU form) or `A`, `C0`, `K`, `N`, `Hardness` (steel form, used when `K` isn't 0). Materials missing from it use tungsten's.

The armour view's simulator fires at whatever **Penetration** you enter, so enter the long-rod value from the cannon panel (with **Calibre**, **Penetrator length** and **Material** matching the rod) and it behaves as combat does.

**Max muzzle velocity**, **Propellant density**, **Target hardness** and **Spall multiplier** are on the MadiLib tab, and in `BepInEx/config/madi.subcaliber.cfg` under `[Muzzle velocity]` and `[Long rod]`.

With Dynamic Gun Sight (`SprocketDynamicSight.dll`) installed, its range marks use the penetrator: the muzzle velocity it predicts before a shot is the sabot round's, and its drag uses the penetrator's diameter and mass. It caches rounds until the scene changes, so after changing a shell's penetrator settings, change scene before trusting the marks.

## composite

Pseudo-composite armour: a stack of thin plates. In a freeform compartment's edit panel, a collapsible **Composite** section:

- **Layers**: plates added behind the selected faces (1 to 50).
- **Layer thickness**: each added plate's thickness in mm; the selected faces keep theirs.
- **Gap**: air between plates, in mm.
- **Add-ons**: how many add-ons the plates alternate between, nearest first (1 to 10). With 3 and 6 layers, plates 1 and 4 go in the first, 2 and 5 in the second, 3 and 6 in the third, so Steel > Kevlar > SiC > Steel > Kevlar > SiC is three add-ons, each given its material.
- **Axis**: **Face normals**, or **X**, **Y** or **Z** (default **Y**). Along an axis, every plate is the same shape moved along that world axis, such as a glacis stacked straight up; the gap is still measured square to the plates.
- **Stack inward**: on, new plates go behind the faces, clear of their thickness; off, they go out, the side the faces face.
- **Stack selected faces**: stacks copies of the selected faces (and their mirrors, with mirror on) in new add-ons on the compartment (see **Add-ons**), so each can have a material of its own (see armourMaterial). The selected faces are left as they are. One undo removes them all.

Copies of adjacent faces share corners, so a selected surface stacks as connected surfaces. Settings reset each session.

## stabilizer

Naive gun stabilization. Tick **Stabilizer** in a traverse motor's or laying drive's edit panel. Each step, a stabilized drive turns back by however far the hull turned around its axes, so the gun holds its direction in the world; the drive then aims as usual, at its normal speed and torque. Elevation stays within the drive's limits.

- A traverse motor stabilizes its turret's traverse; a laying drive stabilizes its elevation and azimuth.
- Once ticked, **Stabilizer speed limit** (0–100 km/h) switches it off while the vehicle is faster than that; 0 keeps it on at any speed.
- Both are saved in the drive's name, as **[stab]** or **[stab 30]** for 30 km/h. The game saves drive names in the vehicle file but doesn't show them, so the setting travels with shared vehicles.
- **Toggle stabilizers** (default Alt+S) switches your vehicle's stabilizers off and back on; other vehicles' stay on. Rebind it on the MadiLib tab.
- A box in the bottom-left corner shows your vehicle's stabilized drives and whether each is on, or off for being over its speed limit, such as `Stabilizer: traverse on, elevation off (over 40 km/h)`, or `Stabilizer: off` when toggled off. Toggle it with **Show indicator** on the MadiLib tab, or in `BepInEx/config/madi.stabilizer.cfg` under `[Display]`.

## armourMaterial

Replaces the plate structure edit panel's **Use RHA** checkbox with a **Material** dropdown of every armour material the game has. It sets the material of the whole structure; to give one plate its own material, split it into its own compartment (the Quality of Life mod can do this) or add-on (composite stacks plates into add-ons). Materials are read at startup from `Sprocket_Data/StreamingAssets/Technology`: any tech file whose `properties` has an `rhaFactor` (engines, cannons, transmissions and the like don't). The dropdown shows its file name and saves its `type` as the structure's `armourTechID`, so the choice travels with the vehicle file. Only materials invented by the vehicle's era are listed. Undo reverts a change.

A **Sort by** dropdown above it orders the list by **Protection**, **Protection per kg**, **Hardness**, **Spall factor**, **Density** or **Cost** (highest first), **Invented** (oldest first) or **Name** (A to Z). Materials whose file leaves the value out go last. The choice is kept across sessions, in `BepInEx/config/madi.armourmaterial.cfg` under `[Dropdown]`.

Under the dropdowns, rows show the selected material's **Protection** (against RHA, per mm and per kg), **Hardness**, **Spall factor**, **Density**, **Cost** and **Invented** date.

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
4. End the release notes with the game versions it supports, which the mod manager registry reads. Releases without it inherit the last release's.

   ```html
   <!-- sp-compat {"hamish.sprocket": "0.2.55.x"} -->
   ```

Every plugin shares the release tag as its version.

## Mod manager registry

`registry/packages/madisavage/` holds this repo's entries for the [SprocketModManager registry](https://github.com/furryaxw/SprocketModManager/blob/registry/CONTRIBUTING.en.md). This mirror the registry's paths: copy them into `packages/madisavage/` on its `registry` branch and open a pull request. The registry reads versions and DLLs from this repo's releases, so only update the entries when their metadata changes. Dependencies on MadiLib accept any version (`"version": "*"`); the plugins themselves refuse to load against a MadiLib older than they are, so the entries don't change per release. If a MadiLib release breaks older plugins, add a dependency rule with a `when` for the plugin versions that need the new MadiLib.
