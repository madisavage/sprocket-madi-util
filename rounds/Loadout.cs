extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime.InteropTypes;
using Properties;
using Sprocket.Blueprints;
using Sprocket.DamageModelling;
using Sprocket.UI;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Weapons;
using Sprocket.WeaponFramework;
using Time = UnityCoreModule::UnityEngine.Time;
using UnityObject = UnityCoreModule::UnityEngine.Object;

namespace Rounds
{
    /// <summary>
    /// Draws one round's own fields in the cannon panel, below its name and
    /// type. Call <paramref name="changed"/> after each edit so the designer
    /// knows the vehicle changed.
    /// </summary>
    /// <param name="drawer">The cannon panel.</param>
    /// <param name="cannon">The cannon selected.</param>
    /// <param name="id">The round's id, for <see cref="Loadout.Get"/> and <see cref="Loadout.Edit"/>.</param>
    /// <param name="changed">Marks the vehicle modified.</param>
    public delegate void RoundDrawer(IGUIElementDrawer drawer, Cannon cannon, string id, Action changed);

    /// <summary>
    /// The projectile a round fires, given the game's projectile for its gun.
    /// Return null to fire the game's own. Each round fires its own projectile
    /// type, made once per round's settings, so shells in flight keep theirs
    /// whatever other guns fire.
    /// </summary>
    /// <param name="game">The game's projectile for the gun; read it, don't change it.</param>
    /// <param name="cannon">The gun firing.</param>
    /// <param name="id">The round's id.</param>
    /// <param name="functions">
    /// The game's function definitions for the projectile (such as AP and
    /// Propellant), in order; add, remove or replace them to change what the
    /// projectile does.
    /// </param>
    public delegate RoundShape? RoundProjectile(ProjectileDefinition game, Cannon cannon, string id, List<ProjectileFunctionDefinition> functions);

    /// <summary>
    /// A hit by one of the round's projectiles on armour, before the game
    /// works it out: change the penetrator (see <see cref="Impacts.SetPenetration"/>)
    /// or the hit's spall (<see cref="Impacts.SpallBursts"/>, <see cref="Impacts.FragmentMass"/>).
    /// </summary>
    public delegate void RoundHit(ref PenetratorInfo penetrator, CannonBlueprint owner, string id);

    /// <summary>A round's projectile in flight, in kg and m, and what the charge pushes at launch.</summary>
    public sealed class RoundShape
    {
        /// <summary>Mass in flight and at launch, kg.</summary>
        public float Mass;

        /// <summary>Diameter in flight, m; at launch it's the game's, so the charge fills the bore.</summary>
        public float Diameter;

        /// <summary>Length in flight, m.</summary>
        public float Length;

        /// <summary>
        /// Length at launch, m: the game works muzzle velocity out from
        /// diameter and length, so this is how long a projectile of the
        /// game's diameter would be to weigh what the charge pushes (with any
        /// sabot).
        /// </summary>
        public float LaunchLength;

        /// <summary>The definition's drag constant.</summary>
        public float Drag;
    }

    /// <summary>
    /// Rounds a cannon can fire, saved with the vehicle. <c>"ap"</c>, the
    /// game's own shell, is built in; plugins add round types, such as a
    /// sabot round, with <see cref="Register"/>; the player
    /// names rounds and picks their types in the cannon panel, gives each
    /// ammo rack one, and picks the next round to load in game.
    /// </summary>
    /// <remarks>
    /// Stored as JSON in the cannon blueprint's description (its
    /// <i>owner</i>), so they save with the vehicle and with the cannon in
    /// the cannon library: <c>{"rounds": {id: {"name": name, "type": type, ...fields}}}</c>,
    /// fields in camelCase. Ids are short and stable, so a rename keeps the
    /// racks holding it. The game saves and loads descriptions untouched, but has no UI
    /// for them. Older versions kept them in the shell slot's description;
    /// <see cref="Owner"/> moves them.
    /// </remarks>
    public static class Loadout
    {
        private static readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Each registered round type, in registration order; IconOf picks a round's icon, or null for the type's
        internal static readonly List<(string Type, string Label, RoundDrawer Draw, RoundProjectile? Projectile, RoundHit? Hit, Func<CannonBlueprint, string, string?>? IconOf)> Types = new();

        // Icon key, its PNG: each type's under its type, and those from AddIcon
        internal static readonly Dictionary<string, byte[]> Icons = new();

