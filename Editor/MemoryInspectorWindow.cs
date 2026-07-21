using System.Collections.Generic;
using MemoryToolkit.Buffers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// Live view of everything the toolkit is handling, organized the way the
    /// memory is organized — by scope:
    ///
    /// - Heap overview: managed used/reserved and total allocated/reserved, so
    ///   toolkit numbers can be read against the real heap.
    /// - Frame scratch: used / peak / capacity (size the arena from peak).
    /// - Per scope: its pools (active/pooled/total), its arenas, its pinned
    ///   assets, and its owned disposables — plus Trim and Dispose actions.
    ///
    /// Use it in play mode to size warm-up counts and arena capacities from
    /// real data, and to spot leaks: a scope that should be dead but still
    /// lists instances means something cached a reference across a load.
    /// </summary>
    public sealed class MemoryInspectorWindow : EditorWindow
    {
        private readonly List<MemoryManager.PoolStat> _poolStats = new();
        private readonly HashSet<string> _collapsed = new();
        private Vector2 _scroll;

        [MenuItem("Window/Analysis/Memory Toolkit Inspector")]
        private static void Open() => GetWindow<MemoryInspectorWindow>("Memory Inspector");

        private void OnEnable() => EditorApplication.update += Repaint;
        private void OnDisable() => EditorApplication.update -= Repaint;

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter play mode to inspect live memory.", MessageType.Info);
                return;
            }

            DrawToolbar();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawHeapOverview();
            DrawFrameScratch();
            DrawScopes();
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Simulate low memory", EditorStyles.toolbarButton))
                MemoryManager.OnLowMemory();
            if (GUILayout.Button("CollectFull", EditorStyles.toolbarButton))
                MemoryManager.CollectFull();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeapOverview()
        {
            EditorGUILayout.LabelField("Heap", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Managed (used / heap)",
                $"{Bytes(Profiler.GetMonoUsedSizeLong())} / {Bytes(Profiler.GetMonoHeapSizeLong())}");
            EditorGUILayout.LabelField("Unity (allocated / reserved)",
                $"{Bytes(Profiler.GetTotalAllocatedMemoryLong())} / {Bytes(Profiler.GetTotalReservedMemoryLong())}");
            EditorGUILayout.Space();
        }

        private void DrawFrameScratch()
        {
            EditorGUILayout.LabelField("Frame scratch (global, reset each frame)", EditorStyles.boldLabel);
            DrawArena(MemoryManager.FrameScratch);
            EditorGUILayout.Space();
        }

        private void DrawScopes()
        {
            IReadOnlyList<MemoryScope> scopes = MemoryManager.LiveScopes;
            EditorGUILayout.LabelField($"Scopes ({scopes.Count})", EditorStyles.boldLabel);

            // Snapshot: scope actions below can mutate the live list.
            for (int s = 0; s < scopes.Count; s++)
            {
                MemoryScope scope = scopes[s];

                EditorGUILayout.BeginHorizontal();
                bool expanded = !_collapsed.Contains(scope.Name);
                bool nowExpanded = EditorGUILayout.Foldout(expanded, scope.Name, toggleOnLabelClick: true);
                if (nowExpanded != expanded)
                {
                    if (nowExpanded) _collapsed.Remove(scope.Name);
                    else _collapsed.Add(scope.Name);
                }
                if (GUILayout.Button("Trim", EditorStyles.miniButton, GUILayout.Width(50)))
                    scope.Trim(MemoryManager.LowMemoryKeepPerPool);
                using (new EditorGUI.DisabledScope(scope == MemoryManager.Permanent))
                {
                    if (GUILayout.Button("Dispose", EditorStyles.miniButton, GUILayout.Width(70)))
                    {
                        scope.Dispose();
                        EditorGUILayout.EndHorizontal();
                        return; // list changed; redraw next repaint
                    }
                }
                EditorGUILayout.EndHorizontal();

                if (!nowExpanded) continue;
                EditorGUI.indentLevel++;
                DrawScopeContents(scope);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawScopeContents(MemoryScope scope)
        {
            _poolStats.Clear();
            scope.CollectStats(_poolStats);
            if (_poolStats.Count > 0)
            {
                EditorGUILayout.LabelField("Pools (active / pooled / total)", EditorStyles.miniBoldLabel);
                foreach (MemoryManager.PoolStat stat in _poolStats)
                    EditorGUILayout.LabelField(stat.PrefabName,
                        $"{stat.CountActive} / {stat.CountInactive} / {stat.CountAll}");
            }

            if (scope.Allocators.Count > 0)
            {
                EditorGUILayout.LabelField("Arenas", EditorStyles.miniBoldLabel);
                for (int i = 0; i < scope.Allocators.Count; i++)
                    DrawArena(scope.Allocators[i]);
            }

            if (scope.PinnedAssets.Count > 0)
            {
                EditorGUILayout.LabelField($"Pinned assets ({scope.PinnedAssets.Count})", EditorStyles.miniBoldLabel);
                for (int i = 0; i < scope.PinnedAssets.Count; i++)
                {
                    Object asset = scope.PinnedAssets[i];
                    if (asset == null)
                        EditorGUILayout.LabelField("(destroyed)", "-");
                    else
                        EditorGUILayout.LabelField(asset.name,
                            $"{asset.GetType().Name}, {Bytes(Profiler.GetRuntimeMemorySizeLong(asset))}");
                }
            }

            if (scope.OwnedDisposableCount > 0)
                EditorGUILayout.LabelField("Owned disposables", scope.OwnedDisposableCount.ToString());

            if (_poolStats.Count == 0 && scope.Allocators.Count == 0 &&
                scope.PinnedAssets.Count == 0 && scope.OwnedDisposableCount == 0)
                EditorGUILayout.LabelField("(empty)");
        }

        private static void DrawArena(FrameAllocator arena)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 18f);
            rect = EditorGUI.IndentedRect(rect);
            float fill = arena.CapacityBytes > 0 ? (float)arena.UsedBytes / arena.CapacityBytes : 0f;
            EditorGUI.ProgressBar(rect, fill,
                $"{Bytes(arena.UsedBytes)} / {Bytes(arena.CapacityBytes)}  (peak {Bytes(arena.PeakUsedBytes)})");
        }

        private static string Bytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:0.0} KB";
            return $"{bytes} B";
        }
    }
}
