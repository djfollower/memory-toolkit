using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoryToolkit.Analyzers
{
    /// <summary>
    /// MTK006. <c>AddComponent</c> in a MonoBehaviour method that runs more than once
    /// per instance — <c>OnEnable</c> (every pool take) or the Update family (every
    /// frame).
    ///
    /// <para>The hazard is from <c>docs/ADOPTION.md</c> §4: AddComponent allocates and
    /// has no cheap inverse, so an instance pooled after setup ran twice carries two
    /// copies of each component. The general case — a custom setup method called on
    /// every spawn — needs project knowledge to identify and is left to the checklist.
    /// This rule takes the subset that is provable from the method alone.</para>
    ///
    /// <para><b>Why not Awake or Start.</b> Those run once per GameObject instance and
    /// the component persists across pool reuse, which is exactly what the guide
    /// recommends ("components at author time"). Flagging them would punish the
    /// correct pattern — the fastest way to get an analyzer switched off.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AddComponentOnReuseAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(MemoryToolkitRules.AddComponentOnReuse);

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

            string cadence = Cadence(name);
            if (cadence == null) return;

            // A method named OnEnable/Update on a plain class is not a Unity message.
            if (context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol symbol)
                return;
            if (!UnityTypes.IsMonoBehaviour(symbol.ContainingType)) return;

            SyntaxNode body = (SyntaxNode)method.Body ?? method.ExpressionBody;
            if (body == null) return;

            foreach (SyntaxNode node in body.DescendantNodes())
            {
                if (node is InvocationExpressionSyntax invocation && IsAddComponent(context, invocation))
                    Report(context, invocation, name, cadence);
            }
        }

        /// <summary>
        /// The per-reuse cadence of a Unity message, or null if the message runs at
        /// most once per instance (Awake/Start) or is not a per-reuse message at all.
        /// </summary>
        private static string Cadence(string methodName)
        {
            switch (methodName)
            {
                case "OnEnable": return "pool take (OnEnable)";
                case "Update":
                case "LateUpdate":
                case "FixedUpdate": return "frame";
                default: return null;
            }
        }

        private static bool IsAddComponent(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        {
            // Cheap syntactic gate before the semantic check: the method's simple name
            // must be AddComponent, generic or not.
            SimpleNameSyntax name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name,
                SimpleNameSyntax simple => simple,
                _ => null,
            };
            if (name == null || name.Identifier.ValueText != "AddComponent") return false;

            // Confirm it is UnityEngine.GameObject.AddComponent, not a same-named
            // method on a project type. (Component.AddComponent was removed from Unity
            // long ago; GameObject is the only real receiver.)
            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                is not IMethodSymbol symbol) return false;

            return symbol.ContainingType?.ToDisplayString() == "UnityEngine.GameObject";
        }

        private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string methodName, string cadence)
            => context.ReportDiagnostic(Diagnostic.Create(
                MemoryToolkitRules.AddComponentOnReuse, node.GetLocation(), methodName, cadence));
    }
}
