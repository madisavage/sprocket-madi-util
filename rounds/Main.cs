extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MadiLib;
using Properties;
using Sprocket.Blueprints;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Weapons;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.WeaponFramework;
using UnityAction = UnityCoreModule::UnityEngine.Events.UnityAction;

namespace Rounds
{
    /// <summary>
    /// BepInEx entry point. Cannons get named rounds of types other plugins
    /// add; each ammo rack holds one of them; the player picks the next round to
    /// load, and the loader only loads it. See <see cref="Loadout"/>.
    /// </summary>
    [BepInPlugin("madi.rounds", "rounds", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;
        internal static ConfigEntry<bool> ShowIndicator = null!;
        internal static ConfigEntry<bool> LogShells = null!;

        /// <summary>Registers the keybind, HUD panel and patches.</summary>
        public override void Load()
        {
            Logger = Log;
            ShowIndicator = Config.Bind("Display", "Show indicator", true, "Show the ammo bar: each of your guns' rounds with shells left, its loaded round and its next.");
            LogShells = Config.Bind("Debug", "Log shells", false, "Logs every shell taken from or put in a rack with rounds, which round it was and why, to BepInEx/LogOutput.log.");
            SettingsTab.AddSection("Rounds", "Rounds", ShowIndicator, LogShells);
            Keybinds.Register(this, "Rounds", "Select gun", "B", Tracker.SelectGun);
            for (int n = 1; n <= 9; n++)
            {
                int round = n;
                Keybinds.Register(this, "Rounds", $"Round {n}", $"Digit{n}", () => Tracker.SelectRound(round));
            }
            using (var icon = typeof(Plugin).Assembly.GetManifestResourceStream("ap.png"))
                Loadout.Register("ap", "AP", (_, _, _, _) => { }, icon: icon);
            Hud.AddPanel(AmmoBar.Draw);
            var harmony = new Harmony("madi.rounds");
            harmony.PatchAll();
            DynamicSight.Patch(harmony);
            try
            {
                SpallDetour.Apply();
            }
            catch (Exception e)
            {
                Log.LogError($"Couldn't hook spall, rounds can't change it: {e}");
            }
        }
    }

    /// <summary>
    /// Which round each rack holds, and which each gun has loaded and loads
    /// next. Each rack holds one round, set in the rack panel, so its shells
    /// are its <c>AmountStored</c>. A rack refuses a load unless it holds
    /// the gun's next round, so the gun waits for one.
    /// </summary>
    internal static class Tracker
    {
        // Gun behaviour, its cannon; one entry per cannon enabled
        internal static readonly Dictionary<IntPtr, Cannon> Cannons = new();
        // Shell slot and vehicle, the gun behaviour using it; vehicles of one design share shell slots
        private static readonly Dictionary<(IntPtr Shell, IntPtr Vehicle), IntPtr> gunOfShell = new();
        // Rack behaviour, its rack
        private static readonly Dictionary<IntPtr, AmmoRack> racks = new();
        // Gun behaviour, round id it loads next / has loaded
        private static readonly Dictionary<IntPtr, string> selected = new(), loaded = new();
        // Gun behaviour, round id its loader has taken from a rack but not yet loaded, and that rack
        private static readonly Dictionary<IntPtr, (string Id, IntPtr Rack)> carried = new();
        // Guns whose loader found no next round in the rack it went to
        private static readonly HashSet<IntPtr> waiting = new();
        // The player's gun the round keys pick for; zero or gone for their first
        private static IntPtr active;

        // The gun whose load task is running, or zero
        internal static IntPtr Loading;
        // Set during a re-rack, which may take from any rack
        internal static bool Reracking;

        internal static bool IsWaiting(IntPtr gun) => waiting.Contains(gun);

        private static void Log(string message)
        {
            if (Plugin.LogShells.Value)
                Plugin.Logger.LogInfo($"Shells: {message}");
        }

        private static string Name(IntPtr rack) =>
            $"{(racks.TryGetValue(rack, out AmmoRack? component) ? component.Blueprint?.Name : null) ?? "rack"} {rack.ToInt64():X}";

        private static void LoadedChanged(IntPtr gun)
        {
            if (!Cannons.TryGetValue(gun, out Cannon? cannon))
                return;
            AiVelocity.Update(gun);
            try
            {
                Loadout.RaiseLoadedChanged(cannon);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"A loaded round handler failed: {e}");
            }
        }

        internal static void AddCannon(Cannon cannon)
        {
            if (cannon.behaviour == null)
                return;
            Prune();
            // A cannon enabled again gets a new behaviour; forget the old one, keeping the player's pick
            foreach (IntPtr old in Cannons.Where(c => c.Value.Pointer == cannon.Pointer && c.Key != cannon.behaviour.Pointer).Select(c => c.Key).ToList())
            {
                Cannons.Remove(old);
                loaded.Remove(old);
                carried.Remove(old);
                waiting.Remove(old);
                if (selected.Remove(old, out string? pick))
                    selected[cannon.behaviour.Pointer] = pick;
            }
            Cannons[cannon.behaviour.Pointer] = cannon;
            if (cannon.ShellBlueprint != null)
                gunOfShell[(cannon.ShellBlueprint.Pointer, VehicleOf(cannon))] = cannon.behaviour.Pointer;
            loaded.Remove(cannon.behaviour.Pointer);
            Loadout.Owner(cannon);
            AiVelocity.Update(cannon.behaviour.Pointer);
        }

        internal static void AddRack(AmmoRack rack)
        {
            if (rack.behaviour == null)
                return;
            foreach (IntPtr old in racks.Where(r => r.Value.Pointer == rack.Pointer).Select(r => r.Key).ToList())
                racks.Remove(old);
            racks[rack.behaviour.Pointer] = rack;
        }

        /// <summary>The round <paramref name="gun"/> loads next: the player's pick, or its first round.</summary>
        internal static string? Selected(IntPtr gun, CannonBlueprint? owner) => PickOrFirst(selected, gun, owner);

        /// <summary>
        /// The round <paramref name="gun"/> has loaded. Before a load is seen
        /// (a gun loaded at spawn), its first round.
        /// </summary>
        internal static string? Loaded(IntPtr gun, CannonBlueprint? owner) => PickOrFirst(loaded, gun, owner);

        /// <summary>The round <paramref name="gun"/>'s loader is loading, or else the one loaded.</summary>
        internal static string? Upcoming(IntPtr gun, CannonBlueprint? owner) => carried.TryGetValue(gun, out var carry) ? carry.Id : Loaded(gun, owner);

        private static string? PickOrFirst(Dictionary<IntPtr, string> picks, IntPtr gun, CannonBlueprint? owner)
        {
            var ids = Loadout.List(owner).Select(r => r.Id).ToList();
            return picks.TryGetValue(gun, out string? id) && ids.Contains(id) ? id : ids.FirstOrDefault();
        }

        private static IntPtr VehicleOf(UnityCoreModule::UnityEngine.Component part) => part.GetComponentInParent<Vehicle>()?.Pointer ?? IntPtr.Zero;

        /// <summary><paramref name="rack"/>'s gun and the blueprint holding its rounds.</summary>
        private static (IntPtr Gun, CannonBlueprint? Owner) GunOf(AmmoRackBehaviour rack)
        {
            ShellSlotBlueprint? shell = racks.TryGetValue(rack.Pointer, out AmmoRack? component) ? component.shellSlotBlueprintSlot?.Blueprint : null;
            IntPtr gun = Cannons.ContainsKey(Loading) ? Loading
                : shell != null && gunOfShell.TryGetValue((shell.Pointer, VehicleOf(component!)), out IntPtr owner) ? owner
                : IntPtr.Zero;
            return (gun, Cannons.TryGetValue(gun, out Cannon? cannon) ? Loadout.Owner(cannon) : Loadout.OwnerOf(shell));
        }

        /// <summary>The round <paramref name="rack"/> holds: its pick, or its gun's first round; null for a rack without rounds.</summary>
        internal static string? RoundOf(AmmoRackBehaviour rack) =>
            racks.TryGetValue(rack.Pointer, out AmmoRack? component) ? RackRound.Get(component.Blueprint, GunOf(rack).Owner) : null;

        /// <summary>
        /// Whether <paramref name="rack"/> holds its gun's next round, counting
        /// one its loader is carrying from it: else taking the last one would
        /// cancel its own load.
        /// </summary>
        internal static bool HoldsNext(AmmoRackBehaviour rack)
        {
            var (gun, owner) = GunOf(rack);
            return RoundOf(rack) is { } id && id == Selected(gun, owner)
                && (rack.AmountStored > 0 || carried.TryGetValue(gun, out var carry) && carry.Rack == rack.Pointer);
        }

        /// <summary>
        /// Whether <paramref name="rack"/> may give a shell: always in a
        /// re-rack, and for a load only if it holds the gun's next round. A
        /// refused load marks the gun waiting.
        /// </summary>
        internal static bool MayTake(AmmoRackBehaviour rack)
        {
            if (Reracking || RoundOf(rack) == null || HoldsNext(rack))
                return true;
            waiting.Add(GunOf(rack).Gun);
            return false;
        }

        /// <summary><paramref name="gun"/>'s load task no longer holds the shell its loader took.</summary>
        internal static void Dropped(IntPtr gun) => carried.Remove(gun);

        /// <summary>A load from <paramref name="rack"/> was refused, so its gun's load task holds no shell.</summary>
        internal static void Refused(AmmoRackBehaviour rack) => Dropped(GunOf(rack).Gun);

        /// <summary>Whether <paramref name="gun"/>'s loader holds a shell it took from <paramref name="rack"/>.</summary>
        internal static bool Carries(IntPtr gun, AmmoRackBehaviour rack) => carried.TryGetValue(gun, out var carry) && carry.Rack == rack.Pointer;

        /// <summary>A shell left <paramref name="rack"/>: for a load, its gun's loader now carries the rack's round.</summary>
        internal static void Took(AmmoRackBehaviour rack)
        {
            if (RoundOf(rack) is not { } id)
                return;
            var (gun, _) = GunOf(rack);
            Log($"{Name(rack.Pointer)} gave {id}{(Reracking ? " for a re-rack" : $" to gun {gun.ToInt64():X}")}, {rack.AmountStored} left");
            if (Reracking)
                return;
            waiting.Remove(gun);
            carried[gun] = (id, rack.Pointer);
            if (Cannons.TryGetValue(gun, out Cannon? cannon) && PlayerVehicle.Contains(cannon) && id != Loaded(gun, Loadout.Owner(cannon)))
                DynamicSight.Clear();
        }

        /// <summary>
        /// After <paramref name="task"/>'s update: once its gun is loaded,
        /// the round its loader carried is the one loaded.
        /// </summary>
        internal static void Landed(LoadTask task)
        {
            IntPtr gun = task.target?.Pointer ?? IntPtr.Zero;
            if (task.State != LoadState.Loaded || !carried.Remove(gun, out var carry))
                return;
            string id = carry.Id;
            bool changed = !loaded.TryGetValue(gun, out string? before) || before != id;
            loaded[gun] = id;
            Log($"gun {gun.ToInt64():X} loaded {id}");
            if (changed)
                LoadedChanged(gun);
        }

        /// <summary><paramref name="gun"/> fired, for the shell log.</summary>
        internal static void Fired(IntPtr gun)
        {
            if (Plugin.LogShells.Value && Cannons.TryGetValue(gun, out Cannon? cannon))
                Log($"gun {gun.ToInt64():X} fired {Loaded(gun, Loadout.Owner(cannon))}");
        }

        /// <summary>The player's guns that have rounds, in the order enabled.</summary>
        internal static List<(IntPtr Gun, Cannon Cannon, CannonBlueprint Owner)> PlayerGuns()
        {
            var guns = new List<(IntPtr, Cannon, CannonBlueprint)>();
            foreach (var (gun, cannon) in Cannons.ToList())
            {
                try
                {
                    if (cannon == null)
                        Forget(gun);
                    else if (PlayerVehicle.Contains(cannon) && Loadout.Owner(cannon) is { } owner && Loadout.List(owner).Count > 0)
                        guns.Add((gun, cannon, owner));
                }
                catch (Exception)
                {
                    // Destroyed with its vehicle
                    Forget(gun);
                }
            }
            return guns;
        }

        /// <summary>Drops guns and racks destroyed with their vehicles; run at each spawn, as the HUD may never walk them.</summary>
        private static void Prune()
        {
            foreach (var (gun, cannon) in Cannons.ToList())
                if (cannon == null)
                    Forget(gun);
            foreach (var (behaviour, rack) in racks.ToList())
                if (rack == null)
                    racks.Remove(behaviour);
        }

        /// <summary>Drops a gun destroyed with its vehicle, so the lists don't grow each battle.</summary>
        private static void Forget(IntPtr gun)
        {
            Cannons.Remove(gun);
            selected.Remove(gun);
            loaded.Remove(gun);
            carried.Remove(gun);
            waiting.Remove(gun);
            foreach (var key in gunOfShell.Where(g => g.Value == gun).Select(g => g.Key).ToList())
                gunOfShell.Remove(key);
        }

        /// <summary>Shells of each round id left in <paramref name="cannon"/>'s racks on its vehicle; those in ready racks also go in <paramref name="ready"/>.</summary>
        private static Dictionary<string, int> Left(Cannon cannon, Dictionary<string, int>? ready = null)
        {
            var left = new Dictionary<string, int>();
            foreach (var (behaviour, rack) in racks.ToList())
            {
                try
                {
                    if (rack == null)
                    {
                        racks.Remove(behaviour);
                        continue;
                    }
                    if (rack.shellSlotBlueprintSlot?.Blueprint?.Pointer != cannon.ShellBlueprint?.Pointer || !PlayerVehicle.Contains(rack) || RoundOf(rack.behaviour) is not { } id)
                        continue;
                    int count = rack.behaviour.AmountStored;
                    left[id] = left.GetValueOrDefault(id) + count;
                    if (ready != null && Loadout.IsReady?.Invoke(rack.behaviour) == true)
                        ready[id] = ready.GetValueOrDefault(id) + count;
                }
                catch (Exception)
                {
                    // Destroyed with its vehicle
                    racks.Remove(behaviour);
                }
            }
            return left;
        }

        /// <summary>The active gun's index in <paramref name="guns"/>: the one the round keys pick for, or the first.</summary>
        private static int Active(List<(IntPtr Gun, Cannon Cannon, CannonBlueprint Owner)> guns) => Math.Max(guns.FindIndex(g => g.Gun == active), 0);

        /// <summary>Makes the player's next gun with rounds the active one, for the round keys.</summary>
        internal static void SelectGun()
        {
            var guns = PlayerGuns();
            if (guns.Count > 0)
                active = guns[(Active(guns) + 1) % guns.Count].Gun;
        }

        /// <summary>
        /// Selects the active gun's round <paramref name="n"/> (from 1, in the
        /// cannon panel's order) for its next load, and has an empty gun ask for
        /// ammo again: one waiting for a round out of reach doesn't ask again on
        /// its own. Does nothing if it has fewer rounds.
        /// </summary>
        internal static void SelectRound(int n)
        {
            var guns = PlayerGuns();
            if (guns.Count == 0)
                return;
            var (gun, cannon, owner) = guns[Active(guns)];
            var ids = Loadout.List(owner);
            if (n > ids.Count)
                return;
            selected[gun] = ids[n - 1].Id;
            Loadout.RequestAmmo(cannon.behaviour);
            waiting.Remove(gun);
        }

        /// <summary>
        /// Each of the player's guns with rounds: its loaded and next round,
        /// whether it's waiting for the next round to be in reach, whether the
        /// round keys pick for it, and each round with its icon key and its
        /// shells left in all its racks and in ready racks.
        /// </summary>
        internal static List<(string? Loaded, string? Next, bool Waiting, bool Active, List<(string Id, string Name, string Type, string Icon, int Left, int Ready)> Rounds)> Guns()
        {
            var guns = new List<(string?, string?, bool, bool, List<(string, string, string, string, int, int)>)>();
            var player = PlayerGuns();
            int current = Active(player);
            for (int i = 0; i < player.Count; i++)
            {
                var (gun, cannon, owner) = player[i];
                var ready = new Dictionary<string, int>();
                var left = Left(cannon, ready);
                var rounds = Loadout.List(owner).Select(r => (r.Id, r.Name, r.Type, Loadout.IconOf(owner, r.Id, r.Type), left.GetValueOrDefault(r.Id), ready.GetValueOrDefault(r.Id))).ToList();
                guns.Add((Loaded(gun, owner), Selected(gun, owner), waiting.Contains(gun), i == current, rounds));
            }
            return guns;
        }
    }

