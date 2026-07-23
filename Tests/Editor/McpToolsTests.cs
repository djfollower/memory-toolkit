using System.IO;
using MemoryToolkit.Editor.Mcp;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers the dispatch layer an agent actually hits: the tool list it reads to
    /// decide what to call, the shape of what comes back, and the two refusals that
    /// keep a model from flailing — an unknown tool, and a mutating tool that is off.
    /// </summary>
    public class McpToolsTests
    {
        private const string Folder = "Assets/MemoryToolkitMcpTests";

        private string _prefabPath;
        private bool _mutationsWereAllowed;

        [SetUp]
        public void SetUp()
        {
            _mutationsWereAllowed = McpServer.AllowMutations;

            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(Folder));

            var source = new GameObject("McpValidationTarget");
            // A Rigidbody with no IPoolable is the canonical warning: velocity and
            // isKinematic survive reuse, so a body frozen by gameplay comes back frozen.
            source.AddComponent<Rigidbody>();

            _prefabPath = Folder + "/McpValidationTarget.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, _prefabPath);
            Object.DestroyImmediate(source);
        }

        [TearDown]
        public void TearDown()
        {
            McpServer.AllowMutations = _mutationsWereAllowed;
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void List_DescribesEveryToolWithAnObjectSchema()
        {
            JsonValue tools = McpTools.List()["tools"];

            Assert.Greater(tools.Count, 0);
            foreach (JsonValue tool in tools.Items)
            {
                Assert.IsNotEmpty(tool["name"].AsString(""), "every tool needs a name");
                Assert.IsNotEmpty(tool["description"].AsString(""), $"{tool["name"].AsString()} needs a description");
                Assert.AreEqual("object", tool["inputSchema"]["type"].AsString());
            }
        }

        [Test]
        public void List_MarksMutatingToolsAsRequiringOptIn()
        {
            foreach (JsonValue tool in McpTools.List()["tools"].Items)
            {
                if (tool["name"].AsString() != "dispose_scope") continue;
                StringAssert.Contains("Allow Mutating Tools", tool["description"].AsString());
                return;
            }

            Assert.Fail("dispose_scope is missing from the tool list.");
        }

        [Test]
        public void EditorStatus_ReportsPlayModeAndRecorderState()
        {
            JsonValue status = McpTools.Call("editor_status", JsonValue.Object());

            Assert.AreEqual(Application.unityVersion, status["unityVersion"].AsString());
            Assert.AreEqual(EditorApplication.isPlaying, status["isPlaying"].AsBool());
            Assert.AreEqual(Diagnostics.MemoryRecorder.IsRecording, status["recording"].AsBool());
        }

        [Test]
        public void ValidatePrefab_ReportsIssuesForTheGivenAssetPath()
        {
            JsonValue result = McpTools.Call("validate_prefab",
                JsonValue.Object().Set("assetPath", _prefabPath));

            Assert.AreEqual(_prefabPath, result["assetPath"].AsString());
            Assert.Greater(result["issues"].Count, 0);

            bool sawRigidbodyWarning = false;
            foreach (JsonValue issue in result["issues"].Items)
            {
                if (issue["severity"].AsString() == "Warning" && issue["message"].AsString("").Contains("Rigidbody"))
                    sawRigidbodyWarning = true;
            }

            Assert.IsTrue(sawRigidbodyWarning, "expected the physics-state warning for a Rigidbody with no IPoolable");
        }

        [Test]
        public void ValidatePrefab_ResolvesByGuidToo()
        {
            string guid = AssetDatabase.AssetPathToGUID(_prefabPath);

            JsonValue result = McpTools.Call("validate_prefab", JsonValue.Object().Set("guid", guid));

            Assert.AreEqual(_prefabPath, result["assetPath"].AsString());
        }

        [Test]
        public void ValidatePrefab_FailsClearlyWhenTheAssetIsMissing()
        {
            Assert.That(
                () => McpTools.Call("validate_prefab", JsonValue.Object().Set("assetPath", "Assets/Nope.prefab")),
                Throws.Exception.With.Message.Contains("No prefab at"));

            Assert.That(
                () => McpTools.Call("validate_prefab", JsonValue.Object()),
                Throws.Exception.With.Message.Contains("assetPath"));
        }

        [Test]
        public void ValidateProject_ScansAFolderAndFiltersBySeverity()
        {
            JsonValue result = McpTools.Call("validate_project",
                JsonValue.Object().Set("folder", Folder).Set("minSeverity", "Warning"));

            Assert.AreEqual(1, result["prefabsScanned"].AsInt());
            Assert.AreEqual(1, result["reports"].Count);
            Assert.AreEqual(_prefabPath, result["reports"][0]["assetPath"].AsString());

            foreach (JsonValue issue in result["reports"][0]["issues"].Items)
                Assert.AreNotEqual("Info", issue["severity"].AsString(), "Info issues must be filtered out at minSeverity=Warning");
        }

        [Test]
        public void UnknownTool_IsRejected()
        {
            Assert.That(() => McpTools.Call("not_a_tool", JsonValue.Object()),
                Throws.Exception.With.Message.Contains("Unknown tool"));
        }

        [Test]
        public void MutatingTools_AreRefusedUntilEnabled()
        {
            McpServer.AllowMutations = false;

            Assert.That(() => McpTools.Call("collect_full", JsonValue.Object()),
                Throws.Exception.With.Message.Contains("mutating tools are disabled"));
        }

        [Test]
        public void MutatingTools_ExplainThatTheyNeedPlayMode()
        {
            if (EditorApplication.isPlaying) Assert.Ignore("Edit-mode assertion.");

            McpServer.AllowMutations = true;

            Assert.That(() => McpTools.Call("trim_pools", JsonValue.Object()),
                Throws.Exception.With.Message.Contains("play mode"));
        }
    }
}
