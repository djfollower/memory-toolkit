using System;
using System.Collections.Generic;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// Maps a finding — a validator issue, a timeline anomaly, a triage signal — to the
    /// section of the field guides that explains it.
    ///
    /// <para><b>Why this exists.</b> The analyzer and validator say <i>what</i> is
    /// wrong; the guides say <i>why it breaks and what to do</i>. Without a link
    /// between them the guides are a document someone has to know to open, and an agent
    /// looking at a finding has nowhere to turn. This is the seam that lets the agent's
    /// method be the documented method.</para>
    ///
    /// <para>Keyed on stable topic slugs rather than on message text, so a reworded
    /// diagnostic does not silently lose its explanation.</para>
    /// </summary>
    public static class FieldGuideIndex
    {
        public readonly struct Entry
        {
            public Entry(string title, string guide, string section, string summary, string action)
            {
                Title = title;
                Guide = guide;
                Section = section;
                Summary = summary;
                Action = action;
            }

            public string Title { get; }
            public string Guide { get; }
            public string Section { get; }

            /// <summary>Why it breaks — one or two sentences from the guide.</summary>
            public string Summary { get; }

            /// <summary>What to do about it.</summary>
            public string Action { get; }
        }

        private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
        {
            ["stop-action-destroy"] = new(
                "ParticleSystem Stop Action = Destroy",
                "docs/ADOPTION.md", "§4",
                "The particle system deletes its own GameObject when it finishes, so the pool hands out " +
                "destroyed instances and Unity's fake-null surfaces the failure far from the cause.",
                "Set Stop Action to Disable and release from OnParticleSystemStopped. Audit child systems too — " +
                "one child still set to Destroy takes the parent's hierarchy with it."),

            ["add-component-spawn"] = new(
                "AddComponent in the spawn path",
                "docs/ADOPTION.md", "§4",
                "AddComponent allocates and has no cheap inverse, so a pooled instance that went through setup " +
                "twice carries two copies of each component.",
                "Move components to author time plus an Init(...), re-Init in OnTakenFromPool. Where components " +
                "genuinely vary, pool per configured variant, not per base prefab."),

            ["ondestroy-cleanup"] = new(
                "OnDestroy doing real cleanup",
                "docs/ADOPTION.md", "§4",
                "Under pooling OnDestroy stops running, so everything it did — unsubscribing events, killing " +
                "tweens, stopping coroutines, resetting physics — silently stops happening.",
                "Move it to OnReturnedToPool, reviewable against the old OnDestroy line by line."),

            ["identity-hashcode"] = new(
                "Collection keyed on instance identity",
                "docs/ADOPTION.md", "§4",
                "A dictionary keyed on GetHashCode works only while instances are unique; under pooling the same " +
                "object is registered and removed repeatedly, and one missed removal throws a duplicate-key error " +
                "that reads as an unrelated crash.",
                "Audit identity-keyed collections for exact add/remove pairing; prefer registering on the " +
                "take/return callbacks over registering at call sites."),

            ["async-after-await"] = new(
                "Async lifetime crossing a scope boundary",
                "docs/ADOPTION.md", "§4",
                "After an await the scope may be disposed and the target destroyed — or pooled and reused, in which " +
                "case it is alive, non-null, and someone else's. A null check passes.",
                "Re-check scope.IsDisposed and the instance after every await; capture a PooledRef<T> across the " +
                "suspension. The analyzer flags this as MTK007."),

            ["null-conditional-unity-object"] = new(
                "?. on a UnityEngine.Object",
                "docs/INTEGRATION.md", "§2 (failure D)",
                "?., ??, ??= and 'is null' compile to reference comparisons that skip Unity's overloaded ==, so a " +
                "destroyed object passes as alive.",
                "Use == null / != null. The analyzer flags this as MTK001."),

            ["registry-scene-owned"] = new(
                "Pool registry owned by a scene object",
                "docs/INTEGRATION.md", "§2 (failure A)",
                "A registry kept on a MonoBehaviour dies with the scene load, and the pool silently degrades into " +
                "Instantiate/Destroy plus lookup overhead — invisible in any post-load snapshot.",
                "Give the registry to a MemoryScope whose lifetime is chosen deliberately. PoolBridge does this " +
                "without touching call sites."),

            ["return-without-reparent"] = new(
                "Release that skips reparenting",
                "docs/INTEGRATION.md", "§2 (failure B)",
                "An instance returned without being reparented under the pool root stays in the scene hierarchy and " +
                "is found by the next GetComponentsInChildren, or is taken down by a scene unload the pool did not " +
                "expect.",
                "Make reparent-on-release unconditional. PoolBridge's release always reparents."),

            ["pool-key-from-asset"] = new(
                "Pool key derived from a loaded asset",
                "docs/INTEGRATION.md", "§2 (failure E)",
                "A key derived from a loaded asset's runtime identity goes stale the moment Addressables releases " +
                "and reloads it, and the pool leaks a whole generation of instances under the old key.",
                "Key on something you own and control the lifetime of. The toolkit keys pools on the prefab " +
                "reference and carries the owning pool on the instance."),

            ["escapes"] = new(
                "Instances escaping the pool",
                "docs/INTEGRATION.md", "§1 / §7",
                "Instances reaching PoolBridge.Return owned by no pool are destroyed rather than pooled, which costs " +
                "more than not pooling at all. This is a transition a snapshot cannot show.",
                "Drive escapes to zero; capture the count before a migration as the regression baseline and gate " +
                "on it in CI (docs/CI.md)."),

            ["lazy-pool"] = new(
                "Pool created lazily during gameplay",
                "docs/ADOPTION.md", "§3",
                "A pool created by a Get rather than a warm-up paid an Instantiate during gameplay and took its " +
                "capacity from whichever call site ran first.",
                "Warm it during the load, sized from the Timeline's peak active. In a budgeted project, a lazy pool " +
                "means the budget is missing an entry."),

            ["per-frame-allocation"] = new(
                "Allocation in a per-frame method",
                "docs/ADOPTION.md", "§3",
                "Allocations in Update/LateUpdate/FixedUpdate are garbage at frame rate — the difference between low " +
                "GC and 0 B/frame, invisible in review and obvious in the Profiler.",
                "Reuse a cached instance, a pooled collection, or the frame arena. The analyzer flags this as MTK002."),
        };

        /// <summary>All topic slugs, for discovery.</summary>
        public static IEnumerable<string> Topics => Entries.Keys;

        /// <summary>Looks up a topic. Returns false for an unknown slug rather than inventing an answer.</summary>
        public static bool TryGet(string topic, out Entry entry)
            => Entries.TryGetValue(topic ?? string.Empty, out entry);
    }
}
