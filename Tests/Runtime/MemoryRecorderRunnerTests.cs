using System.Collections;
using MemoryToolkit.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Play-mode coverage of the one wire the edit-mode tests cannot exercise: that
    /// something actually calls <see cref="MemoryRecorder.Tick"/> each frame. Every
    /// other test drives Tick directly, so a broken hook-up in the runner would
    /// leave the whole feature dead with a fully green suite.
    /// </summary>
    public class MemoryRecorderRunnerTests
    {
        [UnityTest]
        public IEnumerator RunnerDrivesTheRecorder_WithoutBeingCalledManually()
        {
            var prefab = new GameObject("RunnerTestPrefab");
            MemoryRecorder.Enable(sampleCapacity: 16, eventCapacity: 16);
            MemoryRecorder.SampleIntervalSeconds = 0d;

            try
            {
                // Creating a scope spins up MemoryManagerRunner, which owns the tick.
                MemoryScope scope = MemoryManager.CreateScope("Level");
                scope.Warmup(prefab, 2);

                Assert.That(MemoryRecorder.GlobalSamples.Count, Is.EqualTo(0),
                    "nothing should be sampled before a frame elapses");

                yield return null;
                yield return null;

                Assert.That(MemoryRecorder.GlobalSamples.Count, Is.GreaterThan(0),
                    "LateUpdate should have driven at least one sample");
            }
            finally
            {
                MemoryRecorder.Disable();
                MemoryRecorder.SampleIntervalSeconds = 0.25d;
                MemoryManager.Shutdown();
                Object.DestroyImmediate(prefab);
            }
        }
    }
}
