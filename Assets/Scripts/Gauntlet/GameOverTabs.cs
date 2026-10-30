using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

/// <summary>
/// The tabs across the top of the end-of-match panel, so a finished battle can be read against the whole
/// run -- and the next battle started -- without leaving for the Draft scene. One tab owns one panel, and
/// only one panel is up at a time:
///   * Result -- the battle just fought: its score, the run's XP track filling with it, and the rewards
///     the win paid out (<see cref="BattleRewardsPanel"/>);
///   * Draft -- the run's hero and deck as the rewards have left them;
///   * Statistics -- the run's stats, opening on the battle just fought;
///   * Hero Info -- the run's hero and its drafted perks;
///   * Battles -- the run's encounters, any of them picked off the battle track (<see cref="GameOverNextMatch"/>).
///
/// The panel opens on <see cref="openOn"/>, with the bar itself dropping in from above the screen as the
/// outcome title rises and fades (<see cref="PlayEntrance"/>, played by <see cref="GameOverPanelIntro"/>).
/// After that the bar never moves. Switching slides the open panel off the left edge of the screen and
/// brings the new one in behind it -- the same slide the Draft scene's own tabs make
/// (<see cref="DraftSceneTabs"/>), down to the cascade that moves a panel's pieces one after another by
/// their place left to right.
///
/// A tab marked keepAlive is parked off screen rather than switched off when another tab opens, because it
/// is still doing something: the Result panel goes on tallying the battle's score and filling the XP track
/// behind whatever the player is reading, and an inactive GameObject would stop dead halfway through. Off
/// screen is out of the raycaster's reach, so nothing on a parked panel can be clicked from where it sits.
///
/// A tab with nothing authored under it yet leaves its button dead rather than opening a blank panel, so
/// the bar can be built out one panel at a time.
///
/// Outside a run there is nothing to tab through: the bar hides itself and the panel shows
/// <see cref="fallbackOutsideRun"/> alone, which is the plain end-of-match panel Quick Play has always had.
/// </summary>
public class GameOverTabs : MonoBehaviour
{
    public enum TabKind
    {
        Result,
        Draft,
        Statistics,
        HeroInfo,
        Battles,
    }

    /// <summary>One tab: its button, and the panel it opens.</summary>
    [System.Serializable]
    private class Tab
    {
        public TabKind kind;
        public TabButton button;

        [Tooltip("The panel this tab opens. A panel that is not itself a RectTransform (a plain grouping " +
                 "object) slides its children instead.")]
        public GameObject panel;

        [Tooltip("Slide the panel's children one after another, by their place left to right, instead of " +
                 "the panel as one.")]
        public bool cascade;

        [Tooltip("Park the panel off screen instead of switching it off, for a panel that must keep " +
                 "running while another tab is up -- the Result panel, still tallying the battle.")]
        public bool keepAlive;

        [Tooltip("Split the pieces in two: those listed in Right Side leave and come back by the right " +
                 "edge, the rest by the left edge, each side cascading outer-most first out and last in.")]
        public bool splitSides;

        [Tooltip("With Split Sides on, the pieces that go right. Listed rather than worked out from where " +
                 "they sit: a piece's rect can reach well past what it shows (the rewards column's spans " +
                 "most of the screen).")]
        public RectTransform[] rightSide;

        [Tooltip("Faded out as the tab leaves and back in as it comes, outside the slide: the outcome " +
                 "title over Result.")]
        public GameObject[] fadeAlong;

        // What moves: the panel itself, or its children when cascading or when it is not a RectTransform.
        [System.NonSerialized] public RectTransform[] pieces;
        [System.NonSerialized] public Vector2[] homes;
        [System.NonSerialized] public bool parked;
    }

    [SerializeField] private Tab[] tabs;

    [Header("Opening tab")]
    [SerializeField] private TabKind openOn = TabKind.Draft;

    [Tooltip("Shown on its own, with the bar hidden, outside a Forged in Battle run.")]
    [SerializeField] private TabKind fallbackOutsideRun = TabKind.Result;

