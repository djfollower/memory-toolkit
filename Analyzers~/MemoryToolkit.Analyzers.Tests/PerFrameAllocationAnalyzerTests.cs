using System.Threading.Tasks;
using Xunit;

namespace MemoryToolkit.Analyzers.Tests
{
    /// <summary>
    /// MTK002. The negative cases are the important half: this rule is scoped
    /// narrowly on purpose, and the scoping is the thing that can regress.
    /// </summary>
    public class PerFrameAllocationAnalyzerTests
    {
        private static Task<string[]> Run(string source)
            => AnalyzerHarness.IdsAsync(new PerFrameAllocationAnalyzer(), source);

        [Fact]
        public async Task NewList_InUpdate_IsReported()
        {
            string[] ids = await Run(@"
using System.Collections.Generic;
using UnityEngine;
class Spawner : MonoBehaviour
{
    void Update() { var live = new List<int>(); }
}");

            Assert.Equal(new[] { "MTK002" }, ids);
        }

        [Fact]
        public async Task ArrayAllocation_InFixedUpdate_IsReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Physics : MonoBehaviour
{
    void FixedUpdate() { var hits = new int[8]; }
}");

            Assert.Equal(new[] { "MTK002" }, ids);
        }

        [Fact]
        public async Task StringInterpolation_InUpdate_IsReported()
        {
            // The timer label from ADOPTION.md §3, rebuilt every frame.
            string[] ids = await Run(@"
using UnityEngine;
class Hud : MonoBehaviour
{
    int _minute, _second;
    void Update() { Debug.Log($""{_minute}:{_second:00}""); }
}");

            Assert.Equal(new[] { "MTK002" }, ids);
        }

        [Fact]
        public async Task StringConcatenation_IsReportedOnce_ForAChain()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Hud : MonoBehaviour
{
    string _a, _b, _c;
    void Update() { Debug.Log(_a + _b + _c); }
}");

            Assert.Equal(new[] { "MTK002" }, ids);
        }

        [Fact]
        public async Task Linq_InLateUpdate_IsReported()
        {
            string[] ids = await Run(@"
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
class Ai : MonoBehaviour
{
    List<int> _values = new List<int>();
    void LateUpdate() { var first = _values.FirstOrDefault(v => v > 2); }
}");

            Assert.Equal(new[] { "MTK002" }, ids);
        }

        [Fact]
        public async Task NewWaitForSeconds_IsReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Dropper : MonoBehaviour
{
    void Update() { var wait = new WaitForSeconds(0.5f); }
}");

            Assert.Equal(new[] { "MTK002" }, ids);
        }

        // ---- Negative cases -----------------------------------------------------------

        [Fact]
        public async Task StructConstruction_IsNotReported()
        {
            // `new Vector3(...)` in Update is normal and correct. Flagging it would
            // discredit the whole rule on first contact with a real project.
            string[] ids = await Run(@"
using UnityEngine;
class Mover : MonoBehaviour
{
    void Update() { var offset = new Vector3(1f, 2f, 3f); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task AllocationOutsideAPerFrameMethod_IsNotReported()
        {
            string[] ids = await Run(@"
using System.Collections.Generic;
using UnityEngine;
class Spawner : MonoBehaviour
{
    List<int> _live;
    void Awake() { _live = new List<int>(); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task UpdateOnANonMonoBehaviour_IsNotReported()
        {
            // Services, view models and state machines all have a method called
            // Update, and none of them run every frame.
            string[] ids = await Run(@"
using System.Collections.Generic;
class ScoreService
{
    public void Update() { var buffer = new List<int>(); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task ConstantStringConcatenation_IsNotReported()
        {
            // Folded by the compiler; there is no allocation to report.
            string[] ids = await Run(@"
using UnityEngine;
class Hud : MonoBehaviour
{
    void Update() { Debug.Log(""a"" + ""b""); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task CachedInstanceReuse_IsNotReported()
        {
            string[] ids = await Run(@"
using System.Collections.Generic;
using UnityEngine;
class Spawner : MonoBehaviour
{
    readonly List<int> _live = new List<int>();
    void Update() { _live.Clear(); }
}");

            Assert.Empty(ids);
        }
    }
}
