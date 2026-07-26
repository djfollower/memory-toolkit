using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MemoryToolkit.Analyzers.Tests
{
    /// <summary>
    /// Compiles a snippet against a stub UnityEngine and returns what the analyzer
    /// found.
    ///
    /// <para>A stub rather than the real engine assemblies on purpose: these tests
    /// have to run on a build machine with no Unity install, and the analyzers only
    /// ever ask whether a type derives from <c>UnityEngine.Object</c> or
    /// <c>MonoBehaviour</c>, which a stub answers identically. The cost is that a
    /// change to Unity's own type hierarchy would not be caught here — which is why
    /// the acceptance measure for these rules is a run against real projects, not
    /// this suite.</para>
    /// </summary>
    internal static class AnalyzerHarness
    {
        /// <summary>The parts of UnityEngine these rules actually reason about.</summary>
        private const string UnityStub = @"
namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static bool operator ==(Object a, Object b) => false;
        public static bool operator !=(Object a, Object b) => true;
        public override bool Equals(object o) => false;
        public override int GetHashCode() => 0;
    }

    public class Component : Object { public Transform transform; }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class Transform : Component { public void SetParent(Transform p) { } }
    public class GameObject : Object { public T GetComponent<T>() => default; }
    public struct Vector3 { public float x, y, z; public Vector3(float a, float b, float c) { x = a; y = b; z = c; } }
    public class WaitForSeconds { public WaitForSeconds(float s) { } }
    public class Debug { public static void Log(object o) { } }
}
";

        internal static async Task<ImmutableArray<Diagnostic>> RunAsync(DiagnosticAnalyzer analyzer, string source)
        {
            // Rules that ship disabled by default produce nothing at all unless they
            // are switched on here — including their negative cases, which would then
            // pass vacuously and assert nothing. Every supported rule is forced on so
            // a test's result always reflects the analyzer's logic rather than its
            // default severity.
            var enabled = analyzer.SupportedDiagnostics
                .Select(d => new KeyValuePair<string, ReportDiagnostic>(d.Id, ReportDiagnostic.Warn));

            var compilation = CSharpCompilation.Create(
                "Test",
                new[]
                {
                    CSharpSyntaxTree.ParseText(UnityStub),
                    CSharpSyntaxTree.ParseText(source),
                },
                ReferenceAssemblies(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithSpecificDiagnosticOptions(enabled));

            // Surface snippet mistakes as test failures rather than as an empty
            // diagnostic list that reads as "the analyzer found nothing".
            ImmutableArray<Diagnostic> compileErrors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
            if (!compileErrors.IsEmpty)
                throw new Xunit.Sdk.XunitException("Test snippet does not compile: " + string.Join("; ", compileErrors));

            CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
                ImmutableArray.Create(analyzer),
                new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

            return await withAnalyzers.GetAllDiagnosticsAsync();
        }

        internal static async Task<string[]> IdsAsync(DiagnosticAnalyzer analyzer, string source)
        {
            ImmutableArray<Diagnostic> diagnostics = await RunAsync(analyzer, source);
            return diagnostics
                .Where(d => d.Id.StartsWith("MTK"))
                .OrderBy(d => d.Location.SourceSpan.Start)
                .Select(d => d.Id)
                .ToArray();
        }

        private static IEnumerable<MetadataReference> ReferenceAssemblies()
        {
            string dir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
            foreach (string name in new[] { "System.Private.CoreLib", "System.Runtime", "System.Linq", "System.Collections", "netstandard" })
            {
                string path = System.IO.Path.Combine(dir!, name + ".dll");
                if (System.IO.File.Exists(path)) yield return MetadataReference.CreateFromFile(path);
            }

            yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
            yield return MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location);
            yield return MetadataReference.CreateFromFile(typeof(System.Text.StringBuilder).Assembly.Location);
            yield return MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location);
        }
    }
}
