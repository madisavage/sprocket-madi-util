# sprocket-madi

BepInEx 6 (IL2CPP) plugins for Sprocket, plus the library they share.

| Folder | Plugin | What it does |
| --- | --- | --- |
| `MadiLib/` | `MadiLib.dll` | Shared library: keybinds, the **MadiLib** Settings tab, HUD panels. Required by the others. |
| `madiUtil/` | `madiUtil.dll` | Designer sliders and a save keybind. |
| `stagedAmmo/` | `stagedAmmo.dll` | Ready racks, stowage and re-racking. |

## Install

Download the DLLs from the latest release and put them in `BepInEx/plugins`. Always include `MadiLib.dll`; add whichever plugins you want.

## MadiLib

Adds a **MadiLib** tab to the game's Settings screen, shown once any plugin uses it. Changes there save at once.

For plugin authors (reference `MadiLib.dll`, declare `[BepInDependency("madi.madilib")]`):
- `Keybinds.Register(plugin, name, defaultKey, onPressed)` adds a rebindable key, such as `"Ctrl+S"`, stored in the plugin's own config under `[Keybinds]`.
- `SettingsTab.AddSection(header, entries...)` draws config entries: a slider for a `float` (range from an `AcceptableValueRange<float>`, step from a `Step` tag in its `ConfigDescription`), a toggle for a `bool`.
- `SettingsTab.AddSection(Action<IGUIElementDrawer>)` draws anything else.
- `Hud.AddPanel(() => text)` adds a text box in the bottom-left corner, sized to its text; return null or empty to hide it. Panels stack upward in the order added.
- `PlayerVehicle.Root` is the vehicle the player controls (null outside vehicle control); `PlayerVehicle.Contains(part)` checks whether a part is on it.

## madiUtil

Utilities because file editing tanks on Linux is really annoying, you will not catch me navigating to `/home/madi/.steam/steam/steamapps/compatdata/1674170/pfx/drive_c/users/steamuser/Documents/My Games/Sprocket/Factions/Default/Blueprints/Vehicles`.

### Efficiency
Selecting a crew seat in the vehicle designer shows a **Base efficiency** slider (0–100, step 0.25) in the edit pane. The slider reads and writes `CrewSeatBlueprint.BaseEfficiency`.

### Laying drive torque
Selecting a laying drive shows **Elevation torque multiplier** and **Azimuth torque multiplier** sliders (0–100, step 0.25). They read and write `LayingDriveBlueprint.Elevation.TorqueMultiplier` and `LayingDriveBlueprint.Azimuth.TorqueMultiplier`.

### Save keybind
**Save vehicle** (default Ctrl+S) saves the vehicle open in the designer, the same as its save button. Rebind it on the MadiLib tab.

## stagedAmmo

Staged ammunition. The loader only takes shells from **ready racks**, racks close to the gun's breech. Racks farther away are **stowage**; over time, crew move shells from stowage into the ready racks.

- A rack is a ready rack for a gun when the loader path from breech to rack is at most **Ready rack distance** (default 1.5 m).
- The loader ignores stowage racks. Once ready racks run dry, the gun waits for restock.
- While the gun has a living loader who isn't loading it, they re-rack: one shell at a time into the closest ready rack with room, taken from the next matching rack farther out (ready or stowage). Ammo moves inward rack by rack. Each carry takes as long as the game's loader rules give for that shell: pick up, walk the crew path between the two racks, put down; divided by the loader's crew efficiency and scaled by **Re-rack time multiplier** (default 1). Loading pauses re-racking; it resumes where it left off.
- A box in the bottom-left corner shows, per gun on your vehicle, ready and stowed shells and re-rack progress. Toggle with **Show indicator**.

Settings are on the MadiLib tab, and in `BepInEx/config/madi.stagedammo.cfg` under `[Staging]` and `[Display]`.

## Build

`Directory.Build.props` holds the shared settings. Building copies each plugin's DLL to `BepInEx/plugins`; pass `-p:SkipModDeploy=true` to only write to `bin/`. Build everything:

```powershell
dotnet build .\sprocket-madi.sln --configuration Release `
  -p:SprocketGameRoot="<Sprocket Location>"
```

`SprocketGameRoot` defaults to the `SPROCKET_GAME_ROOT` environment variable, then to the folder above this repo. For a release, upload every plugin's `bin/Release/net6.0/<name>.dll`.
