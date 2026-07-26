using MemoryToolkit;
using MemoryToolkit.Diagnostics;
using UnityEngine;

namespace MemoryToolkit.Samples.DeviceSoak
{
    /// <summary>
    /// SCENARIO: an OOM crash report that carries a memory postmortem instead of a
    /// bare stack trace.
    ///
    /// <para>A crash reporter's custom keys are attached to whatever crash comes next.
    /// Point <see cref="MemoryBreadcrumbs"/> at one during boot and every low-memory
    /// signal refreshes the keys, so the kill that follows arrives with the escape
    /// count, the live scopes, and the busiest pools already on it — the difference
    /// between "OOM at frame 90124" and "OOM, 41 escapes, BattleScope still live,
    /// Projectile pool at 512/512".</para>
    ///
    /// <para><b>This is a sample, not a dependency.</b> The toolkit ships no analytics
    /// reference and never will — <see cref="IBreadcrumbSink"/> is one method. Copy
    /// this file, replace the two commented lines with your reporter's real calls
    /// (here, Firebase Crashlytics), and wire it up once:</para>
    ///
    /// <code>
    /// void Awake() => MemoryBreadcrumbs.Sink = new CrashlyticsBreadcrumbSink();
    /// </code>
    ///
    /// <para>Mind the reporter's limits: Crashlytics allows 64 custom keys of 1 KB
    /// each. <see cref="MemoryBreadcrumbs"/> is already budgeted well under that
    /// (under ten keys), which is why it sends a "busiest pools" summary rather than
    /// a key per pool — do not add a key-per-anything on top of it.</para>
    /// </summary>
    public sealed class CrashlyticsBreadcrumbSink : IBreadcrumbSink
    {
        public void Set(string key, string value)
        {
            // Replace with your crash reporter. For Firebase Crashlytics:
            //
            //   Firebase.Crashlytics.Crashlytics.SetCustomKey(key, value);
            //
            // Left as a log so the sample compiles and runs with no analytics package
            // installed — you can see exactly what would be attached to a crash.
            Debug.Log($"[Crashlytics breadcrumb] {key} = {value}");
        }
    }

    /// <summary>
    /// Drop this on a boot object in a development build to run a soak: it wires the
    /// breadcrumb sink, starts periodic disk reports, and (optionally) shows the
    /// on-screen overlay for a tester holding the device.
    /// </summary>
    public sealed class DeviceSoakBootstrapper : MonoBehaviour
    {
        [Tooltip("Seconds between disk reports. 30 is plenty for an overnight run.")]
        [SerializeField] private float reportIntervalSeconds = 30f;

        [Tooltip("Also draw the live overlay for a tester watching the screen.")]
        [SerializeField] private bool showOverlay = true;

        private void Awake()
        {
            MemoryBreadcrumbs.Sink = new CrashlyticsBreadcrumbSink();

            // Both compile out in a release build, so this component is safe to leave
            // on a boot prefab that ships.
            MemorySoak.Begin(reportIntervalSeconds);
            if (showOverlay) MemoryOverlay.Show();
        }
    }
}
