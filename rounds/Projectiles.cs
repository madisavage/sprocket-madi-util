using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MadiLib;
using Sprocket.Blueprints;
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Weapons.Cannons;

namespace Rounds
{
    /// <summary>
    /// Each round's own projectile type: registered with the game once per
    /// round's settings, and fired in place of the gun's own type. Shells in
    /// flight keep their own definition, so guns firing different rounds of
    /// the same shell don't change each other's.
    /// </summary>
    /// <remarks>
    /// The game works muzzle velocity out from the definition's diameter and
    /// length during the shot, so the definition is in launch state (the
    /// game's diameter, <see cref="RoundShape.LaunchLength"/>) only while its
    /// gun fires, and in flight state otherwise.
    /// </remarks>
    internal static class Projectiles
    {
        internal sealed record Entry(ProjectileDefinition Def, RoundShape Shape, float GameDiameter, int GameId, RoundHit? Hit, CannonBlueprint Owner, string Id);

        // Game projectile GUID, round JSON and version, the round's projectile type
        private static readonly Dictionary<string, Il2CppSystem.Guid> guids = new();
        // Definition, its round
        private static readonly Dictionary<IntPtr, Entry> entries = new();
        // Flight mass and diameter, the round; all a hit's penetrator tells of its projectile
        internal static readonly Dictionary<(float Mass, float Diameter), Entry> Hits = new();
        private static int version;

        internal static void Invalidate()
        {
            version++;
            guids.Clear();
        }

        /// <summary>
        /// The projectile the gun with <paramref name="gun"/> (its behaviour)
        /// fires for its loaded round (or round <paramref name="round"/>), made
        /// on first use; null for the game's own <paramref name="game"/>.
        /// </summary>
        internal static Entry? For(IntPtr gun, ProjectileDefinition game, string? round = null)
        {
            if (!Tracker.Cannons.TryGetValue(gun, out Cannon? cannon) || cannon.ShellBlueprint is not { } shell || Loadout.Owner(cannon) is not { } owner
                || (round ?? Tracker.Loaded(gun, owner)) is not { } id || Loadout.Raw(owner, id) is not { } raw)
                return null;
            var type = Loadout.Types.FirstOrDefault(t => t.Type == raw.Type);
            if (type.Projectile == null || IProjectileTypeRegister.Instance is not { } register)
                return null;

            string key = $"{game.Guid}|{raw.Json}|{version}";
            if (guids.TryGetValue(key, out var known) && register.TryGet(known, out IProjectileType existing)
                && existing?.TryCast<ProjectileDefinition>() is { } made && entries.TryGetValue(made.Pointer, out Entry? entry))
                return entry;

            var functions = GameFunctions(shell, game);
            if (type.Projectile(game, cannon, id, functions) is not { } shape)
                return null;
            var guid = Il2CppSystem.Guid.Parse(new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToString());
            var list = new Il2CppSystem.Collections.Generic.List<ProjectileFunctionDefinition>();
            foreach (ProjectileFunctionDefinition function in functions)
                list.Add(function);
            IProjectileType? created = register.TryGet(guid, out IProjectileType again) ? again
                // Named by the round alone: Dynamic Gun Sight labels rounds by type name, cut to 8 characters by default
                : register.Create(Loadout.List(owner).FirstOrDefault(r => r.Id == id).Name ?? game.Name, guid, shape.Mass, shape.Diameter, shape.Length,
                    game.MinSpeed, game.Lifetime, list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<ProjectileFunctionDefinition>>());
            if (created?.TryCast<ProjectileDefinition>() is not { } def)
                return null;
            register.AddUser(guid);

            float mass = shape.Mass;
            while (Hits.ContainsKey((mass, shape.Diameter)) || mass == game.Mass && shape.Diameter == game.Diameter)
                mass = MathF.BitIncrement(mass);
            def.Mass = mass;
            def.Diameter = shape.Diameter;
            def.Length = shape.Length;
            def.dragConstant = shape.Drag;
            entry = new Entry(def, shape, game.Diameter, game.ID.Value, type.Hit, owner, id);
            entries[def.Pointer] = entry;
            Hits[(mass, shape.Diameter)] = entry;
            guids[key] = guid;
            return entry;
        }

