using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace MemoryToolkit.Pooling
{
    /// <summary>
    /// A prefab instance pool built on <see cref="ObjectPool{T}"/>.
    ///
    /// Anti-fragmentation practices demonstrated here:
    /// - Instances are created once and recycled, so the managed heap is not
    ///   churned by Instantiate/Destroy during gameplay.
    /// - <see cref="Warmup"/> pre-allocates during load screens so gameplay
    ///   frames never pay the instantiation cost.
    /// - <see cref="Trim"/> lets the game shed excess capacity on low-memory
    ///   signals instead of holding peak allocations forever.
    /// - Inactive instances are parented under a dedicated root so they do not
    ///   pollute the scene hierarchy or receive transform change callbacks.
    /// </summary>
    public sealed class GameObjectPool : IDisposable
    {
        private readonly GameObject _prefab;
        private readonly Transform _inactiveRoot;
        private readonly ObjectPool<GameObject> _pool;
        private readonly List<IPoolable> _poolableBuffer = new(8);
        private readonly HashSet<GameObject> _active = new();
        private readonly Dictionary<GameObject, PooledInstance> _handles = new();
        private bool _disposed;
        private bool _suppressCallbacks;

        /// <summary>Instances currently held inside the pool.</summary>
        public int CountInactive => _pool.CountInactive;

        /// <summary>Instances handed out and not yet released.</summary>
        public int CountActive => _pool.CountActive;

        /// <summary>Total instances ever created by this pool and still alive.</summary>
        public int CountAll => _pool.CountAll;

        /// <param name="prefab">Prefab to instantiate.</param>
        /// <param name="defaultCapacity">Initial capacity of the internal stack.</param>
        /// <param name="maxSize">
        /// Hard cap on retained inactive instances; releases beyond it destroy the
        /// instance. Size this to the real peak so steady-state never destroys.
        /// </param>
        /// <param name="inactiveRoot">
        /// Optional parent for inactive instances. When null a hidden, scene-persistent
        /// root is created.
        /// </param>
        public GameObjectPool(GameObject prefab, int defaultCapacity = 16, int maxSize = 256, Transform inactiveRoot = null)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            _prefab = prefab;

            if (inactiveRoot == null)
            {
                var rootGo = new GameObject($"[Pool] {prefab.name}");
                rootGo.hideFlags = HideFlags.DontSave;
                rootGo.SetActive(false); // keeps pooled children inactive without per-child SetActive costs on scene ops
                if (Application.isPlaying)
                    UnityEngine.Object.DontDestroyOnLoad(rootGo);
                inactiveRoot = rootGo.transform;
                _ownsRoot = true;
            }

            _inactiveRoot = inactiveRoot;

            _pool = new ObjectPool<GameObject>(
                createFunc: CreateInstance,
                actionOnGet: OnGet,
                actionOnRelease: OnRelease,
                actionOnDestroy: OnDestroyInstance,
                collectionCheck: true, // catches double-release in the editor/development builds
                defaultCapacity: defaultCapacity,
                maxSize: maxSize);
        }

        private readonly bool _ownsRoot;

        /// <summary>
        /// Pre-instantiates <paramref name="count"/> instances. Call from a loading
        /// screen so gameplay never triggers Instantiate.
        /// </summary>
        public void Warmup(int count)
        {
            ThrowIfDisposed();
            if (_pool.CountInactive >= count) return;

            // Get-then-release keeps ObjectPool's active/inactive counters
            // consistent (releasing externally created instances would not).
            // Getting `count` instances drains existing inactive ones and
            // creates the shortfall; releasing them all leaves `count` pooled.
            _suppressCallbacks = true;
            try
            {
                var held = ListPool<GameObject>.Get();
                for (int i = 0; i < count; i++)
                    held.Add(_pool.Get());
                for (int i = 0; i < held.Count; i++)
                    _pool.Release(held[i]);
                ListPool<GameObject>.Release(held);
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        /// <summary>Takes an instance, activates it, and applies the given transform.</summary>
        public GameObject Get(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            ThrowIfDisposed();
            GameObject instance = _pool.Get();
            Transform t = instance.transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(position, rotation);
            return instance;
        }

        /// <summary>Takes an instance and activates it at the prefab's default transform.</summary>
        public GameObject Get() => Get(_prefab.transform.position, _prefab.transform.rotation);

        /// <summary>
        /// Takes an instance and returns the <typeparamref name="T"/> on it.
        /// Game code is component-typed while the pool is GameObject-typed, and
        /// a <c>GetComponent</c> at every call site would land in exactly the hot
        /// path the pool exists to optimize — so the lookup is resolved once per
        /// instance and cached on its <see cref="PooledInstance"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The prefab has no <typeparamref name="T"/>. This is a setup error, so
        /// it fails loudly rather than handing back a null the caller will
        /// dereference several frames later.
        /// </exception>
        public T Get<T>(Vector3 position, Quaternion rotation, Transform parent = null) where T : Component
        {
            GameObject instance = Get(position, rotation, parent);
            T component = _handles[instance].GetCached<T>();
            if (component == null)
            {
                Release(instance);
                throw new InvalidOperationException(
                    $"Prefab '{_prefab.name}' has no {typeof(T).Name} component.");
            }

            return component;
        }

        /// <summary>Typed <see cref="Get()"/> at the prefab's default transform.</summary>
        public T Get<T>() where T : Component
            => Get<T>(_prefab.transform.position, _prefab.transform.rotation);

        /// <summary>Deactivates the instance and returns it to the pool.</summary>
        public void Release(GameObject instance)
        {
            ThrowIfDisposed();
            if (instance == null) return;
            _pool.Release(instance);
        }

        /// <summary>
        /// Releases by component, so component-typed call sites do not have to
        /// reach through <c>.gameObject</c>.
        /// </summary>
        public void Release(Component component)
        {
            if (component == null) return;
            Release(component.gameObject);
        }

        /// <summary>
        /// Destroys retained inactive instances above <paramref name="keep"/>.
        /// Wire this to <c>Application.lowMemory</c> (see <c>MemoryManager</c>).
        /// </summary>
        public void Trim(int keep)
        {
            ThrowIfDisposed();
            if (keep < 0) keep = 0;
            if (_pool.CountInactive <= keep) return;

            if (keep == 0)
            {
                _pool.Clear();
                return;
            }

            // ObjectPool has no partial-trim API: temporarily hold `keep`
            // instances out, clear the rest, then put the held ones back.
            // Callbacks are suppressed so IPoolable does not see phantom cycles.
            _suppressCallbacks = true;
            try
            {
                var held = ListPool<GameObject>.Get();
                for (int i = 0; i < keep; i++)
                    held.Add(_pool.Get());
                _pool.Clear();
                for (int i = 0; i < held.Count; i++)
                    _pool.Release(held[i]);
                ListPool<GameObject>.Release(held);
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pool.Dispose(); // destroys all inactive instances via actionOnDestroy

            // The pool owns its active instances too: disposing (e.g. via a
            // MemoryScope at match end) must not leave orphans in the scene.
            // Instances already destroyed with their scene compare to null and
            // are skipped.
            foreach (GameObject instance in _active)
            {
                if (instance != null)
                    DestroyObject(instance);
            }
            _active.Clear();
            _handles.Clear();
            if (_ownsRoot && _inactiveRoot != null)
                DestroyObject(_inactiveRoot.gameObject);
        }

        private GameObject CreateInstance()
        {
            GameObject instance = UnityEngine.Object.Instantiate(_prefab, _inactiveRoot);
            var handle = instance.GetComponent<PooledInstance>();
            if (handle == null) handle = instance.AddComponent<PooledInstance>();
            handle.Owner = this;
            handle.IsInPool = true;
            // Resolved once here so the per-Get path never pays GetComponent.
            _handles.Add(instance, handle);
            return instance;
        }

        private void OnGet(GameObject instance)
        {
            _handles[instance].IsInPool = false;
            _active.Add(instance);
            if (_suppressCallbacks) return;
            instance.SetActive(true);

            instance.GetComponentsInChildren(includeInactive: true, _poolableBuffer);
            for (int i = 0; i < _poolableBuffer.Count; i++)
                _poolableBuffer[i].OnTakenFromPool();
            _poolableBuffer.Clear();
        }

        private void OnRelease(GameObject instance)
        {
            _active.Remove(instance);
            if (!_suppressCallbacks)
            {
                instance.GetComponentsInChildren(includeInactive: true, _poolableBuffer);
                for (int i = 0; i < _poolableBuffer.Count; i++)
                    _poolableBuffer[i].OnReturnedToPool();
                _poolableBuffer.Clear();
            }

            instance.SetActive(false);
            instance.transform.SetParent(_inactiveRoot, false);
            PooledInstance handle = _handles[instance];
            handle.IsInPool = true;
            // Invalidates every PooledRef captured during the use that just ended.
            handle.BumpGeneration();
        }

        private void OnDestroyInstance(GameObject instance)
        {
            _handles.Remove(instance);
            if (instance != null)
                DestroyObject(instance);
        }

        private static void DestroyObject(GameObject go)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(go);
            else
                UnityEngine.Object.DestroyImmediate(go);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GameObjectPool));
        }
    }
}
