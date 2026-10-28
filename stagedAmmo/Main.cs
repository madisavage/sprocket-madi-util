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
using Time = UnityCoreModule::UnityEngine.Time;

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

        private static readonly Regex tag = new(@"\[auto(?:\s*x\s*([0-9]*\.?[0-9]+))?\]", RegexOptions.IgnoreCase);
        internal static readonly Regex ReadyTag = new(@"\[ready\]", RegexOptions.IgnoreCase);
        internal static readonly Regex ClipTag = new(@"\[clip\s*([0-9]+)\]", RegexOptions.IgnoreCase);

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
            Keybinds.Register(this, "Toggle re-racking", "Alt+R", () => PlayerReRack = !PlayerReRack);
            LoadFromStowage = Config.Bind("Staging", "Load next round from stowage", true,
                "With rounds installed: when no ready rack holds a gun's next round, its loader fetches it from stowage, which takes as long as the walk. Off, the gun waits for re-racking to bring one to a ready rack.");
            LoadFromStowage.SettingChanged += (_, _) => LoadControllerUpdatePatch.RefilterAll();
            RoundsLink.Present = IL2CPPChainloader.Instance.Plugins.ContainsKey("madi.rounds");
            RoundsLink.ShareReady();
            new Harmony("madi.stagedammo").PatchAll();
            Hud.AddPanel(Indicator.Text);
            SettingsTab.AddSection("Staged ammo", ReadyDistance, ReRackTimeMultiplier, AutoloaderSpeed, LoadFromStowage, ShowIndicator);
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
        public Il2CppReferenceArray<IAmmoSourceComponent> Sources = null!;
        public Il2CppReferenceArray<VehicleComponentPath> Paths = null!;
        public bool[] Ready = Array.Empty<bool>();
        public bool Auto;
        public IntPtr Shown;
        public ProjectileSizeID Size;
        public float NextFilter;
        // Which racks the loader was shown for the next round, to filter again when it changes
        public ulong ShownKey;
        public float LastSeen;
        public bool IsPlayer;

        public float Progress, Required;
        public bool HasTarget, LoaderBusy;
        public float LoaderEfficiency;
        public int ReadyShells, StowedShells;
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
        // Rack behaviours that are ready racks, as of their gun's last filter
        internal static readonly HashSet<IntPtr> ReadyRacks = new();
        // Guns the game built no crew load task for (no loader), and their load task; one entry per gun
        internal static readonly List<(WeaponBehaviour Weapon, LoadTask Task)> Orphans = new();
        private static int orphanFrame = -1;
        // Gun GameObject, its current finder; re-entering the game from the designer rebuilds a gun's load task on the same GameObject
        private static readonly Dictionary<IntPtr, IntPtr> gunFinders = new();
        // Finders replaced by a newer one for the same gun; ignored so the old load task doesn't load or show twice
        private static readonly HashSet<IntPtr> retired = new();

        private static void Postfix(VehicleWeaponLoadController __instance)
        {
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

        /// <summary>Stages, restocks and, if autoloaded, loads one gun.</summary>
        private static void Update(LoadTask loadTask, Il2CppReferenceArray<ILoadTaskContributor>? contributors, float now)
        {
            VehicleAmmoRackFinder? finder = loadTask.rackLookup?.TryCast<VehicleAmmoRackFinder>();
            if (finder?.sources == null || finder.paths == null || retired.Contains(finder.Pointer))
                return;

            if (!Staged.ContainsKey(finder.Pointer))
            {
                IntPtr gun = GunKey(loadTask) ?? finder.Pointer;
                if (gunFinders.TryGetValue(gun, out IntPtr old) && old != finder.Pointer)
                {
                    retired.Add(old);
                    Staged.Remove(old);
                }
                gunFinders[gun] = finder.Pointer;
            }

            // New finder, or the game rebuilt its lists: take them as the full lists
            if (!Staged.TryGetValue(finder.Pointer, out Staging? staging) || finder.sources.Pointer != staging.Shown)
            {
                staging = new Staging { Sources = finder.sources, Paths = finder.paths, LastSeen = now };
                Staged[finder.Pointer] = staging;
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
            staging.Size = loadTask.Request.SizeID;
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
            int count = Math.Min(staging.Sources.Length, staging.Paths.Length);
            var auto = new bool[count];
            var flagged = new bool[count];
            bool anyFlagged = false;
            staging.Auto = false;
            for (int i = 0; i < count; i++)
            {
                // The finder only updates the paths it's shown, so keep hidden ones current too
                if (staging.Paths[i]?.RequiresUpdate == true)
                    staging.Paths[i].Update();
                auto[i] = Plugin.IsAuto(staging.Sources[i]);
                staging.Auto |= auto[i];
                flagged[i] = Plugin.IsFlagged(staging.Sources[i]?.AmmoSource?.RackIdentifier);
                anyFlagged |= flagged[i];
            }

            var readySources = new List<IAmmoSourceComponent>();
            var readyPaths = new List<VehicleComponentPath>();
            staging.Ready = new bool[count];
            for (int i = 0; i < count; i++)
            {
                // Autoloader racks, else racks flagged [ready], else by distance
                staging.Ready[i] = staging.Auto ? auto[i] : anyFlagged ? flagged[i] : Plugin.IsNear(staging.Paths[i]);
                if (Rack(staging.Sources[i]) is { } rack)
                {
                    if (staging.Ready[i])
                        ReadyRacks.Add(rack.Pointer);
                    else
                        ReadyRacks.Remove(rack.Pointer);
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
            var sources = staging.Sources;
            var paths = staging.Paths;
            int count = Math.Min(staging.Ready.Length, Math.Min(sources.Length, paths.Length));
            var racks = new AmmoRackBehaviour?[count];
            int readyShells = 0, stowedShells = 0;
            for (int i = 0; i < count; i++)
            {
                racks[i] = sources[i]?.AmmoSource?.TryCast<AmmoRackBehaviour>();
                // Size 0 until the gun's first request
                if (staging.Size.Value != 0 && racks[i]?.ShellSizeID.Equals(staging.Size) == false)
                    racks[i] = null;
                if (racks[i] == null)
                    continue;
                if (staging.Ready[i])
                    readyShells += racks[i]!.AmountStored;
                else
                    stowedShells += racks[i]!.AmountStored;
            }
            staging.ReadyShells = readyShells;
            staging.StowedShells = stowedShells;

            int target = -1, from = -1;
            float targetDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                AmmoRackBehaviour? ready = racks[i];
                if (ready == null || !staging.Ready[i] || ready.AmountStored >= ready.Capacity || paths[i].Distance >= targetDistance)
                    continue;
                int next = NextRackOut(i, racks, staging);
                if (next < 0)
                    continue;
                target = i;
                from = next;
                targetDistance = paths[i].Distance;
            }

            staging.HasTarget = target >= 0;
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
            RoundsLink.Rerack(() =>
            {
                AmmoRackBehaviour ready = racks[target]!;
                for (int n = 0; n < clip && ready.AmountStored < ready.Capacity; n++)
                {
                    int src = n == 0 ? from : NextRackOut(target, racks, staging);
                    if (src < 0 || !racks[src]!.TakeShell())
                        break;
                    ready.LoadAmmo(1);
                }
            });
        }

        /// <summary>
        /// The closest rack to the breech, other than ready rack
        /// <paramref name="target"/>, holding a shell that fits it: any stowage
        /// rack, or a ready rack farther out, except on an autoloaded gun: it
        /// loads from every autoloader rack, and guns sharing them would pass
        /// shells back and forth between them. With rounds installed, a rack
        /// holding the gun's next round comes first. -1 if none.
        /// </summary>
        private static int NextRackOut(int target, AmmoRackBehaviour?[] racks, Staging staging)
        {
            int next = NextRackOut(target, racks, staging, RoundsLink.HoldsNext);
            return next >= 0 ? next : NextRackOut(target, racks, staging, _ => true);
        }

        private static int NextRackOut(int target, AmmoRackBehaviour?[] racks, Staging staging, Func<AmmoRackBehaviour, bool> holds)
        {
            AmmoRackBehaviour ready = racks[target]!;
            float targetDistance = staging.Paths[target].Distance;
            int nearest = -1;
            float distance = float.MaxValue;
            for (int j = 0; j < racks.Length; j++)
            {
                AmmoRackBehaviour? rack = racks[j];
                float d = staging.Paths[j].Distance;
                if (j == target || rack == null || rack.AmountStored == 0 || staging.Ready[j] && (staging.Auto || d <= targetDistance) || d >= distance || !holds(rack)
                    || !rack.ShellSizeID.Equals(ready.ShellSizeID) || !rack.StoredTypeID.Equals(ready.StoredTypeID) || !RoundsLink.SameRound(rack, ready))
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
    /// The HUD panel: one line per gun on the player's vehicle with ready and
    /// stowed shells and what the loader is doing.
    /// </summary>
    internal static class Indicator
    {
        /// <summary>The panel text, or null to hide it.</summary>
        internal static string? Text()
        {
            if (!Plugin.ShowIndicator.Value)
                return null;

            var text = new StringBuilder();
            int gun = 0;
            float now = Time.time;
            foreach (Staging staging in LoadControllerUpdatePatch.Staged.Values)
            {
                if (!staging.IsPlayer || now - staging.LastSeen > 1f)
                    continue;
                string status = !staging.HasTarget ? (staging.StowedShells == 0 ? "stowage empty" : "racks full")
                    : !Plugin.PlayerReRack ? "re-rack off"
                    : staging.LoaderEfficiency <= 0f ? "no loader"
                    : staging.LoaderBusy ? "re-rack paused, loading"
                    : $"re-racking {staging.Progress:0.0}/{staging.Required:0.0}s";
                if (text.Length > 0)
                    text.Append('\n');
                text.Append($"Gun {++gun}{(staging.Auto ? " (autoloader)" : "")}: ready {staging.ReadyShells}, stowed {staging.StowedShells}, {status}");
                if (staging.LoaderEfficiency > 0f)
                    text.Append($" (loader {staging.LoaderEfficiency:P0})");
            }
            return text.ToString();
        }

    }

    /// <summary>
    /// Rounds (madi.rounds), if installed: re-racks only move shells between
    /// racks of the same round, and racks holding a gun's next round are
    /// emptied into the ready racks first. Calls into rounds are in methods of their own, so nothing loads
    /// rounds.dll when it isn't there.
    /// </summary>
    internal static class RoundsLink
    {
        internal static bool Present;

        internal static void Rerack(Action move)
        {
            if (Present)
                RerackWithRounds(move);
            else
                move();
        }

        internal static bool HoldsNext(AmmoRackBehaviour rack) => Present && HoldsNextWithRounds(rack);

        /// <summary>Whether a shell may move from <paramref name="from"/> to <paramref name="to"/>: with rounds, only between racks of the same round.</summary>
        internal static bool SameRound(AmmoRackBehaviour from, AmmoRackBehaviour to) => !Present || SameRoundWithRounds(from, to);

        internal static bool HasRounds(AmmoRackBehaviour? rack) => Present && rack != null && HasRoundsWithRounds(rack);

        /// <summary>Tells rounds which racks are ready racks, for its ammo bar.</summary>
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
        private static void ShareReadyWithRounds() => Rounds.Loadout.IsReady = rack => LoadControllerUpdatePatch.ReadyRacks.Contains(rack.Pointer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RequestAmmoWithRounds(WeaponBehaviour? weapon) => Rounds.Loadout.RequestAmmo(weapon);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RerackWithRounds(Action move) => Rounds.Loadout.Rerack(move);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HoldsNextWithRounds(AmmoRackBehaviour rack) => Rounds.Loadout.HoldsNext(rack);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SameRoundWithRounds(AmmoRackBehaviour from, AmmoRackBehaviour to) => Rounds.Loadout.RoundOf(from) == Rounds.Loadout.RoundOf(to);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HasRoundsWithRounds(AmmoRackBehaviour rack) => Rounds.Loadout.HasRounds(rack);
    }
}
