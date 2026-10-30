using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Battles tab of the end-of-match panel: the run's encounters, any of which can be read.
///
/// It is the Draft scene's own opponent briefing (Assets/Prefabs/UI/Draft/OpponentInfo) nested under the
/// end-of-match panel: the enemy hero, and the blessings it fights with, one row each
/// (<see cref="BlessingRow"/>, cloned from Assets/Prefabs/UI/Draft/BlessingRow). An enemy with no
/// blessings hides the blessings block altogether.
///
/// The battle track's skulls pick the fight shown, past or future -- every enemy is rolled at the hero
/// pick. Each skull shows the run as it stands (beaten ones crossed out and greyed,
/// <see cref="GauntletBattleTrack"/>), and the highlight sits on the one being read. The panel opens on
/// the upcoming fight. Leaving for that fight is the panel's Proceed (<see cref="GameOverVersus"/>), not
/// this tab's.
///
/// Picking another fight splits the briefing down the middle: the left half (the hero) slides off the
/// screen's left edge and the right half (the blessings) off its right edge, both are refilled while
/// away, and each comes back in from the side it left by. Every piece goes on its own turn: the hero,
/// and each blessing row one after another. The timing, easing and cascade are the review
/// tabs' own (<see cref="GameOverTabs"/>, the same as the Draft scene's), so every slide moves alike.
///
/// The halves sit in a layout group, which puts them back where it rests them on every rebuild. So the
/// layout (and its size fitter) is switched off while they move, and back on for one rebuild while they
/// are away -- refilling can show or hide the blessings and so move the hero -- so that they come back
/// to the places the new fight lays them out at, not the old one's. Only x is slid.
/// </summary>
public class GameOverNextMatch : MonoBehaviour
{
    /// <summary>One skull on the battle track, and the fight it opens.</summary>
    [System.Serializable]
    private class TrackNode
    {
        public Button button;
        public Graphic skull;
        public GameObject highlight;
        public GameObject defeatedMark;
    }

    [Header("Briefing")]
    [SerializeField] private GauntletHeroPanelView enemyHero;
    [SerializeField] private TextMeshProUGUI headerText;

    [Header("Blessings")]
    [Tooltip("The whole blessings block, header included: hidden when the enemy has none.")]
    [SerializeField] private GameObject blessingsRoot;
    [Tooltip("Where the blessing rows go, one per blessing. Anything authored under it is a preview, cleared on open.")]
    [SerializeField] private RectTransform blessingList;
    [SerializeField] private BlessingRow blessingRowPrefab;

    [Header("Battle track")]
    [Tooltip("One per encounter, in order. Clicking a skull shows that fight.")]
    [SerializeField] private TrackNode[] nodes;
    [Tooltip("Skull tint once its battle is won; the Draft scene's track uses the same one.")]
    [SerializeField] private Color beatenSkullTint = new Color(0.396f, 0.396f, 0.396f, 1f);

    [Header("Slide")]
    [Tooltip("Leave and come back by the screen's left edge.")]
    [SerializeField] private RectTransform[] slideLeft;
    [Tooltip("Leave and come back by the screen's right edge, along with every blessing row, each on its own turn.")]
    [SerializeField] private RectTransform[] slideRight;
    [Tooltip("Slide each half's pieces one after another, outer-most first out and last in, instead of all at once.")]
    [SerializeField] private bool cascade = true;

    private readonly List<BlessingRow> _rows = new List<BlessingRow>();
    // The layout the sliding pieces sit in: off while they move.
    private readonly List<Behaviour> _layout = new List<Behaviour>();
    private Sequence _slide;
    private bool _initialised;

    public int Selected { get; private set; } = -1;

    private bool IsSwitching => _slide != null && _slide.IsActive();

    private void Awake() => Init();

    private void OnDisable() => FinishSlide();

