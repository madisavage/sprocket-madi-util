extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MadiLib;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.CrewSystems;
using Sprocket.Vehicles.Weapons;
using Sprocket.WeaponFramework;
using Time = UnityCoreModule::UnityEngine.Time;

namespace StagedAmmo
{
    /// <summary>
    /// BepInEx entry point. Racks within <see cref="ReadyDistance"/> of a gun's
    /// breech are its ready racks; the loader only takes shells from those. While
    /// the gun has a living loader who isn't loading it, they move shells inward
    /// into the closest ready rack with room, taking as long as the game's
    /// loader rules say that carry takes, times <see cref="ReRackTimeMultiplier"/>.
    /// </summary>
    [BepInPlugin("madi.stagedammo", "stagedAmmo", "26.10.1")]
    [BepInDependency("madi.madilib")]
    public sealed class Plugin : BasePlugin
    {
        internal static ConfigEntry<float> ReadyDistance = null!;
        internal static ConfigEntry<float> ReRackTimeMultiplier = null!;
        internal static ConfigEntry<bool> ShowIndicator = null!;

        public override void Load()
        {
            ReadyDistance = Config.Bind("Staging", "Ready rack distance", 1.5f, new ConfigDescription(
                "Racks this many metres or closer (loader path) to the breech are ready racks.",
                new AcceptableValueRange<float>(0f, 10f), new Step(0.1f)));
            ReRackTimeMultiplier = Config.Bind("Staging", "Re-rack time multiplier", 1f, new ConfigDescription(
                "Scales the time to carry a shell between racks (pick up, walk, put down; from the game's loader rules).",
                new AcceptableValueRange<float>(0.1f, 5f), new Step(0.05f)));
            ShowIndicator = Config.Bind("Display", "Show indicator", true, "Show ready/stowed counts and re-rack progress for your vehicle's guns.");
            new Harmony("madi.stagedammo").PatchAll();
            Hud.AddPanel(Indicator.Text);
            SettingsTab.AddSection("Staged ammo", ReadyDistance, ReRackTimeMultiplier, ShowIndicator);
        }

        /// <summary>Whether the rack at the end of <paramref name="path"/> counts as a ready rack.</summary>
        internal static bool IsReady(VehicleComponentPath? path) => path != null && path.Distance <= ReadyDistance.Value;
    }

    /// <summary>One gun's rack finder: its full rack lists and restock state.</summary>
    internal sealed class Staging
    {
        public Il2CppReferenceArray<IAmmoSourceComponent> Sources = null!;
        public Il2CppReferenceArray<VehicleComponentPath> Paths = null!;
        public IntPtr Shown;
        public float NextFilter;
        public float LastSeen;
        public bool IsPlayer;

        public float Progress, Required;
        public bool HasTarget, LoaderBusy;
        public float LoaderEfficiency;
        public int ReadyShells, StowedShells;
    }

    /// <summary>
    /// Once per vehicle frame, hides stowage racks from each gun's rack finder
    /// and restocks ready racks from stowage.
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

