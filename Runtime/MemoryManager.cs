using System;
using System.Collections.Generic;
using MemoryToolkit.Buffers;
using MemoryToolkit.Diagnostics;
using MemoryToolkit.Pooling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

namespace MemoryToolkit
{
    /// <summary>
    /// Central memory handler. One place owns the memory policy, organized as
    /// three lifetime tiers (see <see cref="MemoryScope"/>):
    ///
    /// - <see cref="Permanent"/> — session-lifetime pools and allocations.
    ///   The static <see cref="GetPool"/>/<see cref="Warmup"/> helpers are
    ///   sugar for this scope.
    /// - Scene — <see cref="CreateSceneScope"/> returns a scope that is
    ///   disposed automatically when its scene unloads, releasing that level's
    ///   pools and native blocks in one deterministic step.
    /// - Frame — <see cref="FrameScratch"/>, a shared linear arena reset once
    ///   per frame for transient data.
    ///
    /// The manager also reacts to <see cref="Application.lowMemory"/> by
    /// trimming every live scope, unloading unused assets, and collecting —
    /// and exposes <see cref="CollectFull"/> for the one place a blocking GC
    /// is acceptable: loading screens.
    /// </summary>
    public static class MemoryManager
    {
        /// <summary>How many inactive instances each pool keeps after a low-memory trim.</summary>
        public static int LowMemoryKeepPerPool = 4;

        /// <summary>Default arena size for <see cref="FrameScratch"/> (1 MiB).</summary>
        public static int FrameScratchCapacityBytes = 1024 * 1024;

        /// <summary>Raised after a low-memory trim so game systems can drop their own caches.</summary>
        public static event Action LowMemoryTrimmed;

        private static readonly List<MemoryScope> Scopes = new();
        private static MemoryScope _permanent;
        private static FrameAllocator _frameScratch;
        private static MemoryManagerRunner _runner;

        /// <summary>Session-lifetime scope. Created on first use, disposed at shutdown.</summary>
        public static MemoryScope Permanent
        {
            get
            {
                if (_permanent == null || _permanent.IsDisposed)
                    _permanent = AddScope(new MemoryScope("Permanent", null));
                return _permanent;
            }
        }

        /// <summary>
        /// Creates a named scope. Pool lookups fall back to
        /// <paramref name="parent"/> (default: <see cref="Permanent"/>).
        /// Dispose it when its lifetime ends — e.g. end of a match or menu
        /// session — or use <see cref="CreateSceneScope"/> for scene lifetimes.
        /// </summary>
        public static MemoryScope CreateScope(string name, MemoryScope parent = null)
            => AddScope(new MemoryScope(name, parent ?? Permanent));

        /// <summary>
        /// Creates a scope bound to <paramref name="scene"/> (default: the
        /// active scene). It is disposed automatically when that scene
        /// unloads; disposing it manually earlier is also safe.
        /// </summary>
        public static MemoryScope CreateSceneScope(Scene scene = default)
        {
            if (!scene.IsValid())
                scene = SceneManager.GetActiveScene();

            MemoryScope scope = CreateScope($"Scene: {scene.name}");

            void OnSceneUnloaded(Scene unloaded)
            {
                if (unloaded == scene)
                    scope.Dispose(); // Disposed handler below unsubscribes
            }

            SceneManager.sceneUnloaded += OnSceneUnloaded;
            scope.Disposed += () => SceneManager.sceneUnloaded -= OnSceneUnloaded;
            return scope;
        }

        /// <summary>
        /// Shared per-frame linear allocator. Slices become invalid at the end
        /// of the frame — never cache them. Deliberately global, not
        /// per-scope: a per-scene frame arena invites slices that outlive
        /// their scope.
        /// </summary>
        public static FrameAllocator FrameScratch
        {
            get
            {
                if (_frameScratch == null)
                {
                    _frameScratch = new FrameAllocator(FrameScratchCapacityBytes);
                    EnsureRunner();
                }
                return _frameScratch;
            }
        }

        /// <summary>Shorthand for <c>Permanent.GetPool</c>.</summary>
        public static GameObjectPool GetPool(GameObject prefab, int defaultCapacity = 16, int maxSize = 256)
            => Permanent.GetPool(prefab, defaultCapacity, maxSize);

