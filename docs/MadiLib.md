# MadiLib API

MadiLib is the shared library for the plugins in this repo. This page covers each public API with an example. The same docs ship as `MadiLib.xml` beside `MadiLib.dll`, so your editor shows them on hover.

## Setup

Reference `MadiLib.dll` (in this repo, `<ProjectReference Include="../MadiLib/MadiLib.csproj" Private="false" />`) and declare the dependency so MadiLib loads first:

```csharp
[BepInPlugin("you.yourplugin", "yourPlugin", "1.0.0")]
[BepInDependency("madi.madilib", ">=26.10.12")] // the MadiLib version you built against
public sealed class Plugin : BasePlugin { ... }
```

Plugins in this repo use `">=" + ModInfo.Version`.

## Keybinds

Rebindable keys, stored as text in the `[Keybinds]` section of your plugin's config and shown on the **MadiLib** Settings tab.

```csharp
Keybinds.Register(this, "Save vehicle", "Ctrl+S", SaveVehicle);
```

| Parameter | |
| --- | --- |
| `plugin` | Your plugin; its config file stores the key. |
| `name` | Label on the Settings tab and config key. Keep it stable, or players lose their binding. |
| `defaultKey` | A key such as `S` or `F9`, optionally with `Ctrl+`, `Shift+` or `Alt+` in front. |
| `onPressed` | Called on the frame the key is pressed. Not called while the player is rebinding a key. |

## SettingsTab

Sections on the **MadiLib** Settings tab. The tab only appears once something uses it. Apply does nothing, so values must save as soon as they change; a BepInEx `ConfigEntry` does by default.

```csharp
var distance = Config.Bind("Staging", "Ready rack distance", 1.5f, new ConfigDescription(
    "Racks this many metres or closer to the breech are ready racks.",
    new AcceptableValueRange<float>(0f, 100f), new SoftMax(10f), new Step(0.1f)));
var indicator = Config.Bind("Display", "Show indicator", true, "Show ready/stowed counts.");
SettingsTab.AddSection("Staged ammo", distance, indicator);
```

- `AddSection(header, entries...)` draws a header, then a slider per `float` entry and a toggle per `bool` entry, labelled with the config key and described by the config description. An `AcceptableValueRange<float>` sets the hard range, a `SoftMax` tag ends the slider early (typed values may still go up to the hard max, like the game's sliders) and a `Step` tag sets its step. Other entry types are skipped.
- `AddSection(ui => ...)` draws anything else with the game's `IGUIElementDrawer`. It's called every time the tab is drawn, so cache any properties you draw (see `ObjectCache`), or their callbacks get collected.

## Hud

Text boxes in the bottom-left corner of the screen, sized to their text and stacked upward in the order added.

```csharp
Hud.AddPanel(() => ShowIndicator.Value ? $"Ready {ready} / Stowed {stowed}" : null);
```

The function is called every GUI frame. Return null or empty to hide the panel. If it throws, the panel is removed and the error logged.

## PlayerVehicle

- `PlayerVehicle.Root` is the root `Transform` of the vehicle the player controls, or null outside vehicle control. It's looked up at most once a second, so it can lag a vehicle switch by that long.
- `PlayerVehicle.Contains(part)` returns whether a component belongs to that vehicle.

```csharp
if (PlayerVehicle.Contains(__instance))
    showOnHud = true;
```

## EditorGui

Controls for the game's editor panels: a designer editor's `OnGUI(IGUILayout)` and an overlay config's `Draw(IGUILayout)`. The game builds these panels once per selection, not every frame, which is why `Section` needs a redraw and `Readout` needs `Refresh`. Get the drawer with `layout.TryCast<IGUIElementDrawer>()`.

### Section

A collapsible header like the game's own panel sections. Draw the section's contents only when it returns true. Sections start collapsed and stay as the player left them for the session.

```csharp
if (!EditorGui.Section(drawer, "subcaliber.cannon", "Penetrator", __instance.RequestRedraw))
    return;
drawer.Float(diameter);
```

| Parameter | |
| --- | --- |
| `id` | Remembers the section's state across redraws. Make it unique, such as `"yourplugin.panel"`. |
| `header` | The header text. |
| `redraw` | Asks the panel to build again when the header is clicked. In a designer editor pass its `RequestRedraw`; in an overlay config, `() => config.GUIRedrawRequired = true`. Without it, the section only opens after the player reselects. |

### Dropdown

A dropdown of strings. It handles the Il2Cpp list and Unity callback the game's own `Dropdown` wants.

```csharp
List<string> names = Materials.Keys.ToList();
EditorGui.Dropdown(drawer, "Material", names, names.IndexOf(current), i => current = names[i], "Penetrator material.");
```

`selected` is the index shown as chosen, or -1 for none. `onSelect` gets the index the player picks. The label should be short, or it wraps onto the next row.

### Readout and Refresh

A read-only row whose text updates without rebuilding the panel. Make the rows once (cache them), draw them with `drawer.Readonly`, and call `Refresh` whenever their values may have changed, usually from the setters of the controls that change them.

```csharp
StringProperty[] readouts =
{
    EditorGui.Readout("Muzzle velocity", () => $"{Velocity():0} m/s"),
    EditorGui.Readout("Penetration", () => $"{Penetration():0} mm"),
};
var diameter = new FloatProperty("Diameter", (Func<float>)(() => d), (Action<float>)(value =>
{
    d = value;
    EditorGui.Refresh(readouts);
}));
```

## ObjectCache\<T\>

One value per game object, such as the properties a panel draws for each blueprint. Panels rebuild often, so cache what you draw: a cached property keeps its callbacks alive and its state between redraws.

```csharp
private static readonly ObjectCache<FloatProperty> properties = new();

drawer.Float(properties.Get(blueprint, () => new FloatProperty(
    "Base efficiency",
    (Func<float>)(() => blueprint.BaseEfficiency),
    (Action<float>)(value => blueprint.BaseEfficiency = value))));
```

`Get(owner, create)` returns the value for `owner`, calling `create` on first use. Entries are keyed by native pointer, so two wrappers of the same game object share an entry. Entries are never removed.

## KeyedConfig\<T\>

Values stored per key in one config section of your plugin's config, such as a setting per shell type keyed by its GUID. A key reads as the fallback until it's set, and only set keys are written to the file.

```csharp
// In Load:
Diameters = new KeyedConfig<float>(Config, "Projectile diameter", 0f, "Projectile diameter in mm; 0 fires full-bore.");

// Anywhere after:
float d = Diameters[guid];
Diameters[guid] = 40f; // saves
```

| Parameter | |
| --- | --- |
| `file` | Your plugin's `Config`. |
| `section` | The config section, one entry per key. |
| `fallback` | The value of a key never set. |
| `description` | Written to the file above each entry. |

Create it in `Load`, before anything reads it. BepInEx hides values saved last session until their key is bound; the constructor binds every key already in the section so reads find them. It reaches into a private BepInEx property to do that, so a BepInEx update could break it.

Keys can't contain `=`, `\n`, `\t`, `\`, `"`, `'`, `[` or `]` (a BepInEx rule).
