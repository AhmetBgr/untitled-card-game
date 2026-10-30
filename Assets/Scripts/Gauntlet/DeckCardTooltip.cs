using UnityEngine;

/// <summary>
/// Shows the card of a hovered deck list entry as a tooltip beside the list: the run's deck beside the
/// draft and the end-of-match reward picks. Sits on the deck panel; its <see cref="CardButtonHandler"/>
/// entries find it above them when there is no <see cref="DeckPanelController"/> to preview through.
///
/// Both versions of the card are shown side by side, the normal one and its forged (upgraded) one,
/// whichever of the two the deck holds -- the way the deck builder's preview does. A card with no other
/// version shows on its own.
///
/// The previews are display-only cards (CardPreview Variant), built on first hover under this panel so
/// they share the panel's scale, each with a canvas of its own sorted above the root canvas so it draws
/// over everything. Those canvases have no raycaster, so the previews never take the hover from the entry.
/// </summary>
public class DeckCardTooltip : MonoBehaviour
{
    [Tooltip("Display-only card prefab drawn as the tooltip (CardPreview Variant).")]
    [SerializeField] private GameObject cardPrefab;
    [SerializeField] private float cardScale = 0.8f;
    [Tooltip("Space between the list entry and the cards, in this panel's units.")]
    [SerializeField] private float gap = 12f;
    [Tooltip("Space between the normal card and its forged version, in this panel's units: enough for " +
             "the stat badges that hang off the cards' corners to stay clear of each other.")]
    [SerializeField] private float pairGap = 24f;

    [Header("Backdrop")]
    [Tooltip("Sliced panel drawn behind the cards, the way the perk tooltip is boxed. None for bare cards.")]
    [SerializeField] private Sprite backdropSprite;
    [SerializeField] private Color backdropColor = Color.white;
    [Tooltip("How far the panel reaches past the cards' visible frames, in this panel's units.")]
    [SerializeField] private float backdropPadding = 24f;

    [Header("Placement")]
    [Tooltip("The deck list's visible box, which the whole tooltip (backdrop included) keeps gap clear " +
             "of. The hovered entry itself when empty.")]
    [SerializeField] private RectTransform keepClearOf;
    [Tooltip("The least the tooltip stays inside the screen's edges, in this panel's units.")]
    [SerializeField] private float screenMargin = 8f;

    /// <summary>One display-only card of the tooltip.</summary>
    private class Preview
    {
        public RectTransform rect;
        public CardModal modal;
        public CardView view;
    }

    private Preview _normal;
    private Preview _forged;
    private RectTransform _backdrop;
    private Transform _keywordPoint;

    public void Show(CardSO card, RectTransform entry)
    {
        if (card == null || entry == null || !EnsurePreviews()) return;

        Versions(card, out CardSO normal, out CardSO forged);

        Fill(_normal, normal);
        Fill(_forged, forged);
        Place(entry, forged != null);

        if (_backdrop != null) CardTooltipBackdrop.Fit(_backdrop, ShownVisuals(), backdropPadding);
        Settle(entry);

        // Hung under the cards (under their panel, when they have one): beside them, the keywords would
        // land on the list the pointer is reading. One stack for both versions, like the deck builder's.
        _keywordPoint.position = KeywordPointFor(_backdrop != null ? _backdrop : Visual(_normal));
        string desc = forged == null ? normal.desc : normal.desc + "\n" + forged.desc;
        KeywordTooltip.Show(desc, _normal.rect, this, _keywordPoint);
    }

    private static RectTransform Visual(Preview preview) =>
        preview.view.VisualRect != null ? preview.view.VisualRect : preview.rect;

    /// <summary>The visible frames of the cards showing.</summary>
    private RectTransform[] ShownVisuals() =>
        _forged.rect.gameObject.activeSelf ? new[] { Visual(_normal), Visual(_forged) } : new[] { Visual(_normal) };

