using System;
using MemoryToolkit.Buffers;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace MemoryToolkit.Tests
{
    public class FrameAllocatorTests
    {
        [Test]
        public void Allocate_ReturnsWritableSlice_OfRequestedLength()
        {
            using var allocator = new FrameAllocator(1024);

            NativeArray<float> floats = allocator.Allocate<float>(16);
            for (int i = 0; i < floats.Length; i++)
                floats[i] = i;

            Assert.That(floats.Length, Is.EqualTo(16));
            Assert.That(floats[15], Is.EqualTo(15f));
        }

        [Test]
        public void Allocations_DoNotOverlap()
        {
            using var allocator = new FrameAllocator(1024);

            NativeArray<int> a = allocator.Allocate<int>(4);
            NativeArray<int> b = allocator.Allocate<int>(4);
            for (int i = 0; i < 4; i++) { a[i] = 1; b[i] = 2; }

            for (int i = 0; i < 4; i++)
                Assert.That(a[i], Is.EqualTo(1), "second allocation overwrote the first");
        }

        [Test]
        public void Allocate_RespectsAlignment()
        {
            using var allocator = new FrameAllocator(1024);

            allocator.Allocate<byte>(3); // leaves offset at 3, misaligned for larger types
            allocator.Allocate<Vector4>(1);

            // Vector4 alignment is 4; offset 3 must have been rounded up to 4.
            Assert.That(allocator.UsedBytes, Is.EqualTo(4 + 16));
        }

        [Test]
        public void Reset_RecyclesArena_AndTracksPeak()
        {
            using var allocator = new FrameAllocator(1024);

            allocator.Allocate<byte>(100);
            allocator.Reset();

            Assert.That(allocator.UsedBytes, Is.EqualTo(0));
            Assert.That(allocator.PeakUsedBytes, Is.EqualTo(100));

            NativeArray<byte> again = allocator.Allocate<byte>(100);
            Assert.That(again.Length, Is.EqualTo(100));
        }

        [Test]
        public void Allocate_Throws_WhenExhausted()
        {
            using var allocator = new FrameAllocator(64);

            Assert.Throws<InvalidOperationException>(() => allocator.Allocate<byte>(65));
        }

        [Test]
        public void Allocate_Throws_AfterDispose()
        {
            var allocator = new FrameAllocator(64);
            allocator.Dispose();

            Assert.Throws<ObjectDisposedException>(() => allocator.Allocate<byte>(1));
        }
    }

    public class StringBuilderCacheTests
    {
        [Test]
        public void Acquire_AfterRelease_ReusesSameBuilder()
        {
            var sb = StringBuilderCache.Acquire();
            StringBuilderCache.Release(sb);

            var again = StringBuilderCache.Acquire();
            Assert.That(again, Is.SameAs(sb));
        }

        [Test]
        public void GetStringAndRelease_ReturnsContent_AndCachesBuilder()
        {
            var sb = StringBuilderCache.Acquire();
            sb.Append("Score: ").Append(42);

            Assert.That(StringBuilderCache.GetStringAndRelease(sb), Is.EqualTo("Score: 42"));
            Assert.That(StringBuilderCache.Acquire(), Is.SameAs(sb));
        }
    }
}
