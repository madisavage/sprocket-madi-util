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
Keybinds.Register(this, "madiUtil", "Save vehicle", "Ctrl+S", SaveVehicle);
```

| Parameter | |
| --- | --- |
| `plugin` | Your plugin; its config file stores the key. |
| `subtab` | The Settings subtab whose Keybinds section shows it, as passed to `SettingsTab.AddSection`. Left out (`Register(plugin, name, defaultKey, onPressed)`), it's your plugin's name. |
| `name` | Label on the Settings tab and config key. Keep it stable, or players lose their binding. |
| `defaultKey` | A key such as `S` or `F9`, optionally with `Ctrl+`, `Shift+` or `Alt+` in front. |
| `onPressed` | Called on the frame the key is pressed. Not called while the player is rebinding a key or typing in a text field. |

For a key that acts while held, `RegisterHeld` takes the same parameters without `onPressed` and returns a check to call each frame. It's false while the player is rebinding a key or typing:

```csharp
Func<bool> raise = Keybinds.RegisterHeld(this, "Hydropneumatic", "Raise", "PageUp");
// in a per-frame update: if (raise()) height += rate * Time.deltaTime;
```

## SettingsTab

Sections on the **MadiLib** Settings tab, grouped in subtabs. The tab only appears once something uses it. Apply does nothing, so values must save as soon as they change; a BepInEx `ConfigEntry` does by default.

```csharp
var distance = Config.Bind("Staging", "Ready rack distance", 1.5f, new ConfigDescription(
    "Racks this many metres or closer to the breech are ready racks.",
    new AcceptableValueRange<float>(0f, 100f), new SoftMax(10f), new Step(0.1f)));
var indicator = Config.Bind("Display", "Show indicator", true, "Show ready/stowed counts.");
SettingsTab.AddSection("Staged ammo", "Staging", distance, indicator);
```

The first argument is the subtab. A subtab is created the first time it's named, and subtabs show in that order as tabs at the top. An add-on plugin passes its parent's subtab to join it (highExplosive and subcaliber pass `"Rounds"`). Each subtab's keybinds come last, under a Keybinds header.

Sections with a header collapse: they start collapsed, and opening one closes the others in its subtab, so a subtab fits on screen (the Settings screen doesn't scroll). The subtab shown and the open section stay as left for the session.

- `AddSection(subtab, header, entries...)` draws a collapsible header, then a slider per `float` entry and a toggle per `bool` entry, labelled with the config key and described by the config description. An `AcceptableValueRange<float>` sets the hard range, a `SoftMax` tag ends the slider early (typed values may still go up to the hard max, like the game's sliders) and a `Step` tag sets its step. Other entry types are skipped.
- `AddSection(subtab, header, ui => ...)` draws anything else with the game's `IGUIElementDrawer`, under a collapsible header, or without one when `header` is null. It's called every time the tab is drawn (while expanded), so cache any properties you draw (see `ObjectCache`), or their callbacks get collected.
- The older overloads without a subtab still work: `AddSection(header, ...)` puts the section in a subtab named `header`, and `AddSection(ui => ...)` in the "MadiLib" subtab.

## Hud

Text boxes in the bottom-left corner of the screen, sized to their text and stacked upward in the order added.

```csharp
Hud.AddPanel(() => ShowIndicator.Value ? $"Ready {ready} / Stowed {stowed}" : null);
```

The function is called every GUI frame. Return null or empty to hide the panel. If it throws, the panel is removed and the error logged.

`Hud.AddPanel((left, bottom) => ...)` adds a panel that draws itself with IMGUI (`GUI.Box`, `GUI.Label`, ...), upward from its bottom-left corner in screen pixels. It returns its height, or 0 to hide it. Panels only draw on the Repaint pass.

## HudDraw

Drawing helpers for drawn panels, so every plugin's panel looks the same. `GUI.DrawTexture` is stripped from the game, so these draw through label styles.

- `Fill(rect, color)`, `Outline(rect, color)` (a `HudDraw.Frame`-wide frame), `Bar(rect, fraction, color)`, `CentredBar(rect, value, limit, color)`.
- `Label(rect, text, fontSize, color, anchor)`: bold text.
- `Icon(assembly, "name.png")` loads an embedded PNG once (null if missing); `Icon(key, bytes)` from bytes. `Image(rect, icon, color)` draws it scaled to fit, tinted. Draw icons light grey with a dark outline so the tint shows (see `tools/hudicons.py`).
- Colours: `Ok` (green: running, on), `Next` (yellow: next up, paused, over a limit), `Blocked` (red), `Off` (grey: switched off by the player), `Idle`.

```csharp
Hud.AddPanel((left, bottom) =>
{
    if (HudDraw.Icon(typeof(Plugin).Assembly, "thing.png") is { } icon)
        HudDraw.Image(new Rect(left, bottom - 40f, 40f, 40f), icon, on ? HudDraw.Ok : HudDraw.Off);
    return 40f;
});
```

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

## Descriptions

Settings kept as JSON in a blueprint's description, which the game saves and loads untouched but has no UI for. Plugins share one description, each under its own keys; a description holding other text (a player's notes) is left alone.

```csharp
float mils = Descriptions.Parsed(gun)?["elevation"] is JsonValue v && v.TryGetValue(out float f) ? f : 0f;
if (Descriptions.Set(gun, "elevation", 2.5f))
    slot.MarkModified();