        private static void Postfix(VehicleWeaponLoadController __instance)
        {
            var tasks = __instance.loadTasks;
            if (tasks == null)
                return;
            float now = Time.time;
            foreach (var task in tasks)
            {
                LoadTask? loadTask = task?.Task?.TryCast<LoadTask>();
                VehicleAmmoRackFinder? finder = loadTask?.rackLookup?.TryCast<VehicleAmmoRackFinder>();
                if (loadTask == null || finder?.sources == null || finder.paths == null)
                    continue;

                // New finder, or the game rebuilt its lists: take them as the full lists
                if (!Staged.TryGetValue(finder.Pointer, out Staging? staging) || finder.sources.Pointer != staging.Shown)
                {
                    staging = new Staging { Sources = finder.sources, Paths = finder.paths, LastSeen = now };
                    Staged[finder.Pointer] = staging;
                    Filter(finder, staging, now);
                }
                else if (now >= staging.NextFilter)
                    Filter(finder, staging, now);

                // Elapsed since this gun last updated, so distant vehicles updated less often still restock at full rate
                float elapsed = Math.Min(now - staging.LastSeen, 0.5f);
                staging.LastSeen = now;
                staging.LoaderEfficiency = LoaderEfficiency(task!.Contributors);
                staging.LoaderBusy = loadTask.State == LoadState.Loading;
                Restock(staging, elapsed);
            }
        }

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
        /// Gives <paramref name="finder"/> only the ready racks from its full
        /// lists, and notes whether it belongs to the player's vehicle.
        /// </summary>
        private static void Filter(VehicleAmmoRackFinder finder, Staging staging, float now)
        {
            var readySources = new List<IAmmoSourceComponent>();
            var readyPaths = new List<VehicleComponentPath>();
            for (int i = 0; i < staging.Sources.Length && i < staging.Paths.Length; i++)
            {
                // The finder only updates the paths it's shown, so keep hidden ones current too
                if (staging.Paths[i]?.RequiresUpdate == true)
                    staging.Paths[i].Update();
                if (!Plugin.IsReady(staging.Paths[i]))
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
        /// (same size and type) in a rack farther out. While a living loader
        /// isn't loading the gun, builds up progress; at
        /// <see cref="ReRackTime"/> moves one shell in from the next
        /// rack out, ready or stowage, so ammo moves inward rack by rack. Also
        /// counts shells for the indicator.
        /// </summary>
        private static void Restock(Staging staging, float elapsed)
        {
            var sources = staging.Sources;
            var paths = staging.Paths;
            int count = Math.Min(sources.Length, paths.Length);
            var racks = new AmmoRackBehaviour?[count];
            int readyShells = 0, stowedShells = 0;
            for (int i = 0; i < count; i++)
            {
                racks[i] = sources[i]?.AmmoSource?.TryCast<AmmoRackBehaviour>();
                if (racks[i] == null)
                    continue;
                if (Plugin.IsReady(paths[i]))
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
                if (ready == null || !Plugin.IsReady(paths[i]) || ready.AmountStored >= ready.Capacity || paths[i].Distance >= targetDistance)
                    continue;
                int next = NextRackOut(ready, paths[i].Distance, racks, paths);
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
            if (staging.LoaderEfficiency <= 0f || staging.LoaderBusy)
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
        /// The closest rack farther from the breech than <paramref name="readyDistance"/>
        /// holding a shell that fits <paramref name="ready"/>, or null.
        /// </summary>
        private static int NextRackOut(AmmoRackBehaviour ready, float readyDistance, AmmoRackBehaviour?[] racks, Il2CppReferenceArray<VehicleComponentPath> paths)
        {
            int nearest = -1;
            float distance = float.MaxValue;
            for (int j = 0; j < racks.Length; j++)
            {
                AmmoRackBehaviour? rack = racks[j];
                if (rack == null || rack.AmountStored == 0 || paths[j].Distance <= readyDistance || paths[j].Distance >= distance
                    || !rack.ShellSizeID.Equals(ready.ShellSizeID) || !rack.StoredTypeID.Equals(ready.StoredTypeID))
                    continue;
                nearest = j;
                distance = paths[j].Distance;
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
                    : staging.LoaderEfficiency <= 0f ? "no loader"
                    : staging.LoaderBusy ? "re-rack paused, loading"
                    : $"re-racking {staging.Progress:0.0}/{staging.Required:0.0}s";
                if (text.Length > 0)
                    text.Append('\n');
                text.Append($"Gun {++gun}: ready {staging.ReadyShells}, stowed {staging.StowedShells}, {status}");
                if (staging.LoaderEfficiency > 0f)
                    text.Append($" (loader {staging.LoaderEfficiency:P0})");
            }
            return text.ToString();
        }
    }
}
