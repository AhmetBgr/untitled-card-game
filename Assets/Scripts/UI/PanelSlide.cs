using System.Collections.Generic;
using UnityEngine;

/// <summary>Geometry for sliding UI off the edges of the screen, whole or as a cascade of pieces.</summary>
public static class PanelSlide
{
    private static readonly Vector3[] Corners = new Vector3[4];

    /// <summary>
    /// How far left, in their parent's units, <paramref name="rects"/> (siblings, at their resting places)
    /// must move for the right-most of them to sit <paramref name="margin"/> past the screen's left edge.
    /// </summary>
    public static float OffscreenLeftOffset(IReadOnlyList<RectTransform> rects, float margin)
    {
        if (rects == null || rects.Count == 0 || rects[0] == null) return 0f;

        var parent = rects[0].parent;
        var canvas = rects[0].GetComponentInParent<Canvas>(true)?.rootCanvas;
        if (parent == null || canvas == null) return 0f;

        ((RectTransform)canvas.transform).GetWorldCorners(Corners);
        float screenLeft = parent.InverseTransformPoint(Corners[0]).x;

        float right = float.MinValue;
        foreach (var rect in rects)
        {
            if (rect == null) continue;

            rect.GetWorldCorners(Corners);
            right = Mathf.Max(right, parent.InverseTransformPoint(Corners[3]).x);
        }

        return screenLeft - right - margin;
    }

    /// <summary>
    /// How far right, in their parent's units, <paramref name="rects"/> (siblings, at their resting places)
    /// must move for the left-most of them to sit <paramref name="margin"/> past the screen's right edge.
    /// </summary>
    public static float OffscreenRightOffset(IReadOnlyList<RectTransform> rects, float margin)
    {
        if (rects == null || rects.Count == 0 || rects[0] == null) return 0f;

        var parent = rects[0].parent;
        var canvas = rects[0].GetComponentInParent<Canvas>(true)?.rootCanvas;
        if (parent == null || canvas == null) return 0f;

        ((RectTransform)canvas.transform).GetWorldCorners(Corners);
        float screenRight = parent.InverseTransformPoint(Corners[3]).x;

        float left = float.MaxValue;
        foreach (var rect in rects)
        {
            if (rect == null) continue;

            rect.GetWorldCorners(Corners);
            left = Mathf.Min(left, parent.InverseTransformPoint(Corners[0]).x);
        }

        return screenRight - left + margin;
    }

    /// <summary>
    /// How far up, in its parent's units, <paramref name="rect"/> (at its resting place) must move for its
    /// bottom edge to sit <paramref name="margin"/> past the screen's top edge.
    /// </summary>
    public static float OffscreenTopOffset(RectTransform rect, float margin)
    {
        if (rect == null) return 0f;

        var parent = rect.parent;
        var canvas = rect.GetComponentInParent<Canvas>(true)?.rootCanvas;
        if (parent == null || canvas == null) return 0f;

        ((RectTransform)canvas.transform).GetWorldCorners(Corners);
        float screenTop = parent.InverseTransformPoint(Corners[1]).y;

        rect.GetWorldCorners(Corners);
        float bottom = parent.InverseTransformPoint(Corners[0]).y;

        return screenTop - bottom + margin;
    }

    /// <summary>
    /// Each rect's turn in a left-to-right cascade, spread evenly from 0 (first) to 1 (last): by its
    /// centre left to right, and top to bottom among rects level with each other, so no two share a turn.
    /// Hidden rects take no turn (they get 0), so they leave no pause in it. <paramref name="rightToLeft"/>
    /// runs it from the right instead, still top to bottom among level rects.
    /// </summary>
    public static float[] CascadeOrder(IReadOnlyList<RectTransform> rects, bool rightToLeft = false)
    {
        var centres = new Vector2[rects.Count];
        var ranked = new List<int>();
        for (int i = 0; i < rects.Count; i++)
        {
            if (rects[i] == null || !rects[i].gameObject.activeSelf) continue;

            rects[i].GetWorldCorners(Corners);
            centres[i] = (Corners[0] + Corners[2]) * 0.5f;
            ranked.Add(i);
        }

        ranked.Sort( (a, b) =>
        {
            int byX = rightToLeft ? centres[b].x.CompareTo(centres[a].x) : centres[a].x.CompareTo(centres[b].x);
            return byX != 0 ? byX : centres[b].y.CompareTo(centres[a].y);
        });

        int count = ranked.Count;
        var order = new float[rects.Count];
        for (int turn = 0; turn < count; turn++)
            order[ranked[turn]] = count > 1 ? turn / (float)(count - 1) : 0f;
        return order;
    }
}
