using System.Collections.Generic;
using MemoryToolkit.Buffers;
using MemoryToolkit.Diagnostics;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// Live view of everything the toolkit is handling, organized the way the
    /// memory is organized — by scope:
    ///
    /// - Heap overview: managed used/reserved and total allocated/reserved, so
    ///   toolkit numbers can be read against the real heap.
    /// - Timeline: history of escapes, managed heap, and per-pool occupancy —
    ///   the pane that can show a transition rather than a moment.
    /// - Frame scratch: used / peak / capacity (size the arena from peak).
    /// - Per scope: its pools (active/pooled/total), its arenas, its pinned
    ///   assets, and its owned disposables — plus Trim and Dispose actions.
    ///
    /// Use it in play mode to size warm-up counts and arena capacities from
    /// real data, and to spot leaks: a scope that should be dead but still
    /// lists instances means something cached a reference across a load.
    ///
    /// <para>Built with UI Toolkit rather than IMGUI. The distinction is not
    /// cosmetic here: an IMGUI window rebuilds its entire contents on every
    /// repaint, so a diagnostic that has to stay open for minutes at a time was
    /// re-issuing every label and every chart column many times a second. Here the
    /// element tree is retained, text is updated in place, and the charts
    /// regenerate geometry only when new samples arrive.</para>
    /// </summary>
    public sealed class MemoryInspectorWindow : EditorWindow
    {
        private const long RefreshIntervalMs = 250;

        private readonly List<MemoryManager.PoolStat> _poolStats = new();
        private readonly Dictionary<string, PoolRow> _poolRows = new();

        private VisualElement _playModeHint;
        private VisualElement _body;

        private Label _managedLabel;
        private Label _unityLabel;

        private ToolbarButton _recordButton;
        private ToolbarButton _clearButton;
        private ToolbarButton _dumpButton;

        private Foldout _timelineFoldout;
        private Label _timelineStatus;
        private VisualElement _timelineContent;
        private Label _escapesLabel;
        private TimelineChart _escapeChart;
        private TimelineChart _heapChart;
        private VisualElement _poolRowContainer;
        private VisualElement _eventList;

        private VisualElement _frameScratchContainer;
        private VisualElement _scopeContainer;

        [MenuItem("Window/Analysis/Memory Toolkit Inspector")]
        private static void Open() => GetWindow<MemoryInspectorWindow>("Memory Inspector");

        private void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Add(BuildToolbar());

            _playModeHint = new HelpBox("Enter play mode to inspect live memory.", HelpBoxMessageType.Info);
            root.Add(_playModeHint);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1f } };
            root.Add(scroll);

            _body = new VisualElement();
            scroll.Add(_body);

            _body.Add(BuildHeapSection());
            _body.Add(BuildTimelineSection());
            _body.Add(BuildFrameScratchSection());
            _body.Add(BuildScopeSection());

            // One scheduled refresh instead of repainting on every editor tick.
            // Four times a second matches the recorder's sample rate; anything
            // faster only redraws data that has not changed.
            root.schedule.Execute(Refresh).Every(RefreshIntervalMs);
            Refresh();
        }

        private VisualElement BuildToolbar()
        {
            var toolbar = new Toolbar();

            _recordButton = new ToolbarButton(ToggleRecording) { text = "Record" };
            toolbar.Add(_recordButton);

            _clearButton = new ToolbarButton(MemoryRecorder.Clear) { text = "Clear" };
            toolbar.Add(_clearButton);

            _dumpButton = new ToolbarButton(() => Debug.Log(MemoryRecorder.Dump())) { text = "Dump to console" };
            toolbar.Add(_dumpButton);

            toolbar.Add(new VisualElement { style = { flexGrow = 1f } });

            toolbar.Add(new ToolbarButton(() => MemoryManager.OnLowMemory()) { text = "Simulate low memory" });
            toolbar.Add(new ToolbarButton(() => MemoryManager.CollectFull()) { text = "CollectFull" });
            return toolbar;
        }

        private static void ToggleRecording()
        {
            if (MemoryRecorder.IsRecording) MemoryRecorder.Disable();
            else MemoryRecorder.Enable();
        }

        private VisualElement BuildHeapSection()
        {
            VisualElement section = Section("Heap");
            _managedLabel = AddRow(section, "Managed (used / heap)");
            _unityLabel = AddRow(section, "Unity (allocated / reserved)");
            return section;
        }

        /// <summary>
        /// The history pane. Everything else in this window is a snapshot, and a
        /// snapshot cannot show the failures that matter: a scope that outlived the
        /// load which should have killed it, or a pool that quietly stopped pooling.
        /// Those are transitions — clean before, clean after, wrong only in between.
        /// </summary>
        private VisualElement BuildTimelineSection()
        {
            _timelineFoldout = new Foldout { text = "Timeline", value = true };
            _timelineFoldout.style.marginTop = 6f;

            _timelineStatus = new Label { style = { marginLeft = 4f, marginBottom = 4f } };
            _timelineFoldout.Add(_timelineStatus);

            _timelineContent = new VisualElement();
            _timelineFoldout.Add(_timelineContent);

            // Escapes first: it is the number that says whether pooling is working
            // at all, and it is meaningless without a time axis.
            _escapesLabel = new Label("Escapes: 0") { style = { unityFontStyleAndWeight = FontStyle.Bold } };
            _timelineContent.Add(_escapesLabel);
            _escapeChart = new TimelineChart(38f);
            _timelineContent.Add(_escapeChart);

            _timelineContent.Add(new Label("Managed heap") { style = { marginTop = 4f } });
            _heapChart = new TimelineChart(46f);
            _timelineContent.Add(_heapChart);

            _timelineContent.Add(new Label("Pools — the peak line is the warm-up count")
            {
                style = { marginTop = 6f, unityFontStyleAndWeight = FontStyle.Bold }
            });
            _poolRowContainer = new VisualElement();
            _timelineContent.Add(_poolRowContainer);

            _timelineContent.Add(new Label("Events")
            {
                style = { marginTop = 6f, unityFontStyleAndWeight = FontStyle.Bold }
            });
            _eventList = new VisualElement();
            _timelineContent.Add(_eventList);

            return _timelineFoldout;
        }

        private VisualElement BuildFrameScratchSection()
        {
            VisualElement section = Section("Frame scratch (global, reset each frame)");
            _frameScratchContainer = new VisualElement();
            section.Add(_frameScratchContainer);
            return section;
        }

        private VisualElement BuildScopeSection()
        {
            VisualElement section = Section("Scopes");
            _scopeContainer = new VisualElement();
            section.Add(_scopeContainer);
            return section;
        }

        private void Refresh()
        {
            bool playing = Application.isPlaying;
            _playModeHint.style.display = playing ? DisplayStyle.None : DisplayStyle.Flex;
            _body.style.display = playing ? DisplayStyle.Flex : DisplayStyle.None;

            bool recording = MemoryRecorder.IsRecording;
            _recordButton.text = recording ? "Stop recording" : "Record";
            _clearButton.SetEnabled(recording);
            _dumpButton.SetEnabled(recording);

            if (!playing) return;
            RefreshLiveData();
        }

        /// <summary>
        /// The refresh proper, split from the play-mode gate in <see cref="Refresh"/>
        /// so it can be driven directly by tests, which run outside play mode.
        /// </summary>
        private void RefreshLiveData()
        {
            _managedLabel.text =
                $"{Bytes(Profiler.GetMonoUsedSizeLong())} / {Bytes(Profiler.GetMonoHeapSizeLong())}";
            _unityLabel.text =
                $"{Bytes(Profiler.GetTotalAllocatedMemoryLong())} / {Bytes(Profiler.GetTotalReservedMemoryLong())}";

            RefreshTimeline();
            RefreshFrameScratch();
            RefreshScopes();
        }

        private void RefreshTimeline()
        {
            if (!_timelineFoldout.value) return;

            MemoryRing<GlobalSample> samples = MemoryRecorder.GlobalSamples;
            bool hasData = samples != null && samples.Count >= 2;

            _timelineContent.style.display = hasData ? DisplayStyle.Flex : DisplayStyle.None;
            _timelineStatus.style.display = hasData ? DisplayStyle.None : DisplayStyle.Flex;
            if (!hasData)
            {
                _timelineStatus.text = MemoryRecorder.IsRecording
                    ? "Collecting…"
                    : "Press Record to capture pool and scope activity over time.";
                return;
            }

            int count = samples.Count;

            int escapes = 0;
            float escapeMax = 1f;
            for (int i = 0; i < count; i++)
            {
                escapes += samples[i].EscapeDelta;
                escapeMax = Mathf.Max(escapeMax, samples[i].EscapeDelta);
            }

            _escapesLabel.text = escapes > 0
                ? $"Escapes (destroyed, not pooled): {escapes}"
                : "Escapes: 0";
            _escapesLabel.style.color = escapes > 0 ? new Color(1f, 0.45f, 0.4f) : Color.gray;

            _escapeChart.LineColor = escapes > 0 ? new Color(1f, 0.4f, 0.35f) : new Color(0.5f, 0.5f, 0.5f);
            _escapeChart.FillColor = new Color(_escapeChart.LineColor.r, _escapeChart.LineColor.g,
                _escapeChart.LineColor.b, 0.18f);
            _escapeChart.SetData(count, i => samples[i].EscapeDelta, escapeMax);

            float heapMax = 1f;
            for (int i = 0; i < count; i++)
                heapMax = Mathf.Max(heapMax, samples[i].ManagedUsedBytes);
            _heapChart.SetData(count, i => samples[i].ManagedUsedBytes, heapMax);

            RefreshPoolRows();
            RefreshEvents();
        }

        private void RefreshPoolRows()
        {
            IReadOnlyList<PoolSeries> series = MemoryRecorder.PoolSeriesList;
            for (int i = 0; i < series.Count; i++)
            {
                PoolSeries pool = series[i];
                if (pool.Samples.Count < 2) continue;

                string key = pool.ScopeName + "/" + pool.PrefabName;
                if (!_poolRows.TryGetValue(key, out PoolRow row))
                {
                    row = new PoolRow(key);
                    _poolRows.Add(key, row);
                    _poolRowContainer.Add(row.Root);
                }

                row.Update(pool);
            }
        }

        private void RefreshEvents()
        {
            MemoryRing<MemoryEvent> events = MemoryRecorder.Events;
            if (events == null) return;

            const int maxShown = 12;
            int first = Mathf.Max(0, events.Count - maxShown);
            int shown = events.Count - first;

            // Labels are pooled rather than rebuilt: this runs four times a second
            // for as long as the window is open.
            while (_eventList.childCount < shown)
                _eventList.Add(new Label { style = { fontSize = 11f } });

            for (int i = 0; i < _eventList.childCount; i++)
            {
                var label = (Label)_eventList[i];
                if (i >= shown)
                {
                    label.style.display = DisplayStyle.None;
                    continue;
                }

                MemoryEvent e = events[first + i];
                label.style.display = DisplayStyle.Flex;
                string suffix = e.Value != 0 ? $"  ({e.Value})" : string.Empty;
                label.text = $"t+{e.Time - MemoryRecorder.StartTime:0.0}s   {e.Kind}   {e.Label}{suffix}";
                label.style.color = EventColor(e.Kind);
            }
        }

        private static Color EventColor(MemoryEventKind kind) => kind switch
        {
            MemoryEventKind.ScopeDisposed => new Color(1f, 0.75f, 0.4f),
            MemoryEventKind.LowMemory => new Color(1f, 0.45f, 0.4f),
            MemoryEventKind.PoolCreated => new Color(0.75f, 0.75f, 0.75f),
            _ => new Color(0.6f, 0.6f, 0.6f),
        };

        private void RefreshFrameScratch()
        {
            FrameAllocator arena = MemoryManager.FrameScratchOrNull;
            if (arena == null)
            {
                // Deliberately not touching MemoryManager.FrameScratch: reading it
                // creates the arena, and an inspector must not allocate a megabyte
                // of native memory into a game that never used the feature.
                SetSingleLabel(_frameScratchContainer, "(not in use)");
                return;
            }

            SetSingleLabel(_frameScratchContainer, ArenaText(arena));
        }

        private void RefreshScopes()
        {
            IReadOnlyList<MemoryScope> scopes = MemoryManager.LiveScopes;

            // Scopes come and go; rebuild only when the set actually changed, so
            // foldout state survives an ordinary refresh.
            if (_scopeContainer.childCount != scopes.Count)
                RebuildScopes(scopes);
            else
            {
                for (int i = 0; i < scopes.Count; i++)
                {
                    if (((ScopeView)_scopeContainer[i].userData).Scope == scopes[i]) continue;
                    RebuildScopes(scopes);
                    break;
                }
            }

            for (int i = 0; i < _scopeContainer.childCount; i++)
                ((ScopeView)_scopeContainer[i].userData).Update(_poolStats);
        }

        private void RebuildScopes(IReadOnlyList<MemoryScope> scopes)
        {
            _scopeContainer.Clear();
            for (int i = 0; i < scopes.Count; i++)
            {
                var view = new ScopeView(scopes[i]);
                _scopeContainer.Add(view.Root);
            }
        }

        // ---- small helpers ----------------------------------------------------------

        private static VisualElement Section(string title)
        {
            var section = new VisualElement { style = { marginTop = 6f } };
            section.Add(new Label(title) { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            return section;
        }

        private static Label AddRow(VisualElement parent, string label)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            row.Add(new Label(label) { style = { width = 190f } });
            var value = new Label();
            row.Add(value);
            parent.Add(row);
            return value;
        }

        private static void SetSingleLabel(VisualElement container, string text)
        {
            if (container.childCount == 0) container.Add(new Label());
            ((Label)container[0]).text = text;
        }

        internal static string ArenaText(FrameAllocator arena)
            => $"{Bytes(arena.UsedBytes)} / {Bytes(arena.CapacityBytes)}  (peak {Bytes(arena.PeakUsedBytes)})";

        internal static string Bytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:0.0} KB";
            return $"{bytes} B";
        }

        /// <summary>One pool's row in the timeline: label, sparkline, peak.</summary>
        private sealed class PoolRow
        {
            internal readonly VisualElement Root;
            private readonly Label _name;
            private readonly TimelineChart _chart;
            private readonly Label _peak;

            internal PoolRow(string key)
            {
                Root = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 1f }
                };

                _name = new Label(key) { style = { width = 210f, fontSize = 11f, overflow = Overflow.Hidden } };
                Root.Add(_name);

                _chart = new TimelineChart(18f)
                {
                    ShowPeakLine = true,
                    LineColor = new Color(0.45f, 0.9f, 0.55f),
                    FillColor = new Color(0.45f, 0.9f, 0.55f, 0.2f),
                };
                Root.Add(_chart);

                _peak = new Label { style = { width = 74f, fontSize = 11f, marginLeft = 4f } };
                Root.Add(_peak);
            }

            internal void Update(PoolSeries pool)
            {
                bool alive = pool.Alive;
                _name.text = alive ? pool.Name : pool.Name + "  (gone)";
                _name.style.color = alive ? Color.white : new Color(0.55f, 0.55f, 0.55f);
                _peak.text = $"peak {pool.PeakActive}";

                MemoryRing<PoolSample> samples = pool.Samples;
                int count = samples.Count;

                // NaN for a dead sample: the chart renders a gap, so "this pool went
                // away" cannot be mistaken for "this pool is idle".
                _chart.SetData(count, i => samples[i].Alive ? samples[i].Active : float.NaN,
                    Mathf.Max(1, pool.PeakActive));
            }
        }

        /// <summary>One scope's foldout, with its pools, arenas, pinned assets and actions.</summary>
        private sealed class ScopeView
        {
            internal readonly VisualElement Root;
            internal readonly MemoryScope Scope;

            private readonly Foldout _foldout;
            private readonly VisualElement _contents;

            internal ScopeView(MemoryScope scope)
            {
                Scope = scope;

                Root = new VisualElement { userData = this };

                var header = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center }
                };

                // Not flexGrow: a stretched foldout strands its own buttons against
                // the far edge of a wide window, where they read as belonging to
                // nothing. Keep them beside the name they act on.
                _foldout = new Foldout { text = scope.Name, value = true, style = { minWidth = 150f } };
                header.Add(_foldout);

                var trim = new Button(() => scope.Trim(MemoryManager.LowMemoryKeepPerPool))
                {
                    text = "Trim", style = { width = 54f }
                };
                header.Add(trim);

                var dispose = new Button(() => scope.Dispose()) { text = "Dispose", style = { width = 68f } };
                dispose.SetEnabled(scope != MemoryManager.Permanent);
                header.Add(dispose);

                Root.Add(header);

                _contents = new VisualElement { style = { marginLeft = 14f } };
                _foldout.Add(_contents);
            }

            internal void Update(List<MemoryManager.PoolStat> statBuffer)
            {
                if (!_foldout.value) return;
                if (Scope.IsDisposed)
                {
                    SetOnlyLine("(disposed)");
                    return;
                }

                statBuffer.Clear();
                Scope.CollectStats(statBuffer);

                int lineCount = statBuffer.Count + Scope.Allocators.Count + Scope.PinnedAssets.Count +
                                (Scope.OwnedDisposableCount > 0 ? 1 : 0);
                if (lineCount == 0)
                {
                    SetOnlyLine("(empty)");
                    return;
                }

                while (_contents.childCount < lineCount)
                    _contents.Add(new Label { style = { fontSize = 11f } });

                int line = 0;
                for (int i = 0; i < statBuffer.Count; i++)
                {
                    MemoryManager.PoolStat stat = statBuffer[i];

                    // A pool with no Warmup was sized by whichever call site spawned
                    // first and paid an Instantiate during gameplay to exist at all.
                    // Marking it here is the only place that fact is visible.
                    string suffix = stat.WasWarmedUp ? string.Empty : "  (not warmed)";
                    SetLine(line++, $"{stat.PrefabName}{suffix}   " +
                                    $"{stat.CountActive} / {stat.CountInactive} / {stat.CountAll}",
                        stat.WasWarmedUp ? Color.white : new Color(1f, 0.8f, 0.45f));
                }

                for (int i = 0; i < Scope.Allocators.Count; i++)
                    SetLine(line++, "arena  " + ArenaText(Scope.Allocators[i]), Color.white);

                for (int i = 0; i < Scope.PinnedAssets.Count; i++)
                {
                    Object asset = Scope.PinnedAssets[i];
                    SetLine(line++, asset == null
                            ? "pinned  (destroyed)"
                            : $"pinned  {asset.name}  {asset.GetType().Name}, " +
                              $"{Bytes(Profiler.GetRuntimeMemorySizeLong(asset))}",
                        Color.white);
                }

                if (Scope.OwnedDisposableCount > 0)
                    SetLine(line++, $"owned disposables  {Scope.OwnedDisposableCount}", Color.white);

                for (int i = line; i < _contents.childCount; i++)
                    _contents[i].style.display = DisplayStyle.None;
            }

            /// <summary>
            /// Shows one line and hides the rest. Without the hiding, a scope that
            /// emptied out would keep displaying the pools it used to own — which is
            /// precisely the wrong thing for a leak-hunting window to do.
            /// </summary>
            private void SetOnlyLine(string text)
            {
                if (_contents.childCount == 0)
                    _contents.Add(new Label { style = { fontSize = 11f } });

                SetLine(0, text, new Color(0.6f, 0.6f, 0.6f));
                for (int i = 1; i < _contents.childCount; i++)
                    _contents[i].style.display = DisplayStyle.None;
            }

            private void SetLine(int index, string text, Color color)
            {
                var label = (Label)_contents[index];
                label.style.display = DisplayStyle.Flex;
                label.text = text;
                label.style.color = color;
            }
        }
    }
}
