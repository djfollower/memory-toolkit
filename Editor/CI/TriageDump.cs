using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MemoryToolkit.Editor.CI
{
    /// <summary>
    /// Batch-mode entry point that runs <see cref="ProjectTriage"/> against an
    /// arbitrary directory and prints the result.
    ///
    /// <para>Its reason for existing is the acceptance test: the triage's ground truth
    /// is the hand-written scope maps in the field guides, which were derived from two
    /// real production codebases. This runs the <i>shipped</i> triage code against
    /// those same codebases so the output can be diffed against the maps — measuring
    /// the method, not a synthetic scene.</para>
    ///
    /// <code>
    /// Unity -batchmode -quit -projectPath . \
    ///       -executeMethod MemoryToolkit.Editor.CI.TriageDump.Run \
    ///       -mtk-triage-path /path/to/OtherProject/Assets
    /// </code>
    /// </summary>
    public static class TriageDump
    {
        public static void Run()
        {
            int code = 0;
            try
            {
                string path = ArgValue("-mtk-triage-path") ?? Application.dataPath;
                ProjectTriage.Result r = ProjectTriage.Run(path);
                Debug.Log(Format(r));
            }
            catch (Exception e)
            {
                Debug.LogError($"[MemoryToolkit triage] {e}");
                code = 2;
            }

            EditorApplication.Exit(code);
        }

        private static string Format(ProjectTriage.Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== MemoryToolkit triage ===");
            sb.AppendLine($"root: {r.Root}");
            sb.AppendLine($"files scanned: {r.FilesScanned}");
            sb.AppendLine($"recommended guide: {r.RecommendedGuide}");
            sb.AppendLine($"churn: {r.InstantiateCalls} Instantiate, {r.DestroyCalls} Destroy " +
                          $"(ratio {(r.InstantiateCalls > 0 ? (double)r.DestroyCalls / r.InstantiateCalls : 0):0.00})");
            sb.AppendLine($"files mentioning Pool: {r.FilesMentioningPool}   incumbent pool: {r.HasIncumbentPool}");
            sb.AppendLine($"per-frame: {r.UpdateMethods} Update, {r.LateUpdateMethods} LateUpdate, {r.FixedUpdateMethods} FixedUpdate");

            sb.AppendLine("boot candidates:");
            foreach (ProjectTriage.Evidence e in r.BootCandidates)
                sb.AppendLine($"  {e.Text}  ({Rel(e.Path)}:{e.Line})");

            sb.AppendLine("session boundaries (fattest OnDestroy first):");
            foreach (ProjectTriage.Evidence e in r.SessionBoundaries)
                sb.AppendLine($"  {e.Text}  ({Rel(e.Path)}:{e.Line})");

            if (r.HottestChurnFile.HasValue)
                sb.AppendLine($"hottest churn file: {Rel(r.HottestChurnFile.Value.Path)} — {r.HottestChurnFile.Value.Text}");

            if (r.HasIncumbentPool)
            {
                sb.AppendLine("incumbent pool evidence:");
                foreach (ProjectTriage.Evidence e in r.IncumbentPoolEvidence)
                    sb.AppendLine($"  {e.Text}  ({Rel(e.Path)}:{e.Line})");
            }

            return sb.ToString();
        }

        private static string Rel(string path)
        {
            string p = path.Replace('\\', '/');
            int i = p.IndexOf("/Assets/", StringComparison.Ordinal);
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        private static string ArgValue(string flag)
        {
            string[] argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length - 1; i++)
                if (argv[i] == flag) return argv[i + 1];
            return null;
        }
    }
}
