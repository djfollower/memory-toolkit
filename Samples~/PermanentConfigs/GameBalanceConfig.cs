using UnityEngine;

namespace MemoryToolkit.Samples.PermanentConfigs
{
    /// <summary>A typical config asset: tuning values the whole game reads.</summary>
    [CreateAssetMenu(menuName = "Memory Toolkit Samples/Game Balance Config")]
    public sealed class GameBalanceConfig : ScriptableObject
    {
        public float playerSpeed = 5f;
        public int startingGold = 100;
        public AnimationCurve xpCurve = AnimationCurve.Linear(0, 0, 1, 1);
    }
}
