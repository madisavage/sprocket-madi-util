extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MadiLib;
using Properties;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.Weapons;
using Sprocket.WeaponFramework;
using VehicleDesigner.AmmunitionStorage;
using Rect = UnityCoreModule::UnityEngine.Rect;
using Time = UnityCoreModule::UnityEngine.Time;
using UnityEngine;

namespace StagedAmmo
{
    /// <summary>
    /// BepInEx entry point. Each gun has ready racks, the only ones it loads
    /// from, and stowage. Ready racks are its autoloader racks (name tagged
    /// <c>[auto]</c>) if it has any, else racks tagged <c>[ready]</c> if it has
    /// any, else racks within <see cref="ReadyDistance"/> of the breech. While
    /// the gun has a living loader who isn't loading it, they move shells
    /// inward into the closest ready rack with room (<c>[clip N]</c> at a time), taking
    /// as long as the game's loader rules say that carry takes, times
    /// <see cref="ReRackTimeMultiplier"/>. Autoloaded guns load themselves, at
    /// each rack's tagged speed times <see cref="AutoloaderSpeed"/>; their
    /// loader only re-racks.
    /// </summary>
    [BepInPlugin("madi.stagedammo", "stagedAmmo", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    [BepInDependency("madi.rounds", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BasePlugin
    {
        internal static ConfigEntry<float> ReadyDistance = null!;
        internal static ConfigEntry<float> ReRackTimeMultiplier = null!;
        internal static ConfigEntry<float> AutoloaderSpeed = null!;
        internal static ConfigEntry<bool> ShowIndicator = null!;
        internal static ConfigEntry<bool> LoadFromStowage = null!;
        internal static ManualLogSource Logger = null!;

        /// <summary>Whether the player's loaders re-rack; the keybind flips it. Other vehicles' always do.</summary>
        internal static bool PlayerReRack = true;

        // Tags match with the space before them, so removing one from mid-name leaves no double space
        private static readonly Regex tag = new(@"\s*\[auto(?:\s*x\s*([0-9]*\.?[0-9]+))?\]", RegexOptions.IgnoreCase);
        internal static readonly Regex ReadyTag = new(@"\s*\[ready\]", RegexOptions.IgnoreCase);
        internal static readonly Regex ClipTag = new(@"\s*\[clip\s*([0-9]+)\]", RegexOptions.IgnoreCase);

        public override void Load()
        {
            Logger = Log;
            ReadyDistance = Config.Bind("Staging", "Ready rack distance", 1.5f, new ConfigDescription(
                "Racks this many metres or closer (loader path) to the breech are ready racks.",
                new AcceptableValueRange<float>(0f, 100f), new SoftMax(10f), new Step(0.1f)));
            ReRackTimeMultiplier = Config.Bind("Staging", "Re-rack time multiplier", 1f, new ConfigDescription(
                "Scales the time to carry a shell between racks (pick up, walk, put down; from the game's loader rules).",
                new AcceptableValueRange<float>(0.1f, 100f), new SoftMax(5f), new Step(0.05f)));
            AutoloaderSpeed = Config.Bind("Autoloader", "Speed multiplier", 1f, new ConfigDescription(
                "Scales every autoloader's speed, on top of each rack's own; 1 is a loader at 100% efficiency.",
                new AcceptableValueRange<float>(0.1f, 100f), new SoftMax(10f), new Step(0.05f)));
            ShowIndicator = Config.Bind("Display", "Show indicator", true, "Show ready/stowed counts and re-rack progress for your vehicle's guns.");
            Keybinds.Register(this, "Staged ammo", "Toggle re-racking", "Alt+R", () => PlayerReRack = !PlayerReRack);
            LoadFromStowage = Config.Bind("Staging", "Load next round from stowage", true,
                "With rounds installed: when no ready rack holds a gun's next round, its loader fetches it from stowage, which takes as long as the walk. Off, the gun waits for re-racking to bring one to a ready rack.");
            LoadFromStowage.SettingChanged += (_, _) => LoadControllerUpdatePatch.RefilterAll();
            RoundsLink.Present = IL2CPPChainloader.Instance.Plugins.ContainsKey("madi.rounds");
            RoundsLink.ShareReady();
            new Harmony("madi.stagedammo").PatchAll();
            Hud.AddPanel(Indicator.Draw);
            SettingsTab.AddSection("Staged ammo", "Staged ammo", ReadyDistance, ReRackTimeMultiplier, AutoloaderSpeed, LoadFromStowage, ShowIndicator);
        }

        /// <summary>Whether the rack at the end of <paramref name="path"/> is near enough the breech to be a ready rack.</summary>
        internal static bool IsNear(VehicleComponentPath? path) => path != null && path.Distance <= ReadyDistance.Value;

        /// <summary>The speed in <paramref name="rackName"/>'s <c>[auto]</c> tag, or null when it isn't an autoloader rack.</summary>
        internal static float? RackSpeed(string? rackName)
        {
            Match match = rackName == null ? Match.Empty : tag.Match(rackName);
            if (!match.Success)
                return null;
            return match.Groups[1].Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) && speed > 0f
                ? speed
                : 1f;
        }

        internal static bool IsAuto(IAmmoSourceComponent? source) => RackSpeed(source?.AmmoSource?.RackIdentifier) != null;

        /// <summary>Whether <paramref name="rackName"/> is tagged <c>[ready]</c>, flagging it a ready rack by hand.</summary>
        internal static bool IsFlagged(string? rackName) => rackName != null && ReadyTag.IsMatch(rackName);

        /// <summary>Shells the loader re-racks into <paramref name="rackName"/> per carry: its <c>[clip N]</c> tag, else 1.</summary>
        internal static int ClipSize(string? rackName)
        {
            Match match = rackName == null ? Match.Empty : ClipTag.Match(rackName);
            return match.Success && int.TryParse(match.Groups[1].Value, out int clip) && clip > 0 ? clip : 1;
        }

        /// <summary><paramref name="rackName"/> with its tag replaced by one for <paramref name="speed"/>, or removed for null.</summary>
        internal static string WithTag(string rackName, float? speed) =>
            WithTag(rackName, tag, speed == null ? null
                : Math.Abs(speed.Value - 1f) < 0.001f ? "[auto]" : $"[auto x{speed.Value.ToString("0.##", CultureInfo.InvariantCulture)}]");

        /// <summary><paramref name="rackName"/> with any match of <paramref name="pattern"/> replaced by <paramref name="newTag"/>, or removed for null.</summary>
        internal static string WithTag(string rackName, Regex pattern, string? newTag)
        {
            string untagged = pattern.Replace(rackName, "").Trim();
            return newTag == null ? untagged : $"{untagged} {newTag}".Trim();
        }
    }

    /// <summary>One gun's rack finder: its full rack lists and restock state.</summary>
    internal sealed class Staging
    {
        public VehicleAmmoRackFinder Finder = null!;
        public Il2CppReferenceArray<IAmmoSourceComponent> Sources = null!;
        public Il2CppReferenceArray<VehicleComponentPath> Paths = null!;
        public bool[] Ready = Array.Empty<bool>();
        // Rack behaviours this gun marked ready, unmarked if it's dropped
        public List<IntPtr> ReadyRacks = new();
        public bool Auto;
        public IntPtr Shown;
        public ProjectileSizeID Size;
        public float NextFilter;
        // Which racks the loader was shown for the next round, to filter again when it changes
        public ulong ShownKey;
        public float LastSeen;
        public bool IsPlayer;
        // The gun's WeaponBehaviour, as rounds keys guns
        public IntPtr Weapon;
        // The ready rack being restocked, if any, and with rounds the round going into it
        public AmmoRackBehaviour? Target;
        public string? Round;

        public float Progress, Required;
        public bool HasTarget, LoaderBusy;
        public float LoaderEfficiency;
        public int ReadyShells, StowedShells, ReadyCapacity;
    }

    /// <summary>
    /// Once per vehicle frame, hides stowage racks from each gun's rack finder,
    /// restocks ready racks from stowage, and loads autoloaded guns.
    /// </summary>
    /// <remarks>
    /// The finder's rack lists are swapped for ready-only copies instead of
    /// patching <c>VehicleAmmoRackFinder.TryGet</c>: its <c>out RackPath</c>
    /// throws in the Il2CppInterop trampoline before any patch code runs.
    /// </remarks>
    [HarmonyPatch(typeof(VehicleWeaponLoadController), nameof(VehicleWeaponLoadController.Update))]
    internal static class LoadControllerUpdatePatch
    {
        // Finder, its staging; one Key:Value pair per gun's rack finder
        internal static readonly Dictionary<IntPtr, Staging> Staged = new();
        // Rack behaviour, how many guns it's a ready rack for, as of their last filter
        internal static readonly Dictionary<IntPtr, int> ReadyRacks = new();
        // Guns the game built no crew load task for (no loader), and their load task; one entry per gun
        internal static readonly List<(WeaponBehaviour Weapon, LoadTask Task)> Orphans = new();
        private static int orphanFrame = -1;
        // Gun GameObject, its current finder; re-entering the game from the designer rebuilds a gun's load task on the same GameObject
        private static readonly Dictionary<IntPtr, IntPtr> gunFinders = new();
        // Finders replaced by a newer one for the same gun, and when last seen; ignored so the old load task doesn't load or show twice
        private static readonly Dictionary<IntPtr, float> retired = new();
        private static float nextPrune;
        private static readonly List<IntPtr> dead = new();

        private static void Prefix(out Profile.Section __state) =>
            __state = Profile.Measure("stagedAmmo: load controller, game's Update included");

        private static void Postfix(VehicleWeaponLoadController __instance, Profile.Section __state)
        {
            using var total = __state;
            float now = Time.time;
            var tasks = __instance.loadTasks;
            if (tasks != null)
                foreach (var task in tasks)
                    if (task?.Task?.TryCast<LoadTask>() is { } loadTask)
                        Update(loadTask, task.Contributors, now);

            // Guns with no loader aren't in any controller; update them once a frame from whichever controller runs first
            if (orphanFrame == Time.frameCount)
                return;
            orphanFrame = Time.frameCount;
            if (now >= nextPrune)
            {
                nextPrune = now + 5f;
                Prune(now);
            }
            for (int i = Orphans.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (Orphans[i].Weapon.GameObject == null)
                        Orphans.RemoveAt(i);
                    else
                        Update(Orphans[i].Task, null, now);
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"Dropping gun without loader: {e.Message}");
                    Orphans.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Drops guns not updated for a while, with their vehicle, so the lists
        /// don't grow each battle. A dropped gun gets its full rack lists back,
        /// so if it's still alive it's staged again at its next update.
        /// </summary>
        private static void Prune(float now)
        {
            dead.Clear();
            foreach (var (finder, staging) in Staged)
                if (now - staging.LastSeen > 10f)
                    dead.Add(finder);
            foreach (IntPtr finder in dead)
            {
                Staging staging = Staged[finder];
                Unready(staging);
                try
                {
                    staging.Finder.sources = staging.Sources;
                    staging.Finder.paths = staging.Paths;
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"Couldn't give a dropped gun its racks back: {e.Message}");
                }
                Staged.Remove(finder);
            }
            if (dead.Count > 0)
                rackPaths.Clear();

            dead.Clear();
            foreach (var (finder, seen) in retired)
                if (now - seen > 10f)
                    dead.Add(finder);
            foreach (IntPtr finder in dead)
                retired.Remove(finder);

            dead.Clear();
            foreach (var (gun, finder) in gunFinders)
                if (!Staged.ContainsKey(finder) && !retired.ContainsKey(finder))
                    dead.Add(gun);
            foreach (IntPtr gun in dead)
                gunFinders.Remove(gun);
        }

        /// <summary>Takes back the ready marks <paramref name="staging"/>'s gun put on its racks.</summary>
        private static void Unready(Staging staging)
        {
            foreach (IntPtr rack in staging.ReadyRacks)
                if (ReadyRacks.TryGetValue(rack, out int guns) && guns > 1)
                    ReadyRacks[rack] = guns - 1;
                else
                    ReadyRacks.Remove(rack);
            staging.ReadyRacks.Clear();
        }

        /// <summary>Whether <paramref name="rack"/> is a ready rack for more than one gun.</summary>
        private static bool Shared(AmmoRackBehaviour rack) => ReadyRacks.GetValueOrDefault(rack.Pointer) > 1;

        /// <summary>Stages, restocks and, if autoloaded, loads one gun.</summary>
        private static void Update(LoadTask loadTask, Il2CppReferenceArray<ILoadTaskContributor>? contributors, float now)
        {
            using var _ = Profile.Measure("stagedAmmo: per gun update");
            VehicleAmmoRackFinder? finder = loadTask.rackLookup?.TryCast<VehicleAmmoRackFinder>();
            if (finder?.sources == null || finder.paths == null)
                return;
            if (retired.ContainsKey(finder.Pointer))
            {
                retired[finder.Pointer] = now;
                return;
            }

            if (!Staged.ContainsKey(finder.Pointer))
            {
                IntPtr gun = GunKey(loadTask) ?? finder.Pointer;
                if (gunFinders.TryGetValue(gun, out IntPtr old) && old != finder.Pointer)
                {
                    retired[old] = now;
                    if (Staged.Remove(old, out Staging? replaced))
                        Unready(replaced);
                }
                gunFinders[gun] = finder.Pointer;
            }

            ProjectileSizeID size = loadTask.Request.SizeID;
            // New finder, or the game rebuilt its lists: take them as the full lists
            if (!Staged.TryGetValue(finder.Pointer, out Staging? staging) || finder.sources.Pointer != staging.Shown)
            {
                if (staging != null)
                    Unready(staging);
                staging = new Staging
                {
                    Finder = finder, Sources = finder.sources, Paths = finder.paths, LastSeen = now,
                    Weapon = loadTask.target?.TryCast<WeaponBehaviour>()?.Pointer ?? IntPtr.Zero,
                    Size = size
                };
                Staged[finder.Pointer] = staging;
                Filter(finder, staging, now);
            }
            else if (!size.Equals(staging.Size))
            {
                // Which racks are the gun's own depends on its shell size
                staging.Size = size;
                Filter(finder, staging, now);
            }
            else if (RoundsLink.Present && ShownKey(staging) != staging.ShownKey)
            {
                // The game only asks for ammo again when a rack's contents change, not which racks it's shown
                Filter(finder, staging, now);
                RoundsLink.RequestAmmo(loadTask.target?.TryCast<WeaponBehaviour>());
            }
            else if (now >= staging.NextFilter)
                Filter(finder, staging, now);

            // Elapsed since this gun last updated, so distant vehicles updated less often still run at full rate
            float elapsed = Math.Min(now - staging.LastSeen, 0.5f);
            staging.LastSeen = now;
            staging.LoaderEfficiency = LoaderEfficiency(contributors, staging.LoaderEfficiency);
            staging.LoaderBusy = !staging.Auto && loadTask.State == LoadState.Loading;
            // The game keeps loading from the rack it last picked, which may be one picked before the finder was filtered
            if (loadTask.State != LoadState.Loading && loadTask.rack != null && !Shown(finder, loadTask.rack))
                loadTask.rack = null;
            Restock(staging, elapsed);
            if (staging.Auto)
                LoadTaskUpdatePatch.Autoload(loadTask, elapsed);
        }

        /// <summary>Whether <paramref name="rack"/> is one of the racks <paramref name="finder"/> shows the loader: its ready racks, or with rounds what <see cref="Filter"/> picked.</summary>
        private static bool Shown(VehicleAmmoRackFinder finder, IAmmoSource rack)
        {
            foreach (IAmmoSourceComponent source in finder.sources)
                if (source?.AmmoSource?.Pointer == rack.Pointer)
                    return true;
            return false;
        }

        /// <summary>Filters every gun's racks again on its next update, after a setting that changes them.</summary>
        internal static void RefilterAll()
        {
            foreach (Staging staging in Staged.Values)
                staging.NextFilter = 0f;
        }

        /// <summary>The GameObject of the gun <paramref name="loadTask"/> loads, which outlives a rebuild of its load task.</summary>
        internal static IntPtr? GunKey(LoadTask loadTask) => loadTask.target?.TryCast<WeaponBehaviour>()?.GameObject?.Pointer;

        /// <summary>
        /// The efficiency of the best crew member working one of the gun's load
        /// tasks, or 0 when none can. Crew with other roles leave the load task
        /// once the gun is loaded, so while one is away but still able
        /// (<c>EnableFraction</c> above 0) this keeps <paramref name="last"/>.
        /// </summary>
        private static float LoaderEfficiency(Il2CppReferenceArray<ILoadTaskContributor>? contributors, float last)
        {
            float best = 0f;
            bool able = false;
            if (contributors == null)
                return best;
            foreach (ILoadTaskContributor contributor in contributors)
                if (contributor?.TryCast<LoadContributeTask>() is { } loader)
                {
                    if (loader.OperateCount > 0)
                        best = Math.Max(best, loader.efficiency);
                    able |= loader.EnableFraction > 0f;
                }
            return best > 0f ? best : able ? last : 0f;
        }

        private static AmmoRackBehaviour? Rack(IAmmoSourceComponent? source) => source?.AmmoSource?.TryCast<AmmoRackBehaviour>();

        /// <summary>The ready racks holding the gun's next round, one bit each (the first 64).</summary>
        private static ulong HoldingNext(Staging staging)
        {
            ulong bits = 0;
            for (int i = 0; i < Math.Min(staging.Ready.Length, 64); i++)
                if (staging.Ready[i] && Rack(staging.Sources[i]) is { } rack && RoundsLink.HoldsNext(rack))
                    bits |= 1UL << i;
            return bits;
        }

        /// <summary>
        /// The ready racks holding the gun's next round, or with none, the
        /// stowage racks holding it (top bit set): what the loader is shown.
        /// </summary>
        private static ulong ShownKey(Staging staging) => ShownKey(staging, HoldingNext(staging));

        private static ulong ShownKey(Staging staging, ulong ready)
        {
            if (ready != 0 || staging.Auto || !Plugin.LoadFromStowage.Value)
                return ready;
            ulong stowed = 1UL << 63;
            for (int i = 0; i < Math.Min(staging.Sources.Length, 63); i++)
                if (Rack(staging.Sources[i]) is { } rack && RoundsLink.HoldsNext(rack))
                    stowed |= 1UL << i;
            return stowed;
        }

        /// <summary>
        /// Works out the gun's ready racks, gives <paramref name="finder"/> only
        /// those, and notes whether it belongs to the player's vehicle.
        /// </summary>
        private static void Filter(VehicleAmmoRackFinder finder, Staging staging, float now)
        {
            using var _ = Profile.Measure("stagedAmmo: Filter");
            int count = Math.Min(staging.Sources.Length, staging.Paths.Length);
            var auto = new bool[count];
            var flagged = new bool[count];
            bool anyFlagged = false;
            staging.Auto = false;
            // The finder lists every rack in the vehicle: another gun's [auto] or [ready] racks mustn't count for this one.
            // Size 0 until the gun's first request: count them all until then.
            for (int i = 0; i < count; i++)
            {
                // The finder only updates the paths it's shown, so keep hidden ones current too
                if (staging.Paths[i]?.RequiresUpdate == true)
                    staging.Paths[i].Update();
                bool fits = staging.Size.Value == 0 || staging.Sources[i]?.ShellSizeID.Equals(staging.Size) == true;
                auto[i] = fits && Plugin.IsAuto(staging.Sources[i]);
                staging.Auto |= auto[i];
                flagged[i] = fits && Plugin.IsFlagged(staging.Sources[i]?.AmmoSource?.RackIdentifier);
                anyFlagged |= flagged[i];
            }

            var readySources = new List<IAmmoSourceComponent>();
            var readyPaths = new List<VehicleComponentPath>();
            staging.Ready = new bool[count];
            Unready(staging);
            for (int i = 0; i < count; i++)
            {
                // Autoloader racks, else racks flagged [ready], else by distance
                staging.Ready[i] = staging.Auto ? auto[i] : anyFlagged ? flagged[i] : Plugin.IsNear(staging.Paths[i]);
                if (staging.Ready[i] && Rack(staging.Sources[i]) is { } rack)
                {
                    ReadyRacks[rack.Pointer] = ReadyRacks.GetValueOrDefault(rack.Pointer) + 1;
                    staging.ReadyRacks.Add(rack.Pointer);
                }
            }
            // With rounds, the loader is only shown ready racks holding the gun's next round; with none,
            // stowage racks holding it if allowed, else none
            ulong holdingNext = HoldingNext(staging);
            staging.ShownKey = ShownKey(staging, holdingNext);
            bool fromStowage = holdingNext == 0 && !staging.Auto && Plugin.LoadFromStowage.Value;
            for (int i = 0; i < count; i++)
            {
                AmmoRackBehaviour? rack = Rack(staging.Sources[i]);
                bool show = !RoundsLink.HasRounds(rack) ? staging.Ready[i]
                    : fromStowage ? RoundsLink.HoldsNext(rack!)
                    : staging.Ready[i] && i < 64 && (holdingNext & 1UL << i) != 0;
                if (!show)
                    continue;
                readySources.Add(staging.Sources[i]);
                readyPaths.Add(staging.Paths[i]);
            }

            var shown = new Il2CppReferenceArray<IAmmoSourceComponent>(readySources.ToArray());
            finder.sources = shown;
            finder.paths = new Il2CppReferenceArray<VehicleComponentPath>(readyPaths.ToArray());
            staging.Shown = shown.Pointer;
            staging.NextFilter = now + 1f;

            staging.IsPlayer = PlayerVehicle.Contains(staging.Sources.Length > 0 ? staging.Sources[0]?.Component : null);
        }

        /// <summary>
        /// Picks the closest ready rack with room that has a matching shell
        /// (same size and type) in a stowage rack or a ready rack farther out.
        /// Racks for another caliber are ignored once the gun has asked for a shell.
        /// While a living loader isn't loading the gun, builds up progress; at
        /// <see cref="ReRackTime"/> moves one shell in from the closest such
        /// rack, so ammo moves inward rack by rack. Also counts shells for the
        /// indicator.
        /// </summary>
        private static void Restock(Staging staging, float elapsed)
        {
            using var _ = Profile.Measure("stagedAmmo: Restock");
            var sources = staging.Sources;
            var paths = staging.Paths;
            int count = Math.Min(staging.Ready.Length, Math.Min(sources.Length, paths.Length));
            var racks = new AmmoRackBehaviour?[count];
            // With rounds, each rack's shells per round, found once rather than for every rack pair
            var contents = new IReadOnlyDictionary<string, int>?[count];
            int readyShells = 0, stowedShells = 0, readyCapacity = 0;
            for (int i = 0; i < count; i++)
            {
                racks[i] = sources[i]?.AmmoSource?.TryCast<AmmoRackBehaviour>();
                // Size 0 until the gun's first request
                if (staging.Size.Value != 0 && racks[i]?.ShellSizeID.Equals(staging.Size) == false)
                    racks[i] = null;
                if (racks[i] == null)
                    continue;
                contents[i] = RoundsLink.Contents(racks[i]!);
                if (staging.Ready[i])
                {
                    readyShells += racks[i]!.AmountStored;
                    readyCapacity += racks[i]!.Capacity;
                }
                else
                    stowedShells += racks[i]!.AmountStored;
            }
            staging.ReadyShells = readyShells;
            staging.StowedShells = stowedShells;
            staging.ReadyCapacity = readyCapacity;

            int target = -1, from = -1;
            string? round = null;
            float targetDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                AmmoRackBehaviour? ready = racks[i];
                if (ready == null || !staging.Ready[i] || ready.AmountStored >= ready.Capacity || paths[i].Distance >= targetDistance)
                    continue;
                var (next, id) = NextRackOut(i, racks, contents, staging);
                if (next < 0)
                    continue;
                target = i;
                from = next;
                round = id;
                targetDistance = paths[i].Distance;
            }

            staging.HasTarget = target >= 0;
            staging.Target = target >= 0 ? racks[target] : null;
            staging.Round = round;
            if (target < 0)
            {
                staging.Progress = 0f;
                return;
            }
            if (staging.IsPlayer && !Plugin.PlayerReRack || staging.LoaderEfficiency <= 0f || staging.LoaderBusy)
                return;

            staging.Required = ReRackTime(sources[from]!.Component, sources[target]!.Component) * Plugin.ReRackTimeMultiplier.Value / staging.LoaderEfficiency;
            staging.Progress += elapsed;
            if (staging.Progress < staging.Required)
                return;
            staging.Progress = 0f;
            int clip = Plugin.ClipSize(sources[target]!.AmmoSource?.RackIdentifier);
            AmmoRackBehaviour goal = racks[target]!;
            for (int n = 0; n < clip && goal.AmountStored < goal.Capacity; n++)
            {
                // A clip may be of different rounds
                var (src, id) = n == 0 ? (from, round) : NextRackOut(target, racks, contents, staging);
                if (src < 0 || !RoundsLink.Move(racks[src]!, goal, id))
                    break;
            }
        }

        /// <summary>
        /// A shell to bring to ready rack <paramref name="target"/>, and with
        /// rounds which round: the first the rack wants that any rack can give.
        /// </summary>
        private static (int From, string? Round) NextRackOut(int target, AmmoRackBehaviour?[] racks, IReadOnlyDictionary<string, int>?[] contents, Staging staging)
        {
            if (RoundsLink.Wanted(racks[target]!) is not { } wanted)
                return (NextRackOut(target, racks, staging, contents, null), null);
            foreach (string id in wanted)
            {
                int next = NextRackOut(target, racks, staging, contents, id);
                if (next >= 0)
                    return (next, id);
            }
            return (-1, null);
        }

        /// <summary>
        /// The closest rack to the breech, other than ready rack
        /// <paramref name="target"/>, holding a shell that fits it: any stowage
        /// rack, or a ready rack farther out, except on an autoloaded gun: it
        /// loads from every autoloader rack, and guns sharing them would pass
        /// shells back and forth between them. Nor from a ready rack shared with
        /// another gun, for the same reason. -1 if none.
        /// </summary>
        /// <param name="id">With rounds, the round it must hold; null for a rack without rounds, which can only fit another.</param>
        private static int NextRackOut(int target, AmmoRackBehaviour?[] racks, Staging staging, IReadOnlyDictionary<string, int>?[] contents, string? id)
        {
            AmmoRackBehaviour ready = racks[target]!;
            float targetDistance = staging.Paths[target].Distance;
            int nearest = -1;
            float distance = float.MaxValue;
            for (int j = 0; j < racks.Length; j++)
            {
                AmmoRackBehaviour? rack = racks[j];
                float d = staging.Paths[j].Distance;
                if (j == target || rack == null || rack.AmountStored == 0 || staging.Ready[j] && (staging.Auto || d <= targetDistance || Shared(rack)) || d >= distance
                    || !rack.ShellSizeID.Equals(ready.ShellSizeID) || !rack.StoredTypeID.Equals(ready.StoredTypeID)
                    || (id == null ? contents[j] != null : contents[j]?.GetValueOrDefault(id) is null or 0))
                    continue;
                nearest = j;
                distance = d;
            }
            return nearest;
        }

        // (From rack, to rack), crew path between them; one Key:Value pair per pair re-racked between
        private static readonly Dictionary<(IntPtr, IntPtr), VehicleComponentPath> rackPaths = new();

        /// <summary>
        /// Seconds to carry one shell from <paramref name="from"/> to
        /// <paramref name="to"/> under the game's loader rules: pick it up, walk
        /// the crew path between the racks, and put it down (timed as a pickup).
        /// </summary>
        private static float ReRackTime(VehicleComponent from, VehicleComponent to)
        {
            AmmoRack? fromRack = from.TryCast<AmmoRack>();
            AmmoRack? toRack = to.TryCast<AmmoRack>();
            if (fromRack == null || toRack == null)
                return 0f;

            if (!rackPaths.TryGetValue((from.Pointer, to.Pointer), out VehicleComponentPath? path))
            {
                path = new VehicleComponentPath(from, to)
                {
                    ALocalPosition = fromRack.LoadLocalPosition,
                    BLocalPosition = toRack.LoadLocalPosition
                };
                rackPaths[(from.Pointer, to.Pointer)] = path;
                path.Update();
            }
            else if (path.RequiresUpdate)
                path.Update();

            ShellLoadInfo info = fromRack.LoadInfo;
            CannonLoadTechnique technique = LoaderRules.GetLoadTechnique(ref info);
            return 2f * LoaderRules.CalculatePickupTime(ref info, technique) + LoaderRules.CalculateTravelTime(technique, path.Distance, ref info);
        }
    }

    /// <summary>
    /// On autoloaded guns, blocks the crew's load updates and runs the
    /// autoloader's instead, so the loader only re-racks.
    /// </summary>
    [HarmonyPatch(typeof(LoadTask), nameof(LoadTask.Update))]
    internal static class LoadTaskUpdatePatch
    {
        private static bool autoloading;
        private static bool loggedBlock;

        /// <summary>Advances <paramref name="loadTask"/> by <paramref name="elapsed"/> seconds at autoloader speed.</summary>
        internal static void Autoload(LoadTask loadTask, float elapsed)
        {
            using var _ = Profile.Measure("stagedAmmo: Autoload (game LoadTask.Update)");
            autoloading = true;
            try
            {
                loadTask.Update(0f, 0f, 0f, elapsed);
            }
            finally
            {
                autoloading = false;
            }
        }

        private static bool Prefix(LoadTask __instance, ref float travelSpeed, ref float manipulationSpeed, ref float prepareTime)
        {
            using var _ = Profile.Measure("stagedAmmo: LoadTask.Update prefix");
            if (autoloading)
            {
                // Speed follows the rack being loaded from
                float multiplier = Plugin.AutoloaderSpeed.Value * (Plugin.RackSpeed(__instance.rack?.RackIdentifier) ?? 1f);
                travelSpeed = LoaderRules.Speed * multiplier;
                manipulationSpeed = multiplier;
                prepareTime = LoaderRules.GetPrepareTime(1f) / multiplier;
                return true;
            }
            VehicleAmmoRackFinder? finder = __instance.rackLookup?.TryCast<VehicleAmmoRackFinder>();
            if (finder == null || !LoadControllerUpdatePatch.Staged.TryGetValue(finder.Pointer, out Staging? staging) || !staging.Auto)
                return true;
            if (!loggedBlock)
            {
                loggedBlock = true;
                Plugin.Logger.LogInfo("Blocking crew loading on an autoloaded gun");
            }
            return false;
        }
    }

    /// <summary>
    /// When a vehicle is assembled, notes the guns the game gave no crew load
    /// task (no loader), so <see cref="LoadControllerUpdatePatch"/> can update them.
    /// </summary>
    [HarmonyPatch(typeof(WeaponLoadControllerAssemblyStage), nameof(WeaponLoadControllerAssemblyStage.Execute))]
    internal static class LoadControllerAssemblyPatch
    {
        /// <summary><paramref name="weapon"/>'s GameObject, or null once it's destroyed.</summary>
        private static IntPtr? SafeGameObject(WeaponBehaviour weapon)
        {
            try
            {
                return weapon.GameObject?.Pointer;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Postfix(WeaponLoadControllerAssemblyStage __instance)
        {
            if (__instance.sources == null)
                return;
            foreach (IWeaponSource source in __instance.sources)
            {
                WeaponBehaviour? weapon = source?.Weapon;
                if (weapon == null)
                    continue;
                // Re-assembly (back from the designer) reuses the gun's GameObject: replace its old entry
                IntPtr? gun = weapon.GameObject?.Pointer;
                LoadControllerUpdatePatch.Orphans.RemoveAll(o => o.Weapon.Pointer == weapon.Pointer || gun != null && SafeGameObject(o.Weapon) == gun);
                if ((weapon.LoadTaskContributors?.Length ?? 0) == 0 && weapon.loadTask?.TryCast<LoadTask>() is { } loadTask)
                    LoadControllerUpdatePatch.Orphans.Add((weapon, loadTask));
            }
        }
    }

    /// <summary>
    /// Adds an <b>Autoloader</b> checkbox and, when ticked, a <b>Reload speed</b>
    /// field to the ammo rack editor panel. Both edit the <c>[auto]</c> tag in
    /// the rack's name, so the setting saves with the vehicle.
    /// </summary>
    [HarmonyPatch(typeof(AmmoRackEditor), nameof(AmmoRackEditor.OnGUI))]
    internal static class AmmoRackEditorOnGuiPatch
    {
        // Rack name property, its reload speed field
        private static readonly ObjectCache<FloatProperty> speeds = new();
        // Rack name property, its clip size field
        private static readonly ObjectCache<FloatProperty> clips = new();

        private static void Postfix(AmmoRackEditor __instance, IGUILayout __0)
        {
            var name = __instance.properties?.Name;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (name == null || drawer == null)
                return;
            drawer.ToggleField(
                "Ready rack",
                Plugin.IsFlagged(name.Value),
                (Action<bool>)(ready =>
                {
                    name.Value = Plugin.WithTag(name.Value ?? "", Plugin.ReadyTag, ready ? "[ready]" : null);
                    __instance.RequestRedraw();
                }),
                "The gun loads from this rack. Once any of a gun's racks is ticked, only ticked racks are ready, however far; autoloader racks still come first. Adds or removes [ready] in the rack's name.");
            drawer.Float(clips.Get(name, () => new FloatProperty(
                "Clip size",
                (Func<float>)(() => Plugin.ClipSize(name.Value)),
                (Action<float>)(value =>
                {
                    int clip = Math.Max(1, (int)Math.Round(value));
                    name.Value = Plugin.WithTag(name.Value ?? "", Plugin.ClipTag, clip == 1 ? null : $"[clip {clip}]");
                }),
                "Shells the loader re-racks into this rack per carry, if it's a ready rack. Saved in the rack's name, as [clip 5] for 5.")
            {
                Min = 1f,
                Max = 100f,
                SoftMin = 1f,
                SoftMax = 20f,
                UseSoftLimits = true,
                Step = 1f
            }));
            float? speed = Plugin.RackSpeed(name.Value);
            drawer.ToggleField(
                "Autoloader",
                speed != null,
                (Action<bool>)(auto =>
                {
                    name.Value = Plugin.WithTag(name.Value ?? "", auto ? 1f : null);
                    __instance.RequestRedraw();
                }),
                "The gun loads itself from this rack; its loader only re-racks. Adds or removes [auto] in the rack's name.");
            if (speed == null)
                return;
            FloatProperty reloadSpeed = speeds.Get(name, () => new FloatProperty(
                "Reload speed",
                (Func<float>)(() => Plugin.RackSpeed(name.Value) ?? 1f),
                (Action<float>)(value => name.Value = Plugin.WithTag(name.Value ?? "", value)),
                "Autoloader speed when loading from this rack; 1 is a loader at 100% efficiency. Saved in the rack's name, as [auto x2] for 2.")
            {
                Min = 0.1f,
                Max = 100f,
                SoftMin = 0.1f,
                SoftMax = 10f,
                UseSoftLimits = true,
                Step = 0.05f
            });
            drawer.Float(reloadSpeed);
        }
    }

    /// <summary>
    /// The HUD panel: one row per gun on the player's vehicle with a bar of
    /// ready shells against ready capacity, stowed shells, and a re-rack
    /// progress bar beside an icon coloured by what the loader is doing.
    /// Guns on rounds' ammo bar are left out; rounds draws their re-rack
    /// progress and <see cref="Badge"/> instead.
    /// </summary>
    internal static class Indicator
    {
        internal enum State { Idle, Running, Off, Paused, NoLoader, StowageEmpty }

        private const float Icon = 40f, BarWidth = 140f, Pad = 4f, RowHeight = Icon + 2 * Pad;

        internal static State Of(Staging staging) =>
            !staging.HasTarget ? (staging.StowedShells == 0 ? State.StowageEmpty : State.Idle)
            : !Plugin.PlayerReRack ? State.Off
            : staging.LoaderEfficiency <= 0f ? State.NoLoader
            : staging.LoaderBusy ? State.Paused
            : State.Running;

        /// <summary>The player's gun with WeaponBehaviour <paramref name="gun"/>, if staged and updated in the last second.</summary>
        internal static Staging? Find(IntPtr gun)
        {
            float now = Time.time;
            foreach (Staging staging in LoadControllerUpdatePatch.Staged.Values)
                if (staging.Weapon == gun && staging.IsPlayer && now - staging.LastSeen <= 1f)
                    return staging;
            return null;
        }

        /// <summary>Draws a row per player gun not on rounds' ammo bar, upward from (<paramref name="left"/>, <paramref name="bottom"/>); returns the height.</summary>
        internal static float Draw(float left, float bottom)
        {
            using var _ = Profile.Measure("stagedAmmo: HUD indicator");
            if (!Plugin.ShowIndicator.Value)
                return 0f;
            float top = bottom;
            float now = Time.time;
            // Bottom row first, so guns read top to bottom in the order found
            var rows = new List<Staging>();
            foreach (Staging staging in LoadControllerUpdatePatch.Staged.Values)
                if (staging.IsPlayer && now - staging.LastSeen <= 1f && !RoundsLink.OnBar(staging.Weapon))
                    rows.Add(staging);
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                Staging staging = rows[i];
                top -= RowHeight;
                var row = new Rect(left, top, 2 * Pad + 14f + Icon + Pad + BarWidth + Pad, RowHeight);
                GUI.Box(row, "");
                HudDraw.Label(new Rect(row.x + Pad, row.y, 14f, row.height), $"{i + 1}", 14, HudDraw.Idle);
                Badge(staging, new Rect(row.x + Pad + 14f + Pad, row.y + Pad, Icon, Icon), false);

                float x = row.x + 2 * Pad + 14f + Icon + Pad;
                HudDraw.Label(new Rect(x, row.y + Pad, BarWidth, 16f), $"ready {staging.ReadyShells}/{staging.ReadyCapacity}", 13, HudDraw.Idle, TextAnchor.MiddleLeft);
                HudDraw.Label(new Rect(x, row.y + Pad, BarWidth, 16f), $"stowed {staging.StowedShells}", 13, HudDraw.Idle, TextAnchor.MiddleRight);
                HudDraw.Bar(new Rect(x, row.y + Pad + 18f, BarWidth, 8f), staging.ReadyCapacity > 0 ? (float)staging.ReadyShells / staging.ReadyCapacity : 0f, HudDraw.Next);
                State state = Of(staging);
                HudDraw.Bar(new Rect(x, row.y + Pad + 30f, BarWidth, 6f), state is State.Running or State.Paused ? Progress(staging) : 0f, Color(state));
                if (i > 0)
                    top -= Pad;
            }
            return bottom - top;
        }

        /// <summary>Re-rack progress, 0-1.</summary>
        internal static float Progress(Staging staging) => staging.Required > 0f ? staging.Progress / staging.Required : 0f;

        /// <summary>Rounds' ammo bar column for <paramref name="gun"/>: the icon, and below it a short state.</summary>
        internal static void Badge(IntPtr gun, Rect column)
        {
            if (Find(gun) is { } staging)
                Badge(staging, column, true);
        }

        /// <summary>The loader's icon, coloured by its state; with <paramref name="caption"/>, a short state under it.</summary>
        private static void Badge(Staging staging, Rect r, bool caption)
        {
            State state = Of(staging);
            string resource = state == State.StowageEmpty ? "stowage.png" : staging.Auto && state != State.NoLoader ? "autoloader.png" : "loader.png";
            if (HudDraw.Icon(typeof(Plugin).Assembly, resource) is { } icon)
                HudDraw.Image(new Rect(r.x, r.y, r.width, r.width), icon, Color(state));
            if (!caption)
                return;
            string text = state switch
            {
                State.Running => $"{Math.Max(staging.Required - staging.Progress, 0f):0.0}s",
                State.Paused => "LOAD",
                State.Off => "OFF",
                State.NoLoader => "CREW",
                State.StowageEmpty => "EMPTY",
                _ => "",
            };
            HudDraw.Label(new Rect(r.x - Pad, r.y + r.width + 2f, r.width + 2 * Pad, 16f), text, 11, Color(state));
        }

        private static UnityCoreModule::UnityEngine.Color Color(State state) => state switch
        {
            State.Running => HudDraw.Ok,
            State.Paused => HudDraw.Next,
            State.Off => HudDraw.Off,
            State.NoLoader or State.StowageEmpty => HudDraw.Blocked,
            _ => HudDraw.Idle,
        };
    }

    /// <summary>
    /// Rounds (madi.rounds), if installed: re-racks bring a ready rack the
    /// rounds its slots are assigned to, and an even balance for the rest. Calls into
    /// rounds are in methods of their own, so nothing loads rounds.dll when it
    /// isn't there.
    /// </summary>
    internal static class RoundsLink
    {
        internal static bool Present;

        /// <summary>Moves a shell (with rounds, of <paramref name="round"/>) from <paramref name="from"/> to <paramref name="to"/>; false if there was none to take.</summary>
        internal static bool Move(AmmoRackBehaviour from, AmmoRackBehaviour to, string? round)
        {
            if (Present)
                return MoveWithRounds(from, to, round);
            if (!from.TakeShell())
                return false;
            to.LoadAmmo(1);
            return true;
        }

        internal static bool HoldsNext(AmmoRackBehaviour rack) => Present && HoldsNextWithRounds(rack);

        /// <summary>With rounds, <paramref name="rack"/>'s shells per round; null without.</summary>
        internal static IReadOnlyDictionary<string, int>? Contents(AmmoRackBehaviour rack) => Present ? ContentsWithRounds(rack) : null;

        /// <summary>With rounds, the rounds to bring to <paramref name="rack"/>, best first; null without.</summary>
        internal static IReadOnlyList<string>? Wanted(AmmoRackBehaviour rack) => Present ? WantedWithRounds(rack) : null;

        internal static bool HasRounds(AmmoRackBehaviour? rack) => Present && rack != null && HasRoundsWithRounds(rack);

        /// <summary>Whether rounds' ammo bar shows gun (a WeaponBehaviour pointer), so the panel leaves it out.</summary>
        internal static bool OnBar(IntPtr gun) => Present && OnBarWithRounds(gun);

        /// <summary>Tells rounds which racks are ready racks, and what re-racking is doing, for its ammo bar.</summary>
        internal static void ShareReady()
        {
            if (Present)
                ShareReadyWithRounds();
        }

        internal static void RequestAmmo(WeaponBehaviour? weapon)
        {
            if (Present)
                RequestAmmoWithRounds(weapon);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ShareReadyWithRounds()
        {
            Rounds.Loadout.IsReady = rack => LoadControllerUpdatePatch.ReadyRacks.ContainsKey(rack.Pointer);
            Rounds.Loadout.GunBadge = Indicator.Badge;
            Rounds.Loadout.Reracking = gun =>
                Indicator.Find(gun) is { Target: not null, Round: { } round } staging && Indicator.Of(staging) is Indicator.State.Running or Indicator.State.Paused
                    ? (round, Indicator.Progress(staging)) : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool OnBarWithRounds(IntPtr gun) => Rounds.Loadout.OnBar(gun);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RequestAmmoWithRounds(WeaponBehaviour? weapon) => Rounds.Loadout.RequestAmmo(weapon);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool MoveWithRounds(AmmoRackBehaviour from, AmmoRackBehaviour to, string? round) => Rounds.Loadout.Rerack(from, to, round);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HoldsNextWithRounds(AmmoRackBehaviour rack) => Rounds.Loadout.HoldsNext(rack);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IReadOnlyDictionary<string, int>? ContentsWithRounds(AmmoRackBehaviour rack) => Rounds.Loadout.Contents(rack);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IReadOnlyList<string>? WantedWithRounds(AmmoRackBehaviour rack) => Rounds.Loadout.Wanted(rack);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HasRoundsWithRounds(AmmoRackBehaviour rack) => Rounds.Loadout.HasRounds(rack);
    }
}
