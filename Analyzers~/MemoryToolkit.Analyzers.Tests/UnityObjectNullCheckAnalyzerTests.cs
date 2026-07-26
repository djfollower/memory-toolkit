using System.Threading.Tasks;
using Xunit;

namespace MemoryToolkit.Analyzers.Tests
{
    /// <summary>
    /// MTK001. Half of these are negative cases on purpose: the rule fires on a
    /// pattern that is correct C# everywhere except on one type family, so the way it
    /// fails is by firing on the rest of the codebase.
    /// </summary>
    public class UnityObjectNullCheckAnalyzerTests
    {
        private static Task<string[]> Run(string source)
            => AnalyzerHarness.IdsAsync(new UnityObjectNullCheckAnalyzer(), source);

        [Fact]
        public async Task NullConditional_OnAUnityObject_IsReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Enemy : MonoBehaviour
{
    public GameObject Target;
    void Kill() { Target?.GetComponent<Transform>(); }
}");

            Assert.Equal(new[] { "MTK001" }, ids);
        }

        [Fact]
        public async Task Coalesce_And_CoalesceAssignment_AreReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Enemy : MonoBehaviour
{
    public GameObject A;
    public GameObject B;
    void Pick()
    {
        GameObject chosen = A ?? B;
        A ??= B;
    }
}");

            Assert.Equal(new[] { "MTK001", "MTK001" }, ids);
        }

        [Fact]
        public async Task IsNull_AndIsNotNull_AreReported()
        {
            // The sneakiest of the four: it reads as a deliberate modern null check
            // rather than as an operator someone reached for out of habit.
            string[] ids = await Run(@"
using UnityEngine;
class Enemy : MonoBehaviour
{
    public GameObject Target;
    void Check()
    {
        if (Target is null) { }
        if (Target is not null) { }
    }
}");

            Assert.Equal(new[] { "MTK001", "MTK001" }, ids);
        }

        [Fact]
        public async Task ComponentSubclasses_AreCovered()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Enemy : MonoBehaviour
{
    public Transform Root;
    void Detach() { Root?.SetParent(null); }
}");

            Assert.Equal(new[] { "MTK001" }, ids);
        }

        // ---- Negative cases -----------------------------------------------------------

        [Fact]
        public async Task ProperNullComparison_IsNotReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Enemy : MonoBehaviour
{
    public GameObject Target;
    void Check() { if (Target != null) Debug.Log(Target.name); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task NullConditional_OnAPlainClass_IsNotReported()
        {
            // The rule has to leave ordinary C# alone. A codebase is mostly this.
            string[] ids = await Run(@"
class Config { public string Name; }
class Loader
{
    Config _config;
    string Read() => _config?.Name;
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task NullConditional_OnAString_IsNotReported()
        {
            string[] ids = await Run(@"
class Loader
{
    string _value;
    int Length() => _value?.Length ?? 0;
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task NullConditional_OnANullableStruct_IsNotReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Loader
{
    Vector3? _point;
    float X() => _point?.x ?? 0f;
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task NullConditional_OnAnEvent_IsNotReported()
        {
            // `Handler?.Invoke()` is the correct idiom and appears in nearly every
            // file. Firing here would end the rule's life immediately.
            string[] ids = await Run(@"
using System;
using UnityEngine;
class Enemy : MonoBehaviour
{
    public event Action Died;
    void Die() { Died?.Invoke(); }
}");

            Assert.Empty(ids);
        }
    }
}
