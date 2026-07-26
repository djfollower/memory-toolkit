; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MTK001 | MemoryToolkit.Pooling | Warning | UnityObjectNullCheckAnalyzer — ?. / ?? / ??= / is null bypass UnityEngine.Object's overloaded ==
MTK002 | MemoryToolkit.Allocation | Warning | PerFrameAllocationAnalyzer — allocation in Update / LateUpdate / FixedUpdate
MTK006 | MemoryToolkit.Pooling | Warning | AddComponentOnReuseAnalyzer — AddComponent in OnEnable or the Update family accumulates under pooling
MTK007 | MemoryToolkit.Pooling | Disabled | UseAfterAwaitAnalyzer — UnityEngine.Object used after an await without re-validation
MTK008 | MemoryToolkit.Pooling | Warning | PoolableOnDestroyAnalyzer — an IPoolable type declares OnDestroy; its cleanup stops running per release under pooling
