extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Sprocket.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using MonoBehaviour = UnityCoreModule::UnityEngine.MonoBehaviour;
using UnityAction = UnityCoreModule::UnityEngine.Events.UnityAction;

namespace MadiLib
{
    /// <summary>
    /// Configurable keybinds. Each bind is stored as text (for example "Ctrl+S")
    /// in the <c>[Keybinds]</c> section of the registering plugin's config and
    /// can be rebound from the "MadiLib" tab in the game's Settings screen.
    /// </summary>
    public static class Keybinds
    {
        /// <summary>A registered keybind and the config entry holding its key.</summary>
        private sealed record Bind(string Subtab, string Name, ConfigEntry<string> Entry, Action OnPressed);

        private static readonly List<Bind> binds = new();
        private static readonly List<UnityAction> callbacks = new(); // keeps button callbacks alive while the tab shows
        private static readonly Key[] modifiers = { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LeftMeta, Key.RightMeta, Key.ContextMenu };
        private static Bind? capturing;
        // Bind text, parsed; polled every frame, so parsed once per text
        private static readonly Dictionary<string, (Key Key, bool Ctrl, bool Shift, bool Alt)> parsed = new();

        /// <summary>Registers a keybind and shows it in the Settings tab, in the subtab named after the plugin.</summary>
        /// <param name="plugin">The plugin whose config file stores the key.</param>
        /// <param name="name">Shown in the Settings tab and used as the config key; keep it stable.</param>
        /// <param name="defaultKey">Default key, such as "S", "F9" or "Ctrl+S".</param>
        /// <param name="onPressed">Called on the frame the key is pressed.</param>
        public static void Register(BasePlugin plugin, string name, string defaultKey, Action onPressed) =>
            Register(plugin, MetadataHelper.GetMetadata(plugin).Name, name, defaultKey, onPressed);

        /// <summary>Registers a keybind and shows it in the Keybinds section of <paramref name="subtab"/> in the Settings tab.</summary>
        /// <param name="plugin">The plugin whose config file stores the key.</param>
        /// <param name="subtab">The Settings subtab, as passed to <see cref="SettingsTab.AddSection(string, string, ConfigEntryBase[])"/>.</param>
        /// <param name="name">Shown in the Settings tab and used as the config key; keep it stable.</param>
        /// <param name="defaultKey">Default key, such as "S", "F9" or "Ctrl+S".</param>
        /// <param name="onPressed">Called on the frame the key is pressed.</param>
        public static void Register(BasePlugin plugin, string subtab, string name, string defaultKey, Action onPressed)
        {
            binds.Add(new Bind(subtab, name, plugin.Config.Bind("Keybinds", name, defaultKey, "A key such as S or F9, with Ctrl+, Shift+ or Alt+ in front if wanted; empty for none."), onPressed));
        }

        /// <summary>Whether any keybinds are registered.</summary>
        internal static bool Any => binds.Count > 0;

        /// <summary>The subtab of each keybind, in the order registered.</summary>
        internal static IEnumerable<string> Subtabs => binds.Select(b => b.Subtab);

        /// <summary>
        /// Called every frame by <see cref="KeybindPoller"/>. Fires each bind
        /// whose key was pressed this frame. Paused while a key is being rebound
        /// or the player is typing in a text field.
        /// </summary>
        internal static void Poll()
        {
            using var _ = MadiLib.Profile.Measure("MadiLib: keybinds");
            if (capturing != null || Keyboard.current is not { } keys || Typing())
                return;
            foreach (Bind bind in binds)
                if (Pressed(keys, bind.Entry.Value))
                    bind.OnPressed();
        }

        /// <summary>Whether a text field has focus, as the game's UI text fields (TextMeshPro) do while typed in.</summary>
        internal static bool Typing()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            return selected != null && selected.GetComponent<TMP_InputField>() is { } field && field.isFocused;
        }

        /// <summary>
        /// Whether the key in <paramref name="text"/> was pressed this frame.
        /// Ctrl, Shift and Alt must match exactly, so "S" doesn't also fire on Ctrl+S.
        /// </summary>
        private static bool Pressed(Keyboard keys, string text)
        {
            if (!parsed.TryGetValue(text, out var bind))
                parsed[text] = bind = Parse(text);
            (Key key, bool ctrl, bool shift, bool alt) = bind;
            return key != Key.None && keys[key].wasPressedThisFrame
                && ctrl == keys.ctrlKey.isPressed && shift == keys.shiftKey.isPressed && alt == keys.altKey.isPressed;
        }

