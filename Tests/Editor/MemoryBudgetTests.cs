using MemoryToolkit.Budgets;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers budgets as data: the tier fallback rule, and that applying a budget
    /// produces the same pools a hand-written installer would.
    /// </summary>
    public class MemoryBudgetTests
    {
        private GameObject _prefab;
        private MemoryBudget _budget;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("BudgetPrefab");
            _budget = ScriptableObject.CreateInstance<MemoryBudget>();
            DeviceTier.Override(MemoryBudgetTier.High);
        }

        [TearDown]
        public void TearDown()
        {
            DeviceTier.Clear();
            MemoryManager.Shutdown();
            Object.DestroyImmediate(_budget);
            Object.DestroyImmediate(_prefab);
        }

        // ---- Tier resolution ---------------------------------------------------------

        [Test]
        public void TieredInt_FallsBackUpTheTiers_SoAPartiallyFilledRowIsCoherent()
        {
            var onlyHigh = new TieredInt(high: 32);

            Assert.That(onlyHigh.Get(MemoryBudgetTier.High), Is.EqualTo(32));
            Assert.That(onlyHigh.Get(MemoryBudgetTier.Medium), Is.EqualTo(32));
            Assert.That(onlyHigh.Get(MemoryBudgetTier.Low), Is.EqualTo(32),
                "authoring one number must mean one number everywhere, not zero on the tier that matters most");
        }

        [Test]
        public void TieredInt_UsesTheNearestFilledTier()
        {
            var partial = new TieredInt(high: 64, medium: 32);

            Assert.That(partial.Get(MemoryBudgetTier.Medium), Is.EqualTo(32));
            Assert.That(partial.Get(MemoryBudgetTier.Low), Is.EqualTo(32), "Low is unset, so it follows Medium");
        }

        [Test]
        public void DeviceTier_IsCachedSoItCannotChangeBetweenTwoPoolsInAScene()
        {
            DeviceTier.Clear();
            DeviceTier.Provider = new FixedTierProvider(MemoryBudgetTier.Low);
            Assert.That(DeviceTier.Current, Is.EqualTo(MemoryBudgetTier.Low));

            ((FixedTierProvider)DeviceTier.Provider).Tier = MemoryBudgetTier.High;
            Assert.That(DeviceTier.Current, Is.EqualTo(MemoryBudgetTier.Low),
                "a tier that can change mid-session produces a configuration nobody tested");

            DeviceTier.Provider = new SystemMemoryDeviceTierProvider();
            DeviceTier.Override(MemoryBudgetTier.High);
        }

        // ---- Applying ----------------------------------------------------------------

        [Test]
        public void ApplyTo_WarmsTheScopesPools_AtTheResolvedTier()
        {
            SetBudget(Scope("Permanent", Pool(_prefab, warmup: new TieredInt(8, 4, 2), maxSize: 64)));

            DeviceTier.Override(MemoryBudgetTier.Low);
            MemoryBudget.ApplyResult result = _budget.ApplyTo(MemoryManager.Permanent);

            Assert.That(result.WarmedPools, Is.EqualTo(1));
            Assert.That(MemoryManager.Permanent.TryGetPool(_prefab, out GameObjectPool pool), Is.True);
            Assert.That(pool.CountInactive, Is.EqualTo(2), "Low tier, so 2 — not the authoring default of 8");
            Assert.That(pool.WasWarmedUp, Is.True);
        }

        [Test]
        public void ApplyTo_RaisesAMaxSizeBelowTheWarmup_RatherThanDestroyingWhatItJustCreated()
        {
            SetBudget(Scope("Permanent", Pool(_prefab, warmup: 16, maxSize: 4)));

            _budget.ApplyTo(MemoryManager.Permanent);

            Assert.That(MemoryManager.Permanent.TryGetPool(_prefab, out GameObjectPool pool), Is.True);
            Assert.That(pool.CountInactive, Is.EqualTo(16),
                "a max size under the warm-up must not silently discard instances on the loading screen");
        }

        [Test]
        public void ApplyTo_LeavesAnUnbudgetedScopeAlone()
        {
            SetBudget(Scope("Permanent", Pool(_prefab, warmup: 4, maxSize: 8)));

            MemoryScope match = MemoryManager.CreateScope("Match");
            MemoryBudget.ApplyResult result = _budget.ApplyTo(match);

            Assert.That(result.WarmedPools, Is.Zero);
            Assert.That(match.TryGetPool(_prefab, out GameObjectPool _), Is.False,
                "an unbudgeted scope is normal during adoption, not an error");
        }

        [Test]
        public void ApplyTo_HandsBackKeyedEntries_BecauseTheRuntimeCannotResolveThem()
        {
            SetBudget(Scope("Permanent",
                Pool(_prefab, warmup: 4, maxSize: 8),
                new PoolBudget { AddressableKey = "Enemy_Orc", Warmup = 12, MaxSize = 24 }));

            MemoryBudget.ApplyResult result = _budget.ApplyTo(MemoryManager.Permanent);

            Assert.That(result.WarmedPools, Is.EqualTo(1));
            Assert.That(result.PendingAddressables.Count, Is.EqualTo(1));
            Assert.That(result.PendingAddressables[0].AddressableKey, Is.EqualTo("Enemy_Orc"));
        }

        [Test]
        public void ApplyTo_CreatesTheScopesArena()
        {
            var scope = Scope("Permanent");
            scope.ArenaCapacityBytes = 4096;
            SetBudget(scope);

            _budget.ApplyTo(MemoryManager.Permanent);

            Assert.That(MemoryManager.Permanent.Allocators.Count, Is.EqualTo(1));
            Assert.That(MemoryManager.Permanent.Allocators[0].CapacityBytes, Is.EqualTo(4096));
        }

        // ---- Helpers -----------------------------------------------------------------

        private void SetBudget(params ScopeBudget[] scopes) => _budget.ScopesForEditing = scopes;

        private static ScopeBudget Scope(string name, params PoolBudget[] pools)
            => new() { ScopeName = name, Pools = pools };

        private static PoolBudget Pool(GameObject prefab, TieredInt warmup, TieredInt maxSize)
            => new() { Prefab = prefab, Warmup = warmup, MaxSize = maxSize };

        private sealed class FixedTierProvider : IDeviceTierProvider
        {
            public MemoryBudgetTier Tier;
            public FixedTierProvider(MemoryBudgetTier tier) => Tier = tier;
            public MemoryBudgetTier GetTier() => Tier;
        }
    }
}