        /// <summary>The round of the projectile type <paramref name="id"/>, if it's one of ours.</summary>
        internal static Entry? Of(int id) =>
            IProjectileTypeRegister.Instance is { } register && register.TryGet(new ProjectileTypeID(id), out IProjectileType type)
            && type?.TryCast<ProjectileDefinition>() is { } def && entries.TryGetValue(def.Pointer, out Entry? entry) ? entry : null;

        /// <summary>Puts <paramref name="entry"/>'s definition in launch or flight state.</summary>
        internal static void Size(Entry entry, bool launch)
        {
            entry.Def.Diameter = launch ? entry.GameDiameter : entry.Shape.Diameter;
            entry.Def.Length = launch ? entry.Shape.LaunchLength : entry.Shape.Length;
        }

        /// <summary>The function definitions the game made <paramref name="game"/> from, in order.</summary>
        private static List<ProjectileFunctionDefinition> GameFunctions(ShellSlotBlueprint shell, ProjectileDefinition game)
        {
            string guid = game.Guid.ToString();
            ShellBlueprint? projectile = shell.generatedProjectileBlueprint?.FirstOrDefault(p => p.ProjectileTypeGuid.ToString() == guid);
            return projectile?.functionDefinitions?.ToList() ?? new();
        }
    }

    /// <summary>Fires the loaded round's own projectile type, in launch state for the shot.</summary>
    [HarmonyPatch(typeof(CannonBehaviour), nameof(CannonBehaviour.FireInternal))]
    internal static class FirePatch
    {
        private static bool warned;

        private static void Prefix(CannonBehaviour __instance, ref ProjectileTypeID __0, out Projectiles.Entry? __state)
        {
            __state = null;
            Tracker.Fired(__instance.Pointer);
            IProjectileDefinitionSource? source = __instance.projectileFactory?.TryCast<ProjectileRegister>()?.definitionSource;
            ProjectileDefinition? game = source?.GetProjectileDefinition(__0);
            if (game == null || Projectiles.For(__instance.Pointer, game) is not { } entry)
                return;
            if (source!.GetProjectileDefinition(entry.Def.ID)?.Pointer != entry.Def.Pointer)
            {
                if (!warned)
                    Plugin.Logger.LogError($"The gun can't fire projectile type {entry.Def.ID.Value} ({entry.Def.Name}); rounds fire the game's shell.");
                warned = true;
                return;
            }
            Projectiles.Size(entry, launch: true);
            __0 = entry.Def.ID;
            __state = entry;
            DynamicSight.Fired(__instance.Pointer, entry);
        }

        private static void Postfix(Projectiles.Entry? __state)
        {
            if (__state != null)
                Projectiles.Size(__state, launch: false);
        }
    }

    /// <summary>
    /// Gives each cannon its loaded round's muzzle velocity, which the gunner
    /// AI leads and drops its shots by. The game caches the full-bore
    /// shell's on the cannon when it is built.
    /// </summary>
    [HarmonyPatch(typeof(Cannon), nameof(Cannon.Build))]
    internal static class AiVelocity
    {
        private static void Postfix(Cannon __instance)
        {
            if (__instance.behaviour != null)
                Update(__instance.behaviour.Pointer);
        }

        /// <summary>Sets the cannon of <paramref name="gun"/> (its behaviour) to its loaded round's velocity, or the game's for its own shell.</summary>
        internal static void Update(IntPtr gun)
        {
            // An unloaded gun's type is -1, which the game throws on; its first load updates it
            if (!Tracker.Cannons.TryGetValue(gun, out Cannon? cannon) || cannon.Blueprint is not { } blueprint || cannon.behaviour is not { } behaviour
                || behaviour.LoadedProjectileTypeID.Value < 0
                || behaviour.projectileFactory?.TryCast<ProjectileRegister>()?.definitionSource?.GetProjectileDefinition(behaviour.LoadedProjectileTypeID) is not { } game)
                return;
            float velocity = blueprint.MuzzleVelocity;
            if (Projectiles.For(gun, game) is { } entry)
                velocity *= Fire(blueprint, entry.Shape.LaunchLength / game.Length) / Fire(blueprint, 1f);
            cannon.muzzleVelocity = velocity;
        }

        /// <summary>Muzzle velocity of a projectile <paramref name="length"/> times the full-bore one's length, from the game's interior ballistics, as the round editors work it out.</summary>
        private static float Fire(CannonBlueprint gun, float length) =>
            ShellProperties.CalculateFireInfo(gun.BoreLength, gun.Caliber, 3f * gun.Caliber * length, gun.PropellantLength, gun.OperatingPressure).MuzzleVelocity;
    }

