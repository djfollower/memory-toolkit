using System.Collections.Generic;
using MemoryToolkit.Budgets;
using MemoryToolkit.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// Inspector for <see cref="MemoryBudget"/>, whose reason for existing is one
    /// button.
    ///
    /// <para>Sizing a pool is a measurement — the Timeline's peak active over a
    /// representative session — and until now the last step of that measurement was a
    /// human reading a number off a chart and typing it into an installer. That step
    /// is where the loop breaks: it is done once, at adoption, and never again, so the
    /// numbers rot from the day they are written. <b>Apply measured peaks</b> makes it
    /// one click against a live recording, which is the only version of this workflow
    /// anyone repeats.</para>
    /// </summary>
    [CustomEditor(typeof(MemoryBudget))]
    public sealed class MemoryBudgetEditor : UnityEditor.Editor
    {
        private MemoryBudgetTier _writeTier = MemoryBudgetTier.High;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Measured peaks", EditorStyles.boldLabel);

            var budget = (MemoryBudget)target;
            int seriesCount = MemoryRecorder.PoolSeriesList.Count;

            using (new EditorGUI.DisabledScope(seriesCount == 0))
            {
                _writeTier = (MemoryBudgetTier)EditorGUILayout.EnumPopup("Write into tier", _writeTier);

                if (GUILayout.Button($"Apply measured peaks ({seriesCount} pool(s) recorded)"))
                    ApplyMeasuredPeaks(budget, _writeTier);
            }

            if (seriesCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing recorded yet. Open Window > Analysis > Memory Toolkit Inspector, press Record, " +
                    "and play a representative session — a peak is only as good as the session that produced it.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Writes each recorded pool's peak active count into the matching entry's Warmup, matched on " +
                    "scope name and prefab name. Entries with no recording are left alone; recorded pools with no " +
                    "entry are reported in the console rather than added, because which scope owns a pool is a " +
                    "decision, not a measurement.",
                    MessageType.None);
            }
        }

        private static void ApplyMeasuredPeaks(MemoryBudget budget, MemoryBudgetTier tier)
        {
            // Peak keyed by scope+prefab, matching how the recorder identifies a row.
            var peaks = new Dictionary<(string, string), int>();
            foreach (PoolSeries series in MemoryRecorder.PoolSeriesList)
            {
                var key = (series.ScopeName, series.PrefabName);

                // A pool disposed and recreated appears once; keep the larger peak so
                // a budget is never sized from the quieter of two lives.
                if (!peaks.TryGetValue(key, out int existing) || series.PeakActive > existing)
                    peaks[key] = series.PeakActive;
            }

            Undo.RecordObject(budget, "Apply measured peaks");

            ScopeBudget[] scopes = budget.ScopesForEditing;
            int written = 0;
            var matched = new HashSet<(string, string)>();

            for (int s = 0; s < scopes.Length; s++)
            {
                PoolBudget[] pools = scopes[s].Pools;
                if (pools == null) continue;

                for (int p = 0; p < pools.Length; p++)
                {
                    string prefabName = pools[p].Prefab != null ? pools[p].Prefab.name : pools[p].AddressableKey;
                    if (string.IsNullOrEmpty(prefabName)) continue;

                    var key = (scopes[s].ScopeName, prefabName);
                    if (!peaks.TryGetValue(key, out int peak) || peak <= 0) continue;

                    matched.Add(key);
                    TieredInt warmup = pools[p].Warmup;
                    switch (tier)
                    {
                        case MemoryBudgetTier.Low: warmup.Low = peak; break;
                        case MemoryBudgetTier.Medium: warmup.Medium = peak; break;
                        default: warmup.High = peak; break;
                    }

                    // maxSize below warmup would have the pool destroy instances it
                    // just warmed; raise it rather than writing an incoherent asset.
                    TieredInt maxSize = pools[p].MaxSize;
                    if (maxSize.Get(tier) < peak)
                    {
                        switch (tier)
                        {
                            case MemoryBudgetTier.Low: maxSize.Low = peak; break;
                            case MemoryBudgetTier.Medium: maxSize.Medium = peak; break;
                            default: maxSize.High = peak; break;
                        }
                    }

                    pools[p].Warmup = warmup;
                    pools[p].MaxSize = maxSize;
                    written++;
                }
            }

            budget.ScopesForEditing = scopes;
            EditorUtility.SetDirty(budget);
            AssetDatabase.SaveAssetIfDirty(budget);

            foreach (KeyValuePair<(string Scope, string Prefab), int> entry in peaks)
            {
                if (matched.Contains(entry.Key) || entry.Value <= 0) continue;

                Debug.LogWarning(
                    $"[Memory Budget] Recorded pool '{entry.Key.Scope}/{entry.Key.Prefab}' (peak {entry.Value}) has no " +
                    "budget entry. Add one if this pool should be pre-warmed — which scope owns it is a decision, " +
                    "not something a recording can answer.", budget);
            }

            Debug.Log($"[Memory Budget] Wrote {written} peak(s) into the {tier} tier of '{budget.name}'.", budget);
        }
    }
}