    /// <summary>
    /// The exact spot, once the cards are drawn and boxed: the whole tooltip gap clear of the deck's box
    /// (so a padded backdrop never runs into it), on the side facing the middle of the screen, and
    /// wholly inside the screen top to bottom.
    /// </summary>
    private void Settle(RectTransform entry)
    {
        var self = (RectTransform)transform;
        var root = GetComponentInParent<Canvas>().rootCanvas;

        Rect bounds = CardTooltipBackdrop.LocalBounds(_backdrop != null ? new[] { _backdrop } : ShownVisuals(), self);
        Rect box = CardTooltipBackdrop.LocalBounds(new[] { keepClearOf != null ? keepClearOf : entry }, self);
        Rect screen = CardTooltipBackdrop.LocalBounds(new[] { (RectTransform)root.transform }, self);

        bool toLeft = box.center.x > screen.center.x;
        float dx = toLeft ? box.xMin - gap - bounds.xMax : box.xMax + gap - bounds.xMin;
        float dy = CardTooltipBackdrop.ShiftInside(bounds, screen, screenMargin).y;

        var delta = new Vector3(dx, dy, 0f);
        _normal.rect.localPosition += delta;
        _forged.rect.localPosition += delta;
        if (_backdrop != null) _backdrop.localPosition += delta;
    }

    /// <summary>
    /// The two versions of <paramref name="card"/>: the normal one and its forged one. A forged card in
    /// the deck shows its normal version first, like a normal card does; a card with no other version
    /// has no forged one.
    /// </summary>
    public static void Versions(CardSO card, out CardSO normal, out CardSO forged)
    {
        if (card.isUpgraded && card.normalVersion != null)
        {
            normal = card.normalVersion;
            forged = card;
            return;
        }

        normal = card;
        forged = card.isUpgraded ? null : card.upgradedVersion;
    }

    private static void Fill(Preview preview, CardSO card)
    {
        if (card == null)
        {
            preview.rect.gameObject.SetActive(false);
            return;
        }

        preview.modal.UpdateModal(card, null, true);
        preview.view.UpdateView(preview.modal);
        preview.rect.gameObject.SetActive(true);
    }

    /// <summary>The bottom-left corner of <paramref name="rect"/>, a little below it.</summary>
    private Vector3 KeywordPointFor(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return corners[0] + (corners[0] - corners[1]).normalized * (gap * transform.lossyScale.y);
    }

    public void Hide()
    {
        if (_normal != null) _normal.rect.gameObject.SetActive(false);
        if (_forged != null) _forged.rect.gameObject.SetActive(false);
        if (_backdrop != null) _backdrop.gameObject.SetActive(false);
        KeywordTooltip.Hide(this);
    }

    private void OnDisable() => Hide();

    /// <summary>
    /// A first placing: beside the entry and level with it, the pair normal then forged, read left to
    /// right. <see cref="Settle"/> then moves the whole tooltip to its exact spot.
    /// </summary>
    private void Place(RectTransform entry, bool pair)
    {
        var self = (RectTransform)transform;
        Rect entryRect = CardTooltipBackdrop.LocalBounds(new[] { entry }, self);

        Vector2 size = Vector2.Scale(_normal.rect.rect.size, _normal.rect.localScale);
        float step = pair ? (size.x + pairGap) * 0.5f : 0f;
        float x = entryRect.xMin - gap - (pair ? size.x + step : size.x * 0.5f);

        _normal.rect.localPosition = new Vector3(x, entryRect.center.y, 0f);
        _forged.rect.localPosition = new Vector3(x + step * 2f, entryRect.center.y, 0f);
    }

    private bool EnsurePreviews()
    {
        if (_normal != null) return true;
        if (cardPrefab == null) return false;

        _normal = BuildPreview("CardTooltip");
        if (_normal == null) return false;

        _forged = BuildPreview("CardTooltip (Forged)");
        if (_forged == null)
        {
            Destroy(_normal.rect.gameObject);
            _normal = null;
            return false;
        }

        if (backdropSprite != null)
        {
            // Just under the cards' canvases, so it draws over everything else but behind them.
            var root = GetComponentInParent<Canvas>().rootCanvas;
            _backdrop = CardTooltipBackdrop.Build(transform, "CardTooltipBackdrop", backdropSprite, backdropColor, root, root.sortingOrder + 99);
        }

        _keywordPoint = new GameObject("KeywordTooltipPoint", typeof(RectTransform)).transform;
        _keywordPoint.SetParent(transform, false);
        return true;
    }

