using System.Text;

namespace MemoryToolkit.Buffers
{
    /// <summary>
    /// Thread-local StringBuilder cache for zero-allocation string assembly
    /// (HUD text, log lines, debug labels). Repeatedly newing StringBuilders —
    /// or worse, concatenating strings — creates short-lived garbage every
    /// frame, which is the classic driver of Gen0 churn and heap fragmentation.
    ///
    /// Usage:
    /// <code>
    /// var sb = StringBuilderCache.Acquire();
    /// sb.Append("Score: ").Append(score);
    /// label.text = StringBuilderCache.GetStringAndRelease(sb);
    /// </code>
    /// </summary>
    public static class StringBuilderCache
    {
        // Builders that grew beyond this are not cached; keeping huge builders
        // alive would pin large heap blocks for rare use.
        private const int MaxCachedCapacity = 4 * 1024;
        private const int DefaultCapacity = 256;

        [System.ThreadStatic] private static StringBuilder _cached;

        public static StringBuilder Acquire(int capacity = DefaultCapacity)
        {
            StringBuilder sb = _cached;
            if (sb != null && sb.Capacity >= capacity)
            {
                _cached = null;
                sb.Clear();
                return sb;
            }
            return new StringBuilder(capacity);
        }

        public static void Release(StringBuilder sb)
        {
            if (sb != null && sb.Capacity <= MaxCachedCapacity)
                _cached = sb;
        }

        public static string GetStringAndRelease(StringBuilder sb)
        {
            string result = sb.ToString();
            Release(sb);
            return result;
        }
    }
}
