using MemoryToolkit.Pooling;
using UnityEngine;

namespace MemoryToolkit.Samples.SceneScopeLevel
{
    /// <summary>
    /// Spawns enemies from the level's scene scope in waves. Note what is
    /// absent: no Destroy, no per-spawn pool lookup, no cleanup code — the
    /// installer's scope owns every instance and frees them on scene unload.
    /// </summary>
    public sealed class ScopedEnemySpawner : MonoBehaviour
    {
        [SerializeField] private LevelMemoryInstaller installer;
        [SerializeField] private GameObject enemyPrefab;
        [SerializeField] private int perWave = 8;
        [SerializeField] private float secondsBetweenWaves = 5f;
        [SerializeField] private float spawnRadius = 10f;

        private GameObjectPool _pool;
        private float _cooldown;

        private void Start()
        {
            // Resolved once. If the installer already warmed this prefab, this
            // returns that same pool; it never creates a duplicate.
            _pool = installer.Scope.GetPool(enemyPrefab);
        }

        private void Update()
        {
            _cooldown -= Time.deltaTime;
            if (_cooldown > 0f) return;
            _cooldown = secondsBetweenWaves;

            for (int i = 0; i < perWave; i++)
            {
                Vector2 offset = Random.insideUnitCircle * spawnRadius;
                var position = new Vector3(transform.position.x + offset.x,
                                           transform.position.y,
                                           transform.position.z + offset.y);
                _pool.Get(position, Quaternion.identity);
            }
        }
    }
}
