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
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
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
    /// add; each ammo rack has slots for each round and any left unassigned; the player
    /// picks the next round to load, and the loader only loads it. See <see cref="Loadout"/>.
    /// </summary>
    [BepInPlugin("madi.rounds", "rounds", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;
        internal static ConfigEntry<bool> ShowIndicator = null!;
        internal static ConfigEntry<bool> LogShells = null!;
        internal static ConfigEntry<bool> LogAi = null!;

        /// <summary>Registers the keybind, HUD panel and patches.</summary>
        public override void Load()
        {
            Logger = Log;
            ShowIndicator = Config.Bind("Display", "Show indicator", true, "Show the ammo bar: each of your guns' rounds with shells left, its loaded round and its next.");
            LogShells = Config.Bind("Debug", "Log shells", false, "Logs every shell taken from or put in a rack with rounds, which round it was and why, to BepInEx/LogOutput.log.");
            LogAi = Config.Bind("Debug", "Log AI", false, "Logs the AI's decisions to BepInEx/LogOutput.log: each round pick with every round's penetration, speed and shells left, and, with guidedMunitions, its guided shots and why their guidance ends.");
            SettingsTab.AddSection("Rounds", "Rounds", ShowIndicator, LogShells, LogAi);
            Keybinds.Register(this, "Rounds", "Select gun", "B", Tracker.SelectGun);
            for (int n = 1; n <= 9; n++)
            {
                int round = n;
                Keybinds.Register(this, "Rounds", $"Round {n}", $"Digit{n}", () => Tracker.SelectRound(round));
            }
            using (System.IO.Stream? icon = typeof(Plugin).Assembly.GetManifestResourceStream("ap.png"))
                Loadout.Register("ap", "AP", (_, _, _, _) => { }, icon: icon,
                    penetration: (owner, _, speed) => Impacts.DeMarre(owner.Caliber, ShellProperties.GetProjectileMass(owner.Caliber), speed, owner.PenetratorConstant));
            Hud.AddPanel(AmmoBar.Draw);
            Nera.Load();
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
            try
            {
                ShellHitDetour.Apply();
            }
            catch (Exception e)
            {
                Log.LogError($"Couldn't hook shell hits; rounds' hits act as the game's shell, and the shell cam won't see the player's: {e}");
            }
        }
    }

    /// <summary>
    /// Which rounds each rack holds, and which each gun has loaded and loads
    /// next. A rack's split is set in the rack panel; the game only keeps its
    /// <c>AmountStored</c>, so <see cref="RackMix"/> tracks which shell is
    /// which. A rack refuses a load unless it holds the gun's next round, so
    /// the gun waits for one.
    /// </summary>
    internal static class Tracker
    {
        // Gun behaviour, its cannon; one entry per cannon enabled
        internal static readonly Dictionary<IntPtr, Cannon> Cannons = new();
        // Shell slot and vehicle, the gun behaviour using it; vehicles of one design share shell slots
        private static readonly Dictionary<(IntPtr Shell, IntPtr Vehicle), IntPtr> gunOfShell = new();
        // Rack behaviour, its rack
        private static readonly Dictionary<IntPtr, AmmoRack> racks = new();
        // Rack behaviour and rounds' blueprint, its shells per round and the parsed descriptions they came from; guns of different blueprints sharing a rack each read it their own way
        private static readonly Dictionary<(IntPtr Rack, IntPtr Owner), (JsonObject Rack, JsonObject Owner, RackMix Mix)> mixes = new();
        // Gun behaviour, round id it loads next / has loaded
        private static readonly Dictionary<IntPtr, string> selected = new(), loaded = new();
        // Gun behaviour, round id its loader has taken from a rack but not yet loaded, and that rack
        private static readonly Dictionary<IntPtr, (string Id, IntPtr Rack)> carried = new();
        // Guns whose loader found no next round in the rack it went to
        private static readonly HashSet<IntPtr> waiting = new();
        // The player's gun the round keys pick for; zero or gone for their first
        private static IntPtr active;
        // Guns found on the player's vehicle, which the AI doesn't pick for
        private static readonly HashSet<IntPtr> players = new();

        // The gun whose load task is running, or zero
        internal static IntPtr Loading;
        // Set during a re-rack, which may take from any rack
        internal static bool Reracking;
        // The round a re-rack is moving, if it says
        internal static string? Moving;

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
            List<IntPtr> stale = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, Cannon> known in Cannons)
            {
                if (known.Value.Pointer == cannon.Pointer && known.Key != cannon.behaviour.Pointer)
                    stale.Add(known.Key);
            }
            foreach (IntPtr old in stale)
            {
                Cannons.Remove(old);
                loaded.Remove(old);
                carried.Remove(old);
                waiting.Remove(old);
                if (selected.Remove(old, out string? pick))
                    selected[cannon.behaviour.Pointer] = pick;
                if (players.Remove(old))
                    players.Add(cannon.behaviour.Pointer);
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
            racks.Remove(rack.behaviour.Pointer);
            ForgetMixes();
            racks[rack.behaviour.Pointer] = rack;
        }

        /// <summary>Drops the mixes of racks no longer known.</summary>
        private static void ForgetMixes()
        {
            foreach ((IntPtr Rack, IntPtr Owner) key in mixes.Keys.Where(k => !racks.ContainsKey(k.Rack)).ToList())
                mixes.Remove(key);
        }

        /// <summary>The round <paramref name="gun"/> loads next: the player's pick, the AI's (see <see cref="AiPick"/>), or its first round.</summary>
        internal static string? Selected(IntPtr gun, CannonBlueprint? owner)
        {
            Meet(gun, owner);
            return PickOrFirst(selected, gun, owner);
        }

        /// <summary>The first time <paramref name="gun"/> is asked about: marks it the player's, or has the AI pick its round.</summary>
        private static void Meet(IntPtr gun, CannonBlueprint? owner)
        {
            // Has a pick already, or is known to be the player's
            if (selected.ContainsKey(gun))
                return;
            if (players.Contains(gun))
                return;
            if (owner == null)
                return;
            if (!Cannons.TryGetValue(gun, out Cannon? cannon))
                return;
            // Only once the player's vehicle is known, so theirs isn't taken for the AI's at spawn
            if (PlayerVehicle.Root == null)
                return;
            if (PlayerVehicle.Contains(cannon))
                players.Add(gun);
            else
                AiPick(gun, cannon, owner, "first load");
        }

        /// <summary>
        /// Picks an AI gun's next round: its most penetrating round with shells
        /// left on its vehicle, rated at its speed at the range of the enemy nearest
        /// the gun's aim (with none, at the speed the AI leads it by); ties go
        /// to the panel's order. Keeps its pick if no round has shells.
        /// </summary>
        private static void AiPick(IntPtr gun, Cannon cannon, CannonBlueprint owner, string reason)
        {
            Dictionary<string, int> left = Left(cannon);
            CannonBlueprint blueprint = cannon.Blueprint ?? owner;
            AiVelocity.Games.TryGetValue(gun, out ProjectileDefinition? game);
            List<(string Id, string Name, string Type)> rounds = Loadout.List(owner);
            float? range = null;
            if (game != null)
                range = AiRange.Of(cannon);
            // Rounds with shells left, rated at their speed at the target
            List<(string Id, string Name, float Speed, float Penetration)> rated = new List<(string Id, string Name, float Speed, float Penetration)>();
            foreach ((string Id, string Name, string Type) round in rounds)
            {
                if (left.GetValueOrDefault(round.Id) <= 0)
                    continue;
                float speed = RatedSpeed(gun, blueprint, game, round.Id, range);
                rated.Add((round.Id, round.Name, speed, Loadout.Penetration(owner, round.Id, speed)));
            }
            // A stable sort: ties keep the panel's order
            rated = rated.OrderByDescending(r => r.Penetration).ToList();
            string? best = rated.Count > 0 ? rated[0].Id : PickOrFirst(selected, gun, owner);
            if (best == null)
                return;
            bool changed = !selected.TryGetValue(gun, out string? before) || before != best;
            if (Plugin.LogAi.Value && changed)
                LogAiPick(cannon, gun, reason, range, rounds, rated, left, best);
            selected[gun] = best;
        }

        /// <summary>The speed round <paramref name="id"/> is rated at: at the target's <paramref name="range"/>, or with none the speed the AI leads it by.</summary>
        private static float RatedSpeed(IntPtr gun, CannonBlueprint blueprint, ProjectileDefinition? game, string id, float? range)
        {
            if (range == null)
                return AiVelocity.Speed(gun, blueprint, game, id);
            // A range is only found with the game's projectile known. A round that doesn't reach the target rates at no speed
            return AiVelocity.AtRange(gun, blueprint, game!, id, range.Value)?.Speed ?? 0f;
        }

        private static void LogAiPick(Cannon cannon, IntPtr gun, string reason, float? range, List<(string Id, string Name, string Type)> rounds,
            List<(string Id, string Name, float Speed, float Penetration)> rated, Dictionary<string, int> left, string best)
        {
            string vehicle = cannon.GetComponentInParent<Vehicle>()?.name ?? "vehicle";
            string message = $"AI {vehicle} (gun {gun.ToInt64():X}), {reason}";
            if (range != null)
                message += $", target {range.Value:0} m";
            message += $": picks \"{rounds.FirstOrDefault(r => r.Id == best).Name}\"";
            if (rated.Count == 0)
                message += ", no round has shells left";
            else
                message += " from " + string.Join(", ", rated.Select(r => $"{r.Name} {r.Penetration:0} mm @{r.Speed:0} m/s ×{left[r.Id]}"));
            List<string> empty = rounds.Where(r => left.GetValueOrDefault(r.Id) == 0).Select(r => r.Name).ToList();
            if (empty.Count > 0)
                message += $"; none left: {string.Join(", ", empty)}";
            Plugin.Logger.LogInfo(message);
        }

        /// <summary>
        /// The round <paramref name="gun"/> has loaded. Before a load is seen
        /// (a gun loaded at spawn), its first round.
        /// </summary>
        internal static string? Loaded(IntPtr gun, CannonBlueprint? owner) => PickOrFirst(loaded, gun, owner);

        /// <summary>The round <paramref name="gun"/>'s loader is loading, or else the one loaded.</summary>
        internal static string? Upcoming(IntPtr gun, CannonBlueprint? owner)
        {
            if (carried.TryGetValue(gun, out (string Id, IntPtr Rack) carry))
                return carry.Id;
            return Loaded(gun, owner);
        }

        private static string? PickOrFirst(Dictionary<IntPtr, string> picks, IntPtr gun, CannonBlueprint? owner)
        {
            List<string> ids = Loadout.List(owner).Select(r => r.Id).ToList();
            // The pick, while it's still one of the gun's rounds
            if (picks.TryGetValue(gun, out string? id) && ids.Contains(id))
                return id;
            return ids.FirstOrDefault();
        }

        private static IntPtr VehicleOf(UnityCoreModule::UnityEngine.Component part) => part.GetComponentInParent<Vehicle>()?.Pointer ?? IntPtr.Zero;

        /// <summary><paramref name="rack"/>'s gun and the blueprint holding its rounds.</summary>
        private static (IntPtr Gun, CannonBlueprint? Owner) GunOf(AmmoRackBehaviour rack)
        {
            ShellSlotBlueprint? shell = null;
            if (racks.TryGetValue(rack.Pointer, out AmmoRack? component))
                shell = component.shellSlotBlueprintSlot?.Blueprint;
            // The gun whose load is running, else the one using this rack's shell on its vehicle
            IntPtr gun = IntPtr.Zero;
            if (Cannons.ContainsKey(Loading))
                gun = Loading;
            else if (shell != null && gunOfShell.TryGetValue((shell.Pointer, VehicleOf(component!)), out IntPtr user))
                gun = user;
            CannonBlueprint? owner;
            if (Cannons.TryGetValue(gun, out Cannon? cannon))
                owner = Loadout.Owner(cannon);
            else
                owner = Loadout.OwnerOf(shell);
            return (gun, owner);
        }

        /// <summary>The round <paramref name="rack"/> was given: its pick, or its gun's first round; null for a rack without rounds.</summary>
        internal static string? RoundOf(AmmoRackBehaviour rack) =>
            racks.TryGetValue(rack.Pointer, out AmmoRack? component) ? RackRound.Get(component.Blueprint, GunOf(rack).Owner) : null;

        /// <summary><paramref name="rack"/>'s mix as <paramref name="owner"/>'s rounds read it, without taking in changes to its count; null for a rack without rounds.</summary>
        private static RackMix? MixOf(AmmoRackBehaviour rack, CannonBlueprint? owner)
        {
            if (owner == null)
                return null;
            if (!racks.TryGetValue(rack.Pointer, out AmmoRack? component))
                return null;
            AmmoRackBlueprint? blueprint = component.Blueprint;
            if (blueprint == null)
                return null;
            JsonObject design = Loadout.Parsed(blueprint), rounds = Loadout.Parsed(owner);
            (IntPtr Rack, IntPtr Owner) key = (rack.Pointer, owner.Pointer);
            if (mixes.TryGetValue(key, out (JsonObject Rack, JsonObject Owner, RackMix Mix) known))
            {
                // Read from these same descriptions: nothing changed since
                if (known.Rack == design && known.Owner == rounds)
                    return known.Mix;
            }
            RackMix? mix = RackMix.Read(design, Loadout.List(owner).Select(r => r.Id).ToList(), rack.Capacity);
            if (mix == null)
                return null;
            mixes[key] = (design, rounds, mix);
            return mix;
        }

        /// <summary><paramref name="rack"/>'s mix, up to date with its count; a change <paramref name="round"/> says is that round's.</summary>
        private static RackMix? Synced(AmmoRackBehaviour rack, CannonBlueprint? owner, string? round = null)
        {
            RackMix? mix = MixOf(rack, owner);
            if (mix == null)
                return null;
            // The game puts a shell back by itself when a gun's request changes: that's the round its loader took, and the loader no longer holds it
            if (round == null && rack.AmountStored > mix.Counts.Values.Sum())
                foreach ((IntPtr gun, (string Id, IntPtr Rack) carry) in carried)
                    if (carry.Rack == rack.Pointer)
                    {
                        round = carry.Id;
                        carried.Remove(gun);
                        break;
                    }
            mix.Sync(rack.AmountStored, round);
            return mix;
        }

        /// <summary>Shells per round in <paramref name="rack"/>, live; null for a rack without rounds.</summary>
        internal static IReadOnlyDictionary<string, int>? Contents(AmmoRackBehaviour rack) => Synced(rack, GunOf(rack).Owner)?.Counts;

        /// <summary>The rounds to re-rack into <paramref name="rack"/>, best first; null for a rack without rounds.</summary>
        internal static List<string>? Wanted(AmmoRackBehaviour rack)
        {
            (IntPtr gun, CannonBlueprint? owner) = GunOf(rack);
            return Synced(rack, owner)?.Wanted(Selected(gun, owner));
        }

        /// <summary>A re-rack put a shell of <paramref name="round"/> in <paramref name="rack"/>.</summary>
        internal static void Put(AmmoRackBehaviour rack, string? round) => Synced(rack, GunOf(rack).Owner, round);

        /// <summary>
        /// Whether <paramref name="rack"/> holds its gun's next round, counting
        /// one its loader is carrying from it: else taking the last one would
        /// cancel its own load.
        /// </summary>
        internal static bool HoldsNext(AmmoRackBehaviour rack)
        {
            (IntPtr gun, CannonBlueprint? owner) = GunOf(rack);
            string? next = Selected(gun, owner);
            if (next == null)
                return false;
            RackMix? mix = Synced(rack, owner);
            if (mix == null)
                return false;
            if (mix.Count(next) > 0)
                return true;
            // Its loader carries the next round from this rack
            if (!carried.TryGetValue(gun, out (string Id, IntPtr Rack) carry))
                return false;
            return carry.Rack == rack.Pointer && carry.Id == next;
        }

        /// <summary>
        /// Whether <paramref name="rack"/> may give a shell: always in a
        /// re-rack, and for a load only if it holds the gun's next round and
        /// the loader isn't holding another. A refused load marks the gun waiting.
        /// </summary>
        internal static bool MayTake(AmmoRackBehaviour rack)
        {
            if (Reracking || RoundOf(rack) == null)
                return true;
            (IntPtr gun, CannonBlueprint? owner) = GunOf(rack);
            // The game may just have put the carried shell back in another rack, on its way to this one
            if (carried.TryGetValue(gun, out (string Id, IntPtr Rack) held))
            {
                AmmoRackBehaviour? from = LiveRack(held.Rack);
                if (from != null)
                    Synced(from, owner);
            }
            if (HoldsNext(rack))
            {
                // A mixed rack can hold the round just picked while its loader carries the one picked before
                if (!carried.TryGetValue(gun, out (string Id, IntPtr Rack) carry))
                    return true;
                if (carry.Id == Selected(gun, owner))
                    return true;
            }
            waiting.Add(gun);
            return false;
        }

        /// <summary>The behaviour of the known rack with behaviour <paramref name="rack"/>; null when it's gone.</summary>
        private static AmmoRackBehaviour? LiveRack(IntPtr rack)
        {
            if (!racks.TryGetValue(rack, out AmmoRack? component))
                return null;
            if (component == null)
                return null;
            return component.behaviour;
        }

        /// <summary>
        /// <paramref name="gun"/>'s load task no longer holds the shell its
        /// loader took; any the game put back in its rack is the round taken.
        /// </summary>
        internal static void Dropped(IntPtr gun)
        {
            if (!carried.Remove(gun, out (string Id, IntPtr Rack) carry))
                return;
            AmmoRackBehaviour? rack = LiveRack(carry.Rack);
            if (rack != null)
                Synced(rack, GunOf(rack).Owner, carry.Id);
        }

        /// <summary>A load from <paramref name="rack"/> was refused, so its gun's load task holds no shell.</summary>
        internal static void Refused(AmmoRackBehaviour rack) => Dropped(GunOf(rack).Gun);

        /// <summary>Whether <paramref name="gun"/>'s loader holds a shell it took from <paramref name="rack"/>.</summary>
        internal static bool Carries(IntPtr gun, AmmoRackBehaviour rack)
        {
            if (!carried.TryGetValue(gun, out (string Id, IntPtr Rack) carry))
                return false;
            return carry.Rack == rack.Pointer;
        }

        /// <summary>
        /// A shell left <paramref name="rack"/>: the round a re-rack moved, or
        /// for a load the gun's next round, which its loader now carries.
        /// </summary>
        internal static void Took(AmmoRackBehaviour rack)
        {
            (IntPtr gun, CannonBlueprint? owner) = GunOf(rack);
            RackMix? mix = MixOf(rack, owner);
            if (mix == null)
                return;
            string id = mix.Pick(Reracking ? Moving : Selected(gun, owner));
            mix.Sync(rack.AmountStored, id);
            Log($"{Name(rack.Pointer)} gave {id}{(Reracking ? " for a re-rack" : $" to gun {gun.ToInt64():X}")}, {rack.AmountStored} left");
            if (Reracking)
                return;
            waiting.Remove(gun);
            carried[gun] = (id, rack.Pointer);
            if (!Cannons.TryGetValue(gun, out Cannon? cannon))
                return;
            // The player's gun is about to load a different round
            if (PlayerVehicle.Contains(cannon) && id != Loaded(gun, Loadout.Owner(cannon)))
                DynamicSight.Clear();
        }

        /// <summary>
        /// After <paramref name="task"/>'s update: once its gun is loaded,
        /// the round its loader carried is the one loaded.
        /// </summary>
        internal static void Landed(LoadTask task)
        {
            IntPtr gun = task.target?.Pointer ?? IntPtr.Zero;
            if (task.State != LoadState.Loaded)
                return;
            if (!carried.Remove(gun, out (string Id, IntPtr Rack) carry))
                return;
            string id = carry.Id;
            bool changed = !loaded.TryGetValue(gun, out string? before) || before != id;
            loaded[gun] = id;
            Log($"gun {gun.ToInt64():X} loaded {id}");
            // That may have been the AI's last shell of its pick. Only now it's in the gun: picking again while the loader
            // carries it hides its rack from stagedAmmo, which puts the shell back and takes the new pick's, back and forth
            if (selected.TryGetValue(gun, out string? pick))
                RepickIfOut(gun, pick);
            if (changed)
                LoadedChanged(gun);
            // Its target's range moves between loads, and a rocket's aim speed goes by it
            else if (!players.Contains(gun))
                AiVelocity.Update(gun);
        }

        /// <summary>An AI gun with no shells of <paramref name="pick"/> left picks again.</summary>
        private static void RepickIfOut(IntPtr gun, string pick)
        {
            if (players.Contains(gun))
                return;
            if (!Cannons.TryGetValue(gun, out Cannon? ai))
                return;
            CannonBlueprint? owner = Loadout.Owner(ai);
            if (owner == null)
                return;
            if (Left(ai).GetValueOrDefault(pick) != 0)
                return;
            AiPick(gun, ai, owner, $"out of {Loadout.List(owner).FirstOrDefault(r => r.Id == pick).Name}");
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
            foreach ((IntPtr gun, Cannon cannon) in Cannons.ToList())
            {
                try
                {
                    if (cannon == null)
                    {
                        Forget(gun);
                        continue;
                    }
                    if (!PlayerVehicle.Contains(cannon))
                        continue;
                    CannonBlueprint? owner = Loadout.Owner(cannon);
                    if (owner == null)
                        continue;
                    if (Loadout.List(owner).Count > 0)
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
            foreach ((IntPtr gun, Cannon cannon) in Cannons.ToList())
                if (cannon == null)
                    Forget(gun);
            foreach ((IntPtr behaviour, AmmoRack rack) in racks.ToList())
                if (rack == null)
                    racks.Remove(behaviour);
            ForgetMixes();
        }

        /// <summary>Drops a gun destroyed with its vehicle, so the lists don't grow each battle.</summary>
        private static void Forget(IntPtr gun)
        {
            Cannons.Remove(gun);
            selected.Remove(gun);
            loaded.Remove(gun);
            carried.Remove(gun);
            waiting.Remove(gun);
            players.Remove(gun);
            AiVelocity.Games.Remove(gun);
            foreach ((IntPtr Shell, IntPtr Vehicle) key in gunOfShell.Where(g => g.Value == gun).Select(g => g.Key).ToList())
                gunOfShell.Remove(key);
        }

        /// <summary>Shells of each round id left in <paramref name="cannon"/>'s racks on its vehicle; those in ready racks also go in <paramref name="ready"/>.</summary>
        private static Dictionary<string, int> Left(Cannon cannon, Dictionary<string, int>? ready = null)
        {
            var left = new Dictionary<string, int>();
            UnityCoreModule::UnityEngine.Transform? vehicle = cannon.GetComponentInParent<Vehicle>()?.transform;
            if (vehicle == null)
                return left;
            foreach ((IntPtr behaviour, AmmoRack rack) in racks.ToList())
            {
                try
                {
                    if (rack == null)
                    {
                        racks.Remove(behaviour);
                        continue;
                    }
                    // Another gun's shell
                    if (rack.shellSlotBlueprintSlot?.Blueprint?.Pointer != cannon.ShellBlueprint?.Pointer)
                        continue;
                    // On another vehicle
                    if (!rack.transform.IsChildOf(vehicle))
                        continue;
                    IReadOnlyDictionary<string, int>? held = Contents(rack.behaviour);
                    if (held == null)
                        continue;
                    bool isReady = ready != null && Loadout.IsReady?.Invoke(rack.behaviour) == true;
                    foreach ((string id, int count) in held)
                    {
                        left[id] = left.GetValueOrDefault(id) + count;
                        if (isReady)
                            ready![id] = ready.GetValueOrDefault(id) + count;
                    }
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
            List<(IntPtr Gun, Cannon Cannon, CannonBlueprint Owner)> guns = PlayerGuns();
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
            List<(IntPtr Gun, Cannon Cannon, CannonBlueprint Owner)> guns = PlayerGuns();
            if (guns.Count == 0)
                return;
            (IntPtr gun, Cannon cannon, CannonBlueprint owner) = guns[Active(guns)];
            List<(string Id, string Name, string Type)> ids = Loadout.List(owner);
            if (n > ids.Count)
                return;
            selected[gun] = ids[n - 1].Id;
            // A pick before Selected first saw this gun would leave it out, and the AI's pick would replace it
            players.Add(gun);
            // Only a waiting gun: re-requesting one mid-reload starts a load with no shell taken, and its drop puts back a phantom (bug 25)
            if (waiting.Contains(gun))
                Loadout.RequestAmmo(cannon.behaviour);
            waiting.Remove(gun);
        }

        /// <summary>
        /// Each of the player's guns with rounds: its loaded and next round,
        /// its name, whether it's waiting for the next round to be in reach, whether the
        /// round keys pick for it, and each round with its icon key and its
        /// shells left in all its racks and in ready racks.
        /// </summary>
        internal static List<GunRow> Guns()
        {
            List<GunRow> guns = new List<GunRow>();
            List<(IntPtr Gun, Cannon Cannon, CannonBlueprint Owner)> player = PlayerGuns();
            int current = Active(player);
            for (int i = 0; i < player.Count; i++)
            {
                (IntPtr gun, Cannon cannon, CannonBlueprint owner) = player[i];
                var ready = new Dictionary<string, int>();
                Dictionary<string, int> left = Left(cannon, ready);
                List<RoundCell> rounds = new List<RoundCell>();
                foreach ((string Id, string Name, string Type) round in Loadout.List(owner))
                {
                    string icon = Loadout.IconOf(owner, round.Id, round.Type);
                    rounds.Add(new RoundCell(round.Id, round.Name, round.Type, icon, left.GetValueOrDefault(round.Id), ready.GetValueOrDefault(round.Id)));
                }
                guns.Add(new GunRow(gun, GunName(owner, i), Loaded(gun, owner), Selected(gun, owner), waiting.Contains(gun), i == current, rounds));
            }
            return guns;
        }

        /// <summary>The gun's blueprint name without its plugins' <c>[tags]</c>, or "Gun n" when that leaves nothing.</summary>
        private static string GunName(CannonBlueprint owner, int index)
        {
            string name = System.Text.RegularExpressions.Regex.Replace(owner.Name ?? "", @"\s*\[[^\]]*\]", "").Trim();
            return name.Length > 0 ? name : $"Gun {index + 1}";
        }
    }

    /// <summary>One of the player's guns on the ammo bar, from <see cref="Tracker.Guns"/>.</summary>
    internal readonly record struct GunRow(IntPtr Gun, string Name, string? Loaded, string? Next, bool Waiting, bool Active, List<RoundCell> Rounds);

    /// <summary>One round in a gun's ammo bar row: its icon key, and its shells left in all its racks and in ready racks.</summary>
    internal readonly record struct RoundCell(string Id, string Name, string Type, string Icon, int Left, int Ready);

    /// <summary>
    /// The rounds an ammo rack is designed with, stored as
    /// <c>{"round": id, "split": {id: slots}}</c> in the rack blueprint's
    /// description; see <see cref="RackMix"/>.
    /// </summary>
    internal static class RackRound
    {
        private static List<string> Ids(CannonBlueprint? owner) => Loadout.List(owner).Select(r => r.Id).ToList();

        /// <summary>The round <paramref name="rack"/> was given among <paramref name="owner"/>'s: its pick, or the first; null if there are none.</summary>
        internal static string? Get(AmmoRackBlueprint? rack, CannonBlueprint? owner)
        {
            List<string> ids = Ids(owner);
            return rack == null ? ids.FirstOrDefault() : RackMix.FillOf(Loadout.Parsed(rack), ids);
        }

        /// <summary>The design of <paramref name="rack"/> at <paramref name="capacity"/>, fully loaded; null without rounds.</summary>
        internal static RackMix? Design(AmmoRackBlueprint rack, CannonBlueprint? owner, int capacity) => RackMix.Read(Loadout.Parsed(rack), Ids(owner), capacity);

        /// <summary>
        /// Assigns <paramref name="count"/> slots to <paramref name="id"/>, with
        /// the rest of <paramref name="design"/> as it is. An old rack's whole
        /// count becomes its split. <c>"round"</c> stays the most assigned, for
        /// older versions, which read only that.
        /// </summary>
        internal static void SetCount(AmmoRackBlueprint rack, RackMix design, string id, int count)
        {
            JsonObject? root = Loadout.Read(rack);
            if (root == null)
                return;
            root.Remove("mix");
            var split = new JsonObject();
            string? most = null;
            int largest = 0;
            foreach ((string round, int assigned) in design.Targets)
            {
                int slots = round == id ? count : assigned;
                if (slots <= 0)
                    continue;
                split[round] = slots;
                if (slots > largest)
                    (most, largest) = (round, slots);
            }
            root["split"] = split;
            if (most != null)
                root["round"] = most;
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
            if (__instance.State != LoadState.Loading)
                return true;
            AmmoRackBehaviour? rack = __instance.rack?.TryCast<AmmoRackBehaviour>();
            if (rack == null)
                return true;
            if (Tracker.MayTake(rack))
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
            // The load was cancelled: the game's update would carry on with it
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

        // Cannon blueprint and round id, its "not available until" row
        private static readonly Dictionary<(IntPtr, string), StringProperty> notes = new();

        private static void Postfix(CannonEditor __instance, IGUILayout __0)
        {
            Cannon? cannon = __instance.Component;
            CannonBlueprint? owner = Loadout.Owner(cannon);
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (cannon == null)
                return;
            if (owner == null)
                return;
            if (drawer == null)
                return;
            // No plugin registered a round type
            if (Loadout.Types.Count == 0)
                return;
            if (!EditorGui.Section(drawer, "rounds.cannon", "Rounds", __instance.RequestRedraw))
                return;
            Action changed = () => cannon.blueprintSlot?.MarkModified();
            Loadout.redraw = __instance.RequestRedraw;
            foreach ((string id, string name, string type) in Loadout.List(owner))
            {
                if (!names.TryGetValue((owner.Pointer, id), out StringProperty? field))
                {
                    field = new StringProperty(
                        "Round name",
                        (Func<string>)(() => Loadout.List(owner).FirstOrDefault(r => r.Id == id).Name ?? ""),
                        (Action<string>)(value =>
                        {
                            Loadout.Rename(owner, id, value);
                            changed();
                        }),
                        "Shown in the HUD, the rack panel and the Delete button.");
                    names[(owner.Pointer, id)] = field;
                }
                drawer.InputField(field);
                int index = Loadout.Types.FindIndex(t => t.Type == type);
                // Types too new for the vehicle are hidden, except the round's own
                List<int> offered = new List<int>();
                for (int t = 0; t < Loadout.Types.Count; t++)
                {
                    string candidate = Loadout.Types[t].Type;
                    if (candidate == type || MadiLib.Introduced.Allowed(Loadout.YearOf(candidate)))
                        offered.Add(t);
                }
                List<string> labels = offered.Select(t => Loadout.Types[t].Label).ToList();
                EditorGui.Dropdown(drawer, "Type", labels, offered.IndexOf(index), i =>
                {
                    Loadout.SetType(owner, id, Loadout.Types[offered[i]].Type);
                    changed();
                    __instance.RequestRedraw();
                }, "What kind of round this is. Types newer than the vehicle's year are hidden (MadiLib's Historical availability).");
                if (!MadiLib.Introduced.Allowed(Loadout.YearOf(owner, id, type)))
                {
                    if (!notes.TryGetValue((owner.Pointer, id), out StringProperty? note))
                    {
                        note = EditorGui.Readout("Too new",
                            () => MadiLib.Introduced.Note(Loadout.YearOf(owner, id, Loadout.List(owner).FirstOrDefault(r => r.Id == id).Type ?? "")),
                            "This round is newer than the vehicle's year. It still fires; pick another type or a later year to clear this.");
                        notes[(owner.Pointer, id)] = note;
                    }
                    drawer.Readonly(note);
                }
                if (index >= 0)
                    Loadout.Types[index].Draw(drawer, cannon, id, changed);
                foreach (RoundDrawer fields in Loadout.Fields)
                    fields(drawer, cannon, id, changed);
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

    /// <summary>
    /// Adds to the ammo rack panel, with several rounds, a slider per round: the
    /// rack's slots assigned to it, up to its own and those unassigned. A
    /// readout shows what's left unassigned.
    /// </summary>
    [HarmonyPatch(typeof(AmmoRackEditor), nameof(AmmoRackEditor.OnGUI))]
    internal static class AmmoRackEditorPatch
    {
        // Rack, round id and name, its slider; the rack's unassigned readout
        private static readonly Dictionary<(IntPtr, string, string), FloatProperty> counts = new();
        private static readonly ObjectCache<StringProperty> unassigned = new();
        // The sliders of the panel last drawn; cached sliders outlive a redraw, which may add a round
        private static List<(string Id, FloatProperty Slider)> shown = new();

        private static RackMix? Design(AmmoRack rack) =>
            RackRound.Design(rack.Blueprint, Loadout.OwnerOf(rack.shellSlotBlueprintSlot?.Blueprint), rack.Capacity);

        private static void Postfix(AmmoRackEditor __instance, IGUILayout __0)
        {
            AmmoRack? rack = __instance.Component;
            if (rack == null)
                return;
            if (rack.Blueprint == null)
                return;
            List<(string Id, string Name, string Type)> rounds = Loadout.List(Loadout.OwnerOf(rack.shellSlotBlueprintSlot?.Blueprint));
            // One round has nothing to split
            if (rounds.Count < 2)
                return;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (drawer == null)
                return;
            StringProperty readout = unassigned.Get(rack, () => EditorGui.Readout(
                "Unassigned",
                () => UnassignedText(rack),
                "Slots for no round in particular: they hold an even balance of all rounds, at the start and when re-racked."));
            shown = new List<(string Id, FloatProperty Slider)>();
            foreach ((string id, string name, string _) in rounds)
            {
                if (!counts.TryGetValue((rack.Pointer, id, name), out FloatProperty? slider))
                {
                    slider = new FloatProperty(
                        name,
                        (Func<float>)(() => Design(rack)?.Targets.GetValueOrDefault(id) ?? 0),
                        (Action<float>)(value =>
                        {
                            RackMix? before = Design(rack);
                            if (before == null)
                                return;
                            RackRound.SetCount(rack.Blueprint, before, id, Math.Clamp((int)Math.Round(value), 0, before.Limit(id)));
                            rack.blueprintReference?.MarkModified();
                            Limits(rack);
                            EditorGui.Refresh(new Property[] { readout });
                        }),
                        "Slots in this rack for this round: it starts with them, and only this round is re-racked into them.")
                    {
                        Min = 0f,
                        Step = 1f
                    };
                    counts[(rack.Pointer, id, name)] = slider;
                }
                shown.Add((id, slider));
            }
            Limits(rack);
            foreach ((string Id, FloatProperty Slider) entry in shown)
                drawer.Float(entry.Slider);
            drawer.Readonly(readout);
        }

        /// <summary>The rack's slots for no round in particular, as the readout shows them.</summary>
        private static string UnassignedText(AmmoRack rack)
        {
            RackMix? mix = Design(rack);
            if (mix == null)
                return "";
            return $"{mix.Unassigned}";
        }

        /// <summary>Caps each slider at its round's own slots and those unassigned.</summary>
        private static void Limits(AmmoRack rack)
        {
            RackMix? mix = Design(rack);
            if (mix == null)
                return;
            foreach ((string id, FloatProperty slider) in shown)
            {
                // An empty 0..0 range drops the slider and leaves the pooled field's old text (another panel's value); the setter clamps to the real limit
                slider.Max = Math.Max(mix.Limit(id), 1);
                slider.OnPropertyChanged(PropertyChangeType.Limit);
                slider.OnPropertyChanged(PropertyChangeType.Value);
            }
        }
    }
}