        /// <summary>Adds a round type the player can pick for a round.</summary>
        /// <param name="type">Stored in the round as <c>"type"</c>. Keep it stable, or saved rounds lose their type.</param>
        /// <param name="label">Shown in the type dropdown.</param>
        /// <param name="draw">Draws a round of this type's fields.</param>
        /// <param name="projectile">The projectile it fires; null fires the game's.</param>
        /// <param name="hit">Changes its hits on armour; null leaves them to the game.</param>
        /// <param name="icon">
        /// A PNG of the shell, upright on a transparent background, for the
        /// ammo bar, such as an embedded resource's stream; null shows
        /// <paramref name="label"/>. Read here, so it may be closed after.
        /// </param>
        /// <param name="iconOf">
        /// Picks a round's icon by the key it was added under with
        /// <see cref="AddIcon"/>, from its gun and id; null, or returning null,
        /// uses <paramref name="icon"/>.
        /// </param>
        public static void Register(string type, string label, RoundDrawer draw, RoundProjectile? projectile = null, RoundHit? hit = null, Stream? icon = null,
            Func<CannonBlueprint, string, string?>? iconOf = null)
        {
            if (icon != null)
                AddIcon(type, icon);
            Types.Add((type, label, draw, projectile, hit, iconOf));
        }

        /// <summary>
        /// Adds another icon, as <see cref="Register"/>'s, under
        /// <paramref name="key"/>, for a type's <c>iconOf</c> to pick. Keep keys
        /// unique across plugins, such as by prefixing your type.
        /// </summary>
        public static void AddIcon(string key, Stream png)
        {
            using var bytes = new MemoryStream();
            png.CopyTo(bytes);
            Icons[key] = bytes.ToArray();
        }

        /// <summary>The icon key of round <paramref name="id"/> of <paramref name="type"/> on <paramref name="owner"/>.</summary>
        internal static string IconOf(CannonBlueprint owner, string id, string type) =>
            Types.FirstOrDefault(t => t.Type == type).IconOf?.Invoke(owner, id) ?? type;

        /// <summary>
        /// Whether a rack is one its gun loads from first, for the ammo bar's
        /// ready counts; set by a plugin that stages ammo. Null counts none.
        /// </summary>
        public static Func<AmmoRackBehaviour, bool>? IsReady;

        /// <summary>
        /// Makes every round's projectile again on its next shot, for a setting
        /// outside the round (such as a global multiplier) that its
        /// <see cref="RoundProjectile"/> reads.
        /// </summary>
        public static void Invalidate() => Projectiles.Invalidate();

        /// <summary>The round <paramref name="id"/> in <paramref name="owner"/> as JSON text, and its type; null if there's none.</summary>
        internal static (string Json, string Type)? Raw(CannonBlueprint owner, string id) =>
            RoundsIn(Parsed(owner))[id] is JsonObject round ? (round.ToJsonString(), (string?)round["type"] ?? "") : null;

        /// <summary>The round <paramref name="id"/> in <paramref name="owner"/> as a <typeparamref name="T"/>, if it is of <paramref name="type"/>; otherwise null.</summary>
        public static T? Get<T>(CannonBlueprint? owner, string? id, string type) where T : class
        {
            if (owner == null || id == null || RoundsIn(Parsed(owner))[id] is not JsonObject round || (string?)round["type"] != type)
                return null;
            return round.Deserialize<T>(json);
        }

        /// <summary>Changes the round <paramref name="id"/> in <paramref name="owner"/> and saves it. Fields <typeparamref name="T"/> doesn't have are kept.</summary>
        public static void Edit<T>(CannonBlueprint owner, string id, Action<T> change) where T : new() => Change(owner, rounds =>
        {
            if (rounds[id] is not JsonObject round)
                return;
            T value = round.Deserialize<T>(json) ?? new T();
            change(value);
            JsonObject fields = JsonSerializer.SerializeToNode(value, json)!.AsObject();
            foreach (string key in fields.Select(f => f.Key).ToList())
            {
                // A node has one parent, so it moves rather than copies
                JsonNode? field = fields[key];
                fields.Remove(key);
                round[key] = field;
            }
        });

        /// <summary>
        /// The round the cannon with <paramref name="behaviour"/> (its
        /// <c>CannonBehaviour</c>) has loaded, as a <typeparamref name="T"/>,
        /// if it is of <paramref name="type"/>; otherwise null. Before the
        /// first load is seen, its selected round.
        /// </summary>
        public static T? Fired<T>(Il2CppObjectBase? behaviour, string type) where T : class
        {
            if (behaviour == null || !Tracker.Cannons.TryGetValue(behaviour.Pointer, out Cannon? cannon) || Owner(cannon) is not { } owner)
                return null;
            return Get<T>(owner, Tracker.Loaded(behaviour.Pointer, owner), type);
        }