    [Header("Bar")]
    [Tooltip("The row of tab buttons, which drops in from above the screen. Defaults to this object.")]
    [SerializeField] private RectTransform bar;
    [Tooltip("How long after the panel's entrance starts the bar begins to drop.")]
    [SerializeField] private float barDropDelay = 0.6f;
    [SerializeField] private float barDropDuration = 0.5f;
    [SerializeField] private Ease barDropEase = Ease.OutCubic;
    [Tooltip("Extra distance past the screen's top edge the bar waits at, so its shadow clears it too.")]
    [SerializeField] private float barOffscreenMargin = 30f;
    [Tooltip("What is put away with the bar during the run's opening draft: the whole top buttons " +
             "panel. Defaults to the bar.")]
    [SerializeField] private RectTransform barPanel;

    [Header("Opening tab")]
    [Tooltip("How long after the outcome title starts rising the opening tab's panel slides in, so it " +
             "arrives once the title is out of its way.")]
    [SerializeField] private float panelEnterDelay = 0.55f;

    [Header("Panels")]
    [Tooltip("The Battles panel, refreshed as its tab opens. Optional.")]
    [SerializeField] private GameOverNextMatch battles;

    [Tooltip("The Statistics panel's stats view, filled with the battle just fought. Optional.")]
    [SerializeField] private MatchStatsView lastMatchStats;

    [Header("Slide")]
    [SerializeField] private float exitDuration = 0.5f;
    [SerializeField] private Ease exitEase = Ease.OutExpo;
    [Tooltip("Pause between the old panel leaving the screen and the new one starting to come in.")]
    [SerializeField] private float enterGap = 0f;
    [SerializeField] private float enterDuration = 0.6f;
    [SerializeField] private Ease enterEase = Ease.OutExpo;
    [Tooltip("Extra distance past the screen's left edge a panel is parked at, so its shadow clears it too.")]
    [SerializeField] private float offscreenMargin = 50f;
    [Tooltip("Cascading tabs: how long after the first piece the last one starts moving.")]
    [SerializeField] private float cascadeSpread = 0.2f;

    private static GameOverTabs _instance;

    private Sequence _slide;
    private Tween _barDrop;
    private Vector2 _barHome;
    private bool _interactable = true;
    private bool _initialised;

    /// <summary>The tab the next opening uses instead of openOn, once: the run's opening draft.</summary>
    private TabKind? _openOnce;

    /// <summary>While set, the only tab that can be used, with the bar put away.</summary>
    private TabKind? _lockedTo;

    /// <summary>Tabs shut on their own, with the rest open: Result before the run has fought a battle.</summary>
    private readonly HashSet<TabKind> _locked = new HashSet<TabKind>();

    public TabKind Current { get; private set; }

    // The slide's settings, for the slides inside a panel (the Battles tab's opponents) to move alike.
    public float ExitDuration => exitDuration;
    public Ease ExitEase => exitEase;
    public float EnterGap => enterGap;
    public float EnterDuration => enterDuration;
    public Ease EnterEase => enterEase;
    public float OffscreenMargin => offscreenMargin;
    public float CascadeSpread => cascadeSpread;

    /// <summary>True while a switch is still sliding.</summary>
    public bool IsSwitching => _slide != null && _slide.IsActive();

    /// <summary>True while the bar is still dropping in from above the screen.</summary>
    public bool IsBarDropping => _barDrop != null && _barDrop.IsActive();

    /// <summary>
    /// The end-of-match panel's tab bar, or null on a panel without one. Found the way
    /// <see cref="BattleRewardsPanel.Resolve"/> finds itself: the panel is authored disabled, so it cannot
    /// be dragged into another prefab's inspector.
    /// </summary>
    public static GameOverTabs Resolve()
    {
        if (_instance == null)
        {
            var found = FindObjectsOfType<GameOverTabs>(true);
            _instance = found.Length > 0 ? found[0] : null;
        }
        return _instance;
    }

    private void Awake()
    {
        _instance = this;
        Init();
    }

    private void OnDisable()
    {
        if (_slide != null)
        {
            _slide.Kill();
            _slide = null;
        }

        // Put back wherever the slide had got to. Nothing is parked here: parking measures the screen's
        // edge, and the next open poses everything again from scratch anyway.
        if (tabs != null)
            foreach (var tab in tabs)
                if (tab != null) Home(tab);

        StopEntrance();
    }

