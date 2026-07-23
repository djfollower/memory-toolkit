using UnityEngine;

namespace MemoryToolkit.Samples.PermanentConfigs
{
    /// <summary>
    /// The permanent home for config assets. Systems read configs from here
    /// for the rest of the session — nobody goes back to the login scene, or
    /// to any scene, to find them.
    /// </summary>
    public static class GameConfigs
    {
        public static GameBalanceConfig Balance { get; private set; }
        public static bool IsReady => Balance != null;

        internal static void Initialize(GameBalanceConfig balance) => Balance = balance;
    }
}