    private Preview BuildPreview(string name)
    {
        var go = Instantiate(cardPrefab, transform, false);
        go.name = name;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.localScale = Vector3.one * cardScale;
        var modal = go.GetComponent<CardModal>();
        var view = go.GetComponent<CardView>();
        if (modal == null || view == null)
        {
            Debug.LogWarning("DeckCardTooltip: the card prefab needs a CardModal and a CardView.");
            Destroy(go);
            return null;
        }

        var root = GetComponentInParent<Canvas>().rootCanvas;
        var canvas = go.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingLayerID = root.sortingLayerID;
        canvas.sortingOrder = root.sortingOrder + 100;

        var group = go.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        go.SetActive(false);
        return new Preview { rect = rect, modal = modal, view = view };
    }
}

/// <summary>
/// The sliced panel drawn behind a card tooltip, the way the perk tooltip is boxed: built once on a
/// canvas of its own (sorted just under the cards', over everything else, no raycasts), then fitted
/// round whatever cards are showing.
/// </summary>
public static class CardTooltipBackdrop
{
    public static RectTransform Build(Transform parent, string name, Sprite sprite, Color color, Canvas root, int sortingOrder)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        go.transform.SetParent(parent, false);

        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);

        var image = go.GetComponent<UnityEngine.UI.Image>();
        image.sprite = sprite;
        image.type = UnityEngine.UI.Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;

        var canvas = go.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        if (root != null)
        {
            canvas.sortingLayerID = root.sortingLayerID;
            canvas.sortingOrder = sortingOrder;
        }

        var group = go.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        go.SetActive(false);
        return rect;
    }

    /// <summary>
    /// Sizes and places <paramref name="backdrop"/> to enclose <paramref name="visuals"/> (the cards'
    /// visible frames, in world space) with <paramref name="padding"/> round them, in the backdrop's
    /// parent's units. Nothing to enclose hides it.
    /// </summary>
    public static void Fit(RectTransform backdrop, RectTransform[] visuals, float padding)
    {
        var parent = (RectTransform)backdrop.parent;
        Rect bounds = LocalBounds(visuals, parent);
        if (bounds.width <= 0f && bounds.height <= 0f)
        {
            backdrop.gameObject.SetActive(false);
            return;
        }

        backdrop.localPosition = (Vector3)bounds.center;
        backdrop.sizeDelta = bounds.size + Vector2.one * (padding * 2f);
        backdrop.gameObject.SetActive(true);
    }

    /// <summary>
    /// The rect that encloses <paramref name="rects"/> (those active in the hierarchy), in the local
    /// space of <paramref name="space"/>. An empty rect at the origin when none is active.
    /// </summary>
    public static Rect LocalBounds(RectTransform[] rects, RectTransform space)
    {
        var corners = new Vector3[4];
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        bool any = false;

        foreach (var rect in rects)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy) continue;
            rect.GetWorldCorners(corners);
            foreach (var corner in corners)
            {
                Vector2 local = space.InverseTransformPoint(corner);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }
            any = true;
        }

        return any ? Rect.MinMaxRect(min.x, min.y, max.x, max.y) : Rect.zero;
    }

    /// <summary>
    /// How far to move <paramref name="bounds"/> for it to lie inside <paramref name="area"/> with
    /// <paramref name="margin"/> to spare: nothing when it already does. Something too big to fit is
    /// pinned to the area's left and bottom edges.
    /// </summary>
    public static Vector2 ShiftInside(Rect bounds, Rect area, float margin)
    {
        var delta = Vector2.zero;
        if (bounds.xMax > area.xMax - margin) delta.x = area.xMax - margin - bounds.xMax;
        if (bounds.xMin + delta.x < area.xMin + margin) delta.x = area.xMin + margin - bounds.xMin;
        if (bounds.yMax > area.yMax - margin) delta.y = area.yMax - margin - bounds.yMax;
        if (bounds.yMin + delta.y < area.yMin + margin) delta.y = area.yMin + margin - bounds.yMin;
        return delta;
    }
}