    private void Init()
    {
        if (_initialised) return;
        _initialised = true;

        if (bar == null) bar = transform as RectTransform;
        if (bar != null) _barHome = bar.anchoredPosition;

        Current = openOn;

        if (tabs != null)
            foreach (var tab in tabs)
            {
                if (tab == null) continue;

                CollectPieces(tab);
                if (tab.button == null) continue;

                var kind = tab.kind;
                tab.button.Button.onClick.AddListener(() => Select(kind, animate: true));
            }

        RefreshButtons();
    }

    /// <summary>
    /// Switches the opening tab's panel on and the others off, without placing anything.
    ///
    /// The end-of-match panel is switched on and then filled -- the stats view writes its block of text
    /// and reads the glyph geometry straight back out of it -- so whatever the filling touches has to be
    /// awake before it runs, which is well before the entrance plays. Where each panel sits is settled
    /// later, by <see cref="PlayEntrance"/>: the panel is still scaled to nothing at this point, and the
    /// screen's edge measured through it reads nonsense.
    /// </summary>
    public void ShowOpeningTab()
    {
        Init();

        bool inRun = GauntletRun.IsActive;
        Current = inRun ? _openOnce ?? openOn : fallbackOutsideRun;
        RefreshButtons();

        var shown = Get(Current);
        if (tabs != null)
            foreach (var tab in tabs)
                if (tab?.panel != null) tab.panel.SetActive(tab == shown || tab.keepAlive);

        // Last: the bar is this object, so switching it off takes this component's OnDisable with it.
        if (bar != null) bar.gameObject.SetActive(inRun);
        SetBarShown(_lockedTo == null);
    }

    /// <summary>
    /// Places the panels for the end-of-match entrance -- the opening tab's away off screen, the rest
    /// off, the bar waiting above the screen -- and brings them in once the outcome title has risen:
    /// <paramref name="delay"/> is when the rise starts. The opening tab's panel slides in
    /// panelEnterDelay later, the way a tab switch brings a panel in, and the bar drops barDropDelay later.
    /// </summary>
    public void PlayEntrance(float delay)
    {
        ShowOpeningTab();
        StopEntrance();

        Select(Current, animate: false);

        if (!GauntletRun.IsActive || !Application.isPlaying) return;

        var opening = Get(Current);
        if (opening != null && opening.panel != null && opening.pieces.Length > 0)
        {
            // Away until the title is up and out of the way; an early tab click finishes it (FinishSlide).
            var slide = Measure(opening);
            for (int i = 0; i < opening.pieces.Length; i++)
                if (opening.pieces[i] != null)
                    opening.pieces[i].anchoredPosition = opening.homes[i] + new Vector2(slide.dx[i], 0f);

            _slide = DOTween.Sequence();
            AddEnter(_slide, opening, slide, delay + panelEnterDelay, fade: false);
            _slide.OnKill(() => _slide = null);
        }

        DropBar(delay + barDropDelay);
    }

    /// <summary>
    /// The panel already there, with no entrance: the opening tab home and up, the bar in its place.
    /// What the run's draft opens with -- the Game scene loads straight onto it.
    /// </summary>
    public void ShowAtOnce()
    {
        ShowOpeningTab();
        StopEntrance();
        Select(Current, animate: false);
    }

    /// <summary>Drops the bar's entrance where it stands and puts it back at its place.</summary>
    public void StopEntrance()
    {
        if (_barDrop != null)
        {
            _barDrop.Kill();
            _barDrop = null;
        }

        if (bar == null) return;

        bar.DOKill();
        if (_initialised) bar.anchoredPosition = _barHome;
    }

    /// <summary>
    /// Opens the panel on <paramref name="kind"/> the next time it opens, instead of openOn, with the
    /// bar put away until <see cref="Unlock"/>: the run's opening draft, which has no battle for the
    /// other tabs to show yet. Called before the panel is switched on.
    /// </summary>
    public void OpenLockedTo(TabKind kind)
    {
        Init();
        _openOnce = kind;
        _lockedTo = kind;
        RefreshButtons();
        SetBarShown(false);
    }

