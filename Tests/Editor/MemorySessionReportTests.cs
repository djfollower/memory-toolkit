using System.Collections.Generic;
using MemoryToolkit.Budgets;
using MemoryToolkit.Diagnostics;
using MemoryToolkit.Migration;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers the runtime session report — the schema the CI gate and the on-device
    /// soak both write, so it has one definition and one test.
    /// </summary>
    public class MemorySessionReportTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("ReportPrefab");
            PoolBridge.ResetDiagnostics();
            DeviceTier.Override(MemoryBudgetTier.High);
        }

        [TearDown]
        public void TearDown()
        {
            DeviceTier.Clear();
            PoolBridge.ResetDiagnostics();
            MemoryManager.Permanent.Dispose();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void BuildJson_CarriesTheSchemaVersionAndSessionKind()
        {
            string json = MemorySessionReport.BuildJson(null, null, out _);

            Assert.That(json, Does.Contain("\"schemaVersion\":1"));
            Assert.That(json, Does.Contain("\"kind\":\"session\""));
        }

        [Test]
        public void WithNoEscapesAndNoCeiling_ThePassVerdictIsTrue()
        {
            MemorySessionReport.BuildJson(null, null, out MemorySessionReport.Result result);

            Assert.That(result.Passed, Is.True);
            Assert.That(result.Escapes, Is.Zero);
            Assert.That(result.ManagedCeilingBytes, Is.Zero);
        }

        [Test]
        public void AnEscape_FailsTheVerdict_BecauseSomethingIsBeingDestroyedNotPooled()
        {
            var foreign = new GameObject("NotOurs");
            PoolBridge.UnknownInstances = UnknownInstancePolicy.Ignore;
            PoolBridge.Return(foreign); // owned by no pool → an escape

            MemorySessionReport.BuildJson(null, null, out MemorySessionReport.Result result);

            Assert.That(result.Escapes, Is.EqualTo(1));
            Assert.That(result.Passed, Is.False);

            PoolBridge.UnknownInstances = UnknownInstancePolicy.LogAndDestroy;
            Object.DestroyImmediate(foreign);
        }

        [Test]
        public void ATinyCeiling_FailsTheVerdict()
        {
            var budget = ScriptableObject.CreateInstance<MemoryBudget>();
            SetCeiling(budget, oneMb: true);

            MemorySessionReport.BuildJson(budget, MemoryBudgetTier.High, out MemorySessionReport.Result result);

            Assert.That(result.ManagedCeilingBytes, Is.EqualTo(1024 * 1024));
            Assert.That(result.Passed, Is.False, "a 1 MB managed ceiling is always exceeded, so the gate must fail");

            Object.DestroyImmediate(budget);
        }

        [Test]
        public void PoolsAppearInTheReport()
        {
            MemoryManager.Permanent.Warmup(_prefab, 3);

            string json = MemorySessionReport.BuildJson(null, null, out _);

            Assert.That(json, Does.Contain("\"prefab\":\"ReportPrefab\""));
            Assert.That(json, Does.Contain("\"inactive\":3"));
        }

        private static void SetCeiling(MemoryBudget budget, bool oneMb)
        {
            // The ceiling field is serialized-private; a tiny reflection reach keeps
            // the test honest without widening the runtime API for it.
            var field = typeof(MemoryBudget).GetField(
                "_managedHeapCeilingMb",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(budget, new TieredInt(oneMb ? 1 : 0));
        }
    }
}
