using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace libmsstyle.Import
{
    /// <summary>
    /// Identity comparison for reference types. <see cref="StyleProperty"/> overrides Equals/GetHashCode with its
    /// header and value, but the importer tracks individual records and mutates their values in place: a set or
    /// dictionary of them has to follow the object, not the value it happens to hold right now.
    /// </summary>
    internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

        public bool Equals(T x, T y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
