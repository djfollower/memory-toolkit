using System;
using MemoryToolkit;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MemoryToolkit.Samples.SceneScopeLevel
{
    /// <summary>
    /// SCENARIO: each level owns its memory.
    ///
    /// Put one of these in every gameplay scene and list the prefabs the level
    /// needs. On Awake it creates a scene-bound <see cref="MemoryScope"/> and
    /// warms every pool, so the level's first seconds never Instantiate. When
    /// the scene unloads, the scope — pools, instances, native blocks — is
    /// freed in one step, so a 2-hour session that crosses 40 levels holds
    /// only the current level's memory, not the union of all of them.
    ///
    /// Other scripts in the scene get the scope via
    /// <see cref="LevelMemoryInstaller.Scope"/> (see ScopedEnemySpawner).
    /// </summary>
    [DefaultExecutionOrder(-100)] // scope must exist before spawners' Awake/Start
    public sealed class LevelMemoryInstaller : MonoBehaviour
    {
        [Serializable]
        public struct PrefabWarmup
        {
            public GameObject prefab;
            [Min(0)] public int count;
        }

        [Tooltip("Pools created in the scene scope and pre-instantiated on Awake. Size counts from the Memory Inspector window, not guesses.")]
        [SerializeField] private PrefabWarmup[] warmups;

        /// <summary>The current scene's scope. Valid between Awake and scene unload.</summary>
        public MemoryScope Scope { get; private set; }

        private void Awake()
        {
            Scope = MemoryManager.CreateSceneScope(gameObject.scene);
            for (int i = 0; i < warmups.Length; i++)
            {
                if (warmups[i].prefab != null)
                    Scope.Warmup(warmups[i].prefab, warmups[i].count);
            }
        }

        /// <summary>
        /// Level transition done right: the outgoing scope dies with its scene,
        /// and the one blocking cleanup the game ever does happens here, behind
        /// the load — never during gameplay.
        /// </summary>
        public void LoadLevel(string sceneName)
        {
            AsyncOperation load = SceneManager.LoadSceneAsync(sceneName);
            load.completed += _ => MemoryManager.CollectFull();
        }
    }
}
