#if MEMORYTOOLKIT_UGUI
using System.Collections.Generic;
using MemoryToolkit.Editor;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// The framework-noise regression from the Melon dogfood. Needs a real UGUI type
    /// to exercise, so it is isolated in a UGUI-gated assembly — the main test
    /// assembly must compile in a project without com.unity.ugui.
    ///
    /// <para>On a UI-heavy project the OnDestroy check fired on every Image, Button,
    /// LayoutGroup and TextMeshProUGUI — ~65% of the validator's output — because those
    /// framework types declare the message for their own bookkeeping. That is cleanup
    /// no team can move, and burying real findings under it is how a validator gets
    /// switched off.</para>
    /// </summary>
    public class FrameworkComponentValidatorTests
    {
        private GameObject _prefab;
        private readonly List<PoolSafetyValidator.Issue> _issues = new();

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("UGUIPrefab");
            _issues.Clear();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_prefab);

        private bool HasOnDestroyWarning()
        {
            foreach (PoolSafetyValidator.Issue issue in _issues)
            {
                if (issue.Severity == PoolSafetyValidator.Severity.Warning &&
                    issue.Message.Contains("declares OnDestroy"))
                    return true;
            }

            return false;
        }

        [Test]
        public void OnDestroy_DeclaredByAFrameworkComponent_IsNotFlagged()
        {
            _prefab.AddComponent<UnityEngine.UI.Image>();

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasOnDestroyWarning(), Is.False,
                "a UnityEngine.* component's OnDestroy is framework bookkeeping, not a team's cleanup");
        }

        [Test]
        public void OnDestroy_OnAUserTypeBesideAFrameworkComponent_IsStillFlagged()
        {
            // The filter must not go too far: a project script's OnDestroy on the same
            // prefab as framework components is still the hazard the check exists for.
            _prefab.AddComponent<UnityEngine.UI.Image>();
            _prefab.AddComponent<CleansUpBesideImage>();

            PoolSafetyValidator.Validate(_prefab, _issues);

            Assert.That(HasOnDestroyWarning(), Is.True);
        }

        private sealed class CleansUpBesideImage : MonoBehaviour
        {
            private void OnDestroy() { }
        }
    }
}
#endif