    /// <summary>
    /// Opens every tab up again but <paramref name="stillLocked"/>, the opening tab back to openOn for
    /// the next time.
    /// </summary>
    public void Unlock(params TabKind[] stillLocked)
    {
        Init();
        _openOnce = null;
        _lockedTo = null;
        _locked.Clear();
        if (stillLocked != null) _locked.UnionWith(stillLocked);
        RefreshButtons();

        // Put away for the draft: it drops in now, the way it comes in over a battle's result.
        if (!_barShown)
        {
            SetBarShown(true);
            DropBar(0f);
        }
    }

    private bool _barShown = true;
    private bool _covered;

    /// <summary>
    /// Puts everything out of sight -- the open tab's panel, what fades with it, the top panel -- while
    /// something covers the whole panel, or brings it all back. Faded rather than
    /// switched off, so a parked Result panel goes on tallying underneath.
    /// </summary>
    public void SetCovered(bool covered)
    {
        Init();
        if (_covered == covered) return;
        _covered = covered;

        SetBarShown(!covered && _lockedTo == null);

        var tab = Get(Current);
        if (tab == null) return;

        if (tab.panel != null)
        {
            var group = tab.panel.GetComponent<CanvasGroup>();
            if (group == null) group = tab.panel.AddComponent<CanvasGroup>();
            group.alpha = covered ? 0f : 1f;
            group.blocksRaycasts = !covered;
        }
        SetFaded(tab, !covered);
    }

    /// <summary>
    /// Shows or hides the bar -- with the top panel around it -- by fading: the bar carries this
    /// component, so switching it off would stop the slides it runs.
    /// </summary>
    private void SetBarShown(bool shown)
    {
        _barShown = shown;
        var hidden = barPanel != null ? barPanel : bar;
        if (hidden == null) return;

        var group = hidden.GetComponent<CanvasGroup>();
        if (group == null) group = hidden.gameObject.AddComponent<CanvasGroup>();
        group.alpha = shown ? 1f : 0f;
        group.blocksRaycasts = shown;
    }

    /// <summary>Drops the bar in from above the screen, starting <paramref name="delay"/> seconds from now.</summary>
    private void DropBar(float delay)
    {
        if (bar == null || !Application.isPlaying) return;

        if (_barDrop != null) _barDrop.Kill();
        bar.DOKill();
        bar.anchoredPosition = _barHome + new Vector2(0f, PanelSlide.OffscreenTopOffset(bar, barOffscreenMargin));
        _barDrop = bar.DOAnchorPos(_barHome, barDropDuration).SetEase(barDropEase).SetDelay(delay);
    }

    /// <summary>Whether the tab buttons respond at all. Empty tabs stay dead either way.</summary>
    public void SetInteractable(bool on)
    {
        Init();
        _interactable = on;
        RefreshButtons();
    }

    /// <summary>Opens <paramref name="kind"/>'s panel, sliding the open one out first when animated.</summary>
    public void Select(TabKind kind, bool animate)
    {
        Init();

        var to = Get(kind);
        if (to == null) return;
        if (animate && kind == Current && !IsSwitching) return;

        FinishSlide();

        var from = Get(Current);
        Current = kind;

        Fill(kind);
        RefreshButtons();

        if (!animate || !Application.isPlaying || from == to)
        {
            ShowOnly(to);
            return;
        }

        _slide = DOTween.Sequence();
        float enterAt = enterGap;

        if (from != null && from.panel != null && from.panel.activeSelf && from.pieces.Length > 0)
        {
            // A slide inside the panel (its fight tabs) ends first: its pieces are about to move again.
            DOTween.Complete(from.panel.transform, withCallbacks: true);

            float gone = AddExit(_slide, from, Measure(from), 0f);
            var leaving = from;
            _slide.InsertCallback(gone, () => Park(leaving));
            enterAt += gone;
        }

        if (to.panel != null)
        {
            to.panel.SetActive(true);
            to.parked = false;

            // Measured from where the panel rests, so a parked one is put back first.
            Home(to);
            var slide = Measure(to);
            for (int i = 0; i < to.pieces.Length; i++)
                to.pieces[i].anchoredPosition = to.homes[i] + new Vector2(slide.dx[i], 0f);
            AddEnter(_slide, to, slide, enterAt);
        }

        _slide.OnKill(() => _slide = null);
    }

