using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoryToolkit.Analyzers
{
    /// <summary>
    /// MTK002. Allocations inside <c>Update</c>, <c>LateUpdate</c> and
    /// <c>FixedUpdate</c> on a MonoBehaviour.
    ///
    /// <para><b>Scoped deliberately narrowly.</b> "Any reference type constructed in
    /// a per-frame method" would be the complete rule and an unusable one — lazy
    /// initialisation behind a null check allocates once and would be reported every
    /// time. So this flags only the shapes that are unconditionally per-frame garbage
    /// and that this package has a specific answer for: collections and arrays
    /// (<c>ListPool</c>, <c>ArrayPool</c>, the frame arena), string building
    /// (<c>StringBuilderCache</c>), LINQ (closures and enumerators every call), and
    /// Unity's yield instructions (cache one; they are immutable).</para>
    ///
    /// <para>Each of those is a real finding from a shipped project, not a category
    /// invented for completeness — see <c>docs/ADOPTION.md</c> §3.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PerFrameAllocationAnalyzer : DiagnosticAnalyzer
    {
        private static readonly ImmutableHashSet<string> AllocatingTypes = ImmutableHashSet.Create(
            "System.Collections.Generic.List<T>",
            "System.Collections.Generic.Dictionary<TKey, TValue>",
            "System.Collections.Generic.HashSet<T>",
            "System.Collections.Generic.Queue<T>",
            "System.Collections.Generic.Stack<T>",
            "System.Text.StringBuilder",
            "UnityEngine.WaitForSeconds",
            "UnityEngine.WaitForSecondsRealtime",
            "UnityEngine.WaitForEndOfFrame",
            "UnityEngine.WaitForFixedUpdate");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(MemoryToolkitRules.PerFrameAllocation);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            string name = method.Identifier.ValueText;
            if (!UnityTypes.IsPerFrameMessage(name)) return;

            // A method called Update on a plain class is not a Unity message and is
            // not called every frame. Without this check the rule fires on every
            // service, view-model and state machine that happens to use the name.
            if (context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol symbol)
                return;
            if (!UnityTypes.IsMonoBehaviour(symbol.ContainingType)) return;

            SyntaxNode body = (SyntaxNode)method.Body ?? method.ExpressionBody;
            if (body == null) return;

            foreach (SyntaxNode node in body.DescendantNodes())
            {
                switch (node)
                {
                    case ArrayCreationExpressionSyntax array:
                        Report(context, array, "array allocation", name);
                        break;

                    case ImplicitArrayCreationExpressionSyntax implicitArray:
                        Report(context, implicitArray, "array allocation", name);
                        break;

                    case ObjectCreationExpressionSyntax creation:
                        AnalyzeCreation(context, creation, name);
                        break;

                    case InterpolatedStringExpressionSyntax interpolation:
                        Report(context, interpolation, "string interpolation", name);
                        break;

                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                        AnalyzeConcat(context, binary, name);
                        break;

                    case InvocationExpressionSyntax invocation:
                        AnalyzeInvocation(context, invocation, name);
                        break;
                }
            }
        }

        private static void AnalyzeCreation(
            SyntaxNodeAnalysisContext context, ObjectCreationExpressionSyntax creation, string methodName)
        {
            if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol
                is not IMethodSymbol constructor) return;

            INamedTypeSymbol type = constructor.ContainingType;

            // Structs are not heap allocations. `new Vector3(...)` in Update is
            // normal and correct, and flagging it would discredit the rule instantly.
            if (type.IsValueType) return;

            string name = type.OriginalDefinition.ToDisplayString();
            if (!AllocatingTypes.Contains(name)) return;

            Report(context, creation, $"'new {type.Name}'", methodName);
        }

        private static void AnalyzeConcat(
            SyntaxNodeAnalysisContext context, BinaryExpressionSyntax binary, string methodName)
        {
            // Only the outermost + of a concat chain, so `a + b + c` is one finding.
            if (binary.Parent is BinaryExpressionSyntax parent && parent.IsKind(SyntaxKind.AddExpression)) return;

            if (context.SemanticModel.GetTypeInfo(binary, context.CancellationToken).Type?.SpecialType
                != SpecialType.System_String) return;

            // A compile-time constant concat is folded by the compiler; no allocation.
            if (context.SemanticModel.GetConstantValue(binary, context.CancellationToken).HasValue) return;

            Report(context, binary, "string concatenation", methodName);
        }

        private static void AnalyzeInvocation(
            SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, string methodName)
        {
            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                is not IMethodSymbol symbol) return;

            // LINQ allocates an enumerator per call, plus a closure for any lambda
            // that captures. Both are per-frame garbage and neither is visible.
            if (symbol.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

            Report(context, invocation, $"LINQ '{symbol.Name}'", methodName);
        }

        private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string what, string methodName)
            => context.ReportDiagnostic(Diagnostic.Create(
                MemoryToolkitRules.PerFrameAllocation, node.GetLocation(), what, methodName));
    }
}