```

- `Parsed(blueprint)` is the description as a `JsonObject`, parsed again only when the text changes, so hooks can read it every frame; null when it isn't JSON. It's shared between plugins, so never change it.
- `Set(blueprint, key, value)` sets one key (removes it for null), keeping the others; false when the description isn't JSON. `Read(blueprint)` gives a fresh copy to edit by hand.
- `Parse(text)` and `With(text, key, value)` do the same on plain text, for tests.

## NameTags

Settings kept as `[tags]` in a part's blueprint name, such as `[stab 30]`: names save with the vehicle but don't show in game. Each plugin keeps its own regex, one group per number, matching the space before the tag.

```csharp
static readonly Regex tag = new(@"\s*\[stab(?:\s+([0-9]*\.?[0-9]+))?\]", RegexOptions.IgnoreCase);
float? limit = NameTags.Numbers(tag, blueprint.Name, new[] { 0f })?[0];
blueprint.Name = NameTags.With(blueprint.Name ?? "", tag, $"[stab {NameTags.Format(30f)}]");
```

- `Numbers(tag, name, defaults, anyTag?)` gives each group's number, or its default when the group didn't match; null when untagged. With `anyTag`, a malformed tag counts as none.
- `With(name, tag, newTag)` removes every match and appends `newTag` (none for null). `Format(value)` writes a number as tags do.

## TechFiles

The game's Technology folder (`TechFiles.Folder`), parsed once and shared: `Read(folder)` gives each file's path and root `JsonObject`, read as the game reads them (trailing commas and comments allowed), unreadable files left out. Never change them. `Text(node)` reads a string value, or null.

## Native

Reading game structs at their native offsets, for structs Il2CppInterop wraps as classes (its arrays of them read garbage) and fields it can't reach. Offsets are looked up by name at runtime.

```csharp
int size = Native.ValueSize<ProjectileInstance>();
int velocity = Native.StructOffset<ProjectileInstance>("velocity");
byte* projectile = (byte*)Native.Element(pool.Pointer, index, size);
```

- `FieldOffset<T>(name)` is a field's offset in an object of class `T`; `StructOffset<T>(name)` in struct `T` unboxed (in an array, inside another object or behind a pointer).
- `ValueSize<T>()` is struct `T`'s size as an array element. `Length(array)` and `Element(array, index, size)` read IL2CPP arrays.
