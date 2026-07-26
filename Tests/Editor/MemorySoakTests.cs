using System.IO;
using MemoryToolkit.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers the file-writing half of the soak runner via DumpNow and WriteReport —
    /// the interval MonoBehaviour is trivial glue over these, and a soak dump that a
    /// CI reader cannot parse is the failure that matters, so the schema is what is
    /// tested.
    /// </summary>
    public class MemorySoakTests
    {
        private string _dir;
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mtk-soak-tests-" + System.Guid.NewGuid().ToString("N"));
            _prefab = new GameObject("SoakPrefab");
        }

        [TearDown]
        public void TearDown()
        {
            MemoryManager.Permanent.Dispose();
            Object.DestroyImmediate(_prefab);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        [Test]
        public void DumpNow_WritesAParseableSessionReport()
        {
            string path = MemorySoak.DumpNow(_dir);

            Assert.That(path, Is.Not.Null);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(File.ReadAllText(path), Does.Contain("\"kind\":\"session\""));
        }

        [Test]
        public void Rotation_KeepsOnlyTheMostRecentFiles()
        {
            for (int i = 0; i < 5; i++)
            {
                // Distinct, sortable names come from the UTC timestamp; a small gap
                // guarantees the millisecond field differs across iterations.
                MemorySoak.WriteReport(_dir, null, null, rotateKeeping: 3);
                System.Threading.Thread.Sleep(5);
            }

            string[] files = Directory.GetFiles(_dir, "session-*.json");
            Assert.That(files.Length, Is.EqualTo(3), "rotation must cap the file count, not accumulate an overnight run's worth");
        }

        [Test]
        public void WriteReport_OnAnUnwritablePath_ReturnsNullRatherThanThrowing()
        {
            // A soak writer that throws takes down the session it is observing.
            string path = MemorySoak.WriteReport("\0invalid\0", null, null, rotateKeeping: 0);
            Assert.That(path, Is.Null);
        }
    }
}
