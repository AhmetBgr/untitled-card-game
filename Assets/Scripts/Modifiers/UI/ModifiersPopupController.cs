using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The modifiers popup. Generic over modes: on open it builds one row per modifier in the active
/// <see cref="MatchModifiers.Mode"/> (a switch for a bool, a slider for a number), dealt down the
/// panel's columns in turn, so another mode's setup screen drops in the same prefab and gets its
/// own list.
///
/// Open/close follows the menu's panels (SetActive + a backdrop that swallows clicks), with no
/// animation either way; Escape closes it.
/// </summary>
public class ModifiersPopupController : MonoBehaviour
{
    [Tooltip("The part that is shown and hidden. Empty = this object.")]
    [SerializeField] private GameObject panel;

    [Header("Rows")]
    [Tooltip("The columns rows are dealt into, filled top to bottom and then left to right. Empty = the single rows container below.")]
    [SerializeField] private RectTransform[] columns;

    [Tooltip("Fallback for a panel with no columns wired: every row goes in here.")]
    [SerializeField] private RectTransform rowsContainer;

    [SerializeField] private ModifierToggleRowView toggleRowPrefab;
    [SerializeField] private ModifierSliderRowView sliderRowPrefab;
    [Tooltip("The Weather modifier's row: a short slider and room for a weather's name. Empty = the slider row.")]
    [SerializeField] private ModifierSliderRowView weatherRowPrefab;

    [Header("Chrome")]
    [SerializeField] private TMP_Text titleLabel;
    [SerializeField] private Button resetButton;
    [SerializeField] private Button closeButton;

    private readonly List<ModifierRowView> rows = new List<ModifierRowView>();
    private GameModeSO builtFor;
    private bool opening;

    public bool IsOpen => Panel.activeSelf;

    private GameObject Panel => panel != null ? panel : gameObject;

    void Awake()
    {
        if (resetButton != null) resetButton.onClick.AddListener(MatchModifiers.ResetToDefaults);
        if (closeButton != null) closeButton.onClick.AddListener(Close);

        // Closed until asked, however the prefab was left. Not when Open() is what woke us: the panel
        // starts inactive in the scene, so the first open is what runs Awake, and closing here would
        // swallow that first click.
        if (!opening) Panel.SetActive(false);
    }

    void OnEnable() => MatchModifiers.Changed += RefreshRows;
    void OnDisable() => MatchModifiers.Changed -= RefreshRows;

    void Update()
    {
        if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        Build(MatchModifiers.Mode);

        opening = true;
        Panel.SetActive(true);
        opening = false;
    }

    public void Close()
    {
        if (!IsOpen) return;

        Panel.SetActive(false);
    }

    private void Build(GameModeSO mode)
    {
        if (mode == builtFor && rows.Count > 0)
        {
            RefreshRows();
            return;
        }

        foreach (var row in rows)
            if (row != null) Destroy(row.gameObject);
        rows.Clear();
        builtFor = mode;

        if (titleLabel != null)
            titleLabel.text = mode != null && !string.IsNullOrEmpty(mode.displayName) ? $"{mode.displayName} Modifiers" : "Modifiers";

        if (mode == null) return;

        List<RectTransform> targets = RowParents();
        if (targets.Count == 0) return;

        // Only the modifiers that can actually be shown, so the split across columns stays even when
        // the list holds a blank slot or a kind this panel has no row prefab for.
        var shown = new List<GameModifierSO>();
        foreach (GameModifierSO modifier in mode.modifiers)
            if (modifier != null && PrefabFor(modifier) != null) shown.Add(modifier);

        // Dealt down each column in turn rather than across, so a list that groups its modifiers keeps
        // those groups together. The last column takes the remainder when the count does not divide.
        int perColumn = Mathf.CeilToInt(shown.Count / (float)targets.Count);

        for (int i = 0; i < shown.Count; i++)
        {
            GameModifierSO modifier = shown[i];
            RectTransform parent = targets[Mathf.Min(i / Mathf.Max(perColumn, 1), targets.Count - 1)];

            ModifierRowView row = Instantiate(PrefabFor(modifier), parent);
            row.name = $"Row_{modifier.id}";
            row.Bind(modifier);
            rows.Add(row);
        }
    }

    private ModifierRowView PrefabFor(GameModifierSO modifier)
    {
        if (modifier.Kind == ModifierValueKind.Bool) return toggleRowPrefab;
        if (modifier is WeatherModifierSO && weatherRowPrefab != null) return weatherRowPrefab;
        return sliderRowPrefab;
    }

    /// <summary>The wired columns, or the single container for a panel built before columns existed.</summary>
    private List<RectTransform> RowParents()
    {
        var parents = new List<RectTransform>();

        if (columns != null)
            foreach (RectTransform column in columns)
                if (column != null) parents.Add(column);

        if (parents.Count == 0 && rowsContainer != null) parents.Add(rowsContainer);

        return parents;
    }

    private void RefreshRows()
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] != null) rows[i].Refresh();
    }
}
