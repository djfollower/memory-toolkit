using System.Collections.Generic;
using MemoryToolkit.Budgets;
using UnityEngine;

namespace MemoryToolkit.Editor.CI
{
    /// <summary>
    /// Static coherence checks on a <see cref="MemoryBudget"/> asset.
    ///
    /// <para>A budget is configuration that nothing validates at author time: a
    /// warm-up larger than the max size, a prefab that cannot survive pooling, an
    /// entry referencing nothing. Each of those fails at runtime, on a device, as a
    /// symptom that does not mention the budget — a pool that destroys instances on
    /// the loading screen, a pool serving fake-null. All of them are visible in the
    /// asset, which means a build machine can catch them before anyone plays.</para>
    ///
    /// <para>Reuses <see cref="PoolSafetyValidator.Issue"/> so a CI report has one
    /// finding shape regardless of which check produced it.</para>
    /// </summary>
    public static class MemoryBudgetAudit
    {
        /// <summary>Scope name that may hold direct prefab references without pinning anything extra.</summary>
        private const string PermanentScopeName = "Permanent";

        private static readonly MemoryBudgetTier[] AllTiers =
        {
            MemoryBudgetTier.Low, MemoryBudgetTier.Medium, MemoryBudgetTier.High,
        };

        /// <summary>Appends findings for <paramref name="budget"/>. Does not clear <paramref name="results"/>.</summary>
        public static void Audit(MemoryBudget budget, List<PoolSafetyValidator.Issue> results)
        {
            if (budget == null) return;

            var seenScopes = new HashSet<string>();
            var prefabIssues = new List<PoolSafetyValidator.Issue>();

            foreach (ScopeBudget scope in budget.Scopes)
            {
                string scopeName = scope.ScopeName;
                string scopePath = $"{budget.name}/{(string.IsNullOrEmpty(scopeName) ? "(unnamed)" : scopeName)}";

                if (string.IsNullOrEmpty(scopeName))
                {
                    Add(results, PoolSafetyValidator.Severity.Error, scopePath,
                        "Scope entry has no name, so no scope will ever match it and nothing in it is applied.", budget);
                    continue;
                }

                if (!seenScopes.Add(scopeName))
                {
                    // ApplyTo stops at the first match, so a second entry is silently
                    // dead configuration — the worst kind, because it looks applied.
                    Add(results, PoolSafetyValidator.Severity.Error, scopePath,
                        "Duplicate scope entry. Only the first is ever applied; the rest are silently ignored.", budget);
                }

                var seenPrefabs = new HashSet<string>();
                PoolBudget[] pools = scope.Pools;
                if (pools == null) continue;

                foreach (PoolBudget pool in pools)
                {
                    string poolPath = $"{scopePath}/{pool.DisplayName}";

                    if (pool.Prefab == null && string.IsNullOrEmpty(pool.AddressableKey))
                    {
                        Add(results, PoolSafetyValidator.Severity.Error, poolPath,
                            "Entry references neither a prefab nor an addressable key.", budget);
                        continue;
                    }

                    if (pool.Prefab != null && !string.IsNullOrEmpty(pool.AddressableKey))
                    {
                        Add(results, PoolSafetyValidator.Severity.Warning, poolPath,
                            "Entry has both a direct prefab and an addressable key. The direct reference wins and " +
                            "the key is ignored — and the prefab is pinned regardless.", pool.Prefab);
                    }

                    if (!seenPrefabs.Add(pool.DisplayName))
                    {
                        Add(results, PoolSafetyValidator.Severity.Warning, poolPath,
                            "Duplicate entry in this scope. Both are applied, so the second warm-up runs against " +
                            "a pool that already exists and its max size is whichever ran last.", budget);
                    }

                    // The footgun this asset's docs lead with: a direct reference keeps
                    // the prefab and its meshes, materials and textures resident for as
                    // long as the budget is loaded. Permanent-tier content is resident
                    // anyway, so only non-Permanent entries are a real cost.
                    if (pool.Prefab != null && scopeName != PermanentScopeName)
                    {
                        Add(results, PoolSafetyValidator.Severity.Warning, poolPath,
                            $"Direct prefab reference in non-Permanent scope '{scopeName}'. This pins the prefab and " +
                            "its dependencies for as long as this budget is loaded, which is the whole session. " +
                            "Reference it by addressable key instead.", pool.Prefab);
                    }

                    AuditTiers(pool, poolPath, results, budget);

                    if (pool.Prefab == null) continue;

                    // A prefab that cannot be pooled must not be budgeted for pooling.
                    // This is the check that makes the gate more than a linter: it
                    // connects the budget to the thing it configures.
                    prefabIssues.Clear();
                    PoolSafetyValidator.Validate(pool.Prefab, prefabIssues);
                    foreach (PoolSafetyValidator.Issue issue in prefabIssues)
                    {
                        // Severity is most-severe-first (Error = 0); only errors
                        // disqualify a prefab from being budgeted for pooling.
                        if (issue.Severity != PoolSafetyValidator.Severity.Error) continue;

                        Add(results, PoolSafetyValidator.Severity.Error, poolPath,
                            $"Budgeted for pooling but fails pool safety: {issue.Message}", pool.Prefab);
                    }
                }
            }
        }

        private static void AuditTiers(
            PoolBudget pool, string poolPath, List<PoolSafetyValidator.Issue> results, Object context)
        {
            bool anyWarmup = false;

            foreach (MemoryBudgetTier tier in AllTiers)
            {
                int warmup = pool.Warmup.Get(tier);
                int maxSize = pool.MaxSize.Get(tier);
                if (warmup > 0) anyWarmup = true;

                if (maxSize > 0 && warmup > maxSize)
                {
                    // ApplyTo raises maxSize to cover this, but the asset is still
                    // wrong and the next reader will believe it.
                    Add(results, PoolSafetyValidator.Severity.Error, poolPath,
                        $"{tier} tier warms {warmup} instances into a pool capped at {maxSize}. The pool would " +
                        "destroy what it just created, on the loading screen, silently.", context);
                }
            }

            if (!anyWarmup)
            {
                Add(results, PoolSafetyValidator.Severity.Warning, poolPath,
                    "No warm-up on any tier, so this entry does nothing. Size it from the Timeline's peak active.",
                    context);
            }

            // Tiers exist to give weaker devices less. A Low above High is almost
            // always two fields filled in the wrong order.
            if (pool.Warmup.Low > 0 && pool.Warmup.High > 0 && pool.Warmup.Low > pool.Warmup.High)
            {
                Add(results, PoolSafetyValidator.Severity.Warning, poolPath,
                    $"Low tier warms more ({pool.Warmup.Low}) than High ({pool.Warmup.High}). Tiers are inverted.",
                    context);
            }
        }

        private static void Add(
            List<PoolSafetyValidator.Issue> results,
            PoolSafetyValidator.Severity severity,
            string path,
            string message,
            Object context)
            => results.Add(new PoolSafetyValidator.Issue(severity, path, message, context));
    }
}