    /// <summary>Where each of a tab's pieces goes when it leaves, and its turn in the cascade.</summary>
    private struct SlidePlan
    {
        /// <summary>How far each piece moves, measured from home: negative off the left edge, positive off the right.</summary>
        public float[] dx;

        /// <summary>Each piece's turn leaving, 0 first to 1 last; coming back runs the other way round.</summary>
        public float[] turn;
    }

    /// <summary>
    /// Plans <paramref name="tab"/>'s slide, with its pieces at home. All of them go left, the right-most
    /// last; a split tab sends its Right Side pieces right and the rest left, each side's outer-most
    /// piece first.
    /// </summary>
    private SlidePlan Measure(Tab tab)
    {
        int count = tab.pieces.Length;
        var plan = new SlidePlan { dx = new float[count], turn = new float[count] };
        if (count == 0) return plan;

        if (!tab.splitSides)
        {
            float dx = PanelSlide.OffscreenLeftOffset(tab.pieces, offscreenMargin);
            var order = PanelSlide.CascadeOrder(tab.pieces);
            for (int i = 0; i < count; i++)
            {
                plan.dx[i] = dx;
                plan.turn[i] = order[i];
            }
            return plan;
        }

        var left = new List<int>();
        var right = new List<int>();
        for (int i = 0; i < count; i++)
        {
            // A hidden piece has nowhere to go, and its rect must not stretch the distance the rest travel.
            var piece = tab.pieces[i];
            if (piece == null || !piece.gameObject.activeSelf) continue;

            bool goesRight = tab.rightSide != null && System.Array.IndexOf(tab.rightSide, piece) >= 0;
            (goesRight ? right : left).Add(i);
        }

        Side(left, rightToLeft: false);
        Side(right, rightToLeft: true);
        return plan;

        void Side(List<int> indices, bool rightToLeft)
        {
            if (indices.Count == 0) return;

            var rects = new List<RectTransform>(indices.Count);
            foreach (int i in indices) rects.Add(tab.pieces[i]);

            float dx = rightToLeft
                ? PanelSlide.OffscreenRightOffset(rects, offscreenMargin)
                : PanelSlide.OffscreenLeftOffset(rects, offscreenMargin);
            var order = PanelSlide.CascadeOrder(rects, rightToLeft);
            for (int k = 0; k < indices.Count; k++)
            {
                plan.dx[indices[k]] = dx;
                plan.turn[indices[k]] = order[k];
            }
        }
    }

    /// <summary>Slides <paramref name="tab"/>'s pieces away from <paramref name="at"/> on; returns when the last is gone.</summary>
    private float AddExit(Sequence sequence, Tab tab, SlidePlan plan, float at)
    {
        float spread = tab.cascade ? cascadeSpread : 0f;
        for (int i = 0; i < tab.pieces.Length; i++)
            if (tab.pieces[i] != null)
                sequence.Insert(at + spread * plan.turn[i],
                    tab.pieces[i].DOAnchorPos(tab.homes[i] + new Vector2(plan.dx[i], 0f), exitDuration).SetEase(exitEase));

        foreach (var group in FadeGroups(tab))
            sequence.Insert(at, group.DOFade(0f, exitDuration).SetEase(exitEase));

        return at + spread + exitDuration;
    }

    /// <summary>
    /// Slides <paramref name="tab"/>'s pieces home from where they wait, starting at <paramref name="at"/>,
    /// fading in what fades with it -- except on the opening slide, where the outcome title is the
    /// entrance's own to bring up.
    /// </summary>
    private void AddEnter(Sequence sequence, Tab tab, SlidePlan plan, float at, bool fade = true)
    {
        float spread = tab.cascade ? cascadeSpread : 0f;
        for (int i = 0; i < tab.pieces.Length; i++)
            if (tab.pieces[i] != null)
                sequence.Insert(at + spread * (1f - plan.turn[i]),
                    tab.pieces[i].DOAnchorPos(tab.homes[i], enterDuration).SetEase(enterEase));

        if (!fade) return;
        foreach (var group in FadeGroups(tab))
        {
            group.alpha = 0f;
            sequence.Insert(at, group.DOFade(1f, enterDuration).SetEase(enterEase));
        }
    }

