using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoryToolkit.Analyzers
{
    /// <summary>
    /// MTK001. Flags the four ways C# compares against null without going through
    /// <c>UnityEngine.Object</c>'s overloaded <c>==</c>.
    ///
    /// <para>A destroyed Unity object is not reference-null — Unity overloads
    /// <c>==</c> so it <i>reports</i> as null while the managed wrapper is still a
    /// live reference. <c>?.</c>, <c>??</c>, <c>??=</c> and <c>is null</c> are all
    /// compiled directly to reference comparisons, so each one sails past a destroyed
    /// object and fails somewhere else entirely.</para>
    ///
    /// <para>This is decidable from type information alone, which is why it is on by
    /// default: if the expression's type derives from <c>UnityEngine.Object</c>, the
    /// check is wrong, with no project knowledge required.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UnityObjectNullCheckAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(MemoryToolkitRules.UnityObjectNullCheckBypass);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeConditionalAccess, SyntaxKind.ConditionalAccessExpression);
            context.RegisterSyntaxNodeAction(AnalyzeCoalesce, SyntaxKind.CoalesceExpression);
            context.RegisterSyntaxNodeAction(AnalyzeCoalesceAssignment, SyntaxKind.CoalesceAssignmentExpression);
            context.RegisterSyntaxNodeAction(AnalyzeIsPattern, SyntaxKind.IsPatternExpression);
        }

        private static void AnalyzeConditionalAccess(SyntaxNodeAnalysisContext context)
        {
            var node = (ConditionalAccessExpressionSyntax)context.Node;
            Report(context, node.Expression, "?.");
        }

        private static void AnalyzeCoalesce(SyntaxNodeAnalysisContext context)
        {
            var node = (BinaryExpressionSyntax)context.Node;
            Report(context, node.Left, "??");
        }

        private static void AnalyzeCoalesceAssignment(SyntaxNodeAnalysisContext context)
        {
            var node = (AssignmentExpressionSyntax)context.Node;
            Report(context, node.Left, "??=");
        }

        private static void AnalyzeIsPattern(SyntaxNodeAnalysisContext context)
        {
            var node = (IsPatternExpressionSyntax)context.Node;

            // `x is null` and `x is not null` are constant patterns, and the compiler
            // emits them as reference comparisons for the same reason as the operators
            // above. This one is the sneakiest of the four: it reads as a deliberate,
            // modern null check rather than as an operator someone reached for.
            if (!IsNullPattern(node.Pattern)) return;

            Report(context, node.Expression, node.Pattern is UnaryPatternSyntax ? "is not null" : "is null");
        }

        private static bool IsNullPattern(PatternSyntax pattern)
        {
            switch (pattern)
            {
                case ConstantPatternSyntax constant:
                    return constant.Expression.IsKind(SyntaxKind.NullLiteralExpression);
                case UnaryPatternSyntax unary when unary.IsKind(SyntaxKind.NotPattern):
                    return IsNullPattern(unary.Pattern);
                default:
                    return false;
            }
        }

        private static void Report(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, string operatorText)
        {
            ITypeSymbol type = context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type;
            if (type == null || !UnityTypes.IsUnityObject(type)) return;

            context.ReportDiagnostic(Diagnostic.Create(
                MemoryToolkitRules.UnityObjectNullCheckBypass,
                expression.GetLocation(),
                expression.ToString(),
                operatorText));
        }
    }
}
