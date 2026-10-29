# sprocket-madi

BepInEx 6 (IL2CPP) plugins for Sprocket, plus the library they share.

| Folder | Plugin | What it does |
| --- | --- | --- |
| `MadiLib/` | `MadiLib.dll` | Shared library: keybinds, the **MadiLib** Settings tab, HUD panels, editor panel controls, config helpers. Required by the others. |
| `madiUtil/` | `madiUtil.dll` | Designer sliders, a save keybind and a scenario era unlock toggle. |
| `stagedAmmo/` | `stagedAmmo.dll` | Ready racks, stowage, re-racking and autoloaders. |
| `rounds/` | `rounds.dll` | Named rounds per cannon, one per ammo rack, picked in game with a keybind. Other plugins add round types. |
| `subcaliber/` | `subcaliber.dll` | Discarding sabot rounds: a projectile narrower than the gun, with long-rod penetration. Needs `rounds.dll`. |
| `highExplosive/` | `highExplosive.dll` | HE and HESH rounds that penetrate by mass alone and spall catastrophically, HEAT rounds whose jet penetrates the same at any range, and APHE rounds that burst into spall behind the armour. Needs `rounds.dll`. |
| `composite/` | `composite.dll` | Pseudo-composite armour: stack thin plates behind selected faces, in add-ons that can each have their own material. |
| `stabilizer/` | `stabilizer.dll` | Naive gun stabilization for traverse motors and laying drives. |
| `armourMaterial/` | `armourMaterial.dll` | Pick any armour material for a plate structure. |

## Install

Download the DLLs from the latest release and put them in `BepInEx/plugins`. Always include `MadiLib.dll`; add whichever plugins you want.

## MadiLib

Adds a **MadiLib** tab to the game's Settings screen, shown once any plugin uses it. Changes there save at once.

**Scroll speed** (slider 1–30, type up to 300; default 7) sets how far a mouse wheel step scrolls an open dropdown list, for every dropdown in the game. 7 matches the edit panel; the game's dropdowns use 0.05, a fraction of a pixel per step. It's on the MadiLib tab, and in `BepInEx/config/madi.madilib.cfg` under `[Dropdowns]`.

**Profile** (default off; `[Debug]` in the same file) logs, every 5 seconds, the frame rate and how long each plugin's per-frame hooks took (milliseconds per second and per frame, calls per second), slowest first, to `BepInEx/LogOutput.log`. Turn it on and send the log with a performance report.

For plugin authors: `MadiLib.dll` has keybinds, Settings tab sections, HUD panels, the player's vehicle, editor panel controls (collapsible sections, dropdowns, live readouts), a per-object cache and `Profile.Measure` to time your own hooks in the Profile log. See the [API reference](docs/MadiLib.md); the same docs ship as `MadiLib.xml` for your editor.

## madiUtil

Utilities because file editing tanks on Linux is really annoying, you will not catch me navigating to `/home/madi/.steam/steam/steamapps/compatdata/1674170/pfx/drive_c/users/steamuser/Documents/My Games/Sprocket/Factions/Default/Blueprints/Vehicles`.

### Efficiency
Selecting a crew seat in the vehicle designer shows a **Base efficiency** slider (0–100, type up to 1000; step 0.25) in the edit pane. The slider reads and writes `CrewSeatBlueprint.BaseEfficiency`.

### Laying drive torque
Selecting a laying drive shows **Elevation torque multiplier** and **Azimuth torque multiplier** sliders (0–100, type up to 1000; step 0.25). They read and write `LayingDriveBlueprint.Elevation.TorqueMultiplier` and `LayingDriveBlueprint.Azimuth.TorqueMultiplier`.

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

`tools/shellicons.py` draws the ammo bar's round icons (AP, APHE, HE, HESH, HEAT, and sabot as a long rod or, under 8 calibers, APDS) into each plugin's `icons/` folder. Run it with `uv run tools/shellicons.py` (uv installs Pillow), or pass a file name to also save them side by side as one PNG.

## stagedAmmo

Staged ammunition. The loader only takes shells from **ready racks**, racks close to the gun's breech. Racks farther away are **stowage**; over time, crew move shells from stowage into the ready racks.