    /// <summary>
    /// Compatibility with Dynamic Gun Sight (sprocket.dynamicsight). Its
    /// <c>AmmoCatalog</c> caches each projectile type's mass, drag and
    /// predicted muzzle velocity by the type in the gun's racks, which is the
    /// game's; it's pointed at the loaded round's type instead, and cleared
    /// whenever one of the player's guns loads a different round.
    /// </summary>
    internal static class DynamicSight
    {
        private static readonly Type? catalog = Type.GetType("SprocketDynamicSight.Game.AmmoCatalog, SprocketDynamicSight");
        private static readonly System.Reflection.MethodInfo? clearAll = catalog == null ? null : AccessTools.Method(catalog, "ClearAll");
        // Round types a player gun has fired since the sight last cleared its cache
        private static readonly HashSet<IntPtr> seen = new();

        internal static void Patch(Harmony harmony)
        {
            if (catalog == null)
                return;
            harmony.Patch(AccessTools.Method(catalog, "Load"), prefix: new HarmonyMethod(typeof(DynamicSight), nameof(LoadPrefix)));
            harmony.Patch(AccessTools.Method(catalog, "RecordMeasurement"), prefix: new HarmonyMethod(typeof(DynamicSight), nameof(MeasurePrefix)));
            harmony.Patch(AccessTools.Method(catalog, "TryPredictVelocity"),
                prefix: new HarmonyMethod(typeof(DynamicSight), nameof(PredictPrefix)),
                postfix: new HarmonyMethod(typeof(DynamicSight), nameof(PredictPostfix)));
            Loadout.LoadedChanged += cannon =>
            {
                if (PlayerVehicle.Contains(cannon))
                    Clear();
            };
        }

        internal static void Clear()
        {
            seen.Clear();
            clearAll?.Invoke(null, null);
        }

        /// <summary>
        /// A gun fired <paramref name="entry"/>'s type: the first time one of
        /// the player's does since the cache was cleared, clear it, as it may
        /// hold the game's type from before the round's existed.
        /// </summary>
        internal static void Fired(IntPtr gun, Projectiles.Entry entry)
        {
            if (clearAll != null && !seen.Contains(entry.Def.Pointer) && Tracker.Cannons.TryGetValue(gun, out var cannon) && PlayerVehicle.Contains(cannon))
            {
                Clear();
                seen.Add(entry.Def.Pointer);
            }
        }

        /// <summary>
        /// <c>RecordMeasurement(int typeId, float velocity)</c>: the sight looks
        /// measured muzzle velocity up by the type in the gun's racks, the
        /// game's, so a round's shots are recorded under that.
        /// </summary>
        private static void MeasurePrefix(ref int __0)
        {
            if (Projectiles.Of(__0) is { } entry)
                __0 = entry.GameId;
        }

        /// <summary><c>Load(int typeId)</c>: the game's type of one of the player's guns reads as its loaded round's.</summary>
        private static void LoadPrefix(ref int __0)
        {
            foreach (var (gun, cannon, owner) in Tracker.PlayerGuns())
            {
                if (cannon.behaviour?.LoadedProjectileTypeID.Value != __0
                    || cannon.behaviour.projectileFactory?.TryCast<ProjectileRegister>()?.definitionSource?.GetProjectileDefinition(cannon.behaviour.LoadedProjectileTypeID) is not { } game)
                    continue;
                // The round being loaded, so the sight is ready by the time it lands
                if (Projectiles.For(gun, game, Tracker.Upcoming(gun, owner)) is { } entry)
                    __0 = entry.Def.ID.Value;
                return;
            }
        }

        /// <summary><c>TryPredictVelocity(RoundInfo, CannonBehaviour)</c> runs the charge, so a round's type is in launch state for it.</summary>
        private static void PredictPrefix(object __0, out Projectiles.Entry? __state)
        {
            __state = Projectiles.Of(Traverse.Create(__0).Field<int>("TypeId").Value);
            if (__state != null)
                Projectiles.Size(__state, launch: true);
        }

        private static void PredictPostfix(Projectiles.Entry? __state)
        {
            if (__state != null)
                Projectiles.Size(__state, launch: false);
        }
    }
}