    /// <summary>
    /// The panel is authored disabled and its tab fills it on the way up, before it is switched on, so
    /// none of this can wait for Awake: the first fill would go to a panel listening to nothing.
    /// </summary>
    private void Init()
    {
        if (_initialised) return;
        _initialised = true;

        if (blessingList != null)
            for (int i = blessingList.childCount - 1; i >= 0; i--)
                Destroy(blessingList.GetChild(i).gameObject);

        CollectLayout(Parents(slideLeft));
        CollectLayout(Parents(slideRight));
        // The rows are made later, but always under the list.
        if (blessingList != null) CollectLayout(new Transform[] { blessingList });

        if (nodes == null) return;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i]?.button == null) continue;

            int index = i;
            nodes[i].button.onClick.AddListener(() => Select(index, animate: true));
        }
    }

    /// <summary>Brings up the upcoming battle, without a slide; a won run's last one.</summary>
    public void Refresh()
    {
        Init();

        int count = GauntletRun.EncounterCount;
        Select(count > 0 ? Mathf.Clamp(GauntletRun.EncounterIndex, 0, count - 1) : 0, animate: false);
    }

    /// <summary>
    /// Shows encounter <paramref name="encounterIndex"/>: its enemy, its blessings, its skull highlighted.
    /// Animated, the briefing slides out to both sides and back in with the new fight.
    /// </summary>
    public void Select(int encounterIndex, bool animate)
    {
        Init();
        if (animate && encounterIndex == Selected && !IsSwitching) return;

        FinishSlide();
        Selected = encounterIndex;
        RefreshTrack();

        if (!animate || !Application.isPlaying || !gameObject.activeInHierarchy)
        {
            ShowFight(encounterIndex);
            return;
        }

        var tabs = GameOverTabs.Resolve();
        float exitDuration = tabs != null ? tabs.ExitDuration : 0.5f;
        float enterGap = tabs != null ? tabs.EnterGap : 0f;
        float enterDuration = tabs != null ? tabs.EnterDuration : 0.6f;
        var exitEase = tabs != null ? tabs.ExitEase : Ease.OutExpo;
        var enterEase = tabs != null ? tabs.EnterEase : Ease.OutExpo;
        float margin = tabs != null ? tabs.OffscreenMargin : 50f;
        float spread = cascade ? (tabs != null ? tabs.CascadeSpread : 0.2f) : 0f;

        SetLayout(false);
        _slide = DOTween.Sequence();
        float exitEnd = spread + exitDuration;
        float enterAt = exitEnd + enterGap;

        // Left half: left-most out first and in last. Right half the mirror: right-most out first and in
        // last. Pieces level with each other go top first out, top last in, so each blessing row takes
        // its own turn.
        var left = new Half(LeftPieces, rightToLeft: false);
        var right = new Half(RightPieces, rightToLeft: true);
        left.Exit(_slide, margin, spread, exitDuration, exitEase);
        right.Exit(_slide, margin, spread, exitDuration, exitEase);

        // Refilled while away, so the new fight is what comes back in.
        _slide.InsertCallback(exitEnd, () =>
        {
            ShowFight(encounterIndex);

            // Shown or hidden blessings move the layout: settle it now, then take the pieces -- the new
            // fight's rows -- from their new places.
            SetLayout(true);
            left.Park(margin);
            right.Park(margin);
            SetLayout(false);
        });

        left.Enter(_slide, enterAt, spread, enterDuration, enterEase);
        right.Enter(_slide, enterAt, spread, enterDuration, enterEase);

        // Under the panel's id, so the review tabs can finish it before moving the same pieces themselves.
        _slide.SetId(transform);
        _slide.OnKill(() =>
        {
            _slide = null;
            SetLayout(true);
        });
    }

    private List<RectTransform> LeftPieces() => Showing(slideLeft, null);

    /// <summary>The right half: its authored pieces, and every blessing row showing.</summary>
    private List<RectTransform> RightPieces() => Showing(slideRight, _rows);

    private static List<RectTransform> Showing(RectTransform[] pieces, List<BlessingRow> rows)
    {
        var showing = new List<RectTransform>();
        if (pieces != null)
            foreach (var piece in pieces)
                if (piece != null && piece.gameObject.activeInHierarchy) showing.Add(piece);
        if (rows != null)
            foreach (var row in rows)
                if (row != null && row.gameObject.activeInHierarchy) showing.Add((RectTransform)row.transform);
        return showing;
    }

    /// <summary>The layout groups and size fitters the sliding pieces sit in.</summary>
    private void CollectLayout(IEnumerable<Transform> parents)
    {
        foreach (var parent in parents)
        {
            if (parent == null) continue;

            foreach (var c in parent.GetComponents<Behaviour>())
                if ((c is LayoutGroup || c is ContentSizeFitter) && !_layout.Contains(c)) _layout.Add(c);
        }
    }

    private static IEnumerable<Transform> Parents(RectTransform[] pieces)
    {
        if (pieces == null) yield break;
        foreach (var piece in pieces)
            if (piece != null) yield return piece.parent;
    }

    /// <summary>Switches the layout on (settling it at once) or off, so it leaves moving pieces alone.</summary>
    private void SetLayout(bool on)
    {
        foreach (var c in _layout)
            if (c != null) c.enabled = on;

        if (!on) return;
        foreach (var c in _layout)
            if (c != null) LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)c.transform);
    }

    /// <summary>
    /// One half of the briefing on the move: each piece slid along x by an offset from wherever the
    /// layout rests it, on its own turn in the cascade.
    ///
    /// The pieces that come back in are taken again once the half is away (<see cref="Park"/>): they are
    /// the new fight's, and a fight with more blessings has more rows. So each phase is one tween that
    /// places whichever pieces the half holds by then, each running its own eased slide within it.
    /// </summary>
    private class Half
    {
        private class Piece
        {
            public RectTransform rect;
            public float home;
            public float turn;
        }

        private readonly System.Func<List<RectTransform>> _source;
        private readonly bool _rightToLeft;
        private readonly List<Piece> _pieces = new List<Piece>();
        private float _away;

        public Half(System.Func<List<RectTransform>> source, bool rightToLeft)
        {
            _source = source;
            _rightToLeft = rightToLeft;
            Collect();
        }

        private void Collect()
        {
            _pieces.Clear();
            var rects = _source();
            var order = PanelSlide.CascadeOrder(rects, _rightToLeft);
            for (int i = 0; i < rects.Count; i++)
                _pieces.Add(new Piece { rect = rects[i], home = rects[i].anchoredPosition.x, turn = order[i] });
        }

        private float Offscreen(float margin)
        {
            var rects = new List<RectTransform>(_pieces.Count);
            foreach (var p in _pieces) rects.Add(p.rect);
            return _rightToLeft
                ? PanelSlide.OffscreenRightOffset(rects, margin)
                : PanelSlide.OffscreenLeftOffset(rects, margin);
        }

        public void Exit(Sequence slide, float margin, float spread, float duration, Ease ease)
        {
            _away = Offscreen(margin);
            float span = spread + duration;
            slide.Insert(0f, DOTween.To(elapsed =>
            {
                foreach (var p in _pieces)
                    Place(p, _away * Progress(elapsed, spread * p.turn, duration, ease));
            }, 0f, span, span).SetEase(Ease.Linear));
        }

        /// <summary>Takes the pieces again, at the new places the layout rests them, and holds them away.</summary>
        public void Park(float margin)
        {
            Collect();
            _away = Offscreen(margin);
            foreach (var p in _pieces) Place(p, _away);
        }

        public void Enter(Sequence slide, float at, float spread, float duration, Ease ease)
        {
            float span = spread + duration;
            slide.Insert(at, DOTween.To(elapsed =>
            {
                foreach (var p in _pieces)
                    Place(p, _away * (1f - Progress(elapsed, spread * (1f - p.turn), duration, ease)));
            }, 0f, span, span).SetEase(Ease.Linear));
        }

        /// <summary>A piece's eased 0..1 through its own slide, which starts <paramref name="delay"/> in.</summary>
        private static float Progress(float elapsed, float delay, float duration, Ease ease) =>
            DOVirtual.EasedValue(0f, 1f, Mathf.Clamp01((elapsed - delay) / duration), ease);

        private static void Place(Piece p, float offset)
        {
            if (p.rect == null) return;
            p.rect.anchoredPosition = new Vector2(p.home + offset, p.rect.anchoredPosition.y);
        }
    }

    private void ShowFight(int encounterIndex)
    {
        GauntletNextMatch.RefreshEncounter(encounterIndex, enemyHero, headerText, null, 0f);
        var enemy = GauntletRun.PreviewEnemy(encounterIndex);
        ShowBlessings(enemy?.Blessings);
    }

    /// <summary>Jumps a running switch to its end: both halves home, filled with the selected fight.</summary>
    private void FinishSlide()
    {
        if (_slide != null && _slide.IsActive()) _slide.Complete(withCallbacks: true);
        _slide = null;
    }

    private void RefreshTrack()
    {
        if (nodes == null) return;

        for (int i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (node == null) continue;

            GauntletBattleTrack.RefreshNode(node.skull, node.highlight, node.defeatedMark, i, beatenSkullTint);
            if (node.highlight != null) node.highlight.SetActive(i == Selected);
            if (node.button != null) node.button.interactable = i < GauntletRun.EncounterCount;
        }
    }

    /// <summary>One row per blessing; a stacked one (Extra Passive) once, with a count. None hides the block.</summary>
    private void ShowBlessings(IReadOnlyList<GauntletBlessing> blessings)
    {
        int shown = BlessingRow.Fill(blessingList, blessingRowPrefab, _rows, blessings);
        if (blessingsRoot != null) blessingsRoot.SetActive(shown > 0);
    }
}
