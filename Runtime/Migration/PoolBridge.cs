using System;
using MemoryToolkit.Pooling;
using UnityEngine;

namespace MemoryToolkit.Migration
{
    /// <summary>What to do when an instance arrives that this bridge does not own.</summary>
    public enum UnknownInstancePolicy
    {
        /// <summary>Log a warning and destroy it — matches what most hand-rolled pools already do, so behaviour does not change on day one.</summary>
        LogAndDestroy,

        /// <summary>Destroy it silently. Use only once the count is known and understood.</summary>
        Destroy,

        /// <summary>Throw. Use in CI and development builds once the migration should be complete.</summary>
        Throw,

        /// <summary>Leave it alone — the other registry still owns it. Use while both registries are live.</summary>
        Ignore,
    }

    /// <summary>
    /// A drop-in backing implementation for a project's existing global pool API.
    ///
    /// <para>Brownfield projects do not have a pooling problem, they have a
    /// <i>migration</i> problem: the pool is typically reached through a handful of
    /// extension methods (<c>prefab.GetFromPool()</c>, <c>instance.ReturnToPool()</c>)
    /// called from hundreds of places. Rewriting every call site is not a landable
    /// change, so the toolkit has to be able to run <i>underneath</i> the existing
    /// API instead of replacing it. Re-point those few extension methods at this
    /// bridge and every call site keeps working, now backed by scope-owned pools.</para>
    ///
    /// <code>
    /// // The project's existing extension methods become one-line delegations:
    /// public static GameObject GetFromPool(this GameObject prefab) =&gt; PoolBridge.Get(prefab);
    /// public static void ReturnToPool(this GameObject instance)   =&gt; PoolBridge.Return(instance);
    /// public static bool AlreadyReturned(this GameObject instance) =&gt; PoolBridge.IsPooled(instance);
    /// </code>
    ///
    /// <para>Three failure modes of the typical incumbent are fixed structurally by
    /// doing this, without touching a single call site:</para>
    /// <list type="bullet">
    /// <item><b>The registry stops being scene-owned.</b> Hand-rolled pools usually
    /// keep a static dictionary alongside a plain <c>GameObject</c> pool root; when
    /// that root dies with a scene load it takes the registry with it, and the pool
    /// silently degrades into Instantiate/Destroy plus lookup overhead. Here pools
    /// are owned by a <see cref="MemoryScope"/> whose lifetime is chosen
    /// deliberately, per prefab, via <see cref="ScopeResolver"/>.</item>
    /// <item><b>Instance identity stops depending on a lookup table.</b> An
    /// instance's owning pool travels on the instance itself
    /// (<see cref="PooledInstance"/>), so releasing works even if every registry in
    /// the game were cleared — and pool keys never go stale when an Addressable
    /// asset is released and reloaded.</item>
    /// <item><b>Release is always reparented and always O(1) double-release safe</b>
    /// (see <see cref="GameObjectPool.Release(GameObject)"/>), so the defensive
    /// "have I already returned this?" guards that accumulate at call sites can be
    /// deleted rather than reimplemented.</item>
    /// </list>
    ///
    /// <para>This type is deliberately a migration aid, not a long-term API. New
    /// code should take a <see cref="GameObjectPool"/> from a scope directly; a
    /// global facade cannot express which scope owns what, which is the decision
    /// that matters. See <c>docs/INTEGRATION.md</c>.</para>
    /// </summary>
    public static class PoolBridge
    {
        /// <summary>
        /// Chooses the scope that owns the pool for a given prefab. Defaults to
        /// <see cref="MemoryManager.Permanent"/>, which is the safe starting point
        /// for a migration: a permanent pool is never wiped mid-session, which is
        /// usually the incumbent's worst bug. Narrow it per prefab afterwards —
        /// returning a scene or match scope for prefabs that genuinely die with a
        /// level is how the migration actually reclaims memory.
        /// </summary>
        /// <example>
        /// <code>
        /// PoolBridge.ScopeResolver = prefab =&gt;
        ///     BattleScope != null &amp;&amp; BattlePrefabs.Contains(prefab)
        ///         ? BattleScope
        ///         : MemoryManager.Permanent;
        /// </code>
        /// </example>
        public static Func<GameObject, MemoryScope> ScopeResolver { get; set; }

        /// <summary>
        /// How <see cref="Return"/> treats an instance this bridge does not own.
        /// Start at <see cref="UnknownInstancePolicy.LogAndDestroy"/> (day-one
        /// behaviour parity), move to <see cref="UnknownInstancePolicy.Ignore"/>
        /// while both registries are live, and finish at
        /// <see cref="UnknownInstancePolicy.Throw"/> once migration is complete.
        /// </summary>
        public static UnknownInstancePolicy UnknownInstances { get; set; } = UnknownInstancePolicy.LogAndDestroy;

        /// <summary>Default capacity for pools created lazily. Prefer <see cref="Warmup"/>.</summary>
        public static int DefaultCapacity { get; set; } = 16;

        /// <summary>Default max retained instances for pools created lazily.</summary>
        public static int DefaultMaxSize { get; set; } = 256;

        /// <summary>
        /// How many instances reached <see cref="Return"/> without belonging to any
        /// toolkit pool. <b>This is the migration's headline metric.</b> Before
        /// changing anything, make the incumbent's equivalent fallback path count
        /// too: a non-zero number here (or there) means the pool is falling back to
        /// Destroy, which costs more than not pooling at all. Watch it go to zero.
        /// </summary>
        public static int UnknownInstanceCount { get; private set; }

