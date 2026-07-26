using Microsoft.CodeAnalysis;

namespace MemoryToolkit.Analyzers
{
    /// <summary>
    /// Every rule this package ships, in one place so IDs cannot collide and
    /// severities can be compared at a glance.
    ///
    /// <para><b>Why an analyzer at all.</b> The failures documented in
    /// <c>docs/ADOPTION.md</c> §4 and <c>docs/INTEGRATION.md</c> §2 are call-site
    /// patterns, and prose does not survive contact with a thirty-person team. A rule
    /// with an ID can be suppressed per line, tracked in review, and argued about;
    /// a paragraph in a field guide cannot.</para>
    ///
    /// <para><b>Why most of these are off by default.</b> A rule that cries wolf gets
    /// the whole analyzer switched off, which costs more than never shipping it. Only
    /// the two that are decidable from type information alone are on by default;
    /// the rest need project knowledge the compiler does not have, so they are opt-in
    /// via <c>.editorconfig</c> after a team has looked at their own noise level.</para>
    /// </summary>
    internal static class MemoryToolkitRules
    {
        private const string PoolingCategory = "MemoryToolkit.Pooling";
        private const string AllocationCategory = "MemoryToolkit.Allocation";
        private const string DocsUrl = "https://github.com/DungPhan/memory-toolkit/blob/main/docs/ANALYZER.md#";

        /// <summary>
        /// MTK001 — the single most common bug at a pool boundary, and invisible in
        /// review because it is correct C# for every non-Unity type in the same file.
        /// </summary>
        internal static readonly DiagnosticDescriptor UnityObjectNullCheckBypass = new(
            id: "MTK001",
            title: "Null-conditional operator bypasses UnityEngine.Object's lifetime check",
            messageFormat:
                "'{0}' is a UnityEngine.Object; '{1}' compiles to a reference comparison and skips Unity's " +
                "overloaded ==, so a destroyed object passes as alive. Use '== null' / '!= null' instead.",
            category: PoolingCategory,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "A destroyed UnityEngine.Object is not reference-null: Unity overloads == to report it as null. " +
                "?., ??, ??= and 'is null' are all reference comparisons the compiler emits directly, so they " +
                "sail past a destroyed object and fail deeper in, far from the cause.",
            helpLinkUri: DocsUrl + "mtk001");

        /// <summary>MTK002 — per-frame allocation, the difference between "low GC" and 0 B/frame.</summary>
        internal static readonly DiagnosticDescriptor PerFrameAllocation = new(
            id: "MTK002",
            title: "Allocation in a per-frame method",
            messageFormat:
                "{0} in {1}() allocates every frame. Reuse a cached instance, a pooled collection, or the frame arena.",
            category: AllocationCategory,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "Update, LateUpdate and FixedUpdate run every frame, so an allocation here is garbage at frame rate. " +
                "This is invisible in code review and obvious in the Profiler's GC Alloc column.",
            helpLinkUri: DocsUrl + "mtk002");

        /// <summary>
        /// MTK007 — off by default. Needs to know which awaits can outlive their
        /// target, which is project knowledge, so it is noisy until a team scopes it.
        /// </summary>
        internal static readonly DiagnosticDescriptor UnvalidatedUseAfterAwait = new(
            id: "MTK007",
            title: "UnityEngine.Object used after an await without re-validation",
            messageFormat:
                "'{0}' is used after an await without being re-checked. The level may have ended, or a pooled " +
                "instance may now belong to someone else.",
            category: PoolingCategory,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: false,
            description:
                "After an await, the target may have been destroyed — or, under pooling, released and re-taken, " +
                "in which case it is alive, non-null, and someone else's. A null check cannot tell you that; " +
                "PooledRef<T> can.",
            helpLinkUri: DocsUrl + "mtk007");
    }
}
