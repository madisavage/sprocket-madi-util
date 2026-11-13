extern alias UnityCoreModule;

using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Sprocket.DamageModelling;
using Vector3 = UnityCoreModule::UnityEngine.Vector3;

namespace Rounds
{
    /// <summary>A penetration simulation's fragment, as far as the plugins need it; distances in m.</summary>
    public readonly record struct FragmentInfo(PenetrationFragmentFlag Flags, short Parent, short Material, float Travel, float Speed, float Mass, float Diameter, Vector3 Direction)
    {
        /// <summary>Whether this is the hit's own penetrator (a HEAT jet, a rod), not spall.</summary>
        public bool IsJet => (Flags & PenetrationFragmentFlag.OriginalPenetrator) != 0;

        /// <summary>The game's penetration (mm) at this fragment's speed, with the jet's penetrator constant <paramref name="k"/>.</summary>
        public float Penetration(ushort k) => Impacts.Penetration(new PenetratorInfo(Mass, Diameter, 0f, 0f, k, default, new Vector3(Speed, 0f, 0f)));
    }

    /// <summary>
    /// Reads a penetration simulation's fragments.
    /// </summary>
    /// <remarks>
    /// Il2CppInterop marshals <c>out PenetrationFragment</c>, a struct it
    /// wraps as a class, as a pointer, so the game writes the whole struct
    /// over it and the stack breaks. The native methods are called with a
    /// buffer the struct's size instead, and its fields read at their native
    /// offsets.
    /// </remarks>
    public static class Fragments
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetFragmentFn(IntPtr simulation, int index, IntPtr fragment, IntPtr method);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetFinalFn(IntPtr simulation, IntPtr fragment, IntPtr method);

        private static bool ready;
        private static GetFragmentFn getFragment = null!;
        private static GetFinalFn getFinal = null!;
        private static IntPtr getFragmentInfo, getFinalInfo, buffer;
        private static int travel, speed, diameter, mass, parent, material, flags, direction;

        private static void Init()
        {
            IntPtr Info(string name) => (IntPtr)Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(AccessTools.Method(typeof(PenetrationSimulation), name))!.GetValue(null)!;
            getFragmentInfo = Info(nameof(PenetrationSimulation.GetFragment));
            getFinalInfo = Info(nameof(PenetrationSimulation.GetFinalOriginalPenetrator));
            // MethodInfo starts with its native code pointer
            getFragment = Marshal.GetDelegateForFunctionPointer<GetFragmentFn>(Marshal.ReadIntPtr(getFragmentInfo));
            getFinal = Marshal.GetDelegateForFunctionPointer<GetFinalFn>(Marshal.ReadIntPtr(getFinalInfo));

            buffer = Marshal.AllocHGlobal(MadiLib.Native.ValueSize<PenetrationFragment>());
            static int Offset(string name) => MadiLib.Native.StructOffset<PenetrationFragment>(name);
            travel = Offset("TravelDistance");
            speed = Offset("Speed");
            diameter = Offset("Diameter");
            mass = Offset("Mass");
            parent = Offset("ParentIndex");
            material = Offset("MaterialIndex");
            flags = Offset("Flags");
            direction = Offset("Direction");
            ready = true;
        }

        /// <summary>Fragment <paramref name="index"/> of the simulation at <paramref name="simulation"/>.</summary>
        public static FragmentInfo Get(IntPtr simulation, int index)
        {
            if (!ready)
                Init();
            getFragment(simulation, index, buffer, getFragmentInfo);
            return Read();
        }

        /// <summary>The hit's own penetrator as it ended.</summary>
        public static FragmentInfo Final(IntPtr simulation)
        {
            if (!ready)
                Init();
            getFinal(simulation, buffer, getFinalInfo);
            return Read();
        }

        private static float Float(int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer, offset));

        private static Vector3 Vector(int offset) => new(Float(offset), Float(offset + 4), Float(offset + 8));

        private static FragmentInfo Read() => new(
            (PenetrationFragmentFlag)Marshal.ReadByte(buffer, flags),
            Marshal.ReadInt16(buffer, parent),
            Marshal.ReadInt16(buffer, material),
            Float(travel),
            Float(speed),
            Float(mass),
            Float(diameter),
            Vector(direction));
    }

    /// <summary>
    /// Writes and reads fields of a penetration simulation's own fragments,
    /// in its fragment array, as <c>Fragment</c> is a struct Il2CppInterop
    /// can't write. The array is read each time, as it grows as fragments
    /// are made.
    /// </summary>
    public static class FragmentArray
    {
        private sealed class Layout
        {
            internal readonly int Fragments, Size, Speed, Mass, Travel, Spawn, Direction, Flags;

            internal Layout()
            {
                Fragments = MadiLib.Native.FieldOffset<PenetrationSimulation>("fragments");
                Size = MadiLib.Native.ValueSize<Fragment>();
                static int Offset(string name) => MadiLib.Native.StructOffset<Fragment>(name);
                Speed = Offset("speed");
                Mass = Offset("mass");
                Travel = Offset("travelDistance");
                Spawn = Offset("spawnPoint");
                Direction = Offset("direction");
                Flags = Offset("flags");
            }
        }

        private static Layout? layout;
        private static Layout L => layout ??= new Layout();

        private static IntPtr At(IntPtr simulation, int index, int offset) =>
            MadiLib.Native.Element(Marshal.ReadIntPtr(simulation, L.Fragments), index, L.Size) + offset;

        private static void Write(IntPtr simulation, int index, int offset, float value) =>
            Marshal.WriteInt32(At(simulation, index, offset), BitConverter.SingleToInt32Bits(value));

        private static Vector3 Vector(IntPtr simulation, int index, int offset)
        {
            IntPtr at = At(simulation, index, offset);
            float F(int i) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at, 4 * i));
            return new Vector3(F(0), F(1), F(2));
        }

        /// <summary>Sets fragment <paramref name="index"/>'s speed (m/s); 0 stops it.</summary>
        public static void SetSpeed(IntPtr simulation, int index, float value) => Write(simulation, index, L.Speed, value);

        /// <summary>Sets fragment <paramref name="index"/>'s mass (kg).</summary>
        public static void SetMass(IntPtr simulation, int index, float value) => Write(simulation, index, L.Mass, value);

        /// <summary>Sets how far fragment <paramref name="index"/> goes (m).</summary>
        public static void SetTravel(IntPtr simulation, int index, float value) => Write(simulation, index, L.Travel, value);

        /// <summary>Where fragment <paramref name="index"/> starts, in sim space.</summary>
        public static Vector3 Spawn(IntPtr simulation, int index) => Vector(simulation, index, L.Spawn);

        /// <summary>Fragment <paramref name="index"/>'s direction, in sim space.</summary>
        public static Vector3 Direction(IntPtr simulation, int index) => Vector(simulation, index, L.Direction);

        /// <summary>The fragment's own flags, as the game keeps them; <see cref="FragmentInfo.Flags"/> are converted.</summary>
        public static ushort RawFlags(IntPtr simulation, int index) => (ushort)Marshal.ReadInt16(At(simulation, index, L.Flags));

        /// <summary>Sets the fragment's own flags, as the game keeps them.</summary>
        public static void SetRawFlags(IntPtr simulation, int index, ushort value) => Marshal.WriteInt16(At(simulation, index, L.Flags), (short)value);
    }
}
