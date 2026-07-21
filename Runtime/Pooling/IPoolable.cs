namespace MemoryToolkit.Pooling
{
    /// <summary>
    /// Optional callbacks for pooled MonoBehaviours. Implement this instead of
    /// relying on OnEnable/OnDisable when the reset logic must run only for
    /// pool transitions (not for regular activation).
    /// </summary>
    public interface IPoolable
    {
        /// <summary>Called after the instance is taken from the pool and activated.</summary>
        void OnTakenFromPool();

        /// <summary>
        /// Called just before the instance is deactivated and returned to the pool.
        /// Reset all per-use state here so the next user starts clean.
        /// </summary>
        void OnReturnedToPool();
    }
}
