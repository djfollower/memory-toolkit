using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace MemoryToolkit.Buffers
{
    /// <summary>
    /// A linear (bump/arena) allocator over a single persistent native block.
    ///
    /// Fragmentation cannot occur inside this allocator by construction: every
    /// allocation is a bump of an offset into one contiguous block, and the
    /// whole arena is recycled with a single <see cref="Reset"/> at a known
    /// point (typically once per frame). Use it for transient per-frame data —
    /// scratch vertex lists, query results, staging buffers — instead of
    /// allocating temporary arrays on the managed heap or with
    /// <c>Allocator.Temp</c> in hot loops.
    ///
    /// Not thread-safe; use one instance per thread or allocate from the main
    /// thread only. Slices returned by <see cref="Allocate{T}"/> are invalid
    /// after <see cref="Reset"/> or <see cref="Dispose"/> — never store them
    /// across frames.
    /// </summary>
    public sealed class FrameAllocator : IDisposable
    {
        private NativeArray<byte> _block;
        private int _offset;

        /// <summary>Total capacity in bytes.</summary>
        public int CapacityBytes => _block.IsCreated ? _block.Length : 0;

        /// <summary>Bytes currently allocated since the last reset.</summary>
        public int UsedBytes => _offset;

        /// <summary>High-water mark since creation; use it to size the arena.</summary>
        public int PeakUsedBytes { get; private set; }

        public FrameAllocator(int capacityBytes)
        {
            if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            _block = new NativeArray<byte>(capacityBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>
        /// Allocates <paramref name="count"/> elements of unmanaged type
        /// <typeparamref name="T"/>, properly aligned. Contents are undefined.
        /// Throws <see cref="InvalidOperationException"/> when the arena is
        /// exhausted — size the arena from <see cref="PeakUsedBytes"/> rather
        /// than growing at runtime.
        /// </summary>
        public NativeArray<T> Allocate<T>(int count) where T : unmanaged
        {
            if (!_block.IsCreated) throw new ObjectDisposedException(nameof(FrameAllocator));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            int align = UnsafeUtility.AlignOf<T>();
            int aligned = (_offset + align - 1) & ~(align - 1);
            int sizeBytes = count * UnsafeUtility.SizeOf<T>();

            if (aligned + sizeBytes > _block.Length)
                throw new InvalidOperationException(
                    $"FrameAllocator exhausted: requested {sizeBytes} B at offset {aligned}, capacity {_block.Length} B. " +
                    "Increase capacity (see PeakUsedBytes) or Reset more often.");

            _offset = aligned + sizeBytes;
            if (_offset > PeakUsedBytes) PeakUsedBytes = _offset;

            return _block.GetSubArray(aligned, sizeBytes).Reinterpret<T>(sizeof(byte));
        }

        /// <summary>
        /// Recycles the entire arena in O(1). Call once per frame (e.g. from a
        /// central manager's Update) after all consumers of the previous
        /// frame's slices are done with them.
        /// </summary>
        public void Reset() => _offset = 0;

        public void Dispose()
        {
            if (_block.IsCreated)
                _block.Dispose();
            _offset = 0;
        }
    }
}
