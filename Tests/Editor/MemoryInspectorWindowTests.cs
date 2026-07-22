using System.Reflection;
using MemoryToolkit.Diagnostics;
using MemoryToolkit.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Smoke coverage for the window's UI Toolkit tree. A retained-mode UI fails
    /// differently from IMGUI: instead of a bad frame it throws once during
    /// construction or refresh and leaves an empty window, which no test of the
    /// recorder's data would notice.
    /// </summary>
    public class MemoryInspectorWindowTests
    {
        private MemoryInspectorWindow _window;
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("WindowTestPrefab");
            _window = ScriptableObject.CreateInstance<MemoryInspectorWindow>();
            MemoryRecorder.Enable(sampleCapacity: 16, eventCapacity: 16);
            MemoryRecorder.SampleIntervalSeconds = 0d;
        }

        [TearDown]
        public void TearDown()
        {
            MemoryRecorder.Disable();
            MemoryRecorder.SampleIntervalSeconds = 0.25d;
            MemoryManager.Shutdown();
            Object.DestroyImmediate(_window);
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void CreateGUI_BuildsToolbarAndSections()
        {
            Invoke("CreateGUI");

            VisualElement root = _window.rootVisualElement;
            Assert.That(root.childCount, Is.GreaterThan(0), "window built no UI");
            Assert.That(root.Q<Foldout>(), Is.Not.Null, "timeline foldout missing");
            Assert.That(root.Query<TimelineChart>().ToList().Count, Is.GreaterThanOrEqualTo(2),
                "escape and heap charts should both exist");
        }

        [Test]
        public void Refresh_WithLiveData_DoesNotThrow_AndAddsPoolRows()
        {
            Invoke("CreateGUI");

            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Warmup(_prefab, 3);
            GameObject taken = scope.GetPool(_prefab).Get();
            MemoryRecorder.Tick();
            MemoryRecorder.Tick();

            Assert.DoesNotThrow(() => Invoke("RefreshLiveData"));

            // Two global charts plus one sparkline per pool row.
            Assert.That(_window.rootVisualElement.Query<TimelineChart>().ToList().Count,
                Is.GreaterThanOrEqualTo(3), "the pool should have gained a sparkline row");

            scope.GetPool(_prefab).Release(taken);
        }

        [Test]
        public void Refresh_AfterScopeDisposed_DoesNotThrow()
        {
            Invoke("CreateGUI");

            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Warmup(_prefab, 2);
            MemoryRecorder.Tick();
            Invoke("RefreshLiveData");

            // The scope list shrinking under the view is the case most likely to
            // throw: rows were built against objects that no longer exist.
            scope.Dispose();
            MemoryRecorder.Tick();

            Assert.DoesNotThrow(() => Invoke("RefreshLiveData"));
        }

        private void Invoke(string method)
        {
            MethodInfo info = typeof(MemoryInspectorWindow)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, $"{method} not found");
            info.Invoke(_window, null);
        }
    }
}
