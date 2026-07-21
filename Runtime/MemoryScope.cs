using System;
using System.Collections.Generic;
using MemoryToolkit.Buffers;
using MemoryToolkit.Pooling;
using UnityEngine;

namespace MemoryToolkit
{
    /// <summary>
    /// A lifetime layer for memory: owns pools, arenas, and arbitrary
    /// disposables, and frees them all at once when disposed. Grouping
    /// allocations by lifetime is the structural fix for fragmentation —
    /// things that die together are released together, instead of punching
    /// random holes in the heap over a long session.
    ///
    /// The toolkit uses three tiers. Resist adding more; extra tiers create
    /// ownership ambiguity that causes more leaks than they prevent:
    /// - Permanent: <see cref="MemoryManager.Permanent"/>, lives for the session.
    /// - Scene: <see cref="MemoryManager.CreateSceneScope"/>, auto-disposed on
    ///   scene unload (or dispose manually for match/menu lifetimes).
    /// - Frame: <see cref="MemoryManager.FrameScratch"/>, reset every frame.
    ///   Deliberately NOT scoped — per-scope frame arenas invite slices that
    ///   outlive their scope.
    /// </summary>
    public sealed class MemoryScope : IDisposable
    {
        public string Name { get; }
        public bool IsDisposed { get; private set; }

        /// <summary>Lookup falls back to the parent chain; ownership does not.</summary>
        internal MemoryScope Parent { get; }

        internal event Action Disposed;

        private readonly Dictionary<GameObject, GameObjectPool> _pools = new();
        private readonly List<IDisposable> _owned = new();
        private readonly List<FrameAllocator> _allocators = new();
        private readonly List<UnityEngine.Object> _pinned = new();

        internal MemoryScope(string name, MemoryScope parent)
        {
            Name = name;
            Parent = parent;
        }

        /// <summary>
        /// Returns the pool for <paramref name="prefab"/>, checking this scope
        /// first and then the parent chain, so a prefab already pooled
        /// permanently is never duplicated per scene. When no scope in the
        /// chain has one, the pool is created in — and owned by — this scope.
        /// </summary>
        public GameObjectPool GetPool(GameObject prefab, int defaultCapacity = 16, int maxSize = 256)
        {
            ThrowIfDisposed();
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            for (MemoryScope scope = this; scope != null; scope = scope.Parent)
            {
                if (!scope.IsDisposed && scope._pools.TryGetValue(prefab, out GameObjectPool existing))
                    return existing;
            }

            var pool = new GameObjectPool(prefab, defaultCapacity, maxSize);
            _pools.Add(prefab, pool);
            return pool;
        }

        /// <summary>Pre-instantiates instances for a prefab in this scope. Call during loads.</summary>
        public void Warmup(GameObject prefab, int count, int maxSize = 256)
            => GetPool(prefab, count, Mathf.Max(count, maxSize)).Warmup(count);

        /// <summary>
        /// Creates a linear allocator whose backing block is freed when this
        /// scope is disposed. For persistent-lifetime scratch within the scope;
        /// per-frame data belongs in <see cref="MemoryManager.FrameScratch"/>.
        /// </summary>
        public FrameAllocator CreateAllocator(int capacityBytes)
        {
            FrameAllocator allocator = Register(new FrameAllocator(capacityBytes));
            _allocators.Add(allocator);
            return allocator;
        }

        /// <summary>
        /// Pins an asset to this scope's lifetime: the scope holds a strong
        /// reference so <c>Resources.UnloadUnusedAssets</c> (and thus
        /// <see cref="MemoryManager.CollectFull"/>) cannot reclaim it while the
        /// scope lives. This is how assets loaded in a momentary scene (a login
        /// or bootstrap scene) are kept in permanent memory: load them there,
        /// but pin them to <see cref="MemoryManager.Permanent"/> — ownership
        /// follows lifetime, not the scene that happened to do the loading.
        ///
        /// Disposing the scope drops the references; the memory is reclaimed
        /// by the next unused-assets sweep (e.g. <c>CollectFull</c> behind the
        /// next load), not destroyed immediately — pinning marks ownership, it
        /// does not force-unload shared assets other systems may still use.
        /// Returns the asset for inline use.
        /// </summary>
        public T Pin<T>(T asset) where T : UnityEngine.Object
        {
            ThrowIfDisposed();
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (!_pinned.Contains(asset))
                _pinned.Add(asset);
            return asset;
        }

        /// <summary>
        /// Ties any disposable's lifetime to this scope (native containers,
        /// render textures wrapped in a disposable, etc.). Returns the argument
        /// for inline use. Note: registering a struct disposable boxes it once
        /// here — negligible, but prefer registering the owning class object.
        /// </summary>
        public T Register<T>(T disposable) where T : IDisposable
        {
            ThrowIfDisposed();
            if (disposable == null) throw new ArgumentNullException(nameof(disposable));
            _owned.Add(disposable);
            return disposable;
        }

        /// <summary>Trims every pool owned by this scope. See <see cref="GameObjectPool.Trim"/>.</summary>
        public void Trim(int keepPerPool)
        {
            if (IsDisposed) return;
            foreach (GameObjectPool pool in _pools.Values)
                pool.Trim(keepPerPool);
        }

        // Read-only views for the Memory Inspector window.
        internal IReadOnlyList<FrameAllocator> Allocators => _allocators;
        internal IReadOnlyList<UnityEngine.Object> PinnedAssets => _pinned;
        internal int OwnedDisposableCount => _owned.Count;

        internal void CollectStats(List<MemoryManager.PoolStat> results)
        {
            foreach (KeyValuePair<GameObject, GameObjectPool> kvp in _pools)
            {
                results.Add(new MemoryManager.PoolStat
                {
                    ScopeName = Name,
                    PrefabName = kvp.Key != null ? kvp.Key.name : "(destroyed prefab)",
                    CountActive = kvp.Value.CountActive,
                    CountInactive = kvp.Value.CountInactive,
                    CountAll = kvp.Value.CountAll,
                });
            }
        }

        /// <summary>
        /// Frees everything the scope owns: pooled instances are destroyed and
        /// registered disposables are disposed in reverse registration order.
        /// Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            foreach (GameObjectPool pool in _pools.Values)
                pool.Dispose();
            _pools.Clear();

            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try
                {
                    _owned[i].Dispose();
                }
                catch (Exception e)
                {
                    // One faulty disposable must not leak the rest of the scope.
                    Debug.LogException(e);
                }
            }
            _owned.Clear();
            _allocators.Clear();

            // Drop pin references only; the next unused-assets sweep reclaims
            // anything no longer referenced elsewhere.
            _pinned.Clear();

            Disposed?.Invoke();
            Disposed = null;
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException($"MemoryScope '{Name}'");
        }
    }
}