    /// <summary>What fades with <paramref name="tab"/>; added on demand, so nothing has to be authored for it.</summary>
    private static IEnumerable<CanvasGroup> FadeGroups(Tab tab)
    {
        if (tab.fadeAlong == null) yield break;

        foreach (var go in tab.fadeAlong)
        {
            if (go == null) continue;

            var group = go.GetComponent<CanvasGroup>();
            yield return group != null ? group : go.AddComponent<CanvasGroup>();
        }
    }

    private static void SetFaded(Tab tab, bool shown)
    {
        foreach (var group in FadeGroups(tab))
        {
            group.DOKill();
            group.alpha = shown ? 1f : 0f;
        }
    }

    /// <summary>Jumps a running switch to its end, so every panel is either home and up, or away.</summary>
    private void FinishSlide()
    {
        if (_slide != null && _slide.IsActive()) _slide.Complete(withCallbacks: true);
        _slide = null;

        ShowOnly(Get(Current));
    }

    private void ShowOnly(Tab shown)
    {
        if (tabs == null) return;

        foreach (var tab in tabs)
        {
            if (tab?.panel == null) continue;

            if (tab == shown)
            {
                tab.panel.SetActive(true);
                tab.parked = false;
                Home(tab);
                SetFaded(tab, true);
            }
            else Park(tab);
        }
    }

    /// <summary>
    /// Puts <paramref name="tab"/>'s panel away: switched off, or -- for a panel that must keep running --
    /// left on and moved off the screen's left edge, where it is out of the raycaster's reach.
    /// </summary>
    private void Park(Tab tab)
    {
        if (tab.panel == null) return;

        SetFaded(tab, false);

        if (!tab.keepAlive || tab.pieces.Length == 0)
        {
            Home(tab);
            tab.panel.SetActive(false);
            tab.parked = false;
            return;
        }

        tab.panel.SetActive(true);
        Home(tab);

        var plan = Measure(tab);
        for (int i = 0; i < tab.pieces.Length; i++)
            if (tab.pieces[i] != null) tab.pieces[i].anchoredPosition = tab.homes[i] + new Vector2(plan.dx[i], 0f);

        tab.parked = true;
    }

    private void RefreshButtons()
    {
        if (tabs == null) return;

        foreach (var tab in tabs)
        {
            if (tab?.button == null) continue;

            bool usable = (_lockedTo == null || _lockedTo == tab.kind) && !_locked.Contains(tab.kind);
            tab.button.SetActive(Current == tab.kind);
            tab.button.Button.interactable = _interactable && usable && !IsEmpty(tab);
        }
    }

    /// <summary>Brings a panel's contents up to date as its tab opens.</summary>
    private void Fill(TabKind kind)
    {
        if (kind == TabKind.Battles)
        {
            if (battles != null) battles.Refresh();
            return;
        }

        if (kind != TabKind.Statistics || lastMatchStats == null) return;

        // The most recent battle's stats, as saved when it ended: no count-up, it was just watched.
        var fought = GauntletRun.Battles;
        if (fought.Count > 0) lastMatchStats.Show(fought[fought.Count - 1].stats, animate: false);
    }

    /// <summary>A tab with nothing authored under it yet: its button is left dead rather than opening air.</summary>
    private static bool IsEmpty(Tab tab) => tab?.panel == null || tab.pieces == null || tab.pieces.Length == 0;

    /// <summary>
    /// Records what moves when <paramref name="tab"/> slides, and where each piece rests. A panel that is
    /// not a RectTransform of its own -- a plain grouping object -- slides its children, as a cascading
    /// panel does. Turns in a cascade are dealt at each slide, among the pieces showing then.
    /// </summary>
    private static void CollectPieces(Tab tab)
    {
        var pieces = new List<RectTransform>();
        if (tab.panel != null)
        {
            var own = tab.panel.transform as RectTransform;
            if (tab.cascade || own == null)
                foreach (Transform child in tab.panel.transform)
                    if (child is RectTransform rect) pieces.Add(rect);

            if (pieces.Count == 0 && own != null) pieces.Add(own);
        }

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


    private Tab Get(TabKind kind)
    {
        if (tabs == null) return null;

        foreach (var tab in tabs)
            if (tab != null && tab.kind == kind) return tab;
        return null;
    }
}
