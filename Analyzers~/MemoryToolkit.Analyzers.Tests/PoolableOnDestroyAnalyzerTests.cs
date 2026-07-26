using System.Threading.Tasks;
using Xunit;

namespace MemoryToolkit.Analyzers.Tests
{
    /// <summary>
    /// MTK008. The load-bearing test is the negative one: an OnDestroy on a type that
    /// is not IPoolable must stay silent, because that is the overwhelming majority of
    /// OnDestroy methods and flagging them is what would get the rule switched off.
    /// </summary>
    public class PoolableOnDestroyAnalyzerTests
    {
        private static Task<string[]> Run(string source)
            => AnalyzerHarness.IdsAsync(new PoolableOnDestroyAnalyzer(), source);

        [Fact]
        public async Task IPoolableType_WithOnDestroyCleanup_IsReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
using MemoryToolkit.Pooling;
class Piece : MonoBehaviour, IPoolable
{
    public void OnTakenFromPool() { }
    public void OnReturnedToPool() { }
    void OnDestroy() { Debug.Log(""cleanup""); }
}");

            Assert.Equal(new[] { "MTK008" }, ids);
        }

        [Fact]
        public async Task NonPoolableType_WithOnDestroy_IsNotReported()
        {
            // The gate. Ordinary MonoBehaviours have legitimate OnDestroy methods; the
            // rule must not touch a project that has not adopted pooling.
            string[] ids = await Run(@"
using UnityEngine;
class Enemy : MonoBehaviour
{
    void OnDestroy() { Debug.Log(""cleanup""); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task IPoolableType_WithEmptyOnDestroy_IsNotReported()
        {
            // Nothing to lose, so nothing to warn about.
            string[] ids = await Run(@"
using UnityEngine;
using MemoryToolkit.Pooling;
class Piece : MonoBehaviour, IPoolable
{
    public void OnTakenFromPool() { }
    public void OnReturnedToPool() { }
    void OnDestroy() { }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task IPoolableType_WithNoOnDestroy_IsNotReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
using MemoryToolkit.Pooling;
class Piece : MonoBehaviour, IPoolable
{
    public void OnTakenFromPool() { }
    public void OnReturnedToPool() { }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task IPoolableInheritedFromABase_IsStillGated()
        {
            // The interface may be implemented on a base class; AllInterfaces sees it.
            string[] ids = await Run(@"
using UnityEngine;
using MemoryToolkit.Pooling;
abstract class PooledBase : MonoBehaviour, IPoolable
{
    public void OnTakenFromPool() { }
    public void OnReturnedToPool() { }
}
class Piece : PooledBase
{
    void OnDestroy() { Debug.Log(""cleanup""); }
}");

            Assert.Equal(new[] { "MTK008" }, ids);
        }
    }
}
