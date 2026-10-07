# madiUtil

Utilities because file editing tanks on Linux is really annoying, you will not catch me navigating to `/home/madi/.steam/steam/steamapps/compatdata/1674170/pfx/drive_c/users/steamuser/Documents/My Games/Sprocket/Factions/Default/Blueprints/Vehicles`.


## Efficiency 
Selecting a crew seat in the vehicle designer shows a **Base efficiency** slider (0–100, step 0.25) in the edit pane. The slider reads and writes `CrewSeatBlueprint.BaseEfficiency`.

## Laying drive torque
Selecting a laying drive shows **Elevation torque multiplier** and **Azimuth torque multiplier** sliders (0–100, step 0.25). They read and write `LayingDriveBlueprint.Elevation.TorqueMultiplier` and `LayingDriveBlueprint.Azimuth.TorqueMultiplier`.

## Build

BepInEx 6 (IL2CPP) plugin. Build copies `madiUtil.dll` to `BepInEx/plugins`.

```powershell
dotnet build .\src\madiUtil\madiUtil.csproj --configuration Release `
  -p:SprocketGameRoot="<Sprocket Location>"
```