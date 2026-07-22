using System.Collections.Generic;
using MemoryToolkit.Editor;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    public class PoolSafetyValidatorTests
    {
        private GameObject _prefab;
        private List<PoolSafetyValidator.Issue> _issues;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("ValidationTarget");
            _issues = new List<PoolSafetyValidator.Issue>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_prefab);

        private bool HasIssue(PoolSafetyValidator.Severity severity, string messageFragment)
        {
            foreach (PoolSafetyValidator.Issue issue in _issues)
            {
                if (issue.Severity == severity && issue.Message.Contains(messageFragment))
                    return true;
            }

            return false;
        }

        [Test]
        public void ParticleSystem_WithStopActionDestroy_IsAnError()
        {
            // The classic one-shot VFX prefab: it deletes its own GameObject when
            // the effect ends, so the pool serves fake-null on the next Get.
            var particles = _prefab.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.stopAction = ParticleSystemStopAction.Destroy;

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Error, "Stop Action is Destroy"), Is.True);
        }

        [Test]
        public void ParticleSystem_WithStopActionDisable_IsNotAnError()
        {
            var particles = _prefab.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.stopAction = ParticleSystemStopAction.Disable;

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Error, "Stop Action"), Is.False);
        }

        [Test]
        public void OnDestroy_WithoutIPoolable_IsAWarning()
        {
            _prefab.AddComponent<CleansUpInOnDestroy>();

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Warning, "declares OnDestroy"), Is.True);
        }

        [Test]
        public void OnDestroy_DeclaredOnBaseClass_IsStillFound()
        {
            _prefab.AddComponent<DerivedFromCleansUp>();

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Warning, "declares OnDestroy"), Is.True);
        }

        [Test]
        public void OnDestroy_WithIPoolable_DownGradesToInfo()
        {
            _prefab.AddComponent<PoolableWithOnDestroy>();

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Warning, "declares OnDestroy"), Is.False);
            Assert.That(HasIssue(PoolSafetyValidator.Severity.Info, "declares OnDestroy"), Is.True);
        }

        [Test]
        public void Rigidbody_WithoutIPoolable_WarnsAboutCarriedOverState()
        {
            _prefab.AddComponent<Rigidbody2D>();

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Warning, "Rigidbody2D"), Is.True);
        }

        [Test]
        public void Rigidbody_WithIPoolable_DoesNotWarn()
        {
            _prefab.AddComponent<Rigidbody2D>();
            _prefab.AddComponent<PoolableWithOnDestroy>();

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Warning, "Rigidbody2D"), Is.False);
        }

        [Test]
        public void ChildIssues_AreReportedWithHierarchyPath()
        {
            var child = new GameObject("Fx");
            child.transform.SetParent(_prefab.transform);
            var particles = child.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = particles.main;
            main.loop = false;
            main.stopAction = ParticleSystemStopAction.Destroy;

            PoolSafetyValidator.Validate(_prefab, _issues);

            bool found = false;
            foreach (PoolSafetyValidator.Issue issue in _issues)
            {
                if (issue.Severity == PoolSafetyValidator.Severity.Error && issue.Path == "ValidationTarget/Fx")
                    found = true;
            }

            Assert.That(found, Is.True, "a child system set to Destroy takes the whole hierarchy with it");
        }

        [Test]
        public void PrefabWithNoPoolable_GetsAnInfoNote()
        {
            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasIssue(PoolSafetyValidator.Severity.Info, "No component implements IPoolable"), Is.True);
        }

        private class CleansUpInOnDestroy : MonoBehaviour
        {
            private void OnDestroy() { }
        }

        private class DerivedFromCleansUp : CleansUpInOnDestroy { }

        private class PoolableWithOnDestroy : MonoBehaviour, IPoolable
        {
            public void OnTakenFromPool() { }
            public void OnReturnedToPool() { }
            private void OnDestroy() { }
        }
    }
}