        /// <summary>Shorthand for <c>Permanent.Warmup</c>. Call from loading screens.</summary>
        public static void Warmup(GameObject prefab, int count, int maxSize = 256)
            => Permanent.Warmup(prefab, count, maxSize);

        /// <summary>
        /// Blocking full cleanup: unloads unused assets and runs a full GC.
        /// Call only behind a loading screen or scene transition — typically
        /// right after disposing the outgoing scene's scope.
        /// </summary>
        public static AsyncOperation CollectFull()
        {
            MemoryRecorder.RecordEvent(MemoryEventKind.CollectFull, "CollectFull");
            GC.Collect();
            return Resources.UnloadUnusedAssets();
        }

        /// <summary>Enumerates pool stats across all live scopes for diagnostics UIs.</summary>
        public static void GetPoolStats(List<PoolStat> results)
        {
            results.Clear();
            for (int i = 0; i < Scopes.Count; i++)
                Scopes[i].CollectStats(results);
        }

        public struct PoolStat
        {
            public string ScopeName;
            public string PrefabName;
            public int CountActive;
            public int CountInactive;
            public int CountAll;

            /// <summary>
            /// False when the pool was created lazily on a first <c>Get</c> rather
            /// than by <c>Warmup</c> — meaning its capacity came from a call site's
            /// guess and its first spawn cost an Instantiate during gameplay.
            /// </summary>
            public bool WasWarmedUp;
        }

        /// <summary>Live scopes, oldest first. For the Memory Inspector window.</summary>
        internal static IReadOnlyList<MemoryScope> LiveScopes => Scopes;

        /// <summary>
        /// The frame arena if one exists, without creating it. Diagnostics must
        /// observe rather than cause: reading <see cref="FrameScratch"/> allocates
        /// the arena on first access, so a recorder sampling it would conjure a
        /// megabyte of native memory into a game that never used the feature.
        /// </summary>
        internal static FrameAllocator FrameScratchOrNull => _frameScratch;

        internal static void OnLowMemory()
        {
            MemoryRecorder.RecordEvent(MemoryEventKind.LowMemory, "Application.lowMemory");
            for (int i = 0; i < Scopes.Count; i++)
                Scopes[i].Trim(LowMemoryKeepPerPool);
            Resources.UnloadUnusedAssets();
            GC.Collect();
            LowMemoryTrimmed?.Invoke();
        }

        internal static void ResetFrameScratch() => _frameScratch?.Reset();

        internal static void Shutdown()
        {
            // Dispose newest-first so child scopes go before Permanent; each
            // Dispose removes itself from the list via its Disposed handler.
            while (Scopes.Count > 0)
                Scopes[Scopes.Count - 1].Dispose();
            _permanent = null;
            _frameScratch?.Dispose();
            _frameScratch = null;
            _runner = null;
        }

        private static MemoryScope AddScope(MemoryScope scope)
        {
            Scopes.Add(scope);
            MemoryRecorder.RecordEvent(MemoryEventKind.ScopeCreated, scope.Name);
            scope.Disposed += () => Scopes.Remove(scope);
            EnsureRunner();
            return scope;
        }

        private static void EnsureRunner()
        {
            if (_runner != null || !Application.isPlaying) return;
            var go = new GameObject("[MemoryToolkit] Runner")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<MemoryManagerRunner>();
        }
    }

    /// <summary>
    /// Hidden driver for <see cref="MemoryManager"/>: per-frame arena reset,
    /// low-memory callback subscription, and teardown on application quit.
    /// </summary>
    [Preserve]
    [AddComponentMenu("")]
    internal sealed class MemoryManagerRunner : MonoBehaviour
    {
        private void OnEnable() => Application.lowMemory += MemoryManager.OnLowMemory;
        private void OnDisable() => Application.lowMemory -= MemoryManager.OnLowMemory;

        // LateUpdate rather than Update: consumers of this frame's scratch have
        // run; the arena must survive until everything that allocated from it
        // this frame has finished.
        private void LateUpdate()
        {
            // Sample before the reset, so the recorded arena usage is this frame's
            // real high-water mark rather than the zero it is about to become.
            MemoryRecorder.Tick();
            MemoryManager.ResetFrameScratch();
        }

        private void OnApplicationQuit()
        {
            MemoryManager.Shutdown();
            Destroy(gameObject);
        }
    }
}
