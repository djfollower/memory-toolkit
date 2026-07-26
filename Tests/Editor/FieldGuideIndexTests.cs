using MemoryToolkit.Editor;
using NUnit.Framework;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers the finding → guide-section map. The point of the index is that a
    /// finding always has somewhere to turn; the test that matters is that a known
    /// topic resolves and an unknown one fails cleanly rather than inventing an answer.
    /// </summary>
    public class FieldGuideIndexTests
    {
        [Test]
        public void AKnownTopic_ResolvesToAGuideSection()
        {
            Assert.That(FieldGuideIndex.TryGet("stop-action-destroy", out FieldGuideIndex.Entry entry), Is.True);
            Assert.That(entry.Guide, Does.Contain("ADOPTION"));
            Assert.That(entry.Summary, Is.Not.Empty);
            Assert.That(entry.Action, Is.Not.Empty);
        }

        [Test]
        public void TheAnalyzerRules_HaveEntries()
        {
            // MTK001 and MTK002 point at these; a finding with no explanation is the
            // gap this index exists to close.
            Assert.That(FieldGuideIndex.TryGet("null-conditional-unity-object", out _), Is.True);
            Assert.That(FieldGuideIndex.TryGet("per-frame-allocation", out _), Is.True);
        }

        [Test]
        public void TheHeadlineMetric_HasAnEntry()
        {
            Assert.That(FieldGuideIndex.TryGet("escapes", out FieldGuideIndex.Entry entry), Is.True);
            Assert.That(entry.Section, Does.Contain("§"));
        }

        [Test]
        public void LookupIsCaseInsensitive()
        {
            Assert.That(FieldGuideIndex.TryGet("Stop-Action-Destroy", out _), Is.True);
        }

        [Test]
        public void AnUnknownTopic_FailsCleanly()
        {
            Assert.That(FieldGuideIndex.TryGet("not-a-real-topic", out _), Is.False);
        }

        [Test]
        public void EveryEntryHasBothWhyAndAction()
        {
            foreach (string topic in FieldGuideIndex.Topics)
            {
                Assert.That(FieldGuideIndex.TryGet(topic, out FieldGuideIndex.Entry entry), Is.True);
                Assert.That(entry.Summary, Is.Not.Empty, $"{topic} has no 'why'");
                Assert.That(entry.Action, Is.Not.Empty, $"{topic} has no 'action'");
                Assert.That(entry.Guide, Is.Not.Empty, $"{topic} has no guide reference");
            }
        }
    }
}
