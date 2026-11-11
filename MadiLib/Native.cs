using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace MadiLib
{
    /// <summary>
    /// Reading game objects at their native offsets, for structs that
    /// Il2CppInterop wraps as classes (its arrays of them read garbage) and
    /// for fields it can't reach. Offsets are looked up by name at runtime, so
    /// they follow game updates; only the IL2CPP layout rules below are assumed.
    /// </summary>
    public static class Native
    {
        /// <summary>Bytes before an IL2CPP array's first element: class, monitor, bounds and length.</summary>
        public static readonly int ArrayHeader = 4 * IntPtr.Size;

        // An object starts with its class and monitor pointers, which an unboxed struct doesn't have
        private static readonly int objectHeader = 2 * IntPtr.Size;

        /// <summary>The offset of field <paramref name="name"/> in an object of class <typeparamref name="T"/>, from the object's start.</summary>
        public static int FieldOffset<T>(string name) =>
            (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(Il2CppClassPointerStore<T>.NativeClassPtr, name));

        /// <summary>
        /// The offset of field <paramref name="name"/> in struct <typeparamref name="T"/> as it sits unboxed:
        /// in an array, inside another object, or behind a pointer.
        /// </summary>
        public static int StructOffset<T>(string name) => FieldOffset<T>(name) - objectHeader;

        /// <summary>The size in bytes of struct <typeparamref name="T"/> unboxed, as an array element.</summary>
        public static int ValueSize<T>()
        {
            uint align = 0;
            return IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<T>.NativeClassPtr, ref align);
        }

        /// <summary>The number of elements in the IL2CPP array at <paramref name="array"/>.</summary>
        public static int Length(IntPtr array) => (int)Marshal.ReadIntPtr(array, 3 * IntPtr.Size);

        /// <summary>Element <paramref name="index"/> of the IL2CPP array at <paramref name="array"/>, whose elements are <paramref name="size"/> bytes.</summary>
        public static IntPtr Element(IntPtr array, int index, int size) => (IntPtr)((long)array + ArrayHeader + (long)index * size);
    }
}
