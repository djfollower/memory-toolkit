using System;
using System.Collections.Generic;
using UnityEngine;

namespace MemoryToolkit.Budgets
{
    /// <summary>One pooled prefab's numbers.</summary>
    [Serializable]
    public struct PoolBudget
    {
        [Tooltip("Direct reference. Use ONLY for prefabs that live as long as the app — see the class docs on MemoryBudget.")]
        public GameObject Prefab;

        [Tooltip("Addressable key, for prefabs that must not be pinned by this asset. Resolved by the caller.")]
        public string AddressableKey;

        [Tooltip("Instances to pre-create. Size from the Timeline's peak active, not a guess.")]
        public TieredInt Warmup;

        [Tooltip("Maximum retained. Set to the real peak so steady state never destroys an instance.")]
        public TieredInt MaxSize;

        /// <summary>Name for logs and reports, whichever way the prefab is referenced.</summary>
        public string DisplayName =>
            Prefab != null ? Prefab.name :
            !string.IsNullOrEmpty(AddressableKey) ? AddressableKey : "(empty)";
    }

    /// <summary>Everything one scope owns, by scope name.</summary>
    [Serializable]
    public struct ScopeBudget
    {
        [Tooltip("Matches MemoryScope.Name — 'Permanent', a scene name, or a manual scope like 'Match'.")]
        public string ScopeName;

        public PoolBudget[] Pools;

        [Tooltip("Bytes for this scope's arena, if it has one. 0 = no arena.")]
        public TieredInt ArenaCapacityBytes;
    }

    /// <summary>
    /// Warm-up counts, pool sizes, arena capacities and heap ceilings as an asset
    /// rather than as literals in an installer.
    ///
    /// <para><b>Why this is not code.</b> A single warm-up number is wrong on two of
    /// any three targets, and the person who knows what a level should cost is
    /// usually not the person who can edit an installer and get it through review.
    /// Numbers in code are also invisible to the build farm; numbers in an asset can
    /// be asserted against (see the CI gate) and can be written back from a measured
    /// session (Inspector > Apply measured peaks).</para>
    ///
    /// <para><b>The reference footgun, and the rule that avoids it.</b> A direct
    /// <see cref="GameObject"/> reference in this asset pins that prefab — and its
    /// meshes, materials and textures — in memory for as long as the budget itself is
    /// loaded. A budget listing every level's prefabs by direct reference therefore
    /// loads every level's content at boot: a memory tool that costs more memory than
    /// it saves. <b>Use direct references only for Permanent-tier prefabs</b>, which
    /// are resident anyway; reference everything else by
    /// <see cref="PoolBudget.AddressableKey"/>, which this asset holds as a string and
    /// cannot accidentally load.</para>
    ///
    /// <para>Because the runtime assembly must not depend on Addressables,
    /// <see cref="ApplyTo"/> warms only the direct references and hands back the
    /// keyed entries for the caller to resolve. See the Addressables sample.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "MemoryBudget", menuName = "Memory Toolkit/Memory Budget")]
    public sealed class MemoryBudget : ScriptableObject
    {
        [Tooltip("One entry per scope. Scope names must match MemoryScope.Name exactly.")]
        [SerializeField] private ScopeBudget[] _scopes = Array.Empty<ScopeBudget>();

        [Header("Ceilings (asserted by the CI gate, not enforced at runtime)")]
        [Tooltip("Managed heap ceiling in MB. 0 = no ceiling.")]
        [SerializeField] private TieredInt _managedHeapCeilingMb;

        [Tooltip("Frame-scratch arena size in KB. 0 = leave MemoryManager's default.")]
        [SerializeField] private TieredInt _frameScratchKb;

        [Tooltip("Instances each pool keeps when Application.lowMemory fires. 0 = leave the default.")]
        [SerializeField] private TieredInt _lowMemoryKeepPerPool;

        public IReadOnlyList<ScopeBudget> Scopes => _scopes;
        public TieredInt ManagedHeapCeilingMb => _managedHeapCeilingMb;
        public TieredInt FrameScratchKb => _frameScratchKb;
        public TieredInt LowMemoryKeepPerPool => _lowMemoryKeepPerPool;