- A rack is a ready rack for a gun when the loader path from breech to rack is at most **Ready rack distance** (default 1.5 m).
- Tick **Ready rack** in an ammo rack's edit panel to flag it by hand (adds **[ready]** to its name). Once any of a gun's racks is flagged, only flagged racks are its ready racks, however far; distance no longer counts. Autoloader racks still take precedence.
- The loader ignores stowage racks. Once ready racks run dry, the gun waits for restock.
- With rounds installed, the loader only loads the gun's next round: from the ready racks holding it, or with none there, from stowage if **Load next round from stowage** (default on) is on. The walk to stowage takes as long as the game's loader rules say. Off, the gun waits for re-racking to bring one forward.
- While the gun has a living loader who isn't loading it, they re-rack (with rounds installed, racks holding the gun's next round first): one shell at a time into the closest ready rack with room, taken from the next matching rack farther out (ready or stowage). Ammo moves inward rack by rack. Each carry takes as long as the game's loader rules give for that shell: pick up, walk the crew path between the two racks, put down; divided by the loader's crew efficiency and scaled by **Re-rack time multiplier** (default 1). Loading pauses re-racking; it resumes where it left off.
- **Clip size** (default 1) in a rack's edit panel makes the loader re-rack that many shells into it per carry, for the time of one carry, drawing from the next racks out if the first runs short. Saved in the name as `[clip 5]` for 5; only matters on ready racks.
- **Toggle re-racking** (default Alt+R) stops your vehicle's loaders re-racking and starts them again, for when they have another role to perform; other vehicles' keep re-racking. Rebind it on the MadiLib tab.
- Each gun only counts and re-racks shells of its own caliber.
- A box in the bottom-left corner shows, per gun on your vehicle, ready and stowed shells and re-rack progress. Toggle with **Show indicator**.

### Autoloaders

Tick **Autoloader** in an ammo rack's edit panel to make it an autoloader rack. The checkbox adds **[auto]** to the rack's name, so it travels with shared vehicle files; typing the tag yourself works too.

- A gun with any autoloader racks loads itself from those, with or without a loader. They are its ready racks; all its other racks are stowage, however close.
- The loader doesn't load an autoloaded gun. They only re-rack, from stowage into the autoloader racks, never from one autoloader rack to another, and don't pause for loading.
- **Reload speed** (slider 0.1–10, type up to 100; shown once **Autoloader** is ticked) sets that rack's speed: 1 is a loader at 100% efficiency. It's saved in the name as `[auto x2]` for 2. **Speed multiplier** (default 1) scales every autoloader on top.
- The indicator marks autoloaded guns with **(autoloader)**.

Settings are on the MadiLib tab, and in `BepInEx/config/madi.stagedammo.cfg` under `[Staging]`, `[Autoloader]` and `[Display]`.

## rounds

Named rounds for each cannon, saved with the vehicle and with the cannon when you save it to the cannon library. **AP**, the game's own shell, is built in; other plugins add more types, such as subcaliber's **Sabot**.

Selecting a cannon in the vehicle designer adds a collapsible **Rounds** section. Each round has a name field, a **Type** dropdown and the type's own fields, then a **Delete** button; **Add round** adds one. Changing a round's type clears its old type's fields.

Selecting an ammo rack adds a **Round** dropdown: the one round every shell in that rack is. A rack not given one holds the gun's first round. Place a rack for each round you want to carry.

In game:

- **Round 1** to **Round 9** (default the number keys) pick the next round to load for the active gun, by its place in the cannon panel's list; **Select gun** (default B) makes your next gun with rounds the active one. With more than one gun, the ammo bar frames the active gun's row in white and numbers its rounds. The round already loaded stays loaded; the next load takes the new one. Rebind them on the MadiLib tab.
- The gun only loads its next round; without one in reach it doesn't load and uses no ammo, and waits until one is or you pick a round that is. With stagedAmmo, the loader only goes to ready racks holding the next round, and re-racking brings one forward; with none in the ready racks, they fetch it from stowage (as long as the walk takes), unless stagedAmmo's **Load next round from stowage** is off. Without it, the loader goes to whichever rack the game picks, and waits there if it has none.
- A gun fires the round it has loaded. Before a load has been seen (a gun loaded at spawn), that's its first round; picking a next round doesn't change it.
- With stagedAmmo, re-racking moves shells only between racks of the same round, emptying racks of the next round into the ready racks first.
- An ammo bar in the bottom-left corner shows a row per gun with rounds, and a cell per round: its type's icon, its name and its shells left. With stagedAmmo, it shows them as `ready/all`, where ready is the shells in ready racks. A yellow frame marks the next round, red while the loader found none in the rack they went to. A green bar marks the loaded round. Rounds with none left are dimmed. Toggle it with **Show indicator** on the MadiLib tab, or in `BepInEx/config/madi.rounds.cfg` under `[Display]`.

