using TMPro;
using UnityEngine;
using System.Reflection;

namespace AlienTextFlow;

internal static class TwoLineLayout
{
    private static readonly FieldInfo? PreferredHeight = typeof(TMP_Text).GetField("m_preferredHeight", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? HeightDirty = typeof(TMP_Text).GetField("m_isPreferredHeightDirty", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static bool SupportsInputMetrics => PreferredHeight != null && HeightDirty != null;
    internal readonly struct Candidate
    {
        public readonly float SizeRatio, Spacing;
        public Candidate(float sizeRatio, float spacing) { SizeRatio = sizeRatio; Spacing = spacing; }
    }

    internal readonly struct Ink
    {
        public readonly float Width, Height, Gap;
        public readonly int Lines;
        public Ink(float width, float height, float gap, int lines) { Width = width; Height = height; Gap = gap; Lines = lines; }
    }

    internal static Ink GetInk(TMP_Text text)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float top0 = float.NegativeInfinity, bottom0 = float.PositiveInfinity;
        float top1 = float.NegativeInfinity, bottom1 = float.PositiveInfinity;
        for (int i = 0; i < text.textInfo.characterCount; i++)
        {
            var c = text.textInfo.characterInfo[i];
            if (!c.isVisible) continue;
            minX = Mathf.Min(minX, c.bottomLeft.x); maxX = Mathf.Max(maxX, c.topRight.x);
            if (c.lineNumber == 0) { top0 = Mathf.Max(top0, c.topLeft.y); bottom0 = Mathf.Min(bottom0, c.bottomLeft.y); }
            if (c.lineNumber == 1) { top1 = Mathf.Max(top1, c.topLeft.y); bottom1 = Mathf.Min(bottom1, c.bottomLeft.y); }
        }
        float top = Mathf.Max(top0, top1), bottom = Mathf.Min(bottom0, bottom1);
        return new Ink(maxX - minX, top - bottom, bottom0 - top1, text.textInfo.lineCount);
    }

    internal static void CenterAndFixInputMetrics(TMP_Text text, TMP_TextInfo info)
    {
        if (info.lineCount != 2) return;
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        float left = float.PositiveInfinity, right = float.NegativeInfinity;
        for (int i = 0; i < info.characterCount; i++)
            if (info.characterInfo[i].isVisible)
            {
                min = Mathf.Min(min, info.characterInfo[i].bottomLeft.y);
                max = Mathf.Max(max, info.characterInfo[i].topLeft.y);
                left = Mathf.Min(left, info.characterInfo[i].bottomLeft.x);
                right = Mathf.Max(right, info.characterInfo[i].topRight.x);
            }
        if (float.IsInfinity(min) || float.IsInfinity(max)) return;
        float center = text.rectTransform.rect.center.y + (text.margin.w - text.margin.y) / 2;
        float shift = center - (min + max) / 2;
        var rect = text.rectTransform.rect;
        float shiftX = 0;
        if (right - left <= rect.width - text.margin.x - text.margin.z + 0.01f)
            shiftX = Mathf.Clamp(0, rect.xMin + text.margin.x - left, rect.xMax - text.margin.z - right);
        for (int material = 0; material < info.materialCount; material++)
            for (int vertex = 0; vertex < info.meshInfo[material].vertexCount; vertex++)
            {
                info.meshInfo[material].vertices[vertex].y += shift;
                info.meshInfo[material].vertices[vertex].x += shiftX;
            }
        var top = new[] { float.NegativeInfinity, float.NegativeInfinity };
        var bottom = new[] { float.PositiveInfinity, float.PositiveInfinity };
        for (int i = 0; i < info.characterCount; i++)
        {
            ref var c = ref info.characterInfo[i];
            c.bottomLeft.y += shift; c.bottomRight.y += shift;
            c.topLeft.y += shift; c.topRight.y += shift; c.baseLine += shift;
            c.bottomLeft.x += shiftX; c.bottomRight.x += shiftX;
            c.topLeft.x += shiftX; c.topRight.x += shiftX;
            c.origin += shiftX; c.xAdvance += shiftX;
            if (c.isVisible && c.lineNumber < 2)
            {
                top[c.lineNumber] = Mathf.Max(top[c.lineNumber], c.topLeft.y);
                bottom[c.lineNumber] = Mathf.Min(bottom[c.lineNumber], c.bottomLeft.y);
            }
        }
        for (int line = 0; line < 2; line++)
        {
            if (float.IsInfinity(top[line]) || float.IsInfinity(bottom[line])) continue;
            info.lineInfo[line].ascender = top[line];
            info.lineInfo[line].descender = bottom[line];
            info.lineInfo[line].baseline += shift;
            info.lineInfo[line].lineExtents.min.x += shiftX;
            info.lineInfo[line].lineExtents.max.x += shiftX;
        }
        for (int i = 0; i < info.characterCount; i++)
        {
            ref var c = ref info.characterInfo[i];
            if (c.lineNumber < 2 && !float.IsInfinity(top[c.lineNumber]))
            {
                c.ascender = top[c.lineNumber];
                c.descender = bottom[c.lineNumber];
            }
        }
        // TMP's Geometry alignment centers its caret at y=0 even on the
        // second line. Use normal Middle alignment with ink-correct metadata.
        // Keep preferredHeight consistent too, so native input scrolling does
        // not move text whose two visible rows already fit inside the viewport.
        PreferredHeight?.SetValue(text, max - min);
        HeightDirty?.SetValue(text, false);
    }

    internal static bool TryChoose(TMP_Text text, AdvanceMetrics? metrics, int axisWidth, float maximum,
        float width, float height, float singleSize, float minimumGain, out Candidate result)
    {
        result = default;
        if (singleSize * minimumGain > maximum || text.text.IndexOfAny(new[] { '\n', '\r' }) >= 0) return false;
        float trial = Mathf.Min(maximum, singleSize * 1.9f);
        if (metrics != null && metrics.TryBalancedWidth(text.text, axisWidth, out var em))
            trial = Mathf.Min(maximum, width / em * 0.995f);
        if (trial < singleSize * minimumGain) return false;
        text.enableAutoSizing = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.verticalAlignment = VerticalAlignmentOptions.Middle;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            text.fontSize = trial;
            text.lineSpacing = 0;
            Runtime.RenderForLayout(text);
            var ink = GetInk(text);
            if (ink.Lines != 2 || float.IsInfinity(ink.Gap))
            {
                if (ink.Lines < 2) return false;
                trial *= 0.97f;
                if (trial < singleSize * minimumGain) return false;
                continue;
            }
            // Fit the actual glyph quads, including their SDF padding. Do not
            // mistake large font ascender/descender boxes for visible ink.
            float desiredGap = Mathf.Max(0.4f, trial * 0.06f);
            float spacing = Mathf.Clamp((desiredGap - ink.Gap) / trial * 100f, -80f, 10f);
            text.lineSpacing = spacing;
            Runtime.RenderForLayout(text);
            ink = GetInk(text);
            float fit = Mathf.Min(1, width / ink.Width, height / ink.Height);
            if (fit < 1)
            {
                trial *= fit * 0.985f;
                if (trial < singleSize * minimumGain) return false;
                text.fontSize = trial;
                Runtime.RenderForLayout(text);
                ink = GetInk(text);
            }
            if (ink.Lines == 2 && ink.Width <= width + 0.1f && ink.Height <= height + 0.1f &&
                ink.Gap >= Mathf.Max(0.2f, trial * 0.035f))
            {
                result = new Candidate(trial / maximum, spacing);
                return true;
            }
            trial *= 0.97f;
            if (trial < singleSize * minimumGain) return false;
        }
        return false;
    }
}
