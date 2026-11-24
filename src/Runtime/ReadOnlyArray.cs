#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;

namespace HolySheet
{
    public readonly struct ReadOnlyArray<T> : IReadOnlyList<T>, IEquatable<ReadOnlyArray<T>>
    {
        private static readonly T[] EmptyArray = new T[0];

        private readonly T[]? _items;

        public ReadOnlyArray(T[]? items)
        {
            _items = items;
        }

        public static ReadOnlyArray<T> Empty => new ReadOnlyArray<T>(EmptyArray);

        public int Length => _items == null ? 0 : _items.Length;

        public bool IsEmpty => _items == null || _items.Length == 0;

        int IReadOnlyCollection<T>.Count => Length;

        public T this[int index]
        {
            get
            {
                T[]? items = _items;
                if (items == null || (uint)index >= (uint)items.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return items[index];
            }
        }

        public ref readonly T ItemRef(int index)
        {
            T[]? items = _items;
            if (items == null || (uint)index >= (uint)items.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return ref items[index];
        }

        public ReadOnlySpan<T> AsSpan()
        {
            return new ReadOnlySpan<T>(_items);
        }

        public ReadOnlyMemory<T> AsMemory()
        {
            return new ReadOnlyMemory<T>(_items);
        }

        public T[] ToArray()
        {
            T[]? items = _items;
            if (items == null || items.Length == 0)
            {
                return EmptyArray;
            }

            T[] copy = new T[items.Length];
            Array.Copy(items, copy, items.Length);
            return copy;
        }

        public int IndexOf(T value)
        {
            return _items == null ? -1 : Array.IndexOf(_items, value);
        }

        public bool Contains(T value)
        {
            return IndexOf(value) >= 0;
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(_items);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new Enumerator(_items);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new Enumerator(_items);
        }

        public bool Equals(ReadOnlyArray<T> other)
        {
            return ReferenceEquals(_items, other._items);
        }

        public override bool Equals(object? obj)
        {
            return obj is ReadOnlyArray<T> other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _items == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_items);
        }

        public static bool operator ==(ReadOnlyArray<T> left, ReadOnlyArray<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ReadOnlyArray<T> left, ReadOnlyArray<T> right)
        {
            return !left.Equals(right);
        }

        public static implicit operator ReadOnlySpan<T>(ReadOnlyArray<T> array)
        {
            return array.AsSpan();
        }

        public struct Enumerator : IEnumerator<T>
        {
            private readonly T[]? _items;
            private int _index;

            internal Enumerator(T[]? items)
            {
                _items = items;
                _index = -1;
            }

            public T Current => _items![_index];

            object? IEnumerator.Current => Current;

            public bool MoveNext()
            {
                int next = _index + 1;
                if (_items != null && next < _items.Length)
                {
                    _index = next;
                    return true;
                }

                return false;
            }

            public void Reset()
            {
                _index = -1;
            }

            public void Dispose()
            {
            }
        }
    }
}
