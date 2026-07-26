using System.Threading.Tasks;
using Xunit;

namespace MemoryToolkit.Analyzers.Tests
{
    /// <summary>
    /// MTK006. The negative cases carry the weight: the rule must fire on the
    /// per-reuse messages and stay silent on Awake/Start, which are the correct place
    /// to add a component and pooling-safe.
    /// </summary>
    public class AddComponentOnReuseAnalyzerTests
    {
        private static Task<string[]> Run(string source)
            => AnalyzerHarness.IdsAsync(new AddComponentOnReuseAnalyzer(), source);

        [Fact]
        public async Task AddComponent_InOnEnable_IsReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Piece : MonoBehaviour
{
    void OnEnable() { gameObject.AddComponent<Rigidbody>(); }
}");

            Assert.Equal(new[] { "MTK006" }, ids);
        }

        [Fact]
        public async Task AddComponent_InUpdate_IsReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Piece : MonoBehaviour
{
    void Update() { gameObject.AddComponent<Rigidbody>(); }
}");

            Assert.Equal(new[] { "MTK006" }, ids);
        }

        [Fact]
        public async Task AddComponent_InAwake_IsNotReported()
        {
            // Awake runs once per instance and the component persists across reuse —
            // exactly the pattern the guide recommends. Flagging it punishes correct
            // code and gets the analyzer switched off.
            string[] ids = await Run(@"
using UnityEngine;
class Piece : MonoBehaviour
{
    void Awake() { gameObject.AddComponent<Rigidbody>(); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task AddComponent_InStart_IsNotReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Piece : MonoBehaviour
{
    void Start() { gameObject.AddComponent<Rigidbody>(); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task ASameNamedMethodOnAPlainClass_IsNotReported()
        {
            // A service with an OnEnable method that is not a Unity message does not
            // run per-reuse; the MonoBehaviour gate must exclude it.
            string[] ids = await Run(@"
using UnityEngine;
class Builder
{
    GameObject _go;
    void OnEnable() { _go.AddComponent<Rigidbody>(); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task AProjectMethodCalledAddComponent_IsNotReported()
        {
            // The semantic check must confirm it is UnityEngine's AddComponent, not a
            // same-named method on a project type.
            string[] ids = await Run(@"
using UnityEngine;
class Registry { public void AddComponent<T>() { } }
class Piece : MonoBehaviour
{
    Registry _registry;
    void OnEnable() { _registry.AddComponent<Rigidbody>(); }
}");

            Assert.Empty(ids);
        }

        [Fact]
        public async Task MultipleAddComponents_InOneMethod_AreEachReported()
        {
            string[] ids = await Run(@"
using UnityEngine;
class Piece : MonoBehaviour
{
    void OnEnable()
    {
        gameObject.AddComponent<Rigidbody>();
        gameObject.AddComponent<Transform>();
    }
}");

            Assert.Equal(new[] { "MTK006", "MTK006" }, ids);
        }
    }
}
