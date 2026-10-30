using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Shows the shared <see cref="UITooltip"/> with <see cref="message"/> while the pointer is over this
/// element. Sits on the element itself (a button, an icon, ...) so the text lives next to the thing
/// it describes rather than in a lookup table somewhere else.
/// </summary>
public class UITooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [TextArea(2, 4)]
    [SerializeField] private string message;

    [Tooltip("Optional fixed spot: the tooltip is centered there instead of being placed beside this " +
             "element. For an element spawned at runtime the spot is handed over in code (see " +
             "PassiveSelectionController), since a prefab cannot reference a scene object.")]
    [SerializeField] private Transform point;

    /// <summary>The panel this element showed its text on, so the exit hides that one.</summary>
    private UITooltip _shown;

    public string Message
    {
        get => message;
        set => message = value;
    }

    /// <summary>Where the tooltip shows. Null = beside this element.</summary>
    public Transform Point
    {
        get => point;
        set => point = value;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        _shown = UITooltip.For(transform);
        if (_shown == null) return;

        if (point != null) _shown.ShowAt(message, point);
        else _shown.Show(message, (RectTransform)transform);
    }

    public void OnPointerExit(PointerEventData eventData) => HideShown();

    // A disabled object never receives OnPointerExit, so the tooltip would otherwise stay on screen
    // after the element it describes goes away.
    void OnDisable() => HideShown();

    private void HideShown()
    {
        var tooltip = _shown != null ? _shown : UITooltip.Instance;
        if (tooltip != null) tooltip.Hide();
        _shown = null;
    }
}
