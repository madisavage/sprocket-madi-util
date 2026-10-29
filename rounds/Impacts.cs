extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Sprocket.DamageModelling;
using Sprocket.UI;
using Sprocket.VehicleDesigner.ArmourEditing;
using Sprocket.VehicleDesigner.UI;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace Rounds
{
    /// <summary>Changes a penetrator before the game works out its hit.</summary>
    public delegate void PenetratorHandler(ref PenetratorInfo penetrator);

    /// <summary>Fragment <paramref name="index"/> of a hit's <paramref name="simulation"/>, about to be or just simulated.</summary>
    public delegate void FragmentHandler(PenetrationSimulation simulation, short index);

    /// <summary>A hit's <paramref name="penetrator"/> and the <paramref name="simulation"/> the game ran for it, if any.</summary>
    public delegate void SimulationHandler(PenetratorInfo penetrator, IPenetrationSimulation? simulation);

    /// <summary>
    /// Hits on armour. A round's <see cref="RoundHit"/> runs on each of its
    /// projectiles' hits in combat; the armour view's simulated shots go to
    /// <see cref="Simulated"/> instead.
    /// </summary>
    public static class Impacts
    {
        /// <summary>
        /// Spall bursts per burst the game makes, for the hit being worked out:
        /// 2 is two bursts, 1.25 one and a quarter-mass one, 0 none. Set it
        /// from a <see cref="RoundHit"/> or <see cref="Simulated"/>; it goes
        /// back to 1 after each hit.
        /// </summary>
        public static float SpallBursts = 1f;

        /// <summary>Mass of each spall fragment over the game's, for the hit being worked out; back to 1 after each hit.</summary>
        public static float FragmentMass = 1f;

        /// <summary>
        /// Bursts and fragment mass for only the first spall burst of the hit
        /// being worked out, the first plate it perforates, in place of
        /// <see cref="SpallBursts"/> and <see cref="FragmentMass"/>; null for
        /// none. Used up by that burst, and cleared after each hit.
        /// </summary>
        public static (float Bursts, float FragmentMass)? FirstBurst;

        // Degrees the hit being worked out turns toward each plate's normal, from Normalize; 0 for none
        internal static float Normalization;

        /// <summary>Raised for each of the armour view's simulated shots, to change its penetrator or spall.</summary>
        public static event PenetratorHandler? Simulated;

        /// <summary>
        /// Raised as the armour view's panel draws, after the game's own
        /// controls, to add sections; call the redraw it's passed to have the
        /// panel built again. Use this rather than patching the panel's draw:
        /// when two plugins patch it, one can go missing.
        /// </summary>
        public static event Action<IGUIElementDrawer, Action>? ArmourPanel;

        /// <summary>
        /// Raised just before the game simulates each fragment of a hit, in
        /// index order. Use this and <see cref="FragmentSimulated"/> rather
        /// than patching <c>CompoundStructure.SimulateFragment</c>: when two
        /// plugins patch it, one can go missing.
        /// </summary>
        public static event FragmentHandler? FragmentSimulating;

        /// <summary>Raised just after the game simulates each fragment of a hit; its length and outcome are known.</summary>
        public static event FragmentHandler? FragmentSimulated;

        /// <summary>
        /// Raised as each of a hit's entry points returns, innermost first,
        /// with the simulation it ran (null from the outer ones), before
        /// <see cref="HitEnded"/>. Use this rather than patching
        /// <c>CompoundStructure.Penetrate</c>.
        /// </summary>
        public static event SimulationHandler? HitSimulated;

        /// <summary>Raised once each hit, its simulation included, is done.</summary>
        public static event Action? HitEnded;

        /// <summary>Set while the armour view simulates a shot.</summary>
        internal static bool Simulating;

        // Set once a hit has been handed out, so nested entry points leave it
        internal static bool Hitting;

        private static ArmourOverlayPointerOperator? overlay;

        /// <summary>Makes the armour view simulate its shot again, after a setting that changes it.</summary>
        public static void Resimulate()
        {
            if (overlay == null)
                return;
            overlay.lastSampleSettings = default;
            overlay.visualizedDamageModelHash = 0;
        }

        /// <summary>The game's penetration (mm, at normal) of <paramref name="penetrator"/> at its speed: de Marre, <c>100 (√m v / ((d/100)^0.75 K))^1.43</c>.</summary>
        public static float Penetration(PenetratorInfo penetrator) =>
            DeMarre(penetrator.Diameter * 1000f, penetrator.Mass, penetrator.Velocity.magnitude, penetrator.PenetratorConstant);

        /// <summary>
        /// The game's penetration (mm) for a projectile of <paramref name="diameter"/>
        /// mm and <paramref name="mass"/> kg at <paramref name="speed"/> m/s: de Marre,
        /// <c>100 (√m v / ((d/100)^0.75 K))^1.43</c>, as the game's combat code computes it.
        /// </summary>
        public static float DeMarre(float diameter, float mass, float speed, float penetratorConstant) =>
            100f * MathF.Pow(MathF.Sqrt(mass) * speed / (MathF.Pow(diameter * 0.01f, 0.75f) * penetratorConstant), 1.43f);

        /// <summary>
        /// Gives <paramref name="penetrator"/> the penetrator constant that
        /// makes the game's penetration <paramref name="penetration"/> mm at
        /// its speed. Obliquity, plates and spall are still the game's.
        /// </summary>
        /// <remarks>The game's penetration is de Marre: <c>100 (√m v / ((d/100)^0.75 K))^1.43</c> mm, solved here for <c>K</c>.</remarks>
        public static void SetPenetration(ref PenetratorInfo penetrator, float penetration)
        {
            PenetratorInfo p = penetrator;
            float k = MathF.Sqrt(p.Mass) * p.Velocity.magnitude / (MathF.Pow(p.Diameter * 10f, 0.75f) * MathF.Pow(MathF.Max(penetration, 0.01f) / 100f, 1f / 1.43f));
            ushort constant = (ushort)Math.Clamp(MathF.Round(k), 1f, ushort.MaxValue);
            penetrator = new PenetratorInfo(p.Mass, p.Diameter, p.Density, p.MaxNormalizationAngle, constant, p.Position, p.Velocity);
        }

        /// <summary>
        /// Lets <paramref name="penetrator"/> turn toward the plate's normal by
        /// up to <paramref name="degrees"/> on impact, if that's more than it
        /// had: 90 meets every plate square on, as a HESH round's squash head
        /// does, so a sloped plate counts only its own thickness. For the hit
        /// being worked out; it goes back to none after each hit.
        /// </summary>
        /// <remarks>
        /// The game keeps a penetrator's own normalization but never uses it;
        /// this turns the plate's normal toward the projectile instead (see
        /// <see cref="NormalizePatch"/>), which also aims the plate's spall
        /// along the projectile's line and makes ricochets rarer.
        /// </remarks>
        public static void Normalize(ref PenetratorInfo penetrator, float degrees)
        {
            PenetratorInfo p = penetrator;
            Normalization = MathF.Max(Normalization, Math.Clamp(degrees, 0f, 90f));
            float angle = MathF.Max(p.MaxNormalizationAngle, Normalization);
            penetrator = new PenetratorInfo(p.Mass, p.Diameter, p.Density, angle, p.PenetratorConstant, p.Position, p.Velocity);
        }

        /// <summary>
        /// Spawns a spall burst in <paramref name="simulation"/>, a copy of the
        /// last one the game made this hit with its point and direction (sim
        /// space), fragment mass (kg, all fragments), fragment count (the game
        /// makes at most 32), speed (m/s) and cone given; <paramref name="spread"/> is times the copied burst's. Its
        /// fragments start in whatever fragment <paramref name="from"/> is in
        /// (air, or its plate), still spalling as the copied burst's material.
        /// Only from inside a hit's simulation; false if the game has made no
        /// burst this hit to copy, or <paramref name="from"/> is out of material
        /// but not made as the game makes such fragments.
        /// </summary>
        public static bool Burst(IntPtr simulation, Vector3 point, Vector3 direction, float mass, int fragments, float speed, float spread, short parent, short from) =>
            SpallDetour.Spawn(simulation, point, direction, mass, fragments, speed, spread, parent, from);

        internal static void RaiseSimulated(ref PenetratorInfo penetrator) => Simulated?.Invoke(ref penetrator);

        internal static void RaiseHitEnded() => HitEnded?.Invoke();

        internal static void RaiseFragmentSimulating(PenetrationSimulation simulation, short index) => FragmentSimulating?.Invoke(simulation, index);

        internal static void RaiseFragmentSimulated(PenetrationSimulation simulation, short index) => FragmentSimulated?.Invoke(simulation, index);

        internal static void RaiseHitSimulated(PenetratorInfo penetrator, IPenetrationSimulation? simulation) => HitSimulated?.Invoke(penetrator, simulation);

        internal static void RaiseArmourPanel(IGUIElementDrawer drawer, Action redraw) => ArmourPanel?.Invoke(drawer, redraw);

        internal static void Watch(ArmourOverlayPointerOperator view) => overlay = view;
    }

    /// <summary>The armour view panel's one draw hook, for <see cref="Impacts.ArmourPanel"/>.</summary>
    [HarmonyPatch(typeof(ArmourOverlayConfig), nameof(ArmourOverlayConfig.Draw))]
    internal static class ArmourPanelPatch
    {
        private static void Postfix(ArmourOverlayConfig __instance, IGUILayout __0)
        {
            if (__0?.TryCast<IGUIElementDrawer>() is { } drawer)
                Impacts.RaiseArmourPanel(drawer, () => __instance.GUIRedrawRequired = true);
        }
    }

    /// <summary>Marks the armour view's own simulations, so they aren't taken for combat hits.</summary>
    [HarmonyPatch(typeof(ArmourOverlayPointerOperator), nameof(ArmourOverlayPointerOperator.Update))]
    internal static class ArmourSimulationPatch
    {
        private static void Prefix(ArmourOverlayPointerOperator __instance)
        {
            Impacts.Watch(__instance);
            Impacts.Simulating = true;
        }

        private static void Postfix() => Impacts.Simulating = false;
    }

    /// <summary>
    /// Hands each hit to its round's <see cref="RoundHit"/>, or to
    /// <see cref="Impacts.Simulated"/> for the armour view's. The structure
    /// is patched on every entry point a hit may come through; the first to
    /// run hands it out, and the innermost, which runs the simulation, ends it.
    /// </summary>
    [HarmonyPatch]
    internal static class PenetratePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            nameof(CompoundStructure.Penetrate),
            nameof(CompoundStructure.Sprocket_DamageModelling_IReadOnlyCompoundStructure_Penetrate),
            nameof(CompoundStructure.Sprocket_DamageModelling_IDamageModelStructure_Penetrate),
        }.Select(name => (System.Reflection.MethodBase)AccessTools.Method(typeof(CompoundStructure), name));

        private static void Prefix(ref PenetratorInfo __0)
        {
            using var _ = MadiLib.Profile.Measure("rounds: hit prefix");
            if (Impacts.Hitting)
                return;
            Impacts.Hitting = true;
            if (Impacts.Simulating)
            {
                Impacts.Simulating = false;
                Impacts.RaiseSimulated(ref __0);
            }
            else if (Projectiles.Hits.TryGetValue((__0.Mass, __0.Diameter), out var round) && round.Hit != null)
            {
                // A failing round falls back to the game's shell for this hit only
                try { round.Hit(ref __0, round.Owner, round.Id); }
                catch (Exception e) { Plugin.Logger.LogError($"A round's hit handler failed: {e}"); }
            }
        }

        // A finalizer, not a postfix, so a throwing hit still ends; otherwise every later hit is skipped
        private static Exception? Finalizer(Exception? __exception, PenetratorInfo __0, IPenetrationSimulation? __result)
        {
            try
            {
                if (__exception == null)
                    Impacts.RaiseHitSimulated(__0, __result);
            }
            finally
            {
                Impacts.Hitting = false;
                Impacts.SpallBursts = Impacts.FragmentMass = 1f;
                NormalizePatch.Reset();
                Impacts.FirstBurst = null;
                Impacts.Normalization = 0f;
                SpallDetour.HasTemplate = false;
                Impacts.RaiseHitEnded();
            }
            return __exception;
        }
    }

    /// <summary>
    /// Scales each spall burst by <see cref="Impacts.SpallBursts"/> and
    /// <see cref="Impacts.FragmentMass"/>, by a native detour on
    /// <c>CompoundStructure.CreateSpallBurst(PenetrationSimulation, ref SpallSpawnBurst)</c>.
    /// </summary>
    /// <remarks>
    /// Harmony can't patch it: Il2CppInterop's trampoline breaks on a byref
    /// struct it wraps as a class, which <c>SpallSpawnBurst</c> is. The detour
    /// takes the raw pointers instead and writes the field at its native offset.
    /// A burst spawns a fixed number of fragments and its volume is their mass.
    /// </remarks>
    internal static class SpallDetour
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CreateSpallBurst(IntPtr simulation, IntPtr burst, IntPtr method);

        // Kept alive for as long as the detour points at it
        private static CreateSpallBurst? hook, original;
        private static INativeDetour? detour;
        private static IntPtr method, template;
        private static int size, volume, density, speed, spread, spallFactor, area, direction, planeNormal, spawnPoint, depthLookup, parentIndex;
        private static int fragmentsField, fragCountField, fragmentSize, fragmentDepth;
        internal static int FragmentFlags, FragmentDirection, FragmentMaterial;

        // Set once the game makes a burst this hit, copied to template
        internal static bool HasTemplate;

        internal static unsafe void Apply()
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(AccessTools.Method(typeof(CompoundStructure), nameof(CompoundStructure.CreateSpallBurst)));
            method = (IntPtr)field!.GetValue(null)!;
            IntPtr klass = Il2CppClassPointerStore<CompoundStructure.SpallSpawnBurst>.NativeClassPtr;
            // Field offsets count the object header, which a struct behind a pointer doesn't have
            int Offset(string name) => (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(klass, name)) - 2 * IntPtr.Size;
            volume = Offset("volume");
            density = Offset("density");
            speed = Offset("speed");
            spread = Offset("spread");
            spallFactor = Offset("spallFactor");
            area = Offset("crossectionalArea");
            direction = Offset("direction");
            planeNormal = Offset("planeNormal");
            spawnPoint = Offset("spawnPoint");
            depthLookup = Offset("depthLookup");
            parentIndex = Offset("parentIndex");
            uint align = 0;
            size = IL2CPP.il2cpp_class_value_size(klass, ref align);
            IntPtr sim = Il2CppClassPointerStore<PenetrationSimulation>.NativeClassPtr;
            fragmentsField = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(sim, "fragments"));
            fragCountField = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(sim, "fragCount"));
            IntPtr fragment = Il2CppClassPointerStore<Fragment>.NativeClassPtr;
            fragmentSize = IL2CPP.il2cpp_class_value_size(fragment, ref align);
            // Likewise for an array element
            fragmentDepth = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(fragment, "depthLookup")) - 2 * IntPtr.Size;
            FragmentMaterial = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(fragment, "materialIndex")) - 2 * IntPtr.Size;
            FragmentFlags = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(fragment, "flags")) - 2 * IntPtr.Size;
            FragmentDirection = (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(fragment, "direction")) - 2 * IntPtr.Size;
            template = Marshal.AllocHGlobal(size);
            hook = Hook;
            // MethodInfo starts with its native code pointer
            detour = INativeDetour.CreateAndApply(*(IntPtr*)method, hook, out original);
        }

        private static unsafe void Hook(IntPtr simulation, IntPtr burst, IntPtr method)
        {
            if (burst != IntPtr.Zero)
            {
                Buffer.MemoryCopy((void*)burst, (void*)template, size, size);
                HasTemplate = true;
            }
            float bursts = Impacts.SpallBursts, mass = Impacts.FragmentMass;
            if (Impacts.FirstBurst is { } first)
            {
                (bursts, mass) = first;
                Impacts.FirstBurst = null;
            }
            if (bursts == 1f && mass == 1f || burst == IntPtr.Zero)
            {
                original!(simulation, burst, method);
                return;
            }
            // Whole bursts for the whole part, then one for the rest
            float* v = (float*)((byte*)burst + volume);
            float game = *v;
            *v = game * mass;
            for (int i = 1; i <= bursts; i++)
                original!(simulation, burst, method);
            float rest = bursts - MathF.Floor(bursts);
            if (rest > 0.001f)
            {
                *v = game * mass * rest;
                original!(simulation, burst, method);
            }
            *v = game;
        }

        /// <summary>Fragment <paramref name="index"/> in <paramref name="simulation"/>'s own array, read each time as it grows as fragments are made.</summary>
        internal static unsafe byte* Element(IntPtr simulation, int index) =>
            // An array's elements follow its header: class, monitor, bounds and length
            (byte*)Marshal.ReadIntPtr(simulation, fragmentsField) + 4 * IntPtr.Size + index * fragmentSize;

        /// <summary>See <see cref="Impacts.Burst"/>.</summary>
        internal static unsafe bool Spawn(IntPtr simulation, Vector3 point, Vector3 dir, float kg, int count, float metresPerSecond, float cone, short parent, short from)
        {
            if (!HasTemplate)
                return false;
            // The template's arrays are the hit's own, alive until it ends
            byte* b = stackalloc byte[size];
            Buffer.MemoryCopy((void*)template, b, size, size);
            *(float*)(b + volume) = kg / *(float*)(b + density);
            // The game makes area / spallFactor fragments, rounded up; a quarter under lands on the count, rounded either way
            *(float*)(b + area) = *(float*)(b + spallFactor) * (count - 0.25f);
            *(float*)(b + speed) = metresPerSecond;
            *(float*)(b + spread) *= cone;
            *(Vector3*)(b + direction) = dir;
            *(Vector3*)(b + planeNormal) = dir;
            *(Vector3*)(b + spawnPoint) = point;
            *(short*)(b + parentIndex) = parent;
            // The game puts a burst's fragments in its material, as plate spall starts in the plate; these start where `from` is
            byte* Element(int index) => SpallDetour.Element(simulation, index);
            *(IntPtr*)(b + depthLookup) = *(IntPtr*)(Element(from) + fragmentDepth);
            short medium = *(short*)(Element(from) + FragmentMaterial);
            // The game simulates a fragment out of material (-1) only as precomputed (0x80), set when made, as from's was; in material, as its burst's
            ushort precomputed = (ushort)(*(ushort*)(Element(from) + FragmentFlags) & 0x80);
            if (medium < 0 && precomputed == 0)
                return false;
            int first = Marshal.ReadInt32(simulation, fragCountField);
            original!(simulation, (IntPtr)b, method);
            // Read after: the array grows as fragments are made. The spall profile (material + 1) stays the copied burst's
            for (int i = first, end = Marshal.ReadInt32(simulation, fragCountField); i < end; i++)
            {
                *(short*)(Element(i) + FragmentMaterial) = medium;
                *(ushort*)(Element(i) + FragmentFlags) |= precomputed;
            }
            return true;
        }
    }

    /// <summary>
    /// The one hook on <c>CompoundStructure.SimulateFragment</c>, for
    /// <see cref="Impacts.FragmentSimulating"/> and <see cref="Impacts.FragmentSimulated"/>,
    /// and makes <see cref="Impacts.Normalize"/> work, which the game
    /// never does itself. A plate counts as thick as the projectile's path
    /// through it, so as the projectile enters a plate its path is turned
    /// toward the plate's normal by up to <see cref="Impacts.Normalization"/>
    /// degrees. The normal the game gets while the projectile (not its spall)
    /// is simulated is turned toward its line by as much too, as obliquity
    /// in its penetration and ricochets comes from it; the game also aims the
    /// plate's spall from it.
    /// </summary>
    [HarmonyPatch]
    internal static class NormalizePatch
    {
        // The direction, in sim space, of the projectile's fragment being simulated; null for any other
        private static Vector3? direction;
        // The last plate's own normal the projectile met, before turning; null once used or for none
        private static Vector3? plate;

        internal static void Reset() => plate = null;

        [HarmonyPatch(typeof(CompoundStructure), "SimulateFragment"), HarmonyPrefix]
        private static unsafe void Simulating(PenetrationSimulation __0, short __1)
        {
            direction = null;
            if (__0 == null)
                return;
            if (Impacts.Normalization > 0f)
            {
                byte* fragment = SpallDetour.Element(__0.Pointer, __1);
                // The original penetrator's flag
                if ((*(ushort*)(fragment + SpallDetour.FragmentFlags) & 1) != 0)
                {
                    Vector3* d = (Vector3*)(fragment + SpallDetour.FragmentDirection);
                    // Into a plate: turn its path toward the plate's normal, so it goes through less of it
                    if (*(short*)(fragment + SpallDetour.FragmentMaterial) >= 0 && plate is { } n)
                    {
                        Vector3 inward = Vector3.Dot(n, *d) < 0f ? -n : n;
                        *d = Vector3.RotateTowards(*d, inward, Impacts.Normalization * MathF.PI / 180f, 0f);
                    }
                    plate = null;
                    direction = *d;
                }
            }
            Impacts.RaiseFragmentSimulating(__0, __1);
        }

        [HarmonyPatch(typeof(CompoundStructure), "SimulateFragment"), HarmonyPostfix]
        private static void Simulated(PenetrationSimulation __0, short __1)
        {
            direction = null;
            if (__0 != null)
                Impacts.RaiseFragmentSimulated(__0, __1);
        }

        [HarmonyPatch(typeof(CompoundStructure), "GetIntersectionNormal"), HarmonyPostfix]
        private static void Turn(ref Vector3 __result)
        {
            if (direction is not { } d)
                return;
            Vector3 normal = __result;
            plate = normal;
            // Toward the line on the normal's side: against the projectile going in, along it coming out
            Vector3 line = Vector3.Dot(normal, d) < 0f ? -d : d;
            __result = Vector3.RotateTowards(normal, line, Impacts.Normalization * MathF.PI / 180f, 0f);
        }
    }
}
