extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Properties;
using Sprocket.Blueprints;
using Sprocket.DamageModelling;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Weapons;
using Sprocket.WeaponFramework;
using Rect = UnityCoreModule::UnityEngine.Rect;
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
    /// <param name="id">The round's id, for <see cref="Loadout.Get{T}(CannonBlueprint, string, string)"/> and <see cref="Loadout.Edit"/>.</param>
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

    /// <summary>
    /// Changes any round's projectile after its type made it, such as a rocket
    /// motor added to a round of any type. For a type that fires the game's
    /// own projectile (<c>"ap"</c>), <paramref name="shape"/> is the game's.
    /// Return true if it changed anything: a round no modifier changes and
    /// whose type has no projectile fires the game's.
    /// </summary>
    public delegate bool RoundModifier(ProjectileDefinition game, Cannon cannon, string id, RoundShape shape, List<ProjectileFunctionDefinition> functions);

    /// <summary>
    /// A gun launched one of a round's own projectiles: the game's projectile
    /// register firing it (its pointer), the projectile's id there, its
    /// definition, its round, and the gun that fired it.
    /// </summary>
    public delegate void RoundLaunched(IntPtr register, int projectile, ProjectileDefinition definition, CannonBlueprint owner, string id, Cannon gun);

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

        /// <summary>
        /// Velocity the gunner AI leads and drops its shots by, m/s; 0 for the
        /// muzzle velocity <see cref="LaunchLength"/> gives, as for a round
        /// that speeds up in flight.
        /// </summary>
        public float AimVelocity;

        /// <summary>
        /// Acceleration along its path (m/s²) at an age after launch (s), as a
        /// rocket motor pushes; null for none. Lets rounds work out its speed and
        /// time to a range (<see cref="MadiLib.Trajectory"/>) for the AI.
        /// </summary>
        public Func<float, float>? Thrust;
    }

    /// <summary>
    /// Rounds a cannon can fire, saved with the vehicle. <c>"ap"</c>, the
    /// game's own shell, is built in; plugins add round types, such as a
    /// sabot round, with <see cref="Register"/>; the player
    /// names rounds and picks their types in the cannon panel, assigns each
    /// ammo rack's slots to rounds, or leaves them to an even balance, and
    /// picks the next round to load in game.
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
        internal static readonly List<(string Type, string Label, RoundDrawer Draw, RoundProjectile? Projectile, RoundHit? Hit, Func<CannonBlueprint, string, string?>? IconOf, Func<CannonBlueprint, string, float, float>? Penetration)> Types = new();

        // Changes any round's projectile, in registration order
        internal static readonly List<RoundModifier> Modifiers = new();

        // Draw more fields under every round, after its type's, in registration order
        internal static readonly List<RoundDrawer> Fields = new();

        /// <summary>Raised when a gun launches a round's own projectile.</summary>
        public static event RoundLaunched? Launched;

        internal static void RaiseLaunched(IntPtr register, int projectile, ProjectileDefinition definition, CannonBlueprint owner, string id, Cannon gun) =>
            Launched?.Invoke(register, projectile, definition, owner, id, gun);

        /// <summary>Adds a <see cref="RoundModifier"/>, run for every round of every type.</summary>
        public static void Modify(RoundModifier modifier) => Modifiers.Add(modifier);

        /// <summary>Draws more fields under every round in the cannon panel, whatever its type, such as a rocket motor's.</summary>
        public static void AddFields(RoundDrawer draw) => Fields.Add(draw);

        // Rebuilds the cannon panel last drawn; set by its draw
        internal static Action? redraw;

        /// <summary>
        /// Rebuilds the cannon panel, for a field that adds or removes others,
        /// such as a dropdown: <c>changed</c> doesn't, as sliders call it every step.
        /// </summary>
        public static void Redraw() => redraw?.Invoke();

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
        /// <param name="penetration">
        /// A round's penetration in mm from its gun, id and speed (m/s), as its
        /// panel shows it, by which the AI picks its rounds; null ranks the type last.
        /// </param>
        public static void Register(string type, string label, RoundDrawer draw, RoundProjectile? projectile = null, RoundHit? hit = null, Stream? icon = null,
            Func<CannonBlueprint, string, string?>? iconOf = null, Func<CannonBlueprint, string, float, float>? penetration = null)
        {
            if (icon != null)
                AddIcon(type, icon);
            Types.Add((type, label, draw, projectile, hit, iconOf, penetration));
        }

        // Round type, the year it can first be picked, and the year a given round of it can (sabot: APDS, APFSDS, DU)
        private static readonly Dictionary<string, (Func<float> Year, Func<CannonBlueprint, string, float>? OfRound)> years = new();

        /// <summary>
        /// Limits <paramref name="type"/> to vehicles dated <paramref name="year"/>
        /// or later, as <see cref="MadiLib.Introduced"/>: the designer's Type
        /// dropdown hides it before then, and a round already of it is flagged,
        /// not changed. Types never limited, such as <c>"ap"</c>, are always available.
        /// </summary>
        /// <param name="type">The round type, as passed to <see cref="Register"/>.</param>
        /// <param name="year">The type's first year, read each draw so a setting can change it.</param>
        /// <param name="ofRound">A given round's year from its gun and id, for types whose rounds differ (null uses <paramref name="year"/>).</param>
        public static void Introduce(string type, Func<float> year, Func<CannonBlueprint, string, float>? ofRound = null) =>
            years[type] = (year, ofRound);

        /// <summary>The first year <paramref name="type"/> can be picked; 0 for always.</summary>
        internal static float YearOf(string type)
        {
            if (!years.TryGetValue(type, out (Func<float> Year, Func<CannonBlueprint, string, float>? OfRound) limit))
                return 0f;
            return limit.Year();
        }

        /// <summary>The first year round <paramref name="id"/> of <paramref name="type"/> is available; 0 for always.</summary>
        internal static float YearOf(CannonBlueprint owner, string id, string type)
        {
            if (!years.TryGetValue(type, out (Func<float> Year, Func<CannonBlueprint, string, float>? OfRound) limit))
                return 0f;
            // A type whose rounds differ judges this one
            if (limit.OfRound != null)
                return limit.OfRound(owner, id);
            return limit.Year();
        }

        /// <summary>Whether the player asked for the AI's decisions to be logged (rounds' <b>Log AI</b>).</summary>
        public static bool LogAi => Plugin.LogAi.Value;

        /// <summary>Round <paramref name="id"/>'s penetration in mm at <paramref name="speed"/> m/s, as its type works it out; 0 if its type doesn't say.</summary>
        internal static float Penetration(CannonBlueprint owner, string id, float speed)
        {
            (string Json, string Type)? raw = Raw(owner, id);
            if (raw == null)
                return 0f;
            Func<CannonBlueprint, string, float, float>? of = Types.FirstOrDefault(t => t.Type == raw.Value.Type).Penetration;
            if (of == null)
                return 0f;
            return of(owner, id, speed);
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
        /// For the ammo bar, per gun (its <c>WeaponBehaviour</c>'s pointer):
        /// the round id a plugin is moving into a ready rack and how far
        /// along it is (0-1), or null when it isn't; set by a plugin that
        /// stages ammo.
        /// </summary>
        public static Func<IntPtr, (string Round, float Progress)?>? Reracking;

        /// <summary>
        /// Draws a plugin's status for a gun (its <c>WeaponBehaviour</c>'s
        /// pointer) in a column at the end of its ammo bar row, given the
        /// column's rect.
        /// </summary>
        public static Action<IntPtr, Rect>? GunBadge;

        /// <summary>
        /// Whether the ammo bar drew the gun (its <c>WeaponBehaviour</c>'s
        /// pointer) last GUI frame, so a plugin can leave it out of its own panel.
        /// </summary>
        public static bool OnBar(IntPtr gun) => AmmoBar.Shown.Contains(gun);

        /// <summary>
        /// Makes every round's projectile again on its next shot, for a setting
        /// outside the round (such as a global multiplier) that its
        /// <see cref="RoundProjectile"/> reads.
        /// </summary>
        public static void Invalidate() => Projectiles.Invalidate();

        /// <summary>
        /// The round that fires projectile type <paramref name="type"/> (its
        /// <c>ProjectileTypeID</c> value), and the projectile's mass in kg;
        /// null for a type that isn't a round's own, such as the game's.
        /// </summary>
        public static (CannonBlueprint Owner, string Id, float Mass)? RoundOfProjectile(int type)
        {
            Projectiles.Entry? entry = Projectiles.Of(type);
            if (entry == null)
                return null;
            return (entry.Owner, entry.Id, entry.Def.Mass);
        }

        /// <summary>The round <paramref name="id"/> in <paramref name="owner"/> as JSON text, and its type; null if there's none.</summary>
        internal static (string Json, string Type)? Raw(CannonBlueprint owner, string id) =>
            RoundsIn(Parsed(owner))[id] is JsonObject round ? (round.ToJsonString(), (string?)round["type"] ?? "") : null;

        /// <summary>The round <paramref name="id"/> in <paramref name="owner"/> as a <typeparamref name="T"/>, if it is of <paramref name="type"/>; otherwise null.</summary>
        public static T? Get<T>(CannonBlueprint? owner, string? id, string type) where T : class
        {
            if (owner == null)
                return null;
            if (id == null)
                return null;
            JsonObject? round = RoundsIn(Parsed(owner))[id] as JsonObject;
            if (round == null)
                return null;
            if ((string?)round["type"] != type)
                return null;
            return Get<T>(owner, id);
        }

        /// <summary>
        /// The round <paramref name="id"/> in <paramref name="owner"/> as a
        /// <typeparamref name="T"/>, whatever its type, for fields any round
        /// may have (see <see cref="AddFields"/>); null if there's none.
        /// </summary>
        public static T? Get<T>(CannonBlueprint? owner, string? id) where T : class
        {
            if (owner == null)
                return null;
            if (id == null)
                return null;
            JsonObject? round = RoundsIn(Parsed(owner))[id] as JsonObject;
            if (round == null)
                return null;
            try
            {
                return round.Deserialize<T>(json);
            }
            catch (JsonException e)
            {
                // A bad field in a shared vehicle file falls back to the defaults rather than stopping the gun
                if (badRounds.Add((owner.Pointer, id)))
                    Plugin.Logger.LogError($"Couldn't read round {id} in {owner.Name}, using its defaults: {e.Message}");
                return null;
            }
        }

        // Cannon blueprint and round id of rounds that don't deserialize, logged once each
        private static readonly HashSet<(IntPtr, string)> badRounds = new();

        /// <summary>Changes the round <paramref name="id"/> in <paramref name="owner"/> and saves it. Fields <typeparamref name="T"/> doesn't have are kept.</summary>
        public static void Edit<T>(CannonBlueprint owner, string id, Action<T> change) where T : new() => Change(owner, rounds =>
        {
            JsonObject? round = rounds[id] as JsonObject;
            if (round == null)
                return;
            T value;
            try
            {
                value = round.Deserialize<T>(json) ?? new T();
            }
            catch (JsonException)
            {
                // A bad field, as Get falls back on: start from the defaults, which overwrite it below
                value = new T();
            }
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
            if (behaviour == null)
                return null;
            if (!Tracker.Cannons.TryGetValue(behaviour.Pointer, out Cannon? cannon))
                return null;
            CannonBlueprint? owner = Owner(cannon);
            if (owner == null)
                return null;
            return Get<T>(owner, Tracker.Loaded(behaviour.Pointer, owner), type);
        }

        /// <summary>
        /// Runs <paramref name="move"/>, which takes a shell from one rack and
        /// puts it in another, as a re-rack: it may take from a rack that
        /// isn't holding the gun's next round. Which rounds move isn't told, so
        /// prefer <see cref="Rerack(AmmoRackBehaviour, AmmoRackBehaviour, string?)"/>.
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

        /// <summary>
        /// Moves one shell of round <paramref name="id"/> (see
        /// <see cref="Contents"/>; null for a gun without rounds) from
        /// <paramref name="from"/> to <paramref name="to"/>, as a re-rack.
        /// Returns false if <paramref name="from"/> gave none.
        /// </summary>
        public static bool Rerack(AmmoRackBehaviour from, AmmoRackBehaviour to, string? id)
        {
            bool moved = false;
            Tracker.Moving = id;
            try
            {
                Rerack(() =>
                {
                    moved = from.TakeShell();
                    if (moved)
                        to.LoadAmmo(1);
                });
            }
            finally
            {
                Tracker.Moving = null;
            }
            if (moved)
                Tracker.Put(to, id);
            return moved;
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
            if (weapon == null)
                return;
            if (task == null)
                return;
            if (task.State == LoadState.Loaded)
                return;
            if (HasShell(weapon))
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

        /// <summary>Whether <paramref name="weapon"/> holds a shell, by our count and the game's.</summary>
        private static bool HasShell(WeaponBehaviour weapon)
        {
            if (Tracker.IsWaiting(weapon.Pointer))
                return false;
            if (weapon.NeedsReload)
                return false;
            return weapon.LoadFraction >= 1f;
        }

        /// <summary>Whether <paramref name="rack"/>'s gun has rounds, so its shells are told apart.</summary>
        public static bool HasRounds(AmmoRackBehaviour rack) => Tracker.RoundOf(rack) != null;

        /// <summary>The id of the round <paramref name="rack"/> mostly holds by design (see <see cref="Contents"/> for its shells); null for a rack without rounds.</summary>
        public static string? RoundOf(AmmoRackBehaviour rack) => Tracker.RoundOf(rack);

        /// <summary>The shells of each round id in <paramref name="rack"/> now, live; null for a rack without rounds.</summary>
        public static IReadOnlyDictionary<string, int>? Contents(AmmoRackBehaviour rack) => Tracker.Contents(rack);

        /// <summary>
        /// The rounds a re-rack should bring to <paramref name="rack"/>, best
        /// first: rounds short of their assigned slots, its gun's next round
        /// leading, then with unassigned slots free an even balance of all
        /// rounds. Null for a rack without rounds.
        /// </summary>
        public static IReadOnlyList<string>? Wanted(AmmoRackBehaviour rack) => Tracker.Wanted(rack);

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
            // The cannon has its rounds already
            if (Parsed(owner)["rounds"] is JsonObject)
                return owner;
            // The shell slot has none to move
            if (Parsed(shell)["rounds"] is not JsonObject)
                return owner;
            (string Owner, string Shell)? moved = Moved(owner.Description, shell.Description);
            if (moved != null)
            {
                owner.Description = moved.Value.Owner;
                shell.Description = moved.Value.Shell;
            }
            return owner;
        }

        /// <summary>
        /// The cannon's and shell slot's descriptions with <c>"rounds"</c> moved
        /// from the shell's to the cannon's, the shell's other keys kept; null
        /// when there is nothing to move, the cannon has rounds already, or
        /// either holds text that isn't JSON.
        /// </summary>
        internal static (string Owner, string Shell)? Moved(string? owner, string? shell)
        {
            JsonObject? root = MadiLib.Descriptions.Parse(owner);
            if (root == null)
                return null;
            // The cannon has rounds already
            if (root["rounds"] is JsonObject)
                return null;
            JsonObject? old = MadiLib.Descriptions.Parse(shell);
            if (old == null)
                return null;
            JsonObject? rounds = old["rounds"] as JsonObject;
            if (rounds == null)
                return null;
            old.Remove("rounds");
            root["rounds"] = rounds;
            // A shell slot with no other keys is left with no description
            string shellText = "";
            if (old.Count > 0)
                shellText = old.ToJsonString();
            return (root.ToJsonString(), shellText);
        }

        /// <summary>
        /// Muzzle velocity of a projectile <paramref name="length"/> times the
        /// full-bore length over a full-bore projectile's, from the game's
        /// interior ballistics. One formula for editor readouts, AI lead and shots.
        /// Launched by <paramref name="charge"/> of the gun's charge (its propellant length).
        /// </summary>
        public static float VelocityRatio(CannonBlueprint gun, float length, float charge = 1f)
        {
            float caliber = gun.Caliber;
            float Velocity(float l, float c) => ShellProperties.CalculateFireInfo(gun.BoreLength, caliber, 3f * caliber * l, c * gun.PropellantLength, gun.OperatingPressure).MuzzleVelocity;
            return Velocity(length, charge) / Velocity(1f, 1f);
        }

        // Caliber (mm), a full-bore shell's definition made for its drag; null where making one failed
        private static readonly Dictionary<float, ProjectileDefinition?> fullBore = new();

        /// <summary>
        /// The drag force (N) at a speed (m/s) on the game's full-bore shell of
        /// <paramref name="caliber"/> mm (steel, 3 calibers long), for designer readouts
        /// with no projectile in flight; null if the game won't make one.
        /// </summary>
        public static Func<float, float>? FullBoreDrag(float caliber)
        {
            if (!fullBore.TryGetValue(caliber, out ProjectileDefinition? def))
            {
                try
                {
                    float d = caliber / 1000f;
                    // The constructor wants at least one function; this one only sits there, as the definition never flies
                    var functions = new Il2CppReferenceArray<ProjectileFunction>(new ProjectileFunction[] { new ArmourPiercingProjectileFunction() });
                    def = new ProjectileDefinition("drag", new ProjectileTypeID(-1), Il2CppSystem.Guid.Empty, ShellProperties.GetProjectileMass((ushort)MathF.Round(caliber)),
                        d, 3f * d, 0f, 60f, functions, new Il2CppReferenceArray<ProjectileFunction>(0));
                    if (!(def.CalculateDragForce(300f) > 0f))
                    {
                        Plugin.Logger.LogWarning($"A {caliber:0} mm shell made in the designer has no drag (drag constant {def.dragConstant}); readouts leave drag out");
                        def = null;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"Couldn't make a {caliber:0} mm shell for designer readouts, which leave drag out: {e.Message}");
                    def = null;
                }
                fullBore[caliber] = def;
            }
            return def == null ? null : v => def.CalculateDragForce(v);
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
            if (cannonOfShell.TryGetValue(shell.Pointer, out Cannon? known))
            {
                if (Alive(known) && known.ShellBlueprint?.Pointer == shell.Pointer)
                    return known;
            }
            // The scan finds every object in the scene, and racks are looked up many times a frame: a shell no cannon uses is scanned for at most once a second
            float now = Time.unscaledTime;
            if (missed.TryGetValue(shell.Pointer, out float retry) && now < retry)
                return null;
            using (MadiLib.Profile.Measure("rounds: cannon scan"))
                foreach (Cannon cannon in UnityObject.FindObjectsOfType<Cannon>())
                {
                    ShellSlotBlueprint? used = cannon.ShellBlueprint;
                    if (used != null)
                        cannonOfShell[used.Pointer] = cannon;
                }
            if (!cannonOfShell.TryGetValue(shell.Pointer, out known))
            {
                missed[shell.Pointer] = now + 1f;
                return null;
            }
            // A cannon that has since changed shell slot
            if (known.ShellBlueprint?.Pointer != shell.Pointer)
                return null;
            return known;
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
            JsonObject? root = Read(owner);
            if (root == null)
                return;
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

        // Stands in for a description that isn't JSON; shared, never changed
        private static readonly JsonObject none = new();

        /// <summary>
        /// <paramref name="blueprint"/>'s description as JSON (empty if it holds
        /// other text), parsed again only when it changes: racks and guns are read
        /// many times a frame. Shared, so never change it; use <see cref="Read(Blueprint)"/> to edit.
        /// </summary>
        internal static JsonObject Parsed(Blueprint blueprint) => MadiLib.Descriptions.Parsed(blueprint) ?? Unreadable(blueprint) ?? none;

        /// <summary>
        /// <paramref name="blueprint"/>'s description as JSON, a fresh copy to edit;
        /// null if it holds other text, which an edit must leave as it is.
        /// </summary>
        internal static JsonObject? Read(Blueprint blueprint) => MadiLib.Descriptions.Read(blueprint) ?? Unreadable(blueprint);

        // Blueprints whose description isn't JSON, logged once each
        private static readonly HashSet<IntPtr> unreadable = new();

        private static JsonObject? Unreadable(Blueprint blueprint)
        {
            if (unreadable.Add(blueprint.Pointer))
                Plugin.Logger.LogError($"Couldn't read the rounds in {blueprint.Name}: its description isn't rounds' JSON, so rounds leave it as it is");
            return null;
        }
    }
}
