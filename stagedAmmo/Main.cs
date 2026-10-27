extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <c>[auto]</c>) if it has any, else racks within <see cref="ReadyDistance"/>
    /// of the breech. While the gun has a living loader who isn't loading it,
    /// they move shells inward into the closest ready rack with room, taking
    /// as long as the game's loader rules say that carry takes, times
    /// <see cref="ReRackTimeMultiplier"/>. Autoloaded guns load themselves, at
    /// each rack's tagged speed times <see cref="AutoloaderSpeed"/>; their
    /// loader only re-racks.
    /// </summary>
    [BepInPlugin("madi.stagedammo", "stagedAmmo", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ConfigEntry<float> ReadyDistance = null!;
        internal static ConfigEntry<float> ReRackTimeMultiplier = null!;
        internal static ConfigEntry<float> AutoloaderSpeed = null!;
        internal static ConfigEntry<bool> ShowIndicator = null!;
        internal static ManualLogSource Logger = null!;

        /// <summary>Whether the player's loaders re-rack; the keybind flips it. Other vehicles' always do.</summary>
        internal static bool PlayerReRack = true;

        private static readonly Regex tag = new(@"\[auto(?:\s*x\s*([0-9]*\.?[0-9]+))?\]", RegexOptions.IgnoreCase);

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
            new Harmony("madi.stagedammo").PatchAll();
            Hud.AddPanel(Indicator.Text);
            SettingsTab.AddSection("Staged ammo", ReadyDistance, ReRackTimeMultiplier, AutoloaderSpeed, ShowIndicator);
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

        /// <summary><paramref name="rackName"/> with its tag replaced by one for <paramref name="speed"/>, or removed for null.</summary>
        internal static string WithTag(string rackName, float? speed)
        {
            string untagged = tag.Replace(rackName, "").Trim();
            if (speed == null)
                return untagged;
            string newTag = Math.Abs(speed.Value - 1f) < 0.001f ? "[auto]" : $"[auto x{speed.Value.ToString("0.##", CultureInfo.InvariantCulture)}]";
            return $"{untagged} {newTag}".Trim();
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
            else if (now >= staging.NextFilter)
                Filter(finder, staging, now);

            // Elapsed since this gun last updated, so distant vehicles updated less often still run at full rate
            float elapsed = Math.Min(now - staging.LastSeen, 0.5f);
            staging.LastSeen = now;
            staging.LoaderEfficiency = LoaderEfficiency(contributors);
            staging.LoaderBusy = !staging.Auto && loadTask.State == LoadState.Loading;
            staging.Size = loadTask.Request.SizeID;
            // The game keeps loading from the rack it last picked, which may be a stowage rack picked before the finder was filtered
            if (loadTask.State != LoadState.Loading && loadTask.rack != null && !IsReady(loadTask.rack, staging))
                loadTask.rack = null;
            Restock(staging, elapsed);
            if (staging.Auto)
                LoadTaskUpdatePatch.Autoload(loadTask, elapsed);
        }

        private static bool IsReady(IAmmoSource rack, Staging staging)
        {
            for (int i = 0; i < staging.Ready.Length; i++)
                if (staging.Ready[i] && staging.Sources[i]?.AmmoSource?.Pointer == rack.Pointer)
                    return true;
            return false;
        }

        /// <summary>The GameObject of the gun <paramref name="loadTask"/> loads, which outlives a rebuild of its load task.</summary>
        internal static IntPtr? GunKey(LoadTask loadTask) => loadTask.target?.TryCast<WeaponBehaviour>()?.GameObject?.Pointer;

        /// <summary>
        /// The efficiency of the best crew member working one of the gun's load
        /// tasks, or 0 when none is. Dead or knocked-out crew stop working
        /// tasks, so this is 0 once every loader is down.
        /// </summary>
        private static float LoaderEfficiency(Il2CppReferenceArray<ILoadTaskContributor>? contributors)
        {
            float best = 0f;
            if (contributors == null)
                return best;
            foreach (ILoadTaskContributor contributor in contributors)
                if (contributor?.TryCast<LoadContributeTask>() is { OperateCount: > 0 } loader)
                    best = Math.Max(best, loader.efficiency);
            return best;
        }

        /// <summary>
        /// Works out the gun's ready racks, gives <paramref name="finder"/> only
        /// those, and notes whether it belongs to the player's vehicle.
        /// </summary>
        private static void Filter(VehicleAmmoRackFinder finder, Staging staging, float now)
        {
            int count = Math.Min(staging.Sources.Length, staging.Paths.Length);
            var auto = new bool[count];
            staging.Auto = false;
            for (int i = 0; i < count; i++)
            {
                // The finder only updates the paths it's shown, so keep hidden ones current too
                if (staging.Paths[i]?.RequiresUpdate == true)
                    staging.Paths[i].Update();
                auto[i] = Plugin.IsAuto(staging.Sources[i]);
                staging.Auto |= auto[i];
            }

            var readySources = new List<IAmmoSourceComponent>();
            var readyPaths = new List<VehicleComponentPath>();
            staging.Ready = new bool[count];
            for (int i = 0; i < count; i++)
            {
                staging.Ready[i] = staging.Auto ? auto[i] : Plugin.IsNear(staging.Paths[i]);
                if (!staging.Ready[i])
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
            if (racks[from]!.TakeShell())
                racks[target]!.LoadAmmo(1);
        }

        /// <summary>
        /// The closest rack to the breech, other than ready rack
        /// <paramref name="target"/>, holding a shell that fits it: any stowage
        /// rack, or a ready rack farther out. -1 if none.
        /// </summary>
        private static int NextRackOut(int target, AmmoRackBehaviour?[] racks, Staging staging)
        {
            AmmoRackBehaviour ready = racks[target]!;
            float targetDistance = staging.Paths[target].Distance;
            int nearest = -1;
            float distance = float.MaxValue;
            for (int j = 0; j < racks.Length; j++)
            {
                AmmoRackBehaviour? rack = racks[j];
                float d = staging.Paths[j].Distance;
                if (j == target || rack == null || rack.AmountStored == 0 || staging.Ready[j] && d <= targetDistance || d >= distance
                    || !rack.ShellSizeID.Equals(ready.ShellSizeID) || !rack.StoredTypeID.Equals(ready.StoredTypeID))
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
        private static void Postfix(AmmoRackEditor __instance, IGUILayout __0)
        {
            var name = __instance.properties?.Name;
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (name == null || drawer == null)
                return;
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
            drawer.Float(new FloatProperty(
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
}