        /// <summary>
        /// How many pools were created lazily by a <see cref="Get"/> rather than by
        /// <see cref="Warmup"/>. Each one paid an Instantiate during gameplay and
        /// took its capacity from whichever call site happened to run first.
        /// </summary>
        public static int LazyPoolCount { get; private set; }

        /// <summary>Total instances handed out, for sanity-checking against the incumbent.</summary>
        public static int GetCount { get; private set; }

        /// <summary>Total instances returned successfully.</summary>
        public static int ReturnCount { get; private set; }

        /// <summary>Pre-instantiates a prefab's pool in its resolved scope. Call from loading screens.</summary>
        public static void Warmup(GameObject prefab, int count, int maxSize = 0)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            ResolveScope(prefab).Warmup(prefab, count, maxSize > 0 ? maxSize : Mathf.Max(count, DefaultMaxSize));
        }

        /// <summary>Takes an instance at the prefab's default transform.</summary>
        public static GameObject Get(GameObject prefab)
        {
            GameObjectPool pool = PoolFor(prefab);
            GetCount++;
            return pool.Get();
        }

        /// <summary>Takes an instance and places it.</summary>
        public static GameObject Get(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GameObjectPool pool = PoolFor(prefab);
            GetCount++;
            return pool.Get(position, rotation, parent);
        }

        /// <summary>
        /// Takes an instance and returns the <typeparamref name="T"/> on it, with
        /// the lookup cached per instance. Incumbent APIs almost always spell this
        /// <c>prefab.GetFromPool().GetComponent&lt;T&gt;()</c>, which puts a
        /// <c>GetComponent</c> in the hot path the pool exists to remove.
        /// </summary>
        public static T Get<T>(GameObject prefab) where T : Component
        {
            GameObjectPool pool = PoolFor(prefab);
            GetCount++;
            return pool.Get<T>();
        }

        /// <summary>
        /// Returns an instance to whichever pool created it. Safe to call twice.
        /// Returns true when the instance was pooled, false when it was unknown or
        /// already destroyed.
        /// </summary>
        public static bool Return(GameObject instance)
        {
            // Deliberately `== null`, never `?.`: the null-conditional operator
            // compiles to a reference test and bypasses UnityEngine.Object's
            // overloaded ==, so a destroyed instance would sail past it and fail
            // deeper in. This is the single most common call-site bug at a pool
            // boundary, and it is invisible in review because `?.` is correct C#
            // for every non-Unity type in the same file.
            if (instance == null) return false;

            if (instance.TryGetComponent(out PooledInstance handle) && handle.Owner != null)
            {
                handle.Release();
                ReturnCount++;
                return true;
            }

            UnknownInstanceCount++;
            switch (UnknownInstances)
            {
                case UnknownInstancePolicy.Throw:
                    throw new InvalidOperationException(
                        $"'{instance.name}' was returned to PoolBridge but no toolkit pool owns it. " +
                        "It came from another pool registry, or was created with Instantiate.");
                case UnknownInstancePolicy.LogAndDestroy:
                    Debug.LogWarning(
                        $"[MemoryToolkit] '{instance.name}' returned to PoolBridge but no toolkit pool owns it; " +
                        "destroying. If this fires steadily, a second pool registry is still live.", instance);
                    Destroy(instance);
                    break;
                case UnknownInstancePolicy.Destroy:
                    Destroy(instance);
                    break;
                case UnknownInstancePolicy.Ignore:
                    break;
            }

            return false;
        }

        /// <summary>
        /// Whether this instance is currently sitting in its pool. O(1).
        /// Provided so an incumbent's equivalent query (typically a linear scan of
        /// the free list, often called on every return) can be re-pointed here.
        /// Call sites do not need it — <see cref="Return"/> is already idempotent.
        /// </summary>
        public static bool IsPooled(GameObject instance)
        {
            if (instance == null) return false;
            return instance.TryGetComponent(out PooledInstance handle) && handle.IsPooled;
        }

        /// <summary>Zeroes the counters. Call at session start, or between measured runs.</summary>
        public static void ResetDiagnostics()
        {
            UnknownInstanceCount = 0;
            LazyPoolCount = 0;
            GetCount = 0;
            ReturnCount = 0;
        }

        private static GameObjectPool PoolFor(GameObject prefab)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            MemoryScope scope = ResolveScope(prefab);
            if (!scope.TryGetPool(prefab, out GameObjectPool pool))
            {
                LazyPoolCount++;
                pool = scope.GetPool(prefab, DefaultCapacity, DefaultMaxSize);
            }

            return pool;
        }

        private static MemoryScope ResolveScope(GameObject prefab)
        {
            MemoryScope scope = ScopeResolver?.Invoke(prefab);

            // A resolver that hands back a dead scope is a lifetime bug in the
            // caller's policy, but losing the pool is a worse outcome during a
            // migration than falling back — so say so and keep the game running.
            if (scope == null || scope.IsDisposed)
            {
                if (scope != null)
                {
                    Debug.LogWarning(
                        $"[MemoryToolkit] ScopeResolver returned disposed scope '{scope.Name}' for " +
                        $"'{prefab.name}'; using Permanent instead.", prefab);
                }

                scope = MemoryManager.Permanent;
            }

            return scope;
        }

        private static void Destroy(GameObject go)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