Rounds are stored as JSON in the cannon blueprint's `description`, so they go with the cannon into the cannon library and onto other vehicles, keyed by a short id so renaming a round keeps the racks holding it: `{"rounds": {"3f9a01c2": {"name": "M829", "type": "sabot", "diameter": 25, ...}}}`. Earlier versions kept them in the shell slot's `description`; they move to the cannon the first time the vehicle loads, and stay there once it's saved. A rack's round is in its own `description`: `{"round": "3f9a01c2"}`. The game has no UI for either field, but saves and loads them untouched.

Each round other than AP fires its own projectile type, which the game registers the first time the round is fired and again whenever its settings change. Shells in flight keep their own type, so two guns firing different rounds of the same shell don't change each other's shells.

With Dynamic Gun Sight (`SprocketDynamicSight.dll`) installed, its range marks follow the loaded round: its cache is pointed at the loaded round's projectile type, and cleared whenever a gun on your vehicle loads a different round, so it reads that round's muzzle velocity, mass and drag. After changing a round's settings in the designer, change scene before trusting the marks.

For plugin authors: `Rounds.Loadout.Register(type, label, draw, projectile, hit, icon)` adds a round type (`"ap"` is taken). `icon` is a PNG stream for the ammo bar, an upright shell on a transparent background; without one the bar shows `label`. Embed it in your own plugin (rounds only carries AP's). For icons that differ by round, add more with `Loadout.AddIcon(key, png)` and pass `Register` an `iconOf(cannonBlueprint, id)` that returns a round's key, or null for the type's own. `Loadout.IsReady` is set by a staging plugin to tell the bar which racks are ready racks. `projectile` returns the `RoundShape` its projectile flies with (mass, diameter, length, launch length, drag) and can add to or change the game's function definitions (AP, Propellant, HE); `hit` runs on each of its projectiles' hits on armour, and can call `Impacts.SetPenetration` and set `Impacts.SpallBursts` and `Impacts.FragmentMass` for that hit. `Impacts.Burst(simulation, point, direction, mass, fragments, speed, spread, parent, from)` spawns a spall burst mid-simulation, copied from the hit's last plate spall, its fragments starting where fragment `from` is; `Impacts.HitEnded` is raised once each hit is done. Call `Loadout.Invalidate()` when a setting your `projectile` reads changes. `Impacts.Normalize(ref penetrator, degrees)` lets a hit turn toward the plate's normal by that much (90 is square on, for HESH). `Impacts.Simulated` is raised for the armour view's simulated shots. `Impacts.ArmourPanel` is raised as the armour view's panel draws, with its drawer and a redraw action, to add sections; use it rather than patching `ArmourOverlayConfig.Draw`, as two plugins patching it can lose one. Likewise `Impacts.FragmentSimulating` and `Impacts.FragmentSimulated` are raised around each fragment the game simulates in a hit, and `Impacts.HitSimulated` with each hit's penetrator and simulation, in place of patching `CompoundStructure.SimulateFragment` or `Penetrate`. Hits are told apart by penetrator mass and diameter, so each round's mass is nudged by a few ulps to be unique. `draw` draws a round's fields in the cannon panel and calls the `changed` it's passed after each edit. `Loadout.Get<T>(cannonBlueprint, id, type)` and `Loadout.Edit<T>(cannonBlueprint, id, change)` read and change a round as your own class `T` (its public properties are the fields, camelCase in the file; fields it doesn't have are kept). `Loadout.Fired<T>(cannonBehaviour, type)` is the round a gun has loaded, or null if it isn't of your type. `Loadout.LoadedChanged` is raised with the cannon when a gun's loaded round changes. `Loadout.RoundOf(rack)` is the round a rack holds. `Loadout.Rerack(move)` runs a take-and-put between racks as a re-rack, which may take from a rack not holding the next round; move shells only between racks of the same round. `Loadout.HoldsNext(rack)` is whether a rack holds its gun's next round. Declare `[BepInDependency("madi.rounds")]`.

## subcaliber

