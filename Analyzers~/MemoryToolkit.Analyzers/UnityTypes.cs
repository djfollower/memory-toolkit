using Microsoft.CodeAnalysis;

namespace MemoryToolkit.Analyzers
{
    /// <summary>Symbol lookups these analyzers share.</summary>
    internal static class UnityTypes
    {
        internal const string UnityObject = "UnityEngine.Object";
        internal const string MonoBehaviour = "UnityEngine.MonoBehaviour";

        /// <summary>
        /// True when <paramref name="type"/> is <c>UnityEngine.Object</c> or derives
        /// from it — the types whose <c>==</c> is overloaded to report a destroyed
        /// native object as null.
        ///
        /// <para>Matched by fully-qualified name rather than by comparing to a symbol
        /// resolved from the compilation, because analyzers also run on assemblies
        /// that do not reference UnityEngine at all (an editor-only utility project,
        /// a test assembly), where that lookup returns null and would silently
        /// disable the rule everywhere.</para>
        /// </summary>
        internal static bool IsUnityObject(ITypeSymbol type)
        {
            for (ITypeSymbol current = type; current != null; current = current.BaseType)
            {
                if (current.ToDisplayString() == UnityObject) return true;
            }

            return false;
        }

        /// <summary>True when <paramref name="type"/> derives from <c>UnityEngine.MonoBehaviour</c>.</summary>
        internal static bool IsMonoBehaviour(ITypeSymbol type)
        {
            for (ITypeSymbol current = type; current != null; current = current.BaseType)
            {
                if (current.ToDisplayString() == MonoBehaviour) return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="type"/> implements the toolkit's
        /// <c>MemoryToolkit.Pooling.IPoolable</c>. Matched by name because the analyzer
        /// does not reference the runtime — but the analyzed project does, so the
        /// symbol is present in its compilation.
        /// </summary>
        internal static bool ImplementsIPoolable(ITypeSymbol type)
        {
            if (type == null) return false;

            foreach (INamedTypeSymbol i in type.AllInterfaces)
            {
                if (i.ToDisplayString() == "MemoryToolkit.Pooling.IPoolable") return true;
            }

            return false;
        }

        /// <summary>
        /// The Unity messages that run every frame. Not <c>OnGUI</c>: it runs several
        /// times per frame but is already understood to be slow, and flagging it
        /// would bury the rule in findings from editor-only UI code.
        /// </summary>
        internal static bool IsPerFrameMessage(string methodName)
            => methodName == "Update" || methodName == "LateUpdate" || methodName == "FixedUpdate";
    }
}
