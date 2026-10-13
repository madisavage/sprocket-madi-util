using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace MadiLib
{
    /// <summary>
    /// One <typeparamref name="T"/> per game object, such as the properties an
    /// editor panel draws for each blueprint. Caching them keeps their
    /// callbacks alive and their state across redraws.
    /// </summary>
    /// <remarks>
    /// Keyed by native pointer, so wrappers of the same object share an entry.
    /// Entries are never removed.
    /// </remarks>
    public sealed class ObjectCache<T>
    {
        private readonly Dictionary<IntPtr, T> values = new();

        /// <summary>The value for <paramref name="owner"/>, made by <paramref name="create"/> on first use.</summary>
        public T Get(Il2CppObjectBase owner, Func<T> create)
        {
            if (!values.TryGetValue(owner.Pointer, out T? value))
                values[owner.Pointer] = value = create();
            return value;
        }
    }
}
