using System.Collections.Generic;
using MemoryToolkit;
using UnityEngine;

namespace MemoryToolkit.Samples.LowMemoryResponse
{
    /// <summary>
    /// SCENARIO: surviving a mobile low-memory warning instead of being killed.
    ///
    /// When iOS/Android signals memory pressure, MemoryManager already trims
    /// every pool and unloads unused assets. But games also hold caches the
    /// toolkit cannot see — baked decals, generated meshes, downloaded
    /// avatars. Those systems subscribe to <see cref="MemoryManager.LowMemoryTrimmed"/>
    /// and shed their expendable memory in the same sweep.
    ///
    /// This demo "cache" is procedural decal textures: cheap to regenerate,
    /// expensive to keep. On the signal it keeps the most recent few and
    /// destroys the rest. To watch it fire in the editor, use the
    /// "Simulate low memory" button in Window &gt; Analysis &gt; Memory
    /// Toolkit Inspector (on device, the OS triggers it for real).
    /// </summary>
    public sealed class DecalCache : MonoBehaviour
    {
        [Tooltip("Newest entries kept when a low-memory trim fires.")]
        [SerializeField] private int keepOnTrim = 4;
        [SerializeField] private int textureSize = 256;

        private readonly List<Texture2D> _cache = new();
        private float _cooldown;

        // Subscribe in OnEnable / unsubscribe in OnDisable — a subscriber that
        // forgets to unsubscribe is itself a leak.
        private void OnEnable() => MemoryManager.LowMemoryTrimmed += OnLowMemoryTrimmed;
        private void OnDisable() => MemoryManager.LowMemoryTrimmed -= OnLowMemoryTrimmed;

        private void Update()
        {
            // Demo driver: grow the cache over time, as real gameplay would.
            _cooldown -= Time.deltaTime;
            if (_cooldown > 0f) return;
            _cooldown = 1f;
            _cache.Add(CreateDecal());
        }

        private void OnLowMemoryTrimmed()
        {
            int excess = _cache.Count - keepOnTrim;
            if (excess <= 0) return;

            // Oldest first: entries [0, excess) go, the newest stay.
            for (int i = 0; i < excess; i++)
                Destroy(_cache[i]);
            _cache.RemoveRange(0, excess);

            Debug.Log($"DecalCache: dropped {excess} decals on low-memory signal, kept {_cache.Count}.");
        }

        private void OnDestroy()
        {
            // Deterministic teardown, mirroring the toolkit's own rule: caches
            // are owned and released, never left to the GC/asset GC to find.
            for (int i = 0; i < _cache.Count; i++)
                Destroy(_cache[i]);
            _cache.Clear();
        }

        private Texture2D CreateDecal()
        {
            var tex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            tex.name = $"Decal {_cache.Count}";
            return tex;
        }
    }
}
