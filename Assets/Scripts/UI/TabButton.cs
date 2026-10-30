using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// One tab in a tab bar: a round button with its label beside it. While the tab is the open one its
/// active image (the filled dot) shows; otherwise the dot is off. Whoever owns the bar decides which tab
/// is active; this only shows it.
///
/// The label takes one of four colours, the first that applies: Disabled (the button can't be clicked),
/// Selected (the open tab), Hovered (pointer over it), Normal.
///
/// The colours live on the TabButton prefab, so changing them there restyles every tab.
/// </summary>
[RequireComponent(typeof(Button))]
public class TabButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private GameObject activeImage;
    [SerializeField] private TMP_Text label;

    [Header("Label colours")]
    [FormerlySerializedAs("inactiveTextColor")]
    [SerializeField] private Color normalTextColor = new Color(0.55f, 0.55f, 0.55f, 1f);
    [SerializeField] private Color hoveredTextColor = new Color(0.85f, 0.85f, 0.85f, 1f);
    [FormerlySerializedAs("activeTextColor")]
    [SerializeField] private Color selectedTextColor = Color.white;
    [SerializeField] private Color disabledTextColor = new Color(0.35f, 0.35f, 0.35f, 1f);

    [Tooltip("State the tab starts in, and the state it shows in edit mode.")]
    [SerializeField] private bool isActive;

    private Button _button;
    private bool _hovered;
    private bool _interactable;

    public Button Button
    {
        get
        {
            if (_button == null) _button = GetComponent<Button>();
            return _button;
        }
    }

    public bool IsActive => isActive;

    public void SetLabel(string text)
    {
        if (label != null) label.text = text;
    }

    public void SetActive(bool active)
    {
        isActive = active;
        Apply();
    }

    private void Awake() => Apply();

    private void OnDisable() => _hovered = false;

    // Interactable is flipped on the Button by the bar's owner, with no event to hear it by.
    private void LateUpdate()
    {
        if (Button.IsInteractable() != _interactable) Apply();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        _hovered = true;
        Apply();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _hovered = false;
        Apply();
    }

    private void Apply()
    {
        _interactable = Button.IsInteractable();

        if (activeImage != null) activeImage.SetActive(isActive);
        if (label != null) label.color = LabelColor;
    }

    private Color LabelColor =>
        !_interactable ? disabledTextColor
        : isActive ? selectedTextColor
        : _hovered ? hoveredTextColor
        : normalTextColor;

#if UNITY_EDITOR
    // SetActive is not allowed inside OnValidate itself, so the edit-mode preview waits a tick.
    private void OnValidate()
    {
        UnityEditor.EditorApplication.delayCall += () =>
        {
            // Prefab assets are left alone: only scene and prefab-stage copies preview the state.
            if (this == null || UnityEditor.EditorUtility.IsPersistent(this)) return;
            Apply();
        };
    }
#endif
}
