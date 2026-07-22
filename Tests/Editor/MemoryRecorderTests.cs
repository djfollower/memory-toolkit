using System.Collections.Generic;
using MemoryToolkit.Diagnostics;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    public class MemoryRingTests
    {
        [Test]
        public void Add_BeyondCapacity_KeepsNewestOldestFirst()
        {
            var ring = new MemoryRing<int>(3);
            for (int i = 1; i <= 5; i++) ring.Add(i);

            Assert.That(ring.Count, Is.EqualTo(3));
            Assert.That(ring[0], Is.EqualTo(3), "oldest retained");
            Assert.That(ring[1], Is.EqualTo(4));
            Assert.That(ring[2], Is.EqualTo(5), "newest");
        }

        [Test]
        public void Add_BelowCapacity_IndexesFromZero()
        {
            var ring = new MemoryRing<int>(4);
            ring.Add(7);
            ring.Add(8);

            Assert.That(ring.Count, Is.EqualTo(2));
            Assert.That(ring[0], Is.EqualTo(7));
            Assert.That(ring[1], Is.EqualTo(8));
        }
    }

    public class MemoryRecorderTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("RecorderTestPrefab");
            MemoryRecorder.Enable(sampleCapacity: 32, eventCapacity: 32);
            // Sample on every Tick rather than waiting out wall-clock time.
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
        public void RecordsScopeLifetimeEvents()
        {
            MemoryScope scope = MemoryManager.CreateScope("Match");
            scope.Dispose();

            Assert.That(HasEvent(MemoryEventKind.ScopeCreated, "Match"), Is.True);
            Assert.That(HasEvent(MemoryEventKind.ScopeDisposed, "Match"), Is.True);
        }

        [Test]
        public void RecordsPoolCreationAndWarmup()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Warmup(_prefab, 4);

            Assert.That(HasEvent(MemoryEventKind.PoolCreated, "RecorderTestPrefab"), Is.True);
            Assert.That(HasEvent(MemoryEventKind.PoolWarmedUp, "RecorderTestPrefab"), Is.True);
        }

        // The reason the recorder exists: the value that sizes a warm-up count is the
        // peak over time, which no snapshot of the current frame can report.
        [Test]
        public void PeakActive_SurvivesAfterInstancesAreReleased()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            GameObjectPool pool = scope.GetPool(_prefab);

            var taken = new List<GameObject>();
            for (int i = 0; i < 5; i++) taken.Add(pool.Get());
            MemoryRecorder.Tick();

            foreach (GameObject instance in taken) pool.Release(instance);
            MemoryRecorder.Tick();

            PoolSeries series = FindSeries("RecorderTestPrefab");
            Assert.That(series, Is.Not.Null);
            Assert.That(series.Samples[series.Samples.Count - 1].Active, Is.EqualTo(0),
                "current occupancy is back to zero");
            Assert.That(series.PeakActive, Is.EqualTo(5),
                "but the peak that should size the warm-up is still recorded");
        }

        [Test]
        public void SeriesGoesDead_WhenItsScopeIsDisposed()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.GetPool(_prefab).Warmup(2);
            MemoryRecorder.Tick();
            Assert.That(FindSeries("RecorderTestPrefab").Alive, Is.True);

            scope.Dispose();
            MemoryRecorder.Tick();

            Assert.That(FindSeries("RecorderTestPrefab").Alive, Is.False,
                "the row must end visibly, not silently disappear");
        }

        [Test]
        public void Dump_ReportsPoolsAndEvents()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Warmup(_prefab, 2);
            MemoryRecorder.Tick();

            string dump = MemoryRecorder.Dump();

            Assert.That(dump, Does.Contain("RecorderTestPrefab"));
            Assert.That(dump, Does.Contain("ScopeCreated"));
        }

        [Test]
        public void Tick_DoesNothing_WhenNotRecording()
        {
            MemoryRecorder.Disable();
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Warmup(_prefab, 2);
            MemoryRecorder.Tick();

            Assert.That(HasEvent(MemoryEventKind.ScopeCreated, "Level"), Is.False);
        }

        private static bool HasEvent(MemoryEventKind kind, string label)
        {
            MemoryRing<MemoryEvent> events = MemoryRecorder.Events;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Kind == kind && events[i].Label == label) return true;
            }
            return false;
        }

        private static PoolSeries FindSeries(string prefabName)
        {
            IReadOnlyList<PoolSeries> series = MemoryRecorder.PoolSeriesList;
            for (int i = 0; i < series.Count; i++)
            {
                if (series[i].PrefabName == prefabName) return series[i];
            }
            return null;
        }
    }
}
