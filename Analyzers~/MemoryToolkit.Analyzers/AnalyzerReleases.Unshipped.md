; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MTK001 | MemoryToolkit.Pooling | Warning | UnityObjectNullCheckAnalyzer — ?. / ?? / ??= / is null bypass UnityEngine.Object's overloaded ==
MTK002 | MemoryToolkit.Allocation | Warning | PerFrameAllocationAnalyzer — allocation in Update / LateUpdate / FixedUpdate
MTK007 | MemoryToolkit.Pooling | Disabled | UseAfterAwaitAnalyzer — UnityEngine.Object used after an await without re-validation
