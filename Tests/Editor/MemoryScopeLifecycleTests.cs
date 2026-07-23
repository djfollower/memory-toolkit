using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers binding a scope's disposal to something that is not a scene unload.
    /// These exist because a lifetime the toolkit cannot express is a lifetime
    /// someone hand-rolls beside it, and two systems that disagree about ownership
    /// leak worse than one that never existed.
    /// </summary>
    public class MemoryScopeLifecycleTests
    {
        [TearDown]
        public void TearDown() => MemoryManager.Permanent.Dispose();

        // ---- OnDisposed ---------------------------------------------------------------

        [Test]
        public void OnDisposed_RunsOnDisposal()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            bool ran = false;
            scope.OnDisposed(() => ran = true);

            scope.Dispose();

            Assert.That(ran, Is.True);
        }

        [Test]
        public void OnDisposed_RunsImmediately_WhenTheScopeAlreadyEnded()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Dispose();

            bool ran = false;
            scope.OnDisposed(() => ran = true);

            Assert.That(ran, Is.True,
                "integration code subscribes from outside; 'it already ended' is a race, not an error, " +
                "and never firing would leave the subscriber waiting on an event that has been and gone");
        }

        // ---- AttachTo(GameObject) -----------------------------------------------------

        [Test]
        public void AttachTo_DisposesTheScopeWhenTheHostIsDestroyed()
        {
            var host = new GameObject("LevelRoot");
            MemoryScope scope = MemoryManager.CreateScope("Level").AttachTo(host);

            Assert.That(scope.IsDisposed, Is.False);
            Object.DestroyImmediate(host);

            Assert.That(scope.IsDisposed, Is.True);
        }

        [Test]
        public void AttachTo_RefusesASecondScopeOnTheSameHost()
        {
            var host = new GameObject("LevelRoot");
            MemoryManager.CreateScope("First").AttachTo(host);
            MemoryScope second = MemoryManager.CreateScope("Second");

            // Silently overwriting would drop the first scope's disposal and leak all
            // of it with no symptom at the call site.
            Assert.Throws<InvalidOperationException>(() => second.AttachTo(host));

            Object.DestroyImmediate(host);
        }

        [Test]
        public void AttachTo_IsIdempotentForTheSameScope()
        {
            var host = new GameObject("LevelRoot");
            MemoryScope scope = MemoryManager.CreateScope("Level");

            scope.AttachTo(host);
            Assert.DoesNotThrow(() => scope.AttachTo(host));

            Object.DestroyImmediate(host);
            Assert.That(scope.IsDisposed, Is.True);
        }

        [Test]
        public void AttachTo_RejectsADisposedScope()
        {
            var host = new GameObject("LevelRoot");
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Dispose();

            Assert.Throws<ObjectDisposedException>(() => scope.AttachTo(host));

            Object.DestroyImmediate(host);
        }

        [Test]
        public void DisposingTheScopeFirst_LeavesTheHostHarmless()
        {
            var host = new GameObject("LevelRoot");
            MemoryScope scope = MemoryManager.CreateScope("Level").AttachTo(host);

            scope.Dispose();

            // The flow manager tore the scope down before the object; destroying the
            // host afterwards must not throw during shutdown.
            Assert.DoesNotThrow(() => Object.DestroyImmediate(host));
        }

        // ---- DisposeWhen --------------------------------------------------------------

        [Test]
        public void DisposeWhen_DisposesOnTheProjectsOwnEvent()
        {
            var flow = new FakeFlow();
            MemoryScope scope = MemoryManager.CreateScope("Match")
                .DisposeWhen(h => flow.SessionEnded += h, h => flow.SessionEnded -= h);

            Assert.That(scope.IsDisposed, Is.False);
            flow.End();

            Assert.That(scope.IsDisposed, Is.True);
        }

        [Test]
        public void DisposeWhen_UnsubscribesOnDisposal_SoAnEarlyDisposeDoesNotLeak()
        {
            var flow = new FakeFlow();
            MemoryScope scope = MemoryManager.CreateScope("Match")
                .DisposeWhen(h => flow.SessionEnded += h, h => flow.SessionEnded -= h);

            scope.Dispose();

            Assert.That(flow.HandlerCount, Is.Zero,
                "a scope disposed early must not stay alive on an event that outlives it — " +
                "that is the leak this method is most often used to fix");
        }

        [Test]
        public void AttachTo_RejectsAnInvalidScene()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");

            Assert.Throws<ArgumentException>(() => scope.AttachTo(default(UnityEngine.SceneManagement.Scene)));
        }

        private sealed class FakeFlow
        {
            public event Action SessionEnded;
            public int HandlerCount => SessionEnded?.GetInvocationList().Length ?? 0;
            public void End() => SessionEnded?.Invoke();
        }
    }
}
