using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoryToolkit.Analyzers
{
    /// <summary>
    /// MTK008. A type that implements <c>IPoolable</c> declares <c>OnDestroy</c>.
    ///
    /// <para>This is the check <c>docs/ADOPTION.md</c> §4 calls the highest-value one,
    /// and the one the prefab validator cannot make — it reads prefab data, not method
    /// bodies. Under pooling, <c>OnDestroy</c> runs only when the pool itself is torn
    /// down, not on each release, so any per-use cleanup there (event unsubscribes,
    /// tween kills, coroutine stops) silently stops happening. It must move to
    /// <c>OnReturnedToPool</c>.</para>
    ///
    /// <para><b>Precision comes from the gate.</b> Nearly every MonoBehaviour has a
    /// legitimate <c>OnDestroy</c>, so an unconditional rule would be pure noise on a
    /// project that pools nothing. Firing only on types that implement
    /// <c>IPoolable</c> — types that opted into pooling — means the finding is only
    /// raised where <c>OnDestroy</c> genuinely no longer runs on the hot path. A
    /// project that has not adopted the toolkit sees none of these.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PoolableOnDestroyAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(MemoryToolkitRules.PoolableOnDestroyCleanup);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            if (method.Identifier.ValueText != "OnDestroy") return;
            if (method.ParameterList.Parameters.Count != 0) return;

            if (context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol symbol)
                return;

            // The whole rule: only a type that opted into pooling. This is what keeps
            // it silent on the thousands of correct OnDestroy methods in ordinary code.
            if (!UnityTypes.ImplementsIPoolable(symbol.ContainingType)) return;

            // An empty OnDestroy has no cleanup to lose; flagging it is noise. Anything
            // with a body is worth the line-by-line review the guide asks for.
            if (IsEffectivelyEmpty(method)) return;

            context.ReportDiagnostic(Diagnostic.Create(
                MemoryToolkitRules.PoolableOnDestroyCleanup,
                method.Identifier.GetLocation(),
                symbol.ContainingType.Name));
        }

        private static bool IsEffectivelyEmpty(MethodDeclarationSyntax method)
        {
            if (method.ExpressionBody != null) return false;
            if (method.Body == null) return true;
            return method.Body.Statements.Count == 0;
        }
    }
}
