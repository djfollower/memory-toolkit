using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoryToolkit.Analyzers
{
    /// <summary>
    /// MTK007. A <c>UnityEngine.Object</c> reached through a field or parameter after
    /// an <c>await</c>, with no check in between.
    ///
    /// <para>Pooling breaks the rule that a non-null reference is still yours. During
    /// the await the level may have ended and the target been destroyed — or, worse,
    /// the instance may have been released and re-taken, in which case it is alive,
    /// non-null, and someone else's. A null check passes; <c>PooledRef&lt;T&gt;</c>
    /// is what actually answers the question.</para>
    ///
    /// <para><b>Off by default</b>, and honestly so. Whether an await can outlive its
    /// target is project knowledge — a one-frame yield in a system nothing else can
    /// tear down is fine, and this rule cannot tell that apart from an Addressables
    /// load across a level transition. Enable it per-folder once a team has seen its
    /// own noise level.</para>
    ///
    /// <para>Restricted to fields and parameters: a local declared before the await
    /// is usually a temporary the method itself owns, and including those roughly
    /// doubled the findings without adding a real one.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UseAfterAwaitAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(MemoryToolkitRules.UnvalidatedUseAfterAwait);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeAwait, SyntaxKind.AwaitExpression);
        }

        private static void AnalyzeAwait(SyntaxNodeAnalysisContext context)
        {
            var await = (AwaitExpressionSyntax)context.Node;

            // Only statements that follow the await in the same block are considered.
            // Anything more ambitious needs real flow analysis, and a half-done flow
            // analysis produces exactly the false positives that get a rule disabled.
            if (await.FirstAncestorOrSelf<StatementSyntax>() is not { } awaitStatement) return;
            if (awaitStatement.Parent is not BlockSyntax block) return;

            var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var validated = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            bool afterAwait = false;
            foreach (StatementSyntax statement in block.Statements)
            {
                if (!afterAwait)
                {
                    afterAwait = statement == awaitStatement;
                    continue;
                }

                // Any condition mentioning the symbol counts as a re-check. Coarse on
                // purpose: the goal is to notice that the author thought about it, not
                // to grade how they checked.
                if (statement is IfStatementSyntax ifStatement)
                    MarkValidated(context, ifStatement.Condition, validated);

                foreach (SyntaxNode node in statement.DescendantNodes())
                {
                    if (node is not MemberAccessExpressionSyntax access) continue;

                    ISymbol symbol = context.SemanticModel
                        .GetSymbolInfo(access.Expression, context.CancellationToken).Symbol;
                    if (symbol == null || validated.Contains(symbol) || reported.Contains(symbol)) continue;
                    if (!IsFieldOrParameter(symbol, out ITypeSymbol type)) continue;
                    if (!UnityTypes.IsUnityObject(type)) continue;

                    reported.Add(symbol);
                    context.ReportDiagnostic(Diagnostic.Create(
                        MemoryToolkitRules.UnvalidatedUseAfterAwait,
                        access.Expression.GetLocation(),
                        symbol.Name));
                }
            }
        }

        private static void MarkValidated(
            SyntaxNodeAnalysisContext context, SyntaxNode condition, HashSet<ISymbol> validated)
        {
            foreach (SyntaxNode node in condition.DescendantNodesAndSelf())
            {
                if (node is not IdentifierNameSyntax identifier) continue;

                ISymbol symbol = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol;
                if (symbol != null) validated.Add(symbol);
            }
        }

        private static bool IsFieldOrParameter(ISymbol symbol, out ITypeSymbol type)
        {
            switch (symbol)
            {
                case IFieldSymbol field:
                    type = field.Type;
                    return true;
                case IParameterSymbol parameter:
                    type = parameter.Type;
                    return true;
                default:
                    type = null;
                    return false;
            }
        }
    }
}
