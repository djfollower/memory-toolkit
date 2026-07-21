#if MEMORYTOOLKIT_ADDRESSABLES
using System;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace MemoryToolkit.AddressableAssets
{
    /// <summary>
    /// Ties Addressables handles to <see cref="MemoryScope"/> lifetimes.
    ///
    /// Addressables memory is reference-counted: an asset (and its bundle)
    /// stays resident until every load handle is released. The classic leak is
    /// loading per level and never releasing, so bundles accumulate for the
    /// whole session. The fix is the same lifetime rule as everything else in
    /// this package: the handle belongs to a scope, and the scope releases it.
    ///
    /// - Level content:  <c>sceneScope.LoadAssetAsync&lt;GameObject&gt;(key)</c>
    ///   — released automatically when the scene scope dies.
    /// - Permanent configs loaded from a momentary login scene:
    ///   <c>MemoryManager.Permanent.LoadAssetAsync&lt;GameConfig&gt;(key)</c>
    ///   — the login scene's own scope can come and go; the config's handle is
    ///   owned by the Permanent scope, so its memory block survives.
    ///
    /// Note the scope releases the HANDLE, not the asset directly — actual
    /// unload happens when Addressables' refcount reaches zero, so assets
    /// shared between scopes unload only when the last owning scope dies.
    /// </summary>
    public static class ScopedAddressablesExtensions
    {
        /// <summary>
        /// Loads an addressable asset whose handle is released when
        /// <paramref name="scope"/> is disposed.
        /// </summary>
        public static AsyncOperationHandle<T> LoadAssetAsync<T>(this MemoryScope scope, object key)
            => scope.Track(Addressables.LoadAssetAsync<T>(key));

        /// <summary>
        /// Takes ownership of an existing handle: it is released when
        /// <paramref name="scope"/> is disposed. Returns the handle for
        /// inline use / awaiting.
        /// </summary>
        public static AsyncOperationHandle<T> Track<T>(this MemoryScope scope, AsyncOperationHandle<T> handle)
        {
            scope.Register(new HandleReleaser(handle));
            return handle;
        }

        /// <summary>Untyped overload of <see cref="Track{T}"/>.</summary>
        public static AsyncOperationHandle Track(this MemoryScope scope, AsyncOperationHandle handle)
        {
            scope.Register(new HandleReleaser(handle));
            return handle;
        }

        private sealed class HandleReleaser : IDisposable
        {
            private AsyncOperationHandle _handle;

            public HandleReleaser(AsyncOperationHandle handle) => _handle = handle;

            public void Dispose()
            {
                if (_handle.IsValid())
                    Addressables.Release(_handle);
                _handle = default;
            }
        }
    }
}
#endif
