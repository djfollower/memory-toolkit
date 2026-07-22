using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// A time-series chart drawn with <see cref="Painter2D"/>.
    ///
    /// <para>The IMGUI version of this drew one <c>DrawRect</c> per sample, which
    /// costs a draw call per column, cannot anti-alias, and had to be re-issued on
    /// every repaint of the whole window. A retained-mode element regenerates its
    /// geometry only when the data actually changes
    /// (<see cref="VisualElement.MarkDirtyRepaint"/>), and Painter2D gives real
    /// stroked polylines with a filled area underneath — which is what makes a
    /// trend legible rather than merely present.</para>
    ///
    /// <para><see cref="float.NaN"/> in the data is a gap, not a zero. That
    /// distinction carries the meaning in this window: a pool that went away must
    /// not look like a pool sitting idle at zero.</para>
    /// </summary>
    internal sealed class TimelineChart : VisualElement
    {
        private float[] _values = Array.Empty<float>();
        private int _count;
        private float _max = 1f;
        private float _peak;

        internal Color LineColor { get; set; } = new(0.4f, 0.8f, 1f);
        internal Color FillColor { get; set; } = new(0.4f, 0.8f, 1f, 0.18f);

        /// <summary>Draws a horizontal rule at the high-water mark. The value that sizes a warm-up count.</summary>
        internal bool ShowPeakLine { get; set; }

        internal TimelineChart(float height)
        {
            style.height = height;
            style.marginTop = 2f;
            style.marginBottom = 2f;
            style.backgroundColor = new Color(0f, 0f, 0f, 0.18f);
            style.borderTopLeftRadius = 2f;
            style.borderTopRightRadius = 2f;
            style.borderBottomLeftRadius = 2f;
            style.borderBottomRightRadius = 2f;
            style.flexGrow = 1f;

            generateVisualContent += OnGenerateVisualContent;
        }

        /// <summary>
        /// Replaces the plotted data. The buffer is reused across updates so a
        /// window left open for an hour does not allocate an array every tick.
        /// </summary>
        internal void SetData(int count, Func<int, float> valueAt, float max)
        {
            if (_values.Length < count)
                _values = new float[Mathf.NextPowerOfTwo(Mathf.Max(count, 16))];

            _count = count;
            _peak = 0f;
            for (int i = 0; i < count; i++)
            {
                float value = valueAt(i);
                _values[i] = value;
                if (!float.IsNaN(value) && value > _peak) _peak = value;
            }

            _max = Mathf.Max(max, 0.0001f);
            MarkDirtyRepaint();
        }

        private void OnGenerateVisualContent(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (_count < 2 || rect.width <= 0f || rect.height <= 0f) return;

            Painter2D painter = context.painter2D;
            float stepX = rect.width / (_count - 1);

            // Segments are drawn between runs of non-NaN samples, so a gap in the
            // data becomes a gap on screen instead of a line dropping to the floor.
            int index = 0;
            while (index < _count)
            {
                while (index < _count && float.IsNaN(_values[index])) index++;
                int start = index;
                while (index < _count && !float.IsNaN(_values[index])) index++;
                int end = index; // exclusive

                if (end - start < 2) continue;
                DrawSegment(painter, rect, stepX, start, end);
            }

            if (ShowPeakLine && _peak > 0f)
            {
                float y = rect.yMax - _peak / _max * rect.height;
                painter.strokeColor = new Color(LineColor.r, LineColor.g, LineColor.b, 0.45f);
                painter.lineWidth = 1f;
                painter.BeginPath();
                painter.MoveTo(new Vector2(rect.xMin, y));
                painter.LineTo(new Vector2(rect.xMax, y));
                painter.Stroke();
            }
        }

        private void DrawSegment(Painter2D painter, Rect rect, float stepX, int start, int end)
        {
            // Filled area first, so the stroke sits on top of its own fill.
            painter.fillColor = FillColor;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin + start * stepX, rect.yMax));
            for (int i = start; i < end; i++)
                painter.LineTo(PointAt(rect, stepX, i));
            painter.LineTo(new Vector2(rect.xMin + (end - 1) * stepX, rect.yMax));
            painter.ClosePath();
            painter.Fill();

            painter.strokeColor = LineColor;
            painter.lineWidth = 1.5f;
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();
            painter.MoveTo(PointAt(rect, stepX, start));
            for (int i = start + 1; i < end; i++)
                painter.LineTo(PointAt(rect, stepX, i));
            painter.Stroke();
        }

        private Vector2 PointAt(Rect rect, float stepX, int index)
            => new(rect.xMin + index * stepX,
                rect.yMax - Mathf.Clamp01(_values[index] / _max) * rect.height);
    }
}
