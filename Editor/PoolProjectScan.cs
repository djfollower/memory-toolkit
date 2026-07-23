using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// Runs <see cref="PoolSafetyValidator"/> across every prefab in a folder and
    /// returns the findings as data.
    ///
    /// <para><b>Why this is its own type.</b> Three callers need the same sweep — the
    /// MCP <c>validate_project</c> tool, the batch-mode CI gate, and anything else
    /// that wants a project-wide answer — and each of them wants a different output
    /// format. If the sweep lived inside any one of them the other two would copy it,
    /// and three copies of a validator's entry point drift: a folder-filter fix or a
    /// severity-counting fix lands in one and not the others, and the difference shows
    /// up as CI and the agent disagreeing about whether the project is clean.</para>
    ///
    /// <para>This deliberately produces a model rather than a string or JSON.
    /// Serialization is the caller's business; the sweep is not.</para>
    /// </summary>
    public static class PoolProjectScan
    {
        /// <summary>What to scan, and how much of it.</summary>
        public struct Options
        {
            /// <summary>Asset folder to sweep. Defaults to the whole project.</summary>
            public string Folder;

            /// <summary>Issues below this are counted but not reported.</summary>
            public PoolSafetyValidator.Severity MinSeverity;

            /// <summary>
            /// Hard ceiling on prefabs loaded. A sweep loads every prefab it inspects,
            /// so an unbounded run on a large project is a memory spike inside the
            /// tool that exists to prevent memory spikes.
            /// </summary>
            public int MaxPrefabs;

            public static Options Default => new()
            {
                Folder = "Assets",
                MinSeverity = PoolSafetyValidator.Severity.Warning,
                MaxPrefabs = 5000,
            };
        }

        /// <summary>One prefab's findings, already filtered by severity.</summary>
        public sealed class PrefabReport
        {
            public string AssetPath;
            public string PrefabName;
            public int Errors;
            public int Warnings;
            public readonly List<PoolSafetyValidator.Issue> Issues = new();
        }

        /// <summary>The sweep's result. Counts cover everything scanned, not just what was reported.</summary>
        public sealed class Result
        {
            public string Folder;
            public PoolSafetyValidator.Severity MinSeverity;

            /// <summary>Prefabs matched by the search, before <see cref="Options.MaxPrefabs"/>.</summary>
            public int PrefabsFound;

            public int PrefabsScanned;
            public int PrefabsWithFindings;
            public int TotalErrors;
            public int TotalWarnings;

            /// <summary>True when the cap stopped the sweep short — the result is a floor.</summary>
            public bool HitPrefabCap;

            public readonly List<PrefabReport> Reports = new();
        }

        /// <exception cref="InvalidOperationException">
        /// The folder does not exist. Deliberately loud: <see cref="AssetDatabase.FindAssets(string, string[])"/>
        /// logs its own error and returns nothing for a bad path, which reads to a
        /// caller — an agent, or a CI job about to exit 0 — as "this folder is clean".
        /// </exception>
        public static Result Run(Options options)
        {
            string folder = string.IsNullOrEmpty(options.Folder) ? "Assets" : options.Folder;
            if (folder != "Assets" && !AssetDatabase.IsValidFolder(folder))
                throw new InvalidOperationException($"'{folder}' is not a folder in this project.");

            int maxPrefabs = Mathf.Max(1, options.MaxPrefabs);

            var result = new Result { Folder = folder, MinSeverity = options.MinSeverity };
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            result.PrefabsFound = guids.Length;

            var issues = new List<PoolSafetyValidator.Issue>();

            foreach (string guid in guids)
            {
                if (result.PrefabsScanned >= maxPrefabs)
                {
                    result.HitPrefabCap = true;
                    break;
                }

                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                result.PrefabsScanned++;
                issues.Clear();
                PoolSafetyValidator.Validate(prefab, issues);

                // Severity is declared most-severe-first (Error = 0), so "at least as
                // severe as" is `<=`. Getting this backwards reports every project as
                // clean, which is the one failure mode a gate must not have.
                int errors = 0, warnings = 0;
                for (int i = 0; i < issues.Count; i++)
                {
                    if (issues[i].Severity == PoolSafetyValidator.Severity.Error) errors++;
                    else if (issues[i].Severity == PoolSafetyValidator.Severity.Warning) warnings++;
                }

                result.TotalErrors += errors;
                result.TotalWarnings += warnings;

                var report = new PrefabReport
                {
                    AssetPath = path,
                    PrefabName = prefab.name,
                    Errors = errors,
                    Warnings = warnings,
                };

                for (int i = 0; i < issues.Count; i++)
                {
                    if (issues[i].Severity <= options.MinSeverity)
                        report.Issues.Add(issues[i]);
                }

                if (report.Issues.Count == 0) continue;

                result.PrefabsWithFindings++;
                result.Reports.Add(report);
            }

            return result;
        }
    }
}