Needs `rounds.dll`. Adds the **Sabot** round type. A sabot round in a cannon's **Rounds** section has:

- **Projectile diameter** (1 mm to the caliber). Below the caliber, the shell is a sabot round.
- **Penetrator length** in calibers of its own diameter (1 to 30; the vanilla shells are 3).
- **Sabot coverage**, the fraction of the penetrator's length the sabot covers (0.05 to 1, default 1; about 0.25 to 0.35 for a long rod's saddle sabot), and **Sabot density** relative to steel (0 to 1, default 0.35, about aluminium); they only matter below the caliber.
- **Material**, a dropdown of the materials file.
- Readouts that update as you change them: **Muzzle velocity**, **Penetration** (at the muzzle) and **Penetrator / sabot mass**.

How it works:

- The cartridge stays full-bore: same charge, case and rack size.
- The sabot fills the bore around the penetrator, along **Sabot coverage** of its length, at **Sabot density** times steel's. Muzzle velocity comes from the game's interior ballistics with the full-bore charge pushing penetrator plus sabot. The sabot is discarded at the muzzle.
- The charge has to push its own gas too: about a third of its mass moves at projectile speed, so light projectiles leave slower than the game's formula says. Charge mass is the case's length at bore width times **Propellant density** (default 800 kg/m³; 0 turns it off). Scaled so a full-bore 3-caliber projectile is unchanged; a light sabot round can lose 30–40% (a 25 mm, 3.5-caliber DU rod in a 105 mm gun with a 600 mm case keeps about 0.68 of the game's velocity).
- Every muzzle velocity is softly capped at **Max muzzle velocity** (default 2200 m/s): `v / (1 + (v/max)⁸)^⅛`. Rounds under about three quarters of it lose well under 1%; none pass it.
- In flight the penetrator drags at its own diameter and hits with its own diameter and mass. Penetration blends the game's de Marre formula into the Lanz-Odermatt long-rod equation by length (below).

The armour view's penetration simulator also gets a **Penetrator** section with **Penetrator length** and **Material**. Set **Calibre** to the penetrator's diameter and **Penetration** as usual; These make very small changes to the penetration characteristics.

Materials are in `BepInEx/config/madi.subcaliber.materials.json`, written on first run as material name to density in kg/m³ (steel 7850, tungsten carbide 15600, tungsten heavy alloy 17600, depleted uranium 19100). Edit it and restart to add or change materials.

A gun with no rounds, or firing a round of another type such as **AP**, fires the game's own shell with the game's penetration, without the long-rod blend. Settings from older versions, which were keyed by projectile GUID in `madi.subcaliber.cfg`, aren't read; set them again as rounds.

The game's own designer readouts and the gunner's sight range marks still use the game's full-bore steel round.

### Long rods

A subcaliber penetrator's penetration is a straight blend of the game's de Marre and the Lanz-Odermatt perforation equation by length: all de Marre at 0 calibers, half each at 4, all Lanz-Odermatt at 8 calibers or longer. De Marre gives small fast penetrators far too much (about 1.5x real APDS, 2–3x real APFSDS) and Lanz-Odermatt gives stubby ones too little (about 0.75x real APDS), so APDS-length rounds land between them (the L7's L52 APDS comes out about 360 mm against a real 300 or so); `tools/penetration.py` plots all three against real rounds. With Lanz-Odermatt, length, material and target hardness count, not only mass. A penetrator 8 calibers or longer is a long rod and spalls more.

- On impact, the game's penetrator constant is swapped for the one that makes its de Marre give the blended penetration at the impact speed. Obliquity, multiple plates, spall and damage are still the game's; through stacked plates, penetration falls off with speed as de Marre does.
- Below Lanz-Odermatt's minimum speed for the material, the game's penetration applies.
- A sabot round's **Penetration** readout shows the blended value.
- At a fixed speed the blend dips slightly from about 4 to 8 calibers, where de Marre's share falls faster than Lanz-Odermatt rises: at 2000 m/s a 25 mm DU rod loses up to about 60 mm there. Shorter rods also leave faster, which offsets some of it.
- **Spall multiplier** (slider 0–3, type up to 30; default 1.25) scales each spall burst a long rod makes. The game's burst is a fixed number of fragments; the whole part of the multiplier repeats it (2 is two bursts), and the rest adds one burst of that fraction of the mass (1.25 is a normal burst plus a quarter-mass one). 0 is no spall. The armour view's simulator uses it too when its **Penetrator length** is 8 calibers or more.
- **Target hardness** is the Brinell hardness long rods are rated against (default 260, rolled armour).
- Coefficients per material are in `BepInEx/config/madi.subcaliber.longrod.json`, keyed by material name: `A`, `C0`, `C1` (tungsten and DU form) or `A`, `C0`, `K`, `N`, `Hardness` (steel form, used when `K` isn't 0). Materials missing from it use tungsten's.

The armour view's simulator fires at whatever **Penetration** you enter, so enter the long-rod value from the cannon panel (with **Calibre**, **Penetrator length** and **Material** matching the rod) and it behaves as combat does.

**Max muzzle velocity**, **Propellant density**, **Target hardness** and **Spall multiplier** are on the MadiLib tab, and in `BepInEx/config/madi.subcaliber.cfg` under `[Muzzle velocity]` and `[Long rod]`.

## highExplosive

Needs `rounds.dll`. Adds the **HE** round type. An HE round in a cannon's **Rounds** section has **Shell mass** (0.25 to 4, default 1), over the game's full-bore shell for the gun, with readouts for **Muzzle velocity**, **Penetration** and **Shell weight**.

- The shell is full-bore, its length scaled by its mass, so a heavier shell leaves slower (by the game's interior ballistics, and subcaliber's charge mass and velocity cap if installed).
- It carries the game's own explosive (the one APHE shells have) with no fuse delay, so it explodes on contact. **Explosive power** (default 0) is its blast, times its shell mass; the game's APHE is 1000. At 0 the shell still explodes but the blast does no damage. The game's blast acts as if the shell were already inside, so above 0 it goes through armour the shell doesn't penetrate.
- It blows through plates up to **Penetration** (default 20 mm) times the cube root of the shell's weight in kg, whatever the speed: a 10 kg shell is about 43 mm at any range. The plate's spall gets in, but the shell itself goes no further than the plate's far face; thicker plates stop it outside.
- **Normalization** (0 to 90 degrees, default 30) turns a hit toward the plate's normal on impact, as the blast spreads over the face, so sloped plates and roofs count for less. 90 meets every plate square on. It never lowers the game's own normalization.
- Each hit's spall is **Spall multiplier** (default 16) times its shell mass, split evenly between more bursts and heavier fragments, but no more than **Max spall bursts** (1 to 8, default 1) bursts per one the game makes; the rest goes into heavier fragments. By default a 1x shell's hit has 16x heavier fragments and no extra bursts. The explosion already makes a few hundred bursts per hit, so each extra one costs frame rate.

### HESH

A **HESH** round is HE with a squash head: the same **Shell mass**, spall and explosive, but its own **Penetration** (default 25 mm for a 1 kg shell, a little more than HE's 20) and **Normalization** (default 90, so every plate is met square on and slope doesn't help). Like HE, it goes no further than the far face of the first plate it gets through. The settings are under `[HESH]`.

### HEAT

A **HEAT** round is a full-bore shell like HE, with the same **Shell mass**, explosive and detonation on contact, so it ends its flight at the first hit. Its hit is its jet instead of the shell:

- **Penetration** (1 to 10 calibers, default 4.5) is the jet's, in calibers of the shell, the same at any range. Early HEAT is about 2 to 4, later rounds 5 to 7.
- The jet is **Jet diameter** (default 0.2) of the caliber wide and **Jet mass** (default 0.2) of the shell's mass. Obliquity and spall are the game's for a projectile that size, so it spalls in a tighter cone than the shell would.
- Its penetration is a budget: each plate takes its line-of-sight thickness (times its material's RHA factor) off it, and each gap after a plate takes **Air gap loss** (default 50 mm per metre). The game's own model is for a solid shot, which keeps far more after each plate. What's left is carried in the jet's mass at the speed it hit with, so a weakened jet does less damage and spalls less.
- Once the budget runs out the jet stops: in a plate, with no spall out of its back, even if the game lets it through; in a gap, where the gap used it up: there it breaks up into **Breakup fragments** (default 8, 0 to just stop) going on at its speed, together **Breakup mass** (default 0.25) of its mass, in a cone **Breakup cone** (default 1) times the plate spall's. They hurt what's in the way but rarely go through armour.
- The first plate the jet perforates spalls **First spall burst** (default 8) times the game's, as heavier fragments and more bursts up to **Max spall bursts**, like HE. Plates after it spall as the game's.
- The game may still ricochet the jet at steep angles, or a spent jet off the next surface; it decides that itself, but a ricochet after the jet is stopped does nothing.
- **Log hits** (default off) logs each jet's setup, what each plate and gap took from its budget, and its path through the armour, to `BepInEx/LogOutput.log`.

### APHE

An **APHE** round is the game's full-bore AP shell for the gun, carrying **Filler** (5 g up to 10% of the shell's weight, default 100 g) of explosive; its share of the shell (at most 10%, the slider's top) is what counts below, shown as **Filler share**. It penetrates as the game's shell less **Penetration loss** (default 3) percent per percent of filler, as the cavity weakens it (1.5% filler loses 4.5%), and the game's own explosive is taken off it. Once it has gone **Fuze delay** (0.05 to 3 m, default 0.5) past where it first entered a plate, it bursts:

- The shell stops there; whatever its path past that point would have hit or spalled doesn't happen.
- Its casing (the shell's mass less the filler, times **Fragment mass**, default 1) goes forward and back as spall: **Fragmentation** (default 4) fragments each way per percent of filler, up to **Max fragments** (default 32), the mass split between them. More filler means more, lighter fragments.
- Fragments leave at the filler's Gurney speed, `Explosive speed / √(casing/filler + 1/2)` (**Explosive speed** default 2440 m/s, TNT's), so more filler throws them faster: 1.5% is about 300 m/s, 10% about 730 m/s.
- The bursts are copies of the hit's own plate spall, their cone **Burst cone** (default 3) times its width. A shell that stops in the armour, or leaves the vehicle, before its fuze runs out doesn't burst inside it.

The armour view's penetration simulator gets an **Explosive rounds** section with **Simulate as**: **Shell** (the game's), **HE** (its normalization and spall), **HEAT** (the jet, at the panel's **Penetration**) **APHE** (bursting with the section's own **Filler** and **Fuze delay**). Set **Calibre** to the shell's and **Penetration** to the round's readout (for HEAT, its penetration in calibers times the caliber), and the view shows how deep it gets and the spall it makes. The simulated HEAT jet spends its budget as in game.

The settings are on the MadiLib tab, and in `BepInEx/config/madi.highexplosive.cfg` under `[High explosive]`, `[HESH]`, `[HEAT]` and `[APHE]`.

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
- Once ticked, **Stabilizer speed limit** (slider 0–100 km/h, type up to 1000) switches it off while the vehicle is faster than that; 0 keeps it on at any speed.
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

1. Set `<Version>` in `Directory.Build.props`. Every plugin's `[BepInPlugin]` version and DLL file version come from it (through a generated `ModInfo.Version`). Use CalVer, such as `26.10.12`; a suffix needs a hyphen (`26.10.12-a`), or the mod manager registry skips the release.
2. Commit and push to `main`.
3. Run `tools/release.sh` (needs the [GitHub CLI](https://cli.github.com/), logged in with `gh auth login`). It builds every plugin in Release against your local game, tags the version, and publishes a GitHub release with every DLL listed in `registry/packages/` plus `MadiLib.xml` for plugin authors. It refuses to run with uncommitted changes, off `main`, or when the tag already exists. `tools/release.sh --dry-run` only builds and lists the files.

The release notes start with the game versions it supports, which the mod manager registry reads (releases without it inherit the last release's). By default that's the local game's version as `a.b.c.x`, read from `Sprocket_Data/globalgamemanagers`; pass `--game` for another range, such as `tools/release.sh --game ">=0.2.55.5"`:

```html
<!-- sp-compat {"hamish.sprocket": ["0.2.55.x"]} -->
```

Every plugin shares the release tag as its version.

## Mod manager registry

`registry/packages/madisavage/` holds this repo's entries for the [SprocketModManager registry](https://github.com/furryaxw/SprocketModManager/blob/registry/CONTRIBUTING.en.md). This mirror the registry's paths: copy them into `packages/madisavage/` on its `registry` branch and open a pull request. The registry reads versions and DLLs from this repo's releases, so only update the entries when their metadata changes. Dependencies on MadiLib accept any version (`"version": "*"`); the plugins themselves refuse to load against a MadiLib older than they are, so the entries don't change per release. If a MadiLib release breaks older plugins, add a dependency rule with a `when` for the plugin versions that need the new MadiLib.
