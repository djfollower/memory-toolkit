using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// Runs the adoption triage of <c>docs/ADOPTION.md</c> §1 as code: the six passes
    /// that find a codebase's lifetime boundaries before anyone reads a line of it.
    ///
    /// <para><b>Why this is worth automating.</b> The real asset in this package is the
    /// <i>method</i> in the field guides, and the first step of that method is
    /// mechanical — six greps and a census. Turning it from something a person reads
    /// into something an agent runs is the difference between a document and a tool.
    /// The output is not a fix list; it is a scope map an agent (or a person) then
    /// reasons about, which is exactly where judgement belongs.</para>
    ///
    /// <para>Deliberately depends only on <c>System.IO</c> and regex, not on
    /// <c>UnityEditor</c>. Triage is a read over source text, and keeping it free of
    /// the AssetDatabase means the same shipped code can be pointed at any directory —
    /// which is how it is measured against the reference projects the guides are built
    /// on, rather than against a synthetic scene.</para>
    ///
    /// <para>It is heuristic, and honest about it: every finding carries the evidence
    /// it was drawn from, so a wrong guess is arguable rather than authoritative.</para>
    /// </summary>
    public static class ProjectTriage
    {
        // Compiled once; a triage scan touches thousands of files.
        private static readonly Regex InstantiateRx = new(@"\bInstantiate\s*[(<]", RegexOptions.Compiled);
        private static readonly Regex DestroyRx = new(@"\b(?:Object\.)?Destroy\s*\(", RegexOptions.Compiled);
        private static readonly Regex DestroyImmediateRx = new(@"\bDestroyImmediate\s*\(", RegexOptions.Compiled);
        private static readonly Regex UpdateRx = new(@"\bvoid\s+Update\s*\(", RegexOptions.Compiled);
        private static readonly Regex LateUpdateRx = new(@"\bvoid\s+LateUpdate\s*\(", RegexOptions.Compiled);
        private static readonly Regex FixedUpdateRx = new(@"\bvoid\s+FixedUpdate\s*\(", RegexOptions.Compiled);
        private static readonly Regex OnDestroyRx = new(@"\b(?:void|override\s+void)\s+OnDestroy\s*\(", RegexOptions.Compiled);

        // Incumbent-pool signatures: the extension-method idiom a hand-rolled pool is
        // almost always reached through, and the type names it almost always uses.
        private static readonly Regex PoolMemberRx =
            new(@"\b(GetFromPool|ReturnToPool|SpawnFromPool|Despawn|\.Rent\(|\.Spawn\()", RegexOptions.Compiled);
        private static readonly Regex PoolTypeRx =
            new(@"\bclass\s+\w*Pool\w*\b|\bObjectPool<|\bIObjectPool<", RegexOptions.Compiled);

        // Boot and session-boundary name heuristics. Names, not behaviour — but a
        // class called AppLoader is the boot entry far more often than not, and the
        // finding says which signal fired so it can be discounted.
        private static readonly Regex BootNameRx =
            new(@"\bclass\s+(\w*(?:AppLoader|Bootstrap|Bootstrapper|GameInit|Startup|EntryPoint|AppMain|RootInstaller|ProjectContext)\w*)\b",
                RegexOptions.Compiled);

        /// <summary>Where a candidate was found, so a reader can go look.</summary>
        public readonly struct Evidence
        {
            public Evidence(string path, int line, string text)
            {
                Path = path;
                Line = line;
                Text = text;
            }

            public string Path { get; }
            public int Line { get; }
            public string Text { get; }
        }

        /// <summary>The triage result. Counts are exact; candidates are heuristic.</summary>
        public sealed class Result
        {
            public string Root;
            public int FilesScanned;

            // Grep 3: the churn.
            public int InstantiateCalls;
            public int DestroyCalls;

            // Grep 4: existing pooling → adopt vs integrate.
            public int FilesMentioningPool;
            public bool HasIncumbentPool;

            /// <summary>ADOPT (greenfield) or INTEGRATE (an incumbent pool exists). Grep 4's branch.</summary>
            public string RecommendedGuide;

            // Grep 6: the per-frame census.
            public int UpdateMethods;
            public int LateUpdateMethods;
            public int FixedUpdateMethods;

            // Grep 1 / 2: lifetime boundaries.
            public readonly List<Evidence> BootCandidates = new();
            public readonly List<Evidence> SessionBoundaries = new();

            // Grep 5: the innermost loop, approximated as the file with the most churn.
            public Evidence? HottestChurnFile;
            public int HottestChurnCount;

            public readonly List<Evidence> IncumbentPoolEvidence = new();
        }

        /// <summary>Scans <paramref name="rootDirectory"/> for C# files and runs all six passes.</summary>
        public static Result Run(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new InvalidOperationException($"'{rootDirectory}' is not a directory.");

            var result = new Result { Root = rootDirectory };

            foreach (string path in Directory.EnumerateFiles(rootDirectory, "*.cs", SearchOption.AllDirectories))
            {
                // Skip generated and third-party trees: they inflate the churn counts
                // with code nobody on the team will touch, which is exactly the noise
                // grep 3 is supposed to cut through.
                if (IsExcluded(path)) continue;

                string text;
                try { text = File.ReadAllText(path); }
                catch { continue; }

                result.FilesScanned++;
                ScanFile(result, path, text);
            }

            Finish(result);
            return result;
        }

        private static void ScanFile(Result result, string path, string text)
        {
            int instantiate = InstantiateRx.Matches(text).Count;
            int destroy = DestroyRx.Matches(text).Count + DestroyImmediateRx.Matches(text).Count;
            result.InstantiateCalls += instantiate;
            result.DestroyCalls += destroy;

            int churn = instantiate + destroy;
            if (churn > result.HottestChurnCount)
            {
                result.HottestChurnCount = churn;
                result.HottestChurnFile = new Evidence(path, 0, $"{instantiate} Instantiate, {destroy} Destroy");
            }

            result.UpdateMethods += UpdateRx.Matches(text).Count;
            result.LateUpdateMethods += LateUpdateRx.Matches(text).Count;
            result.FixedUpdateMethods += FixedUpdateRx.Matches(text).Count;

            bool mentionsPool = text.IndexOf("Pool", StringComparison.Ordinal) >= 0;
            if (mentionsPool) result.FilesMentioningPool++;

            if (PoolMemberRx.IsMatch(text) || PoolTypeRx.IsMatch(text))
            {
                result.HasIncumbentPool = true;
                if (result.IncumbentPoolEvidence.Count < 10)
                    AddFirstMatch(result.IncumbentPoolEvidence, path, text, PoolMemberRx, PoolTypeRx);
            }

            Match boot = BootNameRx.Match(text);
            if (boot.Success && result.BootCandidates.Count < 10)
                result.BootCandidates.Add(new Evidence(path, LineOf(text, boot.Index), boot.Groups[1].Value));

            // Session boundary: a class whose OnDestroy is substantial is the teardown
            // someone maintains by hand — the natural scope. Cheap proxy for "big":
            // more than a couple of statements between the braces.
            Match onDestroy = OnDestroyRx.Match(text);
            if (onDestroy.Success && result.SessionBoundaries.Count < 20)
            {
                int body = CountStatementsInBody(text, onDestroy.Index);
                if (body >= 3)
                {
                    result.SessionBoundaries.Add(new Evidence(
                        path, LineOf(text, onDestroy.Index), $"OnDestroy with ~{body} statements"));
                }
            }
        }

        private static void Finish(Result result)
        {
            result.RecommendedGuide = result.HasIncumbentPool ? "INTEGRATE" : "ADOPT";

            // Order boundaries so the fattest OnDestroy is first — that is the most
            // likely session scope, and the guide says to look for exactly it.
            result.SessionBoundaries.Sort((a, b) => string.CompareOrdinal(b.Text, a.Text));
        }

        private static void AddFirstMatch(List<Evidence> into, string path, string text, params Regex[] patterns)
        {
            foreach (Regex rx in patterns)
            {
                Match m = rx.Match(text);
                if (!m.Success) continue;
                into.Add(new Evidence(path, LineOf(text, m.Index), m.Value.Trim()));
                return;
            }
        }

        /// <summary>Counts <c>;</c> between the method's opening and closing brace. A rough size, not a parse.</summary>
        private static int CountStatementsInBody(string text, int methodIndex)
        {
            int open = text.IndexOf('{', methodIndex);
            if (open < 0) return 0;

            int depth = 0, count = 0;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (c == ';' && depth >= 1) count++;
            }

            return count;
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        private static bool IsExcluded(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("/Library/") || p.Contains("/Temp/") || p.Contains("/obj/")
                || p.Contains("/PackageCache/") || p.Contains("/Plugins/")
                || p.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
