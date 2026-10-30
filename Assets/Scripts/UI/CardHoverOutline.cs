using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Hover feedback for a card view: lights the hover outline, and optionally swells the card while the
/// pointer is over it.
///
/// For cards in hand this job belongs to <see cref="CardController"/>, whose OnPointerEnter/OnPointerExit
/// are already wired through the Card prefab's EventTrigger and also drive the peek. This component is for
/// card views that have neither — notably the display-only options spawned into the card selection panel,
/// which are CardPreview Variants (Button + CardModal + CardView and nothing else).
/// </summary>
[RequireComponent(typeof(CardView))]
public class CardHoverOutline : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Tooltip("Scale multiplier while hovered. 1 leaves the card's size alone.")]
    [SerializeField] private float hoverScale = 1f;

    [Tooltip("How long the swell takes, in each direction.")]
    [SerializeField] private float scaleDuration = 0.12f;

    private CardView _view;
    private Vector3 _restScale = Vector3.one;

    private CardView View => _view != null ? _view : (_view = GetComponent<CardView>());

    /// <summary>
    /// Set the swell and the size to return to. Callers pass the rest scale explicitly rather than letting
    /// this read transform.localScale, because a spawner may start the card at zero for a reveal tween —
    /// capturing that would leave the card stuck at nothing once the pointer left.
    /// </summary>
    public void ConfigureHoverScale(float scale, Vector3 restScale)
    {
        hoverScale = scale;
        _restScale = restScale;
    }

    /// <summary>
    /// The pointer has come onto the card (true) or left it (false). Also raised with false when the
    /// component is disabled under the pointer, so a listener never stays stuck on.
    /// </summary>
    public event System.Action<bool> HoverChanged;

    public void OnPointerEnter(PointerEventData eventData)
    {
        View.SetHoveredOutline(true);
        ScaleTo(_restScale * hoverScale);

        var modal = GetComponent<CardModal>();
        if (modal != null)
            KeywordTooltip.Show(modal.desc, (RectTransform)transform, this);

        HoverChanged?.Invoke(true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        View.SetHoveredOutline(false);
        ScaleTo(_restScale);
        KeywordTooltip.Hide(this);
        HoverChanged?.Invoke(false);
    }

    private void ScaleTo(Vector3 target)
    {
        if (Mathf.Approximately(hoverScale, 1f)) return;

        // Kills the spawner's reveal tween if it is still running, so the two can't fight over localScale.
        // Worst case the reveal is cut short by the player hovering, which is what they asked for anyway.
        transform.DOKill();
        transform.DOScale(target, scaleDuration);
    }

    private void OnDisable()
    {
        // The pointer never "exits" a card that is destroyed or hidden underneath it, so reset on the way
        // out — otherwise a pooled/reused view would come back lit and still swollen.
        View.SetHoveredOutline(false);
        KeywordTooltip.Hide(this);
        HoverChanged?.Invoke(false);

        if (Mathf.Approximately(hoverScale, 1f)) return;
        transform.DOKill();
        transform.localScale = _restScale;
    }
}
