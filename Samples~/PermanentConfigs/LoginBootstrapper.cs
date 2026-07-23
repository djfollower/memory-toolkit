using MemoryToolkit;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MemoryToolkit.Samples.PermanentConfigs
{
    /// <summary>
    /// SCENARIO: configs loaded in the login scene must be permanent, while
    /// the login scene itself is momentary.
    ///
    /// The rule that resolves it: ownership follows LIFETIME, not the scene
    /// that happened to do the loading. Two scopes coexist here:
    ///
    /// - The login scene's scope (momentary): its UI pools and scratch die
    ///   with the scene.
    /// - <see cref="MemoryManager.Permanent"/> (session): the config assets
    ///   are PINNED there the moment they're loaded. Pinning holds a strong
    ///   reference in the Permanent scope, so when the login scene unloads and
    ///   <see cref="MemoryManager.CollectFull"/> sweeps unused assets behind
    ///   the transition, the configs are still referenced and their memory
    ///   block survives untouched.
    ///
    /// With Addressables installed the same idea is one call —
    /// <c>MemoryManager.Permanent.LoadAssetAsync&lt;GameBalanceConfig&gt;(key)</c>
    /// (MemoryToolkit.Addressables) — the Permanent scope then owns the load
    /// handle, keeping the bundle resident for the session.
    /// </summary>
    public sealed class LoginBootstrapper : MonoBehaviour
    {
        [Tooltip("Serialized reference: the asset streams in with the login scene, but its lifetime is decided below, not by the scene.")]
        [SerializeField] private GameBalanceConfig balanceConfig;

        [SerializeField] private GameObject loginUiPrefab;
        [SerializeField] private string nextSceneName = "Main";
        [SerializeField] private float simulatedLoginSeconds = 2f;

        private MemoryScope _loginScope;

        private void Awake()
        {
            // Momentary memory: everything the login screen itself needs.
            _loginScope = MemoryManager.CreateSceneScope(gameObject.scene);
            if (loginUiPrefab != null)
                _loginScope.Warmup(loginUiPrefab, 1);

            // Permanent memory: same scene, different owner. After this line
            // the login scene is irrelevant to the config's survival.
            GameConfigs.Initialize(MemoryManager.Permanent.Pin(balanceConfig));
        }

        private void Start() => Invoke(nameof(OnLoginComplete), simulatedLoginSeconds);

        private void OnLoginComplete()
        {
            Debug.Log($"Login done. Balance config pinned permanently: playerSpeed={GameConfigs.Balance.playerSpeed}");

            // The transition: the login scope dies with its scene, and the
            // blocking sweep runs behind the load. The pinned config is
            // referenced by the Permanent scope, so the sweep skips it.
            AsyncOperation load = SceneManager.LoadSceneAsync(nextSceneName);
            load.completed += _ => MemoryManager.CollectFull();
        }
    }
}
