using System.IO;
using MemoryToolkit.Editor;
using NUnit.Framework;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers the triage engine over a synthetic source tree. The real acceptance
    /// measure is the run against the two reference codebases the field guides are
    /// built on (see TriageDump); these lock the mechanics so that run stays
    /// meaningful.
    /// </summary>
    public class ProjectTriageTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "mtk-triage-" + System.Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private void WriteCs(string name, string body)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, name), body);
        }

        [Test]
        public void GreenfieldProject_RecommendsAdopt()
        {
            WriteCs("Spawner.cs", "class Spawner { void Fire() { Instantiate(prefab); Destroy(old); } }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.RecommendedGuide, Is.EqualTo("ADOPT"));
            Assert.That(r.HasIncumbentPool, Is.False);
            Assert.That(r.InstantiateCalls, Is.EqualTo(1));
            Assert.That(r.DestroyCalls, Is.EqualTo(1));
        }

        [Test]
        public void AnIncumbentPool_FlipsTheRecommendationToIntegrate()
        {
            // The extension-method idiom a hand-rolled pool is reached through — the
            // signal that churn greps will now understate the real churn.
            WriteCs("PoolExtensions.cs",
                "static class PoolExtensions { public static GameObject GetFromPool(this GameObject p) => null; }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.RecommendedGuide, Is.EqualTo("INTEGRATE"));
            Assert.That(r.HasIncumbentPool, Is.True);
            Assert.That(r.IncumbentPoolEvidence, Is.Not.Empty);
        }

        [Test]
        public void APoolClassAlsoCountsAsAnIncumbent()
        {
            WriteCs("MyPool.cs", "class ProjectilePool { }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.HasIncumbentPool, Is.True);
        }

        [Test]
        public void TheBootEntryPoint_IsFoundByName()
        {
            WriteCs("AppLoader.cs", "class AppLoader { void Awake() { } }");
            WriteCs("Other.cs", "class Other { }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.BootCandidates, Has.Some.Matches<ProjectTriage.Evidence>(e => e.Text == "AppLoader"));
        }

        [Test]
        public void OnlyASubstantialOnDestroy_IsASessionBoundary()
        {
            // A trivial OnDestroy is not the hand-maintained teardown the guide points
            // at; a fat one is. The cheap size proxy has to tell them apart.
            WriteCs("Trivial.cs", "class Trivial { void OnDestroy() { x = null; } }");
            WriteCs("Manager.cs",
                "class Manager { void OnDestroy() { a.Dispose(); b.Dispose(); c.Dispose(); d.Dispose(); } }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.SessionBoundaries, Has.Some.Matches<ProjectTriage.Evidence>(
                e => e.Path.EndsWith("Manager.cs")));
            Assert.That(r.SessionBoundaries, Has.None.Matches<ProjectTriage.Evidence>(
                e => e.Path.EndsWith("Trivial.cs")));
        }

        [Test]
        public void PerFrameMethods_AreCounted()
        {
            WriteCs("A.cs", "class A { void Update() { } void FixedUpdate() { } }");
            WriteCs("B.cs", "class B { void Update() { } void LateUpdate() { } }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.UpdateMethods, Is.EqualTo(2));
            Assert.That(r.LateUpdateMethods, Is.EqualTo(1));
            Assert.That(r.FixedUpdateMethods, Is.EqualTo(1));
        }

        [Test]
        public void TheHottestChurnFile_IsTheOneWithTheMostInstantiateAndDestroy()
        {
            WriteCs("Quiet.cs", "class Quiet { void F() { Instantiate(a); } }");
            WriteCs("Loop.cs",
                "class Loop { void F() { Instantiate(a); Instantiate(b); Destroy(c); Destroy(d); } }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.HottestChurnFile.HasValue, Is.True);
            Assert.That(r.HottestChurnFile.Value.Path, Does.EndWith("Loop.cs"));
        }

        [Test]
        public void GeneratedAndPluginTrees_AreExcluded()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "Plugins"));
            File.WriteAllText(Path.Combine(_dir, "Plugins", "Vendor.cs"),
                "class Vendor { void F() { Instantiate(a); Instantiate(b); Instantiate(c); } }");
            WriteCs("Game.cs", "class Game { void F() { Instantiate(a); } }");

            ProjectTriage.Result r = ProjectTriage.Run(_dir);

            Assert.That(r.InstantiateCalls, Is.EqualTo(1), "third-party churn must not drown out the game's own");
        }

        [Test]
        public void AMissingDirectory_ThrowsRatherThanReportingEmpty()
        {
            Assert.Throws<System.InvalidOperationException>(
                () => ProjectTriage.Run(Path.Combine(_dir, "nope")));
        }
    }
}
