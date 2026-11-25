//
// Copyright (c) 2026 Rex Woodfield and DivisionCompute contributors
//
// This file is part of DivisionCompute and is subject to the terms
// of the DivisionCompute License. See the LICENSE.txt file in the
// project root for full license terms.
//
using System.Collections;

namespace DivisionCompute.SourceGenerators
{
    /// <summary>
    /// A small immutable array with value equality, so incremental generator models
    /// compare by content instead of by array reference.
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        private readonly T[]? items;

        public EquatableArray(T[] items) => this.items = items;

        public static EquatableArray<T> Empty => new([]);

        public int Count => items?.Length ?? 0;

        public T this[int index] => items![index];

        public bool Equals(EquatableArray<T> other)
        {
            T[] self = items ?? [];
            T[] otherItems = other.items ?? [];
            if (self.Length != otherItems.Length) return false;

            for (int i = 0; i < self.Length; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(self[i], otherItems[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            T[] self = items ?? [];
            var hash = new HashCode();
            foreach (T item in self)
                hash.Add(item);

            return hash.ToHashCode();
        }

        public IEnumerator<T> GetEnumerator()
            => ((IEnumerable<T>)(items ?? [])).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
