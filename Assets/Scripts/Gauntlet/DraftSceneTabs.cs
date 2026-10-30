using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

/// <summary>
/// The tabs across the top of the Draft scene. Each tab owns one panel, and only one panel is up at a
/// time:
///   * Draft -- the hero, card and perk picks and milestone claims (OptionsParent). The scene opens on it
///     while anything is owed, and on Next Match otherwise. Once drafted it shows the draft-completed image;
///   * Next Match -- the upcoming battle's briefing (OpponentInfo). Drafting done, the scene switches to it;
///   * Statistics -- the run's stats. Opens on its Last Match page: the most recent battle's score
///     breakdown, shown as it stood when the battle ended (no count-up, it was seen at game over).
///
/// Switching slides the open panel off the left edge of the screen; once it is gone, the new one comes
/// in from the left to where it was authored. The tab bar itself never moves.
///
/// A tab set to cascade moves its panel's children (the pieces of its background) instead of the panel,
/// each starting a little after the one to its left on the way out -- the right-most leaves last -- and
/// after the one to its right on the way in, so the left-most arrives last. Pieces level with each other
/// still take turns, the upper one first out.
///
/// <see cref="DraftController"/> decides which tab is open and which can be clicked (only Draft while a
/// pick is owed, all of them after; Statistics not before a battle has been fought); this only shows and switches. Its
/// hover previews show another tab's panel for a moment with <see cref="Peek"/>, without switching.
/// </summary>
public class DraftSceneTabs : MonoBehaviour
{
    public enum TabKind
    {
        Draft,
        NextMatch,
        Statistics,
    }

    /// <summary>One tab: its button, and the panel it opens.</summary>
    [System.Serializable]
    private class Tab
    {
        public TabButton button;
        public RectTransform panel;
        [Tooltip("Slide the panel's children one after another, by their place left to right, instead of the panel as one.")]
        public bool cascade;

        // What moves: the panel itself, or its children when cascading.
        [System.NonSerialized] public RectTransform[] pieces;
        [System.NonSerialized] public Vector2[] homes;
    }

    [SerializeField] private Tab draft;
    [SerializeField] private Tab nextMatch;
    [SerializeField] private Tab statistics;

    [Header("Statistics: Last Match")]
    [SerializeField] private MatchStatsView lastMatchStats;

    [Header("Slide")]
    [SerializeField] private float exitDuration = 0.25f;
    [SerializeField] private Ease exitEase = Ease.Linear;
    [Tooltip("Pause between the old panel leaving the screen and the new one starting to come in.")]
    [SerializeField] private float enterGap = 0f;
    [SerializeField] private float enterDuration = 0.3f;
    [SerializeField] private Ease enterEase = Ease.Linear;
    [Tooltip("Extra distance past the screen's left edge a panel is parked at, so its shadow clears it too.")]
    [SerializeField] private float offscreenMargin = 50f;
    [Tooltip("Cascading tabs: how long after the first piece the last one starts moving.")]
    [SerializeField] private float cascadeSpread = 0.2f;

    private Sequence _slide;
    private bool _initialised;

    // The slide is tuned here only: sub-tabs (OpponentFightTabs) move with the same timing.
    public float ExitDuration => exitDuration;
    public Ease ExitEase => exitEase;
    public float EnterGap => enterGap;
    public float EnterDuration => enterDuration;
    public Ease EnterEase => enterEase;
    public float OffscreenMargin => offscreenMargin;
    public float CascadeSpread => cascadeSpread;

    public TabKind Current { get; private set; } = TabKind.Draft;

    /// <summary>True while a switch is still sliding.</summary>
    public bool IsSwitching => _slide != null && _slide.IsActive();

    private void Awake() => Init();

    private void Init()
    {
        if (_initialised) return;
        _initialised = true;

        foreach (var kind in Kinds)
        {
            var tab = Get(kind);
            if (tab == null) continue;

            if (tab.panel != null) CollectPieces(tab);
            if (tab.button != null) tab.button.Button.onClick.AddListener(() => Select(kind, animate: true));
        }

        RefreshButtons();
    }

    private void OnDisable() => FinishSlide();

    /// <summary>Which tab buttons respond.</summary>
    public void SetInteractable(bool draftTab, bool nextMatchTab, bool statisticsTab)
    {
        if (draft?.button != null) draft.button.Button.interactable = draftTab;
        if (nextMatch?.button != null) nextMatch.button.Button.interactable = nextMatchTab;
        if (statistics?.button != null) statistics.button.Button.interactable = statisticsTab;
    }

    /// <summary>
    /// Shows <paramref name="kind"/>'s panel in place of the open one, instantly and without switching
    /// tabs: a hover preview. <see cref="EndPeek"/> puts the open tab's panel back. Ignored mid-switch.
    /// </summary>
    public void Peek(TabKind kind)
    {
        if (!IsSwitching) ShowOnly(Get(kind));
    }

