# madiUtil

Utilities because file editing tanks on Linux is really annoying, you will not catch me navigating to `/home/madi/.steam/steam/steamapps/compatdata/1674170/pfx/drive_c/users/steamuser/Documents/My Games/Sprocket/Factions/Default/Blueprints/Vehicles`.


## Efficiency 
Selecting a crew seat in the vehicle designer shows a
**Base efficiency** slider (0–100, step 0.25) in the edit pane. The slider reads and writes `CrewSeatBlueprint.BaseEfficiency`, the per-seat value

## Build

```powershell
dotnet build .\src\madiUtil\madiUtil.csproj --configuration Release `
  -p:SprocketGameRoot="<Sprocket Location>"
```