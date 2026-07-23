using System;
using UnityEngine;

namespace MemoryToolkit.Budgets
{
    /// <summary>
    /// Device class a budget resolves against.
    ///
    /// <para>Three, for the same reason there are three lifetime tiers: a fourth
    /// creates an authoring decision nobody can make consistently. These map to what
    /// a studio's QA matrix already has — the minimum-spec device, the median phone,
    /// and everything comfortable.</para>
    /// </summary>
    public enum MemoryBudgetTier
    {
        /// <summary>Minimum-spec hardware. The tier that decides whether the game ships.</summary>
        Low,

        Medium,

        /// <summary>Headroom to spare. Also the authoring default.</summary>
        High,
    }

    /// <summary>Decides which tier this device is. Replace to use your own device database.</summary>
    public interface IDeviceTierProvider
    {
        MemoryBudgetTier GetTier();
    }

    /// <summary>
    /// Tier from system RAM.
    ///
    /// <para>Deliberately crude. A real studio has a device database keyed by model
    /// string, and should supply one via <see cref="DeviceTier.Provider"/> — this
    /// exists so that a project which has not built one yet still gets tiering rather
    /// than a single number tuned for whatever device the programmer owns, which is
    /// the failure this whole feature is here to prevent.</para>
    /// </summary>
    public sealed class SystemMemoryDeviceTierProvider : IDeviceTierProvider
    {
        public MemoryBudgetTier GetTier()
        {
            int mb = SystemInfo.systemMemorySize;

            // SystemInfo returns 0 on some platforms rather than failing. Assume the
            // worst: under-warming a pool costs frames, over-warming costs the crash
            // this package exists to avoid.
            if (mb <= 0) return MemoryBudgetTier.Low;

            if (mb < 3072) return MemoryBudgetTier.Low;
            if (mb < 6144) return MemoryBudgetTier.Medium;
            return MemoryBudgetTier.High;
        }
    }

    /// <summary>The device tier this build resolves to, resolved once.</summary>
    public static class DeviceTier
    {
        private static IDeviceTierProvider _provider = new SystemMemoryDeviceTierProvider();
        private static MemoryBudgetTier? _cached;

        /// <summary>
        /// Set this during boot, before anything applies a budget. Setting it clears
        /// the cached tier, so a QA build can force a tier at runtime to test the
        /// minimum-spec configuration on whatever hardware is on the desk.
        /// </summary>
        public static IDeviceTierProvider Provider
        {
            get => _provider;
            set
            {
                _provider = value ?? throw new ArgumentNullException(nameof(value));
                _cached = null;
            }
        }

        /// <summary>
        /// This device's tier. Cached: tiering must not be able to change between two
        /// pools in the same scene, which would produce a half-Low, half-High budget
        /// that matches no configuration anyone tested.
        /// </summary>
        public static MemoryBudgetTier Current => _cached ??= _provider.GetTier();

        /// <summary>Forces a tier for this session. For QA builds and tests.</summary>
        public static void Override(MemoryBudgetTier tier) => _cached = tier;

        /// <summary>Drops the cached tier so the provider is asked again.</summary>
        public static void Clear() => _cached = null;
    }

    /// <summary>
    /// An integer with a per-tier value.
    ///
    /// <para>Authoring rule: fill in <see cref="High"/> and leave the rest at zero to
    /// get one number everywhere. A zero means "same as the tier above", resolved by
    /// <see cref="Get"/> — so a partially filled row is always coherent, and the
    /// common case (this prefab does not need tiering) costs one field instead of
    /// three.</para>
    /// </summary>
    [Serializable]
    public struct TieredInt
    {
        [Tooltip("Value on high-end devices. Also the fallback for any tier left at 0.")]
        public int High;

        [Tooltip("0 = same as High.")]
        public int Medium;

        [Tooltip("0 = same as Medium.")]
        public int Low;

        public TieredInt(int high, int medium = 0, int low = 0)
        {
            High = high;
            Medium = medium;
            Low = low;
        }

        /// <summary>Value for a tier, falling back up the tiers when a value is 0.</summary>
        public int Get(MemoryBudgetTier tier)
        {
            switch (tier)
            {
                case MemoryBudgetTier.Low:
                    if (Low > 0) return Low;
                    goto case MemoryBudgetTier.Medium;
                case MemoryBudgetTier.Medium:
                    if (Medium > 0) return Medium;
                    goto default;
                default:
                    return High;
            }
        }

        /// <summary>Value for this device.</summary>
        public int Current => Get(DeviceTier.Current);

        public static implicit operator TieredInt(int value) => new(value);
    }
}
