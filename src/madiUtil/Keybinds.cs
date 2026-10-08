extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.SettingConfiguration;
using Sprocket.UI;
using UnityEngine.InputSystem;
using MonoBehaviour = UnityCoreModule::UnityEngine.MonoBehaviour;
using UnityAction = UnityCoreModule::UnityEngine.Events.UnityAction;

namespace MadiUtil
{
    // Keys live in the config's [Keybinds] section as text ("Ctrl+S") and can be
    // rebound from a "madiUtil" tab in the game's Settings screen.
    [HarmonyPatch]
    internal static class Keybinds
    {
        private sealed record Bind(string Name, ConfigEntry<string> Entry, Action OnPressed);

        private static readonly List<Bind> binds = new();
        private static readonly List<UnityAction> callbacks = new(); // the game holds these while the page shows
        private static readonly Key[] modifiers = { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LeftMeta, Key.RightMeta, Key.ContextMenu };
        private static Bind? capturing;

        public static void Register(BasePlugin plugin, string name, string defaultKey, Action onPressed)
        {
            if (binds.Count == 0)
            {
                plugin.AddComponent<KeybindPoller>();
                ClassInjector.RegisterTypeInIl2Cpp<KeybindsMenu>();
            }
            binds.Add(new Bind(name, plugin.Config.Bind("Keybinds", name, defaultKey, "A key such as S or F9, with Ctrl+, Shift+ or Alt+ in front if wanted; empty for none."), onPressed));
        }

        internal static void Poll()
        {
            if (capturing != null || Keyboard.current is not { } keys)
                return;
            foreach (Bind bind in binds)
                if (Pressed(keys, bind.Entry.Value))
                    bind.OnPressed();
        }

        private static bool Pressed(Keyboard keys, string text)
        {
            (Key key, bool ctrl, bool shift, bool alt) = Parse(text);
            return key != Key.None && keys[key].wasPressedThisFrame
                && ctrl == keys.ctrlKey.isPressed && shift == keys.shiftKey.isPressed && alt == keys.altKey.isPressed;
        }

        private static (Key Key, bool Ctrl, bool Shift, bool Alt) Parse(string text)
        {
            Key key = Key.None;
            bool ctrl = false, shift = false, alt = false;
            foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
                else if (!Enum.TryParse(part, true, out key)) return default;
            }
            return (key, ctrl, shift, alt);
        }

        internal static void Capture(KeybindsMenu page)
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

        internal static void Draw(KeybindsMenu page, IGUILayout layout)
        {
            callbacks.Clear();
            if (layout.TryCast<IGUIElementDrawer>() is not { } ui)
                return;

            ui.Header("Keybinds");
            ui.InfoField(capturing == null
                ? "Click a key, then press the new one (with Ctrl, Shift or Alt if wanted). Saved at once."
                : $"Press the new key for {capturing.Name}. Esc cancels, Backspace leaves it unbound.", 2);
            ui.ColumnCount = 2;
            foreach (Bind bind in binds)
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

        internal static void StopCapture() => capturing = null;

        [HarmonyPrefix, HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SetupMenuButtons))]
        private static void AddSettingsTab(SettingsMenu __instance)
        {
            Il2CppReferenceArray<SettingsSubMenu>? menus = __instance.subMenus;
            if (binds.Count == 0 || menus == null)
                return;
            foreach (SettingsSubMenu menu in menus)
                if (menu?.TryCast<KeybindsMenu>() != null)
                    return;

            var more = new Il2CppReferenceArray<SettingsSubMenu>(menus.Length + 1);
            for (int i = 0; i < menus.Length; i++)
                more[i] = menus[i];
            more[menus.Length] = new KeybindsMenu();
            __instance.subMenus = more;
        }
    }

    public sealed class KeybindsMenu : SettingsSubMenu
    {
        public KeybindsMenu(IntPtr ptr) : base(ptr) { }

        public KeybindsMenu() : base(ClassInjector.DerivedConstructorPointer<KeybindsMenu>())
        {
            ClassInjector.DerivedConstructorBody(this);
            MenuName = "madiUtil";
        }

        public override void DisplayGUI(IGUILayout layout) => Keybinds.Draw(this, layout);
        public override void Update() => Keybinds.Capture(this);
        public override void ClearGUI() => Keybinds.StopCapture();
        public override void Apply() { }
        public override void Cancel() => Keybinds.StopCapture();
    }

    internal sealed class KeybindPoller : MonoBehaviour
    {
        public KeybindPoller(IntPtr ptr) : base(ptr) { }

        private void Update() => Keybinds.Poll();
    }
}
