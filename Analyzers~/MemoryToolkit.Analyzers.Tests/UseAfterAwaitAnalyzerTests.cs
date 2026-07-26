using System.Threading.Tasks;
using Xunit;

namespace MemoryToolkit.Analyzers.Tests
{
    /// <summary>
    /// MTK007. Disabled by default, so these tests run it explicitly — a rule nobody
    /// enables still has to be correct for the teams that do.
    /// </summary>
    public class UseAfterAwaitAnalyzerTests
    {
        private static Task<string[]> Run(string source)
            => AnalyzerHarness.IdsAsync(new UseAfterAwaitAnalyzer(), source);

        [Fact]
        public async Task FieldUsedAfterAwait_IsReported()
        {
            string[] ids = await Run(@"
using System.Threading.Tasks;
using UnityEngine;
class Loader : MonoBehaviour
{
    GameObject _piece;
    async Task Load()
    {
        await Task.Delay(1);
        Debug.Log(_piece.name);
    }
}");

            Assert.Equal(new[] { "MTK007" }, ids);
        }

        [Fact]
        public async Task ParameterUsedAfterAwait_IsReported()
        {
            string[] ids = await Run(@"
using System.Threading.Tasks;
using UnityEngine;
class Loader : MonoBehaviour
{
    async Task Equip(GameObject piece)
    {
        await Task.Delay(1);
        Debug.Log(piece.name);
    }
}");

            Assert.Equal(new[] { "MTK007" }, ids);
        }

        [Fact]
        public async Task EachSymbolIsReportedOnce()
        {
            string[] ids = await Run(@"
using System.Threading.Tasks;
using UnityEngine;
class Loader : MonoBehaviour
{
    GameObject _piece;
    async Task Load()
    {
        await Task.Delay(1);
        Debug.Log(_piece.name);
        Debug.Log(_piece.name);
    }
}");

            Assert.Equal(new[] { "MTK007" }, ids);
        }

        // ---- Negative cases -----------------------------------------------------------

        [Fact]
        public async Task ARecheckAfterTheAwait_SuppressesTheReport()
        {
            string[] ids = await Run(@"
using System.Threading.Tasks;
using UnityEngine;
class Loader : MonoBehaviour
{
    GameObject _piece;
    async Task Load()
    {
        await Task.Delay(1);
        if (_piece == null) return;
        Debug.Log(_piece.name);
    }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task UseBeforeTheAwait_IsNotReported()
        {
            string[] ids = await Run(@"
using System.Threading.Tasks;
using UnityEngine;
class Loader : MonoBehaviour
{
    GameObject _piece;
    async Task Load()
    {
        Debug.Log(_piece.name);
        await Task.Delay(1);
    }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task NonUnityFields_AreNotReported()
        {
            string[] ids = await Run(@"
using System.Threading.Tasks;
using UnityEngine;
class Config { public string Name; }
class Loader : MonoBehaviour
{
    Config _config;
    async Task Load()
    {
        await Task.Delay(1);
        Debug.Log(_config.Name);
    }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task LocalsAreNotReported()
        {
            // A local declared before the await is usually a temporary the method
            // itself owns; including them roughly doubles the findings without
            // adding a real one.
            string[] ids = await Run(@"
using System.Threading.Tasks;
using UnityEngine;
class Loader : MonoBehaviour
{
    GameObject Make() => null;
    async Task Load()
    {
        GameObject local = Make();
        await Task.Delay(1);
        Debug.Log(local.name);
    }
}");

            Assert.Empty(ids);
        }
    }
}