        /// <summary>
        /// Parses text such as "Ctrl+Shift+S" into a key and modifiers. Names
        /// follow the Input System <see cref="Key"/> enum, case-insensitive;
        /// a bare digit is that digit key, not the enum's number. Unknown names
        /// or empty text give <see cref="Key.None"/>.
        /// </summary>
        private static (Key Key, bool Ctrl, bool Shift, bool Alt) Parse(string text)
        {
            Key key = Key.None;
            bool ctrl = false, shift = false, alt = false;
            foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
                // Enum.TryParse reads "1" as the enum's value 1, Space
                else if (!Enum.TryParse(part.Length == 1 && char.IsDigit(part[0]) ? "Digit" + part : part, true, out key)) return default;
            }
            return (key, ctrl, shift, alt);
        }

        /// <summary>
        /// Called every frame while the Settings tab is shown. If a bind is being
        /// rebound, waits for a key: Esc cancels, Backspace unbinds, and any other
        /// key (with the modifiers held) becomes the new binding.
        /// </summary>
        /// <param name="page">The Settings tab to redraw when rebinding finishes.</param>
        internal static void Capture(MadiLibMenu page)
        {
            if (capturing == null || Keyboard.current is not { } keys)
                return;

            if (keys.backspaceKey.wasPressedThisFrame)
                capturing.Entry.Value = "";
            else if (!keys.escapeKey.wasPressedThisFrame)
            {
                Key? pressed = null;
                foreach (Key key in Enum.GetValues<Key>())
                {
                    if (key == Key.None || Array.IndexOf(modifiers, key) >= 0)
                        continue;
                    try { if (keys[key].wasPressedThisFrame) { pressed = key; break; } }
                    catch (Exception) { }
                }
                if (pressed == null)
                    return;
                capturing.Entry.Value = (keys.ctrlKey.isPressed ? "Ctrl+" : "") + (keys.shiftKey.isPressed ? "Shift+" : "") + (keys.altKey.isPressed ? "Alt+" : "") + pressed;
            }

            capturing = null;
            page.GUIRepaint = true;
        }

        /// <summary>
        /// Draws a subtab's keybinds section: a row per bind with its name and
        /// a button showing its key. Clicking the button starts rebinding.
        /// Draws nothing when the subtab has no keybinds.
        /// </summary>
        /// <param name="page">The Settings tab being drawn.</param>
        /// <param name="ui">The drawer for the tab.</param>
        /// <param name="subtab">The subtab whose keybinds to draw.</param>
        internal static void Draw(MadiLibMenu page, IGUIElementDrawer ui, string subtab)
        {
            callbacks.Clear();
            List<Bind> shown = binds.FindAll(b => b.Subtab == subtab);
            if (shown.Count == 0)
                return;

            ui.InfoField(capturing == null
                ? "Click a key, then press the new one (with Ctrl, Shift or Alt if wanted). Saved at once."
                : $"Press the new key for {capturing.Name}. Esc cancels, Backspace leaves it unbound.", 2);
            ui.ColumnCount = 2;
            foreach (Bind bind in shown)
            {
                Bind current = bind;
                UnityAction onClick = DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => { capturing = current; page.GUIRepaint = true; }))!;
                callbacks.Add(onClick);
                var tooltip = new UITooltip(bind.Name, $"Default: {bind.Entry.DefaultValue}");
                ui.InfoField(bind.Name, 1);
                ui.Button(bind == capturing ? "press a key..." : bind.Entry.Value.Length == 0 ? "(none)" : bind.Entry.Value, onClick, ref tooltip);
            }
            ui.ColumnCount = 1;
        }

        /// <summary>Cancels any rebind in progress.</summary>
        internal static void StopCapture() => capturing = null;
    }

    /// <summary>
    /// Component added to the plugin's object to call <see cref="Keybinds.Poll"/>
    /// and <see cref="EditorGui.EndHeaderDrag"/> every frame.
    /// </summary>
    internal sealed class KeybindPoller : MonoBehaviour
    {
        /// <summary>Wraps an existing native instance; used by Il2CppInterop.</summary>
        public KeybindPoller(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            Keybinds.Poll();
            EditorGui.EndHeaderDrag();
        }
    }
}
