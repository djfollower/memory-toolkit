using System;
using System.IO;
using MemoryToolkit.Budgets;
using UnityEngine;

namespace MemoryToolkit.Diagnostics
{
    /// <summary>
    /// Writes a session report to disk on an interval during a long unattended run,
    /// so an overnight QA soak on a device becomes an artifact a build machine parses
    /// — in the same schema as the CI gate.
    ///
    /// <para><b>Why on device, and why a file.</b> Memory failures are field failures:
    /// they happen after forty minutes on a low-end phone, to a tester who cannot read
    /// a pane and would not know what a pool escape is. The overlay shows the same
    /// data, but nobody watches an overlay for forty minutes. A rotating file survives
    /// the crash it is trying to diagnose and can be pulled off the device afterwards.</para>
    ///
    /// <para>Compiled out entirely outside the editor and development builds, like the
    /// recorder and overlay: shipping a soak writer in a release build would be a
    /// background thread writing files to a player's storage.</para>
    /// </summary>
    public static class MemorySoak
    {
        private static SoakRunner _runner;

        /// <summary>
        /// Begins writing a report every <paramref name="intervalSeconds"/> to
        /// <paramref name="directory"/>, keeping the most recent
        /// <paramref name="keepFiles"/>. A <paramref name="budget"/> supplies the heap
        /// ceiling each report is judged against.
        /// </summary>
        /// <param name="directory">
        /// Defaults to <see cref="Application.persistentDataPath"/>/mtk-soak, which is
        /// the one location writable and pullable on every platform. A device tester
        /// does not choose a path; this has to work with no argument.
        /// </param>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Begin(
            float intervalSeconds = 30f,
            string directory = null,
            int keepFiles = 20,
            MemoryBudget budget = null,
            MemoryBudgetTier? tier = null)
        {
            if (!MemoryRecorder.IsRecording) MemoryRecorder.Enable();

            if (_runner == null)
            {
                var go = new GameObject("[MemoryToolkit] Soak") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<SoakRunner>();
            }

            _runner.Configure(
                Mathf.Max(1f, intervalSeconds),
                string.IsNullOrEmpty(directory)
                    ? Path.Combine(Application.persistentDataPath, "mtk-soak")
                    : directory,
                Mathf.Max(1, keepFiles),
                budget,
                tier);
        }

        /// <summary>Stops writing. The files already written are kept.</summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Stop()
        {
            if (_runner != null) _runner.enabled = false;
        }

        /// <summary>
        /// Writes one report immediately and returns its path, or null if it could
        /// not be written. Use at a known moment — level end, a returned-from-store
        /// event — rather than waiting for the next interval tick.
        /// </summary>
        public static string DumpNow(string directory = null, MemoryBudget budget = null, MemoryBudgetTier? tier = null)
        {
            string dir = string.IsNullOrEmpty(directory)
                ? Path.Combine(Application.persistentDataPath, "mtk-soak")
                : directory;
            return WriteReport(dir, budget, tier, rotateKeeping: 0);
        }

        internal static string WriteReport(string directory, MemoryBudget budget, MemoryBudgetTier? tier, int rotateKeeping)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string json = MemorySessionReport.BuildJson(budget, tier, out _);

                // Sortable, collision-free filenames: a soak run can produce hundreds,
                // and "which is newest" has to be answerable by name alone once they
                // are pulled off the device into a folder with no timestamps.
                string name = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
                string path = Path.Combine(directory, name);
                File.WriteAllText(path, json);

                if (rotateKeeping > 0) Rotate(directory, rotateKeeping);
                return path;
            }
            catch (Exception e)
            {
                // A soak writer that throws would take down the very session it is
                // meant to observe. Report and carry on.
                Debug.LogWarning($"[MemoryToolkit] soak report failed: {e.Message}");
                return null;
            }
        }

        private static void Rotate(string directory, int keep)
        {
            string[] files = Directory.GetFiles(directory, "session-*.json");
            if (files.Length <= keep) return;

            Array.Sort(files, StringComparer.Ordinal); // sortable names → oldest first
            for (int i = 0; i < files.Length - keep; i++)
            {
                try { File.Delete(files[i]); }
                catch { /* a locked or vanished file must not stop the run */ }
            }
        }
    }

    [AddComponentMenu("")]
    internal sealed class SoakRunner : MonoBehaviour
    {
        private float _interval;
        private string _directory;
        private int _keepFiles;
        private MemoryBudget _budget;
        private MemoryBudgetTier? _tier;
        private float _nextWrite;

        internal void Configure(float interval, string directory, int keepFiles, MemoryBudget budget, MemoryBudgetTier? tier)
        {
            _interval = interval;
            _directory = directory;
            _keepFiles = keepFiles;
            _budget = budget;
            _tier = tier;
            _nextWrite = Time.realtimeSinceStartup + interval;
            enabled = true;
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextWrite) return;
            _nextWrite = Time.realtimeSinceStartup + _interval;
            MemorySoak.WriteReport(_directory, _budget, _tier, _keepFiles);
        }

        // The last report is the one that matters most, since it is closest to
        // whatever went wrong. Write it on the way out — but not from OnDestroy during
        // a domain reload, where persistentDataPath access can already be gone.
        private void OnApplicationPause(bool paused)
        {
            if (paused) MemorySoak.WriteReport(_directory, _budget, _tier, _keepFiles);
        }

        private void OnApplicationQuit()
        {
            MemorySoak.WriteReport(_directory, _budget, _tier, _keepFiles);
        }
    }
}