        /// <summary>What <see cref="ApplyTo"/> could not do by itself.</summary>
        public readonly struct ApplyResult
        {
            public ApplyResult(int warmedPools, IReadOnlyList<PoolBudget> pendingAddressables)
            {
                WarmedPools = warmedPools;
                PendingAddressables = pendingAddressables;
            }

            /// <summary>Pools warmed from a direct prefab reference.</summary>
            public int WarmedPools { get; }

            /// <summary>
            /// Entries referenced by key. The caller loads these — the runtime
            /// assembly has no Addressables dependency and will not grow one to
            /// make an API look complete.
            /// </summary>
            public IReadOnlyList<PoolBudget> PendingAddressables { get; }
        }

        /// <summary>
        /// Applies this budget's entry for <paramref name="scope"/>, matched on
        /// <see cref="MemoryScope.Name"/>. A scope with no entry is left alone — an
        /// unbudgeted scope is normal during adoption, not an error.
        /// </summary>
        public ApplyResult ApplyTo(MemoryScope scope, MemoryBudgetTier? tier = null)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (scope.IsDisposed) throw new ObjectDisposedException(nameof(MemoryScope));

            MemoryBudgetTier t = tier ?? DeviceTier.Current;
            List<PoolBudget> pending = null;
            int warmed = 0;

            for (int i = 0; i < _scopes.Length; i++)
            {
                if (_scopes[i].ScopeName != scope.Name) continue;

                ScopeBudget entry = _scopes[i];
                PoolBudget[] pools = entry.Pools ?? Array.Empty<PoolBudget>();

                for (int p = 0; p < pools.Length; p++)
                {
                    PoolBudget pool = pools[p];
                    int warmup = pool.Warmup.Get(t);
                    int maxSize = pool.MaxSize.Get(t);

                    if (pool.Prefab == null)
                    {
                        if (!string.IsNullOrEmpty(pool.AddressableKey))
                            (pending ??= new List<PoolBudget>()).Add(pool);
                        continue;
                    }

                    // A max size below the warm-up would have the pool destroy
                    // instances it just created, on the loading screen, silently.
                    if (maxSize < warmup) maxSize = warmup;

                    if (warmup <= 0) continue;

                    scope.Warmup(pool.Prefab, warmup, maxSize);
                    warmed++;
                }

                int arenaBytes = entry.ArenaCapacityBytes.Get(t);
                if (arenaBytes > 0) scope.CreateAllocator(arenaBytes);

                break;
            }

            return new ApplyResult(warmed, (IReadOnlyList<PoolBudget>)pending ?? Array.Empty<PoolBudget>());
        }

        /// <summary>
        /// Applies the process-wide numbers. Call once during boot, <b>before</b>
        /// anything touches <see cref="MemoryManager.FrameScratch"/> — reading that
        /// property allocates the arena at whatever size is configured at the time,
        /// and it is not resized afterwards.
        /// </summary>
        public void ApplyGlobals(MemoryBudgetTier? tier = null)
        {
            MemoryBudgetTier t = tier ?? DeviceTier.Current;

            int scratchKb = _frameScratchKb.Get(t);
            if (scratchKb > 0) MemoryManager.FrameScratchCapacityBytes = scratchKb * 1024;

            int keep = _lowMemoryKeepPerPool.Get(t);
            if (keep > 0) MemoryManager.LowMemoryKeepPerPool = keep;
        }

        /// <summary>Finds a scope's entry, for editors and the CI gate. Returns false when unbudgeted.</summary>
        public bool TryGetScope(string scopeName, out ScopeBudget budget)
        {
            for (int i = 0; i < _scopes.Length; i++)
            {
                if (_scopes[i].ScopeName != scopeName) continue;
                budget = _scopes[i];
                return true;
            }

            budget = default;
            return false;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only write path, used by <c>Apply measured peaks</c>. Not a runtime
        /// API: a budget that a running game can rewrite is not a budget.
        /// </summary>
        internal ScopeBudget[] ScopesForEditing
        {
            get => _scopes;
            set => _scopes = value ?? Array.Empty<ScopeBudget>();
        }
#endif
    }
}
