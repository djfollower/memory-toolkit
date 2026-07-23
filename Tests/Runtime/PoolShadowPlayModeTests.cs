using System.Collections;
using MemoryToolkit.Migration;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Play-mode coverage of the one shadow-mode behaviour edit mode cannot reach:
    /// Unity's <c>Destroy</c> is deferred to the end of the frame, so a same-frame
    /// double return still finds a readable marker. That window is where double
    /// release is actually detectable — and same-frame is the common shape, two
    /// systems both "cleaning up" the same object. In edit mode
    /// <c>DestroyImmediate</c> closes the window before it opens.
    /// </summary>
    public class PoolShadowPlayModeTests
    {
        [UnityTest]
        public IEnumerator SameFrameDoubleReturn_IsAttributedAndCountedOnce()
        {
            var prefab = new GameObject("ShadowPlayModePrefab");
            PoolBridge.ResetDiagnostics();
            PoolShadow.Reset();
            PoolBridge.Mode = PoolBridgeMode.Observe;

            try
            {
                GameObject instance = PoolBridge.Get(prefab);

                Assert.That(PoolBridge.Return(instance), Is.True);
                Assert.That(PoolBridge.Return(instance), Is.True,
                    "still the same frame, so the marker is readable and the repeat is attributable");

                Assert.That(PoolShadow.DoubleReturnCount, Is.EqualTo(1));
                Assert.That(PoolShadow.Entries[0].Returns, Is.EqualTo(1),
                    "a double return must not double-count the projected saving");
                Assert.That(PoolShadow.Entries[0].Live, Is.Zero,
                    "nor may it drive the live count negative and understate the peak");

                yield return null;
            }
            finally
            {
                PoolBridge.Mode = PoolBridgeMode.Active;
                PoolShadow.Reset();
                PoolBridge.ResetDiagnostics();
                MemoryManager.Shutdown();
                Object.DestroyImmediate(prefab);
            }
        }
    }
}
