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
            if (!Tracker.Cannons.TryGetValue(gun, out Cannon? cannon))
                return null;
            ShellSlotBlueprint? shell = cannon.ShellBlueprint;
            if (shell == null)
                return null;
            CannonBlueprint? owner = Loadout.Owner(cannon);
            if (owner == null)
                return null;
            // The round asked for, else the loaded one
            string? id = round ?? Tracker.Loaded(gun, owner);
            if (id == null)
                return null;
            (string Json, string Type)? raw = Loadout.Raw(owner, id);
            if (raw == null)
                return null;
            // The round's type's projectile and hit; none for a type no plugin registered
            RoundProjectile? projectile = null;
            RoundHit? hit = null;
            int typeIndex = Loadout.Types.FindIndex(t => t.Type == raw.Value.Type);
            if (typeIndex >= 0)
            {
                projectile = Loadout.Types[typeIndex].Projectile;
                hit = Loadout.Types[typeIndex].Hit;
            }
            // Nothing would change the game's projectile
            if (projectile == null && Loadout.Modifiers.Count == 0)
                return null;
            IProjectileTypeRegister? register = IProjectileTypeRegister.Instance;
            if (register == null)
                return null;

            string key = $"{game.Guid}|{raw.Value.Json}|{version}";
            Entry? known = Known(register, key);
            if (known != null)
                return known;

            List<ProjectileFunctionDefinition> functions = GameFunctions(shell, game);
            RoundShape? shape;
            if (projectile != null)
                shape = projectile(game, cannon, id, functions);
            else
                shape = new RoundShape { Mass = game.Mass, Diameter = game.Diameter, Length = game.Length, LaunchLength = game.Length, Drag = game.dragConstant };
            if (shape == null)
                return null;
            bool modified = false;
            foreach (RoundModifier modifier in Loadout.Modifiers)
                modified |= modifier(game, cannon, id, shape, functions);
            if (projectile == null && !modified)
                return null;
            Il2CppSystem.Guid guid = Il2CppSystem.Guid.Parse(new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToString());
            var list = new Il2CppSystem.Collections.Generic.List<ProjectileFunctionDefinition>();
            foreach (ProjectileFunctionDefinition function in functions)
                list.Add(function);
            IProjectileType? created;
            if (register.TryGet(guid, out IProjectileType again))
                created = again;
            else
            {
                // Named by the round alone: Dynamic Gun Sight labels rounds by type name, cut to 8 characters by default
                string name = Loadout.List(owner).FirstOrDefault(r => r.Id == id).Name ?? game.Name;
                created = register.Create(name, guid, shape.Mass, shape.Diameter, shape.Length,
                    game.MinSpeed, game.Lifetime, list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<ProjectileFunctionDefinition>>());
            }
            ProjectileDefinition? def = created?.TryCast<ProjectileDefinition>();
            if (def == null)
                return null;
            register.AddUser(guid);

            def.Mass = shape.Mass;
            def.Diameter = shape.Diameter;
            def.Length = shape.Length;
            def.dragConstant = shape.Drag;
            Entry entry = new Entry(def, shape, game.Diameter, game.ID.Value, hit, owner, id);
            entries[def.Pointer] = entry;
            guids[key] = guid;
            return entry;
        }

        /// <summary>The entry made before for <paramref name="key"/>, if the game still has its projectile type; otherwise null.</summary>
        private static Entry? Known(IProjectileTypeRegister register, string key)
        {
            if (!guids.TryGetValue(key, out Il2CppSystem.Guid known))
                return null;
            if (!register.TryGet(known, out IProjectileType existing))
                return null;
            ProjectileDefinition? made = existing?.TryCast<ProjectileDefinition>();
            if (made == null)
                return null;
            return Of(made.Pointer);
        }

        /// <summary>The round of the projectile type <paramref name="id"/>, if it's one of ours.</summary>
        internal static Entry? Of(int id)
        {
            IProjectileTypeRegister? register = IProjectileTypeRegister.Instance;
            if (register == null)
                return null;
            if (!register.TryGet(new ProjectileTypeID(id), out IProjectileType type))
                return null;
            ProjectileDefinition? def = type?.TryCast<ProjectileDefinition>();
            if (def == null)
                return null;
            return Of(def.Pointer);
        }

        /// <summary>The round of the projectile definition at <paramref name="def"/>, if it's one of ours.</summary>
        internal static Entry? Of(IntPtr def) => entries.TryGetValue(def, out Entry? entry) ? entry : null;

        /// <summary>Puts <paramref name="entry"/>'s definition in launch or flight state.</summary>
        internal static void Size(Entry entry, bool launch)
        {
            entry.Def.Diameter = launch ? entry.GameDiameter : entry.Shape.Diameter;
            entry.Def.Length = launch ? entry.Shape.LaunchLength : entry.Shape.Length;
        }

        /// <summary>The game's projectile definition for the type <paramref name="behaviour"/> has loaded; null when it can't be found.</summary>
        internal static ProjectileDefinition? LoadedGame(CannonBehaviour behaviour)
        {
            ProjectileRegister? register = behaviour.projectileFactory?.TryCast<ProjectileRegister>();
            IProjectileDefinitionSource? source = register?.definitionSource;
            return source?.GetProjectileDefinition(behaviour.LoadedProjectileTypeID);
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
            using var _ = MadiLib.Profile.Measure("rounds: fire prefix");
            __state = null;
            Tracker.Fired(__instance.Pointer);
            ProjectileRegister? register = __instance.projectileFactory?.TryCast<ProjectileRegister>();
            IProjectileDefinitionSource? source = register?.definitionSource;
            ProjectileDefinition? game = source?.GetProjectileDefinition(__0);
            if (game == null)
                return;
            // The shot launches during this call, with the ID that's next now
            int projectile = register!.nextID;
            Projectiles.Entry? entry = Projectiles.For(__instance.Pointer, game);
            if (entry == null)
            {
                Impacts.RaisePlayerFired(__instance.Pointer, game.Mass, game.Diameter, projectile);
                return;
            }
            if (source!.GetProjectileDefinition(entry.Def.ID)?.Pointer != entry.Def.Pointer)
            {
                if (!warned)
                    Plugin.Logger.LogError($"The gun can't fire projectile type {entry.Def.ID.Value} ({entry.Def.Name}); rounds fire the game's shell.");
                warned = true;
                Impacts.RaisePlayerFired(__instance.Pointer, game.Mass, game.Diameter, projectile);
                return;
            }
            Impacts.RaisePlayerFired(__instance.Pointer, entry.Def.Mass, entry.Shape.Diameter, projectile);
            if (Tracker.Cannons.TryGetValue(__instance.Pointer, out Cannon? cannon))
                Loadout.RaiseLaunched(register.Pointer, projectile, entry.Def, entry.Owner, entry.Id, cannon);
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
    /// AI leads and drops its shots by (through <see cref="GunLayerAiLead"/>).
    /// The game caches the full-bore shell's on the cannon when it is built.
    /// </summary>
    [HarmonyPatch(typeof(Cannon), nameof(Cannon.Build))]
    internal static class AiVelocity
    {
        // Gun behaviour, the game's projectile for its shell, as last seen loaded; a gun waiting for a load has none loaded
        internal static readonly Dictionary<IntPtr, ProjectileDefinition> Games = new();

        private static void Postfix(Cannon __instance)
        {
            if (__instance.behaviour != null)
                Update(__instance.behaviour.Pointer);
        }

        /// <summary>Sets the cannon of <paramref name="gun"/> (its behaviour) to its loaded round's velocity, or the game's for its own shell.</summary>
        internal static void Update(IntPtr gun)
        {
            if (!Tracker.Cannons.TryGetValue(gun, out Cannon? cannon))
                return;
            CannonBlueprint? blueprint = cannon.Blueprint;
            if (blueprint == null)
                return;
            CannonBehaviour? behaviour = cannon.behaviour;
            if (behaviour == null)
                return;
            // An unloaded gun's type is -1, which the game throws on; its first load updates it
            if (behaviour.LoadedProjectileTypeID.Value < 0)
                return;
            ProjectileDefinition? game = Projectiles.LoadedGame(behaviour);
            if (game == null)
                return;
            Games[gun] = game;
            float speed = Speed(gun, blueprint, game);
            if (!PlayerVehicle.Contains(cannon))
                speed = AiLead(gun, cannon, blueprint, game, speed);
            cannon.muzzleVelocity = speed;
        }

        /// <summary>
        /// The speed the AI gunner leads by: it takes flight time as range over
        /// this speed, so its mean speed to its target, drag and motor counted.
        /// <paramref name="speed"/> without a target, or when the round doesn't get there.
        /// </summary>
        private static float AiLead(IntPtr gun, Cannon cannon, CannonBlueprint blueprint, ProjectileDefinition game, float speed)
        {
            float? range = AiRange.Of(cannon);
            if (range == null)
                return speed;
            (float Speed, float Time)? at = AtRange(gun, blueprint, game, null, range.Value);
            if (at == null)
                return speed;
            // No flight time to divide by
            if (!(at.Value.Time > 0f))
                return speed;
            float lead = range.Value / at.Value.Time;
            if (Plugin.LogAi.Value)
                Plugin.Logger.LogInfo($"AI {cannon.GetComponentInParent<Sprocket.Vehicles.Vehicle>()?.name ?? "vehicle"} aim: target {range.Value:0} m, there in {at.Value.Time:0.00} s at {at.Value.Speed:0} m/s; leads by {lead:0} m/s (else {speed:0})");
            return lead;
        }

        /// <summary>The speed round <paramref name="round"/> (else the loaded one) of <paramref name="gun"/> leaves the muzzle at, m/s.</summary>
        internal static float Launch(IntPtr gun, CannonBlueprint blueprint, ProjectileDefinition game, string? round = null)
        {
            Projectiles.Entry? entry = Projectiles.For(gun, game, round);
            // The game's own shell
            if (entry == null)
                return blueprint.MuzzleVelocity;
            return blueprint.MuzzleVelocity * Loadout.VelocityRatio(blueprint, entry.Shape.LaunchLength / game.Length);
        }

        /// <summary>Round <paramref name="round"/>'s (else the loaded one's) speed and flight time at <paramref name="range"/> m, with its drag and any motor; null if it doesn't get there.</summary>
        internal static (float Speed, float Time)? AtRange(IntPtr gun, CannonBlueprint blueprint, ProjectileDefinition game, string? round, float range)
        {
            Projectiles.Entry? entry = Projectiles.For(gun, game, round);
            return AiRange.At(entry?.Def ?? game, Launch(gun, blueprint, game, round), range, entry?.Shape.Thrust);
        }

        /// <summary>
        /// The speed the AI leads round <paramref name="round"/> (else the loaded
        /// one) of <paramref name="gun"/> by: its projectile's aim velocity or launch
        /// speed, or the gun's muzzle velocity for the game's shell or without <paramref name="game"/>.
        /// </summary>
        internal static float Speed(IntPtr gun, CannonBlueprint blueprint, ProjectileDefinition? game, string? round = null)
        {
            float velocity = blueprint.MuzzleVelocity;
            if (game == null)
                return velocity;
            Projectiles.Entry? entry = Projectiles.For(gun, game, round);
            // The game's own shell
            if (entry == null)
                return velocity;
            if (entry.Shape.AimVelocity > 0f)
                return entry.Shape.AimVelocity;
            return velocity * Loadout.VelocityRatio(blueprint, entry.Shape.LaunchLength / game.Length);
        }
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
            // Dynamic Gun Sight isn't installed
            if (clearAll == null)
                return;
            if (seen.Contains(entry.Def.Pointer))
                return;
            if (!Tracker.Cannons.TryGetValue(gun, out Cannon? cannon))
                return;
            if (!PlayerVehicle.Contains(cannon))
                return;
            Clear();
            seen.Add(entry.Def.Pointer);
        }

        /// <summary>
        /// <c>RecordMeasurement(int typeId, float velocity)</c>: the sight looks
        /// measured muzzle velocity up by the type in the gun's racks, the
        /// game's, so a round's shots are recorded under that.
        /// </summary>
        private static void MeasurePrefix(ref int __0)
        {
            Projectiles.Entry? entry = Projectiles.Of(__0);
            if (entry != null)
                __0 = entry.GameId;
        }

        /// <summary><c>Load(int typeId)</c>: the game's type of one of the player's guns reads as its loaded round's.</summary>
        private static void LoadPrefix(ref int __0)
        {
            foreach ((IntPtr gun, Cannon cannon, CannonBlueprint owner) in Tracker.PlayerGuns())
            {
                CannonBehaviour? behaviour = cannon.behaviour;
                if (behaviour == null)
                    continue;
                if (behaviour.LoadedProjectileTypeID.Value != __0)
                    continue;
                ProjectileDefinition? game = Projectiles.LoadedGame(behaviour);
                if (game == null)
                    continue;
                // The round being loaded, so the sight is ready by the time it lands
                Projectiles.Entry? entry = Projectiles.For(gun, game, Tracker.Upcoming(gun, owner));
                if (entry != null)
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