        /// <summary>
        /// Runs <paramref name="move"/>, which takes a shell from one rack and
        /// puts it in another, as a re-rack: it may take from a rack that
        /// isn't holding the gun's next round. Move shells only between racks
        /// of the same round (<see cref="RoundOf"/>): a rack's shells are all
        /// its round.
        /// </summary>
        public static void Rerack(Action move)
        {
            Tracker.Reracking = true;
            try
            {
                move();
            }
            finally
            {
                Tracker.Reracking = false;
            }
        }

        /// <summary>Whether <paramref name="rack"/> holds its gun's next round; false for a rack without rounds.</summary>
        public static bool HoldsNext(AmmoRackBehaviour rack) => Tracker.HoldsNext(rack);

        /// <summary>
        /// Has the gun with <paramref name="weapon"/> (its behaviour) ask for
        /// ammo again, after a change in which racks it may load from: the
        /// game only asks again when a rack's contents change. Only a gun
        /// without a shell asks: asking swaps out a loaded one. That's a gun
        /// whose loader found no next round, or one the game says isn't
        /// loaded, as when its racks had none to show the loader at all.
        /// </summary>
        public static void RequestAmmo(WeaponBehaviour? weapon)
        {
            // The weapon's own load state lags its task's on the frame a load lands
            LoadTask? task = weapon?.loadTask?.TryCast<LoadTask>();
            if (weapon == null || task == null || task.State == LoadState.Loaded
                || !Tracker.IsWaiting(weapon.Pointer) && !weapon.NeedsReload && weapon.LoadFraction >= 1f)
                return;
            try
            {
                task.UpdateRequest();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Couldn't have a gun ask for ammo again: {e.Message}");
            }
        }

        /// <summary>Whether <paramref name="rack"/>'s gun has rounds, so its shells are told apart.</summary>
        public static bool HasRounds(AmmoRackBehaviour rack) => Tracker.RoundOf(rack) != null;

        /// <summary>The id of the round <paramref name="rack"/> holds; null for a rack without rounds.</summary>
        public static string? RoundOf(AmmoRackBehaviour rack) => Tracker.RoundOf(rack);

        /// <summary>
        /// The blueprint holding <paramref name="cannon"/>'s rounds: its cannon
        /// blueprint, once rounds an older version kept in its shell slot are
        /// moved there.
        /// </summary>
        internal static CannonBlueprint? Owner(Cannon? cannon)
        {
            CannonBlueprint? owner = cannon?.Blueprint;
            ShellSlotBlueprint? shell = cannon?.ShellBlueprint;
            if (owner == null || shell == null)
                return owner;
            // Checked on the cached copies: this runs every frame, the move once
            if (Parsed(owner)["rounds"] is JsonObject || Parsed(shell)["rounds"] is not JsonObject)
                return owner;
            JsonObject root = Read(owner);
            JsonObject old = Read(shell);
            if (old["rounds"] is not JsonObject rounds)
                return owner;
            old.Remove("rounds");
            root["rounds"] = rounds;
            owner.Description = root.ToJsonString();
            shell.Description = old.Count == 0 ? "" : old.ToJsonString();
            return owner;
        }

        // Shell slot, the cannon using it, for racks, which only know their shell slot
        private static readonly Dictionary<IntPtr, Cannon> cannonOfShell = new();
        // Shell slot no cannon used at the last scan, when to scan for it again
        private static readonly Dictionary<IntPtr, float> missed = new();

        /// <summary>The cannon using <paramref name="shell"/>, in game or in the designer.</summary>
        internal static Cannon? CannonOf(ShellSlotBlueprint? shell)
        {
            if (shell == null)
                return null;
            if (cannonOfShell.TryGetValue(shell.Pointer, out Cannon? known) && Alive(known) && known.ShellBlueprint?.Pointer == shell.Pointer)
                return known;
            // The scan finds every object in the scene, and racks are looked up many times a frame: a shell no cannon uses is scanned for at most once a second
            float now = Time.unscaledTime;
            if (missed.TryGetValue(shell.Pointer, out float retry) && now < retry)
                return null;
            using (MadiLib.Profile.Measure("rounds: cannon scan"))
                foreach (Cannon cannon in UnityObject.FindObjectsOfType<Cannon>())
                    if (cannon.ShellBlueprint is { } s)
                        cannonOfShell[s.Pointer] = cannon;
            if (!cannonOfShell.ContainsKey(shell.Pointer))
                missed[shell.Pointer] = now + 1f;
            return cannonOfShell.TryGetValue(shell.Pointer, out known) && known.ShellBlueprint?.Pointer == shell.Pointer ? known : null;
        }

