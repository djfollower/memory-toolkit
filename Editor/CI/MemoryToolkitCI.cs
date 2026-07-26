using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MemoryToolkit.Budgets;
using MemoryToolkit.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace MemoryToolkit.Editor.CI
{
    /// <summary>
    /// Batch-mode entry point: runs the static checks, writes machine-readable
    /// reports, and exits non-zero when the project regresses.
    ///
    /// <code>
    /// Unity -batchmode -quit -projectPath . \
    ///       -executeMethod MemoryToolkit.Editor.CI.MemoryToolkitCI.Validate \
    ///       -mtk-out memory-report.json -mtk-junit memory-report.xml -mtk-fail-on error
    /// </code>
    ///
    /// <para><b>Why the static gate is the one that ships first.</b> It reads prefab
    /// data and a budget asset — no play session, no graphics device, nothing the
    /// studio has to build. A team with no automated play sessions still gets value on
    /// day one, which decides whether any of this gets wired up at all. The dynamic
    /// gate (escapes and heap ceilings over a real session) needs a play session the
    /// project supplies, so it is opt-in and lives in
    /// <see cref="WriteSessionReport"/>, called from the studio's own automation.</para>
    ///
    /// <para>Every argument is optional. Called with none, this validates every prefab
    /// under <c>Assets</c> and fails on errors, which is the behaviour a team wants
    /// before they have read any documentation.</para>
    /// </summary>
    public static class MemoryToolkitCI
    {
        /// <summary>Version of the JSON report's schema. Shared with the device dumps.</summary>
        public const int JsonSchemaVersion = 1;

        private const int ExitClean = 0;
        private const int ExitFindings = 1;
        private const int ExitError = 2;

        /// <summary>The <c>-executeMethod</c> target. Exits the editor with a status code.</summary>
        public static void Validate()
        {
            int code;
            try
            {
                code = Run(Arguments.FromCommandLine());
            }
            catch (Exception e)
            {
                // A gate that crashes must not look like a gate that passed.
                Debug.LogError($"[MemoryToolkit CI] {e}");
                code = ExitError;
            }

            // -quit alone exits 0 regardless of what happened, so the status code has
            // to be set explicitly or the build stays green through any failure.
            EditorApplication.Exit(code);
        }

        /// <summary>Runs the gate without exiting. For tests and for embedding in a larger build script.</summary>
        public static int Run(Arguments args)
        {
            var findings = new List<PoolSafetyValidator.Issue>();

            PoolProjectScan.Result scan = PoolProjectScan.Run(new PoolProjectScan.Options
            {
                Folder = args.Folder,
                MinSeverity = args.MinSeverity,
                MaxPrefabs = args.MaxPrefabs,
            });

            foreach (PoolProjectScan.PrefabReport report in scan.Reports)
            {
                foreach (PoolSafetyValidator.Issue issue in report.Issues)
                {
                    findings.Add(new PoolSafetyValidator.Issue(
                        issue.Severity, $"{report.AssetPath}: {issue.Path}", issue.Message, issue.Context));
                }
            }

            MemoryBudget budget = LoadBudget(args.BudgetPath);
            if (budget != null) MemoryBudgetAudit.Audit(budget, findings);

            int errors = 0, warnings = 0;
            foreach (PoolSafetyValidator.Issue issue in findings)
            {
                if (issue.Severity == PoolSafetyValidator.Severity.Error) errors++;
                else if (issue.Severity == PoolSafetyValidator.Severity.Warning) warnings++;
            }

            if (!string.IsNullOrEmpty(args.JsonPath)) WriteJson(args.JsonPath, args, scan, budget, findings);
            if (!string.IsNullOrEmpty(args.JunitPath)) WriteJunit(args.JunitPath, findings, errors);

            Debug.Log(
                $"[MemoryToolkit CI] {scan.PrefabsScanned} prefab(s) scanned, " +
                $"{errors} error(s), {warnings} warning(s)" +
                (budget != null ? $", budget '{budget.name}' audited" : ", no budget audited") +
                (scan.HitPrefabCap ? " — PREFAB CAP HIT, result is a floor" : "") + ".");

            bool failed = args.FailOn switch
            {
                FailLevel.Never => false,
                FailLevel.Warning => errors > 0 || warnings > 0,
                _ => errors > 0,
            };

            return failed ? ExitFindings : ExitClean;
        }

        /// <summary>
        /// Writes the dynamic half of the gate: what a real session did. Call this
        /// from the project's own automated play session, after it ends.
        ///
        /// <para>Escapes is the number that matters here — instances that reached
        /// <c>PoolBridge.Return</c> owned by no pool, and so were destroyed rather
        /// than pooled. Non-zero means pooling is not working and is costing more than
        /// not pooling at all. Captured before a migration it is the baseline; captured
        /// nightly afterwards it is the regression test, and it is the only one that
        /// catches a pool that quietly stopped pooling.</para>
        /// </summary>
        /// <returns>True when the session is within budget.</returns>
        public static bool WriteSessionReport(string path, MemoryBudget budget = null, MemoryBudgetTier? tier = null)
        {
            // Delegates to the runtime builder so the session schema has exactly one
            // definition — the device soak dump and this gate must be parseable by the
            // same reader, or a device dump is one nobody reads.
            string json = MemorySessionReport.BuildJson(budget, tier, out MemorySessionReport.Result result);
            WriteAllText(path, json);
            return result.Passed;
        }

        // ---- Arguments ---------------------------------------------------------------

        /// <summary>How much has to be wrong before the build fails.</summary>
        public enum FailLevel
        {
            /// <summary>Report only. Use for the first nightly run, to see the size of the backlog.</summary>
            Never,

            Error,

            /// <summary>Once the backlog is at zero, this is what stops it coming back.</summary>
            Warning,
        }

        /// <summary>Parsed CLI options.</summary>
        public struct Arguments
        {
            public string Folder;
            public string BudgetPath;
            public string JsonPath;
            public string JunitPath;
            public PoolSafetyValidator.Severity MinSeverity;
            public FailLevel FailOn;
            public int MaxPrefabs;

            public static Arguments Default => new()
            {
                Folder = "Assets",
                MinSeverity = PoolSafetyValidator.Severity.Warning,
                FailOn = FailLevel.Error,
                MaxPrefabs = 5000,
            };

            public static Arguments FromCommandLine()
            {
                Arguments args = Default;
                string[] argv = Environment.GetCommandLineArgs();

                for (int i = 0; i < argv.Length; i++)
                {
                    string next = i + 1 < argv.Length ? argv[i + 1] : null;
                    switch (argv[i])
                    {
                        case "-mtk-folder": args.Folder = next; break;
                        case "-mtk-budget": args.BudgetPath = next; break;
                        case "-mtk-out": args.JsonPath = next; break;
                        case "-mtk-junit": args.JunitPath = next; break;
                        case "-mtk-max-prefabs":
                            if (int.TryParse(next, out int max)) args.MaxPrefabs = max;
                            break;
                        case "-mtk-min-severity":
                            if (Enum.TryParse(next, true, out PoolSafetyValidator.Severity severity))
                                args.MinSeverity = severity;
                            break;
                        case "-mtk-fail-on":
                            if (Enum.TryParse(next, true, out FailLevel level)) args.FailOn = level;
                            else if (string.Equals(next, "none", StringComparison.OrdinalIgnoreCase))
                                args.FailOn = FailLevel.Never;
                            break;
                    }
                }

                return args;
            }
        }

        // ---- Reports -----------------------------------------------------------------

        private static MemoryBudget LoadBudget(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var budget = AssetDatabase.LoadAssetAtPath<MemoryBudget>(path);

            // Asked for a budget and it is not there: fail loudly rather than auditing
            // nothing and reporting clean, which is how a typo becomes a green build.
            if (budget == null)
                throw new InvalidOperationException($"No MemoryBudget asset at '{path}'.");

            return budget;
        }

        private static void WriteJson(
            string path,
            Arguments args,
            PoolProjectScan.Result scan,
            MemoryBudget budget,
            List<PoolSafetyValidator.Issue> findings)
        {
            var sb = new StringBuilder();
            sb.Append("{\"schemaVersion\":").Append(JsonSchemaVersion)
                .Append(",\"kind\":\"static\"")
                .Append(",\"folder\":\"").Append(Escape(scan.Folder)).Append('"')
                .Append(",\"budget\":").Append(budget != null ? $"\"{Escape(budget.name)}\"" : "null")
                .Append(",\"minSeverity\":\"").Append(args.MinSeverity).Append('"')
                .Append(",\"prefabsFound\":").Append(scan.PrefabsFound)
                .Append(",\"prefabsScanned\":").Append(scan.PrefabsScanned)
                .Append(",\"prefabsWithFindings\":").Append(scan.PrefabsWithFindings)
                .Append(",\"hitPrefabCap\":").Append(scan.HitPrefabCap ? "true" : "false")
                .Append(",\"totalErrors\":").Append(scan.TotalErrors)
                .Append(",\"totalWarnings\":").Append(scan.TotalWarnings)
                .Append(",\"findings\":[");

            for (int i = 0; i < findings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"severity\":\"").Append(findings[i].Severity)
                    .Append("\",\"path\":\"").Append(Escape(findings[i].Path))
                    .Append("\",\"message\":\"").Append(Escape(findings[i].Message))
                    .Append("\"}");
            }

            sb.Append("]}");
            WriteAllText(path, sb.ToString());
        }

        private static void WriteJunit(string path, List<PoolSafetyValidator.Issue> findings, int errors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<testsuite name=\"MemoryToolkit\" tests=\"")
                .Append(Math.Max(findings.Count, 1))
                .Append("\" failures=\"").Append(errors).AppendLine("\">");

            if (findings.Count == 0)
            {
                sb.AppendLine("  <testcase classname=\"MemoryToolkit\" name=\"PoolSafety\" />");
            }
            else
            {
                foreach (PoolSafetyValidator.Issue issue in findings)
                {
                    sb.Append("  <testcase classname=\"MemoryToolkit.")
                        .Append(issue.Severity)
                        .Append("\" name=\"").Append(EscapeXml(issue.Path)).Append('"');

                    // Only errors are failures; warnings ride along as skipped so they
                    // stay visible in the CI UI without turning the build red before
                    // the team has agreed to that.
                    if (issue.Severity == PoolSafetyValidator.Severity.Error)
                    {
                        sb.AppendLine(">");
                        sb.Append("    <failure message=\"").Append(EscapeXml(issue.Message)).AppendLine("\" />");
                        sb.AppendLine("  </testcase>");
                    }
                    else
                    {
                        sb.AppendLine(">");
                        sb.Append("    <skipped message=\"").Append(EscapeXml(issue.Message)).AppendLine("\" />");
                        sb.AppendLine("  </testcase>");
                    }
                }
            }

            sb.AppendLine("</testsuite>");
            WriteAllText(path, sb.ToString());
        }

        private static void WriteAllText(string path, string contents)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, contents);
            Debug.Log($"[MemoryToolkit CI] wrote {path}");
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }

            return sb.ToString();
        }

        private static string EscapeXml(string value)
            => string.IsNullOrEmpty(value)
                ? string.Empty
                : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
    }
}
