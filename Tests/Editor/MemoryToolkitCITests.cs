using System;
using System.Collections.Generic;
using System.IO;
using MemoryToolkit.Budgets;
using MemoryToolkit.Editor;
using MemoryToolkit.Editor.CI;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers the batch-mode gate. The property that matters most is the one a gate
    /// can silently lose: that a broken project actually fails. A validator wired up
    /// backwards reports everything clean and nobody notices for months.
    /// </summary>
    public class MemoryToolkitCITests
    {
        private GameObject _prefab;
        private MemoryBudget _budget;
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("CiPrefab");
            _budget = ScriptableObject.CreateInstance<MemoryBudget>();
            _tempDir = Path.Combine(Path.GetTempPath(), "mtk-ci-tests-" + Guid.NewGuid().ToString("N"));
            DeviceTier.Override(MemoryBudgetTier.High);
        }

        [TearDown]
        public void TearDown()
        {
            DeviceTier.Clear();
            Object.DestroyImmediate(_budget);
            Object.DestroyImmediate(_prefab);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }

        // ---- Budget audit -------------------------------------------------------------

        [Test]
        public void Audit_FlagsAWarmupLargerThanItsMaxSize()
        {
            SetBudget(Scope("Permanent", new PoolBudget { Prefab = _prefab, Warmup = 32, MaxSize = 8 }));

            List<PoolSafetyValidator.Issue> findings = Audit();

            Assert.That(findings, Has.Some.Matches<PoolSafetyValidator.Issue>(
                i => i.Severity == PoolSafetyValidator.Severity.Error && i.Message.Contains("capped at 8")));
        }

        [Test]
        public void Audit_FlagsADirectPrefabReferenceOutsidePermanent_BecauseItPinsTheAsset()
        {
            SetBudget(Scope("Level_01", new PoolBudget { Prefab = _prefab, Warmup = 8, MaxSize = 16 }));

            List<PoolSafetyValidator.Issue> findings = Audit();

            Assert.That(findings, Has.Some.Matches<PoolSafetyValidator.Issue>(
                i => i.Message.Contains("pins the prefab")),
                "a budget that drags every level's content into memory at boot is the footgun this asset has to guard");
        }

        [Test]
        public void Audit_DoesNotFlagADirectReferenceInPermanent_WhichIsResidentAnyway()
        {
            SetBudget(Scope("Permanent", new PoolBudget { Prefab = _prefab, Warmup = 8, MaxSize = 16 }));

            List<PoolSafetyValidator.Issue> findings = Audit();

            Assert.That(findings, Has.None.Matches<PoolSafetyValidator.Issue>(
                i => i.Message.Contains("pins the prefab")));
        }

        [Test]
        public void Audit_FlagsAnEmptyEntry()
        {
            SetBudget(Scope("Permanent", new PoolBudget { Warmup = 8 }));

            Assert.That(Audit(), Has.Some.Matches<PoolSafetyValidator.Issue>(
                i => i.Severity == PoolSafetyValidator.Severity.Error
                     && i.Message.Contains("neither a prefab nor an addressable key")));
        }

        [Test]
        public void Audit_FlagsADuplicateScope_BecauseOnlyTheFirstIsEverApplied()
        {
            SetBudget(
                Scope("Permanent", new PoolBudget { Prefab = _prefab, Warmup = 8, MaxSize = 16 }),
                Scope("Permanent", new PoolBudget { AddressableKey = "Other", Warmup = 4, MaxSize = 8 }));

            Assert.That(Audit(), Has.Some.Matches<PoolSafetyValidator.Issue>(
                i => i.Severity == PoolSafetyValidator.Severity.Error && i.Message.Contains("Duplicate scope")));
        }

        [Test]
        public void Audit_FlagsAnEntryThatDoesNothing()
        {
            SetBudget(Scope("Permanent", new PoolBudget { AddressableKey = "Enemy", MaxSize = 16 }));

            Assert.That(Audit(), Has.Some.Matches<PoolSafetyValidator.Issue>(
                i => i.Message.Contains("No warm-up on any tier")));
        }

        [Test]
        public void Audit_FlagsInvertedTiers()
        {
            SetBudget(Scope("Permanent", new PoolBudget
            {
                AddressableKey = "Enemy",
                Warmup = new TieredInt(high: 4, medium: 0, low: 32),
                MaxSize = 64,
            }));

            Assert.That(Audit(), Has.Some.Matches<PoolSafetyValidator.Issue>(
                i => i.Message.Contains("Tiers are inverted")));
        }

        [Test]
        public void Audit_PassesACoherentBudget()
        {
            SetBudget(Scope("Permanent", new PoolBudget
            {
                Prefab = _prefab,
                Warmup = new TieredInt(high: 32, medium: 16, low: 8),
                MaxSize = 64,
            }));

            Assert.That(Audit(), Is.Empty);
        }

        // ---- The gate -----------------------------------------------------------------

        [Test]
        public void Run_ExitsZeroOnACleanProject_AndWritesBothReports()
        {
            string json = Path.Combine(_tempDir, "report.json");
            string junit = Path.Combine(_tempDir, "report.xml");

            int code = MemoryToolkitCI.Run(Args(json, junit));

            Assert.That(code, Is.Zero);
            Assert.That(File.Exists(json), Is.True);
            Assert.That(File.Exists(junit), Is.True);
            Assert.That(File.ReadAllText(json), Does.Contain("\"schemaVersion\":1"));
            Assert.That(File.ReadAllText(junit), Does.Contain("failures=\"0\""));
        }

        [Test]
        public void Run_CreatesTheOutputDirectory_SoCiDoesNotHaveToMkdirFirst()
        {
            string json = Path.Combine(_tempDir, "nested", "deeper", "report.json");

            MemoryToolkitCI.Run(Args(json, null));

            Assert.That(File.Exists(json), Is.True);
        }

        [Test]
        public void Run_FailsOnAMissingBudget_RatherThanAuditingNothingAndReportingClean()
        {
            MemoryToolkitCI.Arguments args = Args(null, null);
            args.BudgetPath = "Assets/DoesNotExist.asset";

            // The typo-becomes-a-green-build case. Run throws; Validate turns it into
            // exit code 2, which is not the same as "clean".
            Assert.Throws<InvalidOperationException>(() => MemoryToolkitCI.Run(args));
        }

        [Test]
        public void FailOn_Never_ReportsWithoutFailing_ForTheFirstNightlyRun()
        {
            MemoryToolkitCI.Arguments args = Args(null, null);
            args.FailOn = MemoryToolkitCI.FailLevel.Never;

            Assert.That(MemoryToolkitCI.Run(args), Is.Zero);
        }

        [Test]
        public void ProjectScan_ThrowsOnAMissingFolder_RatherThanReportingItClean()
        {
            Assert.Throws<InvalidOperationException>(() => PoolProjectScan.Run(new PoolProjectScan.Options
            {
                Folder = "Assets/NoSuchFolder",
                MinSeverity = PoolSafetyValidator.Severity.Warning,
                MaxPrefabs = 100,
            }));
        }

        [Test]
        public void ProjectScan_SeverityFilterKeepsTheMoreSevere_NotTheLess()
        {
            // Severity is declared most-severe-first, so a filter written with the
            // comparison backwards silently drops every error. Assert the ordering
            // the scan depends on, since nothing else in the suite would notice.
            Assert.That(PoolSafetyValidator.Severity.Error, Is.LessThan(PoolSafetyValidator.Severity.Warning));
            Assert.That(PoolSafetyValidator.Severity.Warning, Is.LessThan(PoolSafetyValidator.Severity.Info));
        }

        // ---- Helpers ------------------------------------------------------------------

        private List<PoolSafetyValidator.Issue> Audit()
        {
            var findings = new List<PoolSafetyValidator.Issue>();
            MemoryBudgetAudit.Audit(_budget, findings);
            return findings;
        }

        private MemoryToolkitCI.Arguments Args(string json, string junit)
        {
            MemoryToolkitCI.Arguments args = MemoryToolkitCI.Arguments.Default;
            args.JsonPath = json;
            args.JunitPath = junit;
            return args;
        }

        private void SetBudget(params ScopeBudget[] scopes) => _budget.ScopesForEditing = scopes;

        private static ScopeBudget Scope(string name, params PoolBudget[] pools)
            => new() { ScopeName = name, Pools = pools };
    }
}