        private static bool Alive(Cannon cannon)
        {
            try
            {
                return cannon != null && cannon.ShellBlueprint != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The rounds' blueprint for a rack using <paramref name="shell"/>.</summary>
        internal static CannonBlueprint? OwnerOf(ShellSlotBlueprint? shell) => Owner(CannonOf(shell));

        /// <summary>
        /// Raised when a gun's loaded round changes, with its cannon: a load of
        /// a different round, or a new pick before its first load. For
        /// anything caching a round, such as a gun sight.
        /// </summary>
        public static event Action<Cannon>? LoadedChanged;

        internal static void RaiseLoadedChanged(Cannon cannon) => LoadedChanged?.Invoke(cannon);

        /// <summary>The rounds in <paramref name="owner"/>, in order.</summary>
        internal static List<(string Id, string Name, string Type)> List(CannonBlueprint? owner) =>
            owner == null ? new() : RoundsIn(Parsed(owner))
                .Where(r => r.Value is JsonObject)
                .Select(r => (r.Key, (string?)r.Value!["name"] ?? r.Key, (string?)r.Value!["type"] ?? ""))
                .ToList();

        /// <summary>Adds a round of <paramref name="type"/> to <paramref name="owner"/>.</summary>
        internal static void Add(CannonBlueprint owner, string type) => Change(owner, rounds =>
            rounds[Guid.NewGuid().ToString("N")[..8]] = new JsonObject { ["name"] = $"Round {rounds.Count + 1}", ["type"] = type });

        internal static void Rename(CannonBlueprint owner, string id, string name) => Change(owner, rounds =>
        {
            if (rounds[id] is JsonObject round)
                round["name"] = name;
        });

        /// <summary>Gives the round a new type, dropping its old type's fields.</summary>
        internal static void SetType(CannonBlueprint owner, string id, string type) => Change(owner, rounds =>
        {
            if (rounds[id] is JsonObject round)
                rounds[id] = new JsonObject { ["name"] = (string?)round["name"] ?? id, ["type"] = type };
        });

        internal static void Delete(CannonBlueprint owner, string id) => Change(owner, rounds => rounds.Remove(id));

        private static void Change(CannonBlueprint owner, Action<JsonObject> change)
        {
            JsonObject root = Read(owner);
            change(RoundsOf(root));
            owner.Description = root.ToJsonString();
        }

        private static JsonObject RoundsOf(JsonObject root) => Child(root, "rounds");

        /// <summary><paramref name="root"/>'s rounds without adding them, for a cached root from <see cref="Parsed"/>.</summary>
        private static JsonObject RoundsIn(JsonObject root) => root["rounds"] as JsonObject ?? new JsonObject();

        /// <summary><paramref name="root"/>'s object <paramref name="key"/>, added if missing.</summary>
        internal static JsonObject Child(JsonObject root, string key)
        {
            JsonObject? child = root[key] as JsonObject;
            if (child == null)
            {
                child = new JsonObject();
                root[key] = child;
            }
            return child;
        }

        // Blueprint, its description and that parsed; one entry per blueprint read
        private static readonly Dictionary<IntPtr, (string Text, JsonObject Json)> parsed = new();

        /// <summary>
        /// <paramref name="blueprint"/>'s description as JSON, parsed again
        /// only when it changes: racks and guns are read many times a frame.
        /// Shared, so never change it; use <see cref="Read(Blueprint)"/> to edit.
        /// </summary>
        internal static JsonObject Parsed(Blueprint blueprint)
        {
            string description = blueprint.Description ?? "";
            if (parsed.TryGetValue(blueprint.Pointer, out var cached) && cached.Text == description)
                return cached.Json;
            JsonObject json = Read(blueprint, description);
            parsed[blueprint.Pointer] = (description, json);
            return json;
        }

        /// <summary><paramref name="blueprint"/>'s description as JSON, a fresh copy to edit; empty if it isn't JSON, and an edit then replaces it.</summary>
        internal static JsonObject Read(Blueprint blueprint) => Read(blueprint, blueprint.Description);

        private static JsonObject Read(Blueprint blueprint, string? description)
        {
            if (string.IsNullOrEmpty(description))
                return new JsonObject();
            try
            {
                return JsonNode.Parse(description) as JsonObject ?? new JsonObject();
            }
            catch (JsonException e)
            {
                Plugin.Logger.LogError($"Couldn't read the rounds in {blueprint.Name}: {e.Message}");
                return new JsonObject();
            }
        }
    }
}