    /// <summary>The round an ammo rack holds, stored as <c>{"round": id}</c> in the rack blueprint's description.</summary>
    internal static class RackRound
    {
        /// <summary><paramref name="rack"/>'s round among <paramref name="owner"/>'s: its pick, or the first; null if there are none.</summary>
        internal static string? Get(AmmoRackBlueprint? rack, CannonBlueprint? owner)
        {
            var ids = Loadout.List(owner).Select(r => r.Id).ToList();
            string? pick = rack == null ? null : (string?)(Loadout.Parsed(rack)["round"] as JsonValue);
            return pick != null && ids.Contains(pick) ? pick : ids.FirstOrDefault();
        }

        internal static void Set(AmmoRackBlueprint rack, string id)
        {
            if (Loadout.Read(rack) is not { } root)
                return;
            root.Remove("mix");
            root["round"] = id;
            rack.Description = root.ToJsonString();
        }
    }

    [HarmonyPatch]
    internal static class TrackingPatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Cannon), nameof(Cannon.EnableBehaviour))]
        private static void CannonEnabled(Cannon __instance) => Tracker.AddCannon(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(AmmoRack), nameof(AmmoRack.EnableBehaviour))]
        private static void RackEnabled(AmmoRack __instance) => Tracker.AddRack(__instance);

        /// <summary>
        /// Holds a load whose rack doesn't hold the gun's next round, and
        /// sends it back to pick a rack again. The game loads whatever
        /// <c>TakeShell</c> says, so refusing the shell alone isn't enough.
        /// Only while loading: cancelling a loaded task puts its shell back in
        /// the rack, even one already fired.
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(LoadTask), nameof(LoadTask.Update))]
        private static bool LoadStart(LoadTask __instance)
        {
            using var _ = MadiLib.Profile.Measure("rounds: LoadTask.Update prefix");
            Tracker.Loading = __instance.target?.Pointer ?? IntPtr.Zero;
            if (__instance.State != LoadState.Loading || __instance.rack?.TryCast<AmmoRackBehaviour>() is not { } rack || Tracker.MayTake(rack))
                return true;
            bool holds = Tracker.Carries(Tracker.Loading, rack);
            int before = rack.AmountStored;
            __instance.Cancel();
            Tracker.Dropped(Tracker.Loading);
            // Cancel puts a shell back even when the game already has (switching rounds swaps out the one loading), so take that one out again
            if (!holds && rack.AmountStored > before)
                Loadout.Rerack(() => rack.TakeShell());
            Tracker.Loading = IntPtr.Zero;
            // The game reloads from the rack it last used without asking for ammo, so a round picked while loaded would never load
            Loadout.RequestAmmo(__instance.target?.TryCast<WeaponBehaviour>());
            return false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(LoadTask), nameof(LoadTask.Update))]
        private static void LoadEnd(LoadTask __instance)
        {
            using var _ = MadiLib.Profile.Measure("rounds: LoadTask.Update postfix");
            Tracker.Landed(__instance);
            Tracker.Loading = IntPtr.Zero;
        }

        // A load from a rack without the gun's next round is refused
        [HarmonyPrefix, HarmonyPatch(typeof(AmmoRackBehaviour), nameof(AmmoRackBehaviour.TakeShell))]
        private static bool BeforeTake(AmmoRackBehaviour __instance, out int __state, ref bool __result)
        {
            using var _ = MadiLib.Profile.Measure("rounds: TakeShell prefix");
            __state = __instance.AmountStored;
            __result = Tracker.MayTake(__instance);
            // A refused take leaves the load task with no shell
            if (!__result)
                Tracker.Refused(__instance);
            return __result;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(AmmoRackBehaviour), nameof(AmmoRackBehaviour.TakeShell))]
        private static void Took(AmmoRackBehaviour __instance, int __state)
        {
            using var _ = MadiLib.Profile.Measure("rounds: TakeShell postfix");
            if (__state > __instance.AmountStored)
                Tracker.Took(__instance);
        }
    }

    /// <summary>
    /// Adds a <b>Rounds</b> section to the cannon panel: each round's name,
    /// type and the type's own fields, with buttons to add and delete rounds.
    /// </summary>
    [HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    internal static class CannonEditorPatch
    {
        // Cannon blueprint and round id, its name field
        private static readonly Dictionary<(IntPtr, string), StringProperty> names = new();

        private static void Postfix(CannonEditor __instance, IGUILayout __0)
        {
            Cannon? cannon = __instance.Component;
            CannonBlueprint? owner = Loadout.Owner(cannon);
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (cannon == null || owner == null || drawer == null || Loadout.Types.Count == 0)
                return;
            if (!EditorGui.Section(drawer, "rounds.cannon", "Rounds", __instance.RequestRedraw))
                return;
            Action changed = () => cannon.blueprintSlot?.MarkModified();
            List<string> labels = Loadout.Types.Select(t => t.Label).ToList();
            foreach (var (id, name, type) in Loadout.List(owner))
            {
                if (!names.TryGetValue((owner.Pointer, id), out StringProperty? field))
                    names[(owner.Pointer, id)] = field = new StringProperty(
                        "Round name",
                        (Func<string>)(() => Loadout.List(owner).FirstOrDefault(r => r.Id == id).Name ?? ""),
                        (Action<string>)(value =>
                        {
                            Loadout.Rename(owner, id, value);
                            changed();
                        }),
                        "Shown in the HUD, the rack panel and the Delete button.");
                drawer.InputField(field);
                int index = Loadout.Types.FindIndex(t => t.Type == type);
                EditorGui.Dropdown(drawer, "Type", labels, index, i =>
                {
                    Loadout.SetType(owner, id, Loadout.Types[i].Type);
                    changed();
                    __instance.RequestRedraw();
                }, "What kind of round this is.");
                if (index >= 0)
                    Loadout.Types[index].Draw(drawer, cannon, id, changed);
                var delete = new UITooltip($"Delete {name}", "Removes this round. Racks of it hold the first round instead.");
                drawer.Button($"Delete {name}", (UnityAction)(() =>
                {
                    Loadout.Delete(owner, id);
                    changed();
                    __instance.RequestRedraw();
                }), ref delete);
            }
            var add = new UITooltip("Add round", $"Adds a {Loadout.Types[0].Label} round; change its type above.");
            drawer.Button("Add round", (UnityAction)(() =>
            {
                Loadout.Add(owner, Loadout.Types[0].Type);
                changed();
                __instance.RequestRedraw();
            }), ref add);
        }
    }

    /// <summary>Adds a <b>Round</b> dropdown to the ammo rack panel: which of its gun's rounds the rack holds.</summary>
    [HarmonyPatch(typeof(AmmoRackEditor), nameof(AmmoRackEditor.OnGUI))]
    internal static class AmmoRackEditorPatch
    {
        private static void Postfix(AmmoRackEditor __instance, IGUILayout __0)
        {
            AmmoRack? rack = __instance.Component;
            AmmoRackBlueprint? blueprint = rack?.Blueprint;
            CannonBlueprint? owner = Loadout.OwnerOf(rack?.shellSlotBlueprintSlot?.Blueprint);
            var rounds = Loadout.List(owner);
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (rack == null || blueprint == null || rounds.Count == 0 || drawer == null)
                return;
            string? current = RackRound.Get(blueprint, owner);
            EditorGui.Dropdown(drawer, "Round", rounds.Select(r => r.Name).ToList(), rounds.FindIndex(r => r.Id == current), i =>
            {
                RackRound.Set(blueprint, rounds[i].Id);
                rack.blueprintReference?.MarkModified();
                __instance.RequestRedraw();
            }, "The round every shell in this rack is. Place a rack for each round you want to carry.");
        }
    }
}