    public void EndPeek()
    {
        if (!IsSwitching) ShowOnly(Get(Current));
    }

    /// <summary>Opens <paramref name="kind"/>'s panel, sliding the open one out first when animated.</summary>
    public void Select(TabKind kind, bool animate)
    {
        Init();
        if (animate && kind == Current && !IsSwitching) return;

        FinishSlide();

        var from = Get(Current);
        var to = Get(kind);
        Current = kind;

        if (kind == TabKind.Statistics) FillStatistics();
        RefreshButtons();

        if (!animate || !Application.isPlaying || from == to)
        {
            ShowOnly(to);
            return;
        }

        _slide = DOTween.Sequence();
        float enterAt = enterGap;

        if (from?.panel != null && from.panel.gameObject.activeSelf)
        {
            // A slide inside the panel (its fight tabs) ends first: its pieces are about to move again.
            DOTween.Complete(from.panel, withCallbacks: true);
            float dx = OffscreenLeftOffset(from);
            float spread = from.cascade ? cascadeSpread : 0f;
            var order = PanelSlide.CascadeOrder(from.pieces);

            // Left-most piece first, so the right-most is the last to go.
            for (int i = 0; i < from.pieces.Length; i++)
                _slide.Insert(spread * order[i],
                    from.pieces[i].DOAnchorPos(from.homes[i] + new Vector2(dx, 0f), exitDuration).SetEase(exitEase));

            float gone = spread + exitDuration;
            _slide.InsertCallback(gone, () =>
            {
                from.panel.gameObject.SetActive(false);
                Home(from);
            });
            enterAt += gone;
        }

        if (to?.panel != null)
        {
            to.panel.gameObject.SetActive(true);
            float dx = OffscreenLeftOffset(to);
            float spread = to.cascade ? cascadeSpread : 0f;
            var order = PanelSlide.CascadeOrder(to.pieces);

            // Right-most piece first, so the left-most is the last to arrive.
            for (int i = 0; i < to.pieces.Length; i++)
            {
                to.pieces[i].anchoredPosition = to.homes[i] + new Vector2(dx, 0f);
                _slide.Insert(enterAt + spread * (1f - order[i]),
                    to.pieces[i].DOAnchorPos(to.homes[i], enterDuration).SetEase(enterEase));
            }
        }

        _slide.OnKill(() => _slide = null);
    }

    /// <summary>Jumps a running switch to its end, so every panel is either home and up, or hidden.</summary>
    private void FinishSlide()
    {
        if (_slide != null && _slide.IsActive()) _slide.Complete(withCallbacks: true);
        _slide = null;

        ShowOnly(Get(Current));
    }

    private void ShowOnly(Tab shown)
    {
        foreach (var kind in Kinds)
        {
            var tab = Get(kind);
            if (tab?.panel == null) continue;

            Home(tab);
            tab.panel.gameObject.SetActive(tab == shown);
        }
    }

    private void RefreshButtons()
    {
        foreach (var kind in Kinds)
        {
            var tab = Get(kind);
            if (tab?.button != null) tab.button.SetActive(Current == kind);
        }
    }

    /// <summary>The most recent battle's stats, as saved when it ended.</summary>
    private void FillStatistics()
    {
        var battles = GauntletRun.Battles;
        if (lastMatchStats == null || battles.Count == 0) return;

        lastMatchStats.Show(battles[battles.Count - 1].stats, animate: false);
    }

    /// <summary>
    /// Records what moves when <paramref name="tab"/> slides, and where each piece rests. Turns in a
    /// cascade are dealt at each slide, among the pieces showing then.
    /// </summary>
    private static void CollectPieces(Tab tab)
    {
        var pieces = new List<RectTransform>();
        if (tab.cascade)
            foreach (Transform child in tab.panel)
                if (child is RectTransform rect) pieces.Add(rect);
        if (pieces.Count == 0) pieces.Add(tab.panel);

        tab.pieces = pieces.ToArray();
        tab.homes = new Vector2[pieces.Count];
        for (int i = 0; i < pieces.Count; i++) tab.homes[i] = pieces[i].anchoredPosition;
    }

    private static void Home(Tab tab)
    {
        if (tab.pieces == null) return;
        for (int i = 0; i < tab.pieces.Length; i++)
            if (tab.pieces[i] != null) tab.pieces[i].anchoredPosition = tab.homes[i];
    }

    /// <summary>
    /// How far left <paramref name="tab"/>'s pieces move, all alike, for the right-most of them to clear
    /// the screen's left edge. Measured with the pieces at home.
    /// </summary>
    private float OffscreenLeftOffset(Tab tab) => PanelSlide.OffscreenLeftOffset(tab.pieces, offscreenMargin);

    private static readonly TabKind[] Kinds = { TabKind.Draft, TabKind.NextMatch, TabKind.Statistics };

    private Tab Get(TabKind kind) => kind switch
    {
        TabKind.Draft => draft,
        TabKind.Statistics => statistics,
        _ => nextMatch,
    };
}
