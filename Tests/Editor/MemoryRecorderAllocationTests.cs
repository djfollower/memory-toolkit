using MemoryToolkit.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// The recorder samples every quarter second for the whole session. If a tick
    /// allocates, the tool generates the garbage it exists to report — so this is a
    /// correctness test, not a performance one.
    ///
    /// <para>In its own file because it aliases <c>Is</c> to the Unity constraint
    /// set, which would shadow NUnit's <c>Is</c> for every other assertion.</para>
    /// </summary>
    public class MemoryRecorderAllocationTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("AllocTestPrefab");
            MemoryRecorder.Enable(sampleCapacity: 32, eventCapacity: 32);
            MemoryRecorder.SampleIntervalSeconds = 0d;
        }

        [TearDown]
        public void TearDown()
        {
            MemoryRecorder.Disable();
            MemoryRecorder.SampleIntervalSeconds = 0.25d;
            MemoryManager.Shutdown();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void Tick_DoesNotAllocate_InSteadyState()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Warmup(_prefab, 3);

            // First ticks legitimately allocate: the series row and its ring buffer
            // are created on first sighting. Steady state is what must stay clean.
            for (int i = 0; i < 4; i++)
                MemoryRecorder.Tick();

            Assert.That(() => MemoryRecorder.Tick(), Is.Not.AllocatingGCMemory());
        }
    }
}
