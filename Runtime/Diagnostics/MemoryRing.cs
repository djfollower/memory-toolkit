using System;

namespace MemoryToolkit.Diagnostics
{
    /// <summary>
    /// Fixed-capacity circular buffer of value types, indexed oldest-first.
    ///
    /// <para>Sized once and never resized: a diagnostic that allocates while
    /// sampling changes the thing it is measuring. Overwriting the oldest entry
    /// is the intended behaviour — the recorder answers "what happened recently",
    /// and an unbounded history in a shipping build is itself a leak.</para>
    /// </summary>
    internal sealed class MemoryRing<T> where T : struct
    {
        private readonly T[] _items;
        private int _head; // index of the next write
        private int _count;

        internal MemoryRing(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new T[capacity];
        }

        internal int Count => _count;
        internal int Capacity => _items.Length;

        /// <summary>Oldest-first: <c>this[0]</c> is the oldest retained entry.</summary>
        internal ref T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
                int start = _count == _items.Length ? _head : 0;
                return ref _items[(start + index) % _items.Length];
            }
        }

        internal void Add(in T item)
        {
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }

        internal void Clear()
        {
            _head = 0;
            _count = 0;
            Array.Clear(_items, 0, _items.Length);
        }
    }
}
