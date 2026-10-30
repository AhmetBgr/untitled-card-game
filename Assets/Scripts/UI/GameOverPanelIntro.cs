using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

/// <summary>
/// The end-of-match panel's entrance. The outcome lands on its own first -- VICTORY (or DEFEAT) pops in
/// over the middle of the screen, holds a beat, then rises to its place at the top, fading out as it goes
/// -- and the rest of the panel assembles itself around it as it rises: the stats column and the run's
/// milestone track come in from the left, the rewards column from the right.
///
/// In a Forged in Battle run the panel is tabbed (<see cref="GameOverTabs"/>) and the columns are no
/// longer the panel: they are one tab of several, and the bar owns their slide. The entrance then plays
/// the title alone and hands the panel over -- the bar drops in from above the screen as the title rises
/// out of its way -- rather than moving pieces the bar is about to move itself.
///
/// Outside a Forged in Battle run there is no milestone track under the stats column and no rewards
/// column beside it, so the column would be left sitting against one edge of an otherwise empty panel.
/// It slides to the middle instead (<see cref="statsCentreOffset"/>).
///
/// <see cref="PopupManager.OpenPopup"/> drives this in place of its own scale-in: a panel carrying this
/// component is opened at full size and faded in, so each piece can travel on its own.
///
/// The two pieces that are not authored on the prefab -- the milestone track and the rewards panel are
/// added to the scene's instance of it -- are found at runtime rather than wired in the inspector, the
/// same way <see cref="BattleRewardsPanel.Resolve"/> finds itself.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class GameOverPanelIntro : MonoBehaviour
{
    /// <summary>One travelling piece: where it was authored, and which edge it comes in from.</summary>
    private struct Piece
    {
        public RectTransform target;
        public Vector2 home;

        /// <summary>-1 to come in from the left, +1 from the right.</summary>
        public float fromSide;

        /// <summary>Takes the centring offset outside a run.</summary>
        public bool centresOutsideRun;
    }

    [Header("Title")]
    [Tooltip("The outcome titles. Whichever one the panel enabled for this ending is the one that plays; " +
             "the other is left where it is.")]
    [SerializeField] private RectTransform[] titles;

    [Header("Stats column")]
    [Tooltip("The authored left-hand column -- its frame, its divider and the stats view itself. These " +
             "travel together.")]
    [SerializeField] private RectTransform[] statsColumn;

    [Tooltip("Where the stats column goes outside a run, as an offset from where it is authored: with no " +
             "milestone track under it and no rewards beside it, it belongs in the middle.")]
    [SerializeField] private Vector2 statsCentreOffset = new Vector2(209f, 0f);

    [Header("Timing")]
    [Tooltip("The panel fading up under the title's pop.")]
    [SerializeField] private float fadeDuration = 0.25f;

    [Tooltip("The title appearing in the middle of the screen.")]
    [SerializeField] private float titlePopDuration = 0.35f;

    [Tooltip("How long the title holds there before it rises.")]
    [SerializeField] private float titleHold = 0.35f;

    [Tooltip("The rise, and the slide the columns make alongside it.")]
    [SerializeField] private float riseDuration = 0.55f;

    [Tooltip("The title fading away as it rises, so the panel is left to the tabs under it. Off leaves " +
             "it at its place at the top.")]
    [SerializeField] private bool titleFadesOut = true;

    [SerializeField] private Ease popEase = Ease.OutBack;
    [SerializeField] private Ease slideEase = Ease.OutCubic;

    private RectTransform _rect;
    private CanvasGroup _group;
    private Sequence _sequence;

    /// <summary>Collected once, before anything has been moved, so the homes are the authored ones.</summary>
    private readonly List<Piece> _pieces = new List<Piece>();
    private readonly List<Vector2> _titleHomes = new List<Vector2>();
    private readonly List<CanvasGroup> _titleGroups = new List<CanvasGroup>();
    private GameOverTabs _tabs;
    private bool _collected;

    /// <summary>
    /// The tab bar owns the panels, so the entrance leaves them alone and plays the title only. Outside a
    /// run the bar hides itself and the authored columns are the whole panel again, as they always were.
    /// </summary>
    private bool TabsOwnPanels => _tabs != null && GauntletRun.IsActive;

    /// <summary>How long the entrance runs, after the panel's own open delay.</summary>
    public float Duration => Mathf.Max(fadeDuration, titlePopDuration) + titleHold + riseDuration;

    /// <summary>True while the entrance is still playing (its open delay included).</summary>
    public bool IsEntering => _sequence != null && _sequence.IsActive();

    private CanvasGroup Group
    {
        get
        {
            if (_group == null)
            {
                _group = GetComponent<CanvasGroup>();
                if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
            }
            return _group;
        }
    }

    /// <summary>
    /// Poses the panel for its entrance straight away -- faded out, title in the middle, columns off
    /// screen -- and plays it <paramref name="delay"/> seconds later, so nothing is ever glimpsed in its
    /// final place while the killing blow is still playing out underneath.
    /// </summary>
    public void Play(float delay)
    {
        Collect();
        Stop();

        bool inRun = GauntletRun.IsActive;
        float offscreen = _rect.rect.width > 0f ? _rect.rect.width : Screen.width;

        // Faded out and click-through until it plays: the panel is live for the whole of the open delay,
        // and an invisible full-screen panel must not be swallowing clicks meant for the board.
        Group.alpha = 0f;
        Group.blocksRaycasts = false;

        if (!TabsOwnPanels)
            foreach (var piece in _pieces)
            {
                Vector2 home = HomeOf(piece, inRun);
                piece.target.anchoredPosition = new Vector2(home.x + piece.fromSide * offscreen, home.y);
            }

        int playing = ActiveTitle();
        if (playing >= 0)
        {
            titles[playing].anchoredPosition = new Vector2(_titleHomes[playing].x, -_rect.rect.height * 0.5f);
            titles[playing].localScale = Vector3.zero;
            if (_titleGroups[playing] != null) _titleGroups[playing].alpha = 1f;
        }

        _sequence = DOTween.Sequence();
        _sequence.AppendInterval(delay);

        _sequence.AppendCallback(() => Group.blocksRaycasts = true);
        _sequence.Append(Group.DOFade(1f, fadeDuration));
        if (playing >= 0) _sequence.Join(titles[playing].DOScale(1f, titlePopDuration).SetEase(popEase));

        _sequence.AppendInterval(titleHold);

        // The rise, with the columns arriving beside it -- or, under the tabs, with the bar dropping in.
        _sequence.AppendInterval(riseDuration);
        if (playing >= 0)
        {
            _sequence.Join(titles[playing].DOAnchorPos(_titleHomes[playing], riseDuration).SetEase(slideEase));
            if (titleFadesOut && _titleGroups[playing] != null)
                _sequence.Join(_titleGroups[playing].DOFade(0f, riseDuration).SetEase(slideEase));
        }

        // The bar poses itself either way: in a run it drops in as the title rises, and outside one it
        // hides and puts the plain panel's own tab up, which is the panel this entrance then assembles.
        if (_tabs != null) _tabs.PlayEntrance(delay + Mathf.Max(fadeDuration, titlePopDuration) + titleHold);

        if (!TabsOwnPanels)
            foreach (var piece in _pieces)
                _sequence.Join(piece.target.DOAnchorPos(HomeOf(piece, inRun), riseDuration).SetEase(slideEase));
    }

    /// <summary>
    /// The panel already up, with no entrance at all: what the run's draft opens with, the Game scene
    /// loading straight onto it. No outcome title -- nothing has been fought.
    /// </summary>
    public void ShowAtOnce()
    {
        Collect();
        Stop();

        Group.alpha = 1f;
        Group.blocksRaycasts = true;
        if (_tabs != null) _tabs.ShowAtOnce();
    }

    /// <summary>Drops the entrance where it stands. The next <see cref="Play"/> poses everything again.</summary>
    public void Stop()
    {
        if (_sequence != null)
        {
            _sequence.Kill();
            _sequence = null;
        }

        foreach (var piece in _pieces)
            if (piece.target != null) piece.target.DOKill();

        if (titles != null)
            foreach (var title in titles)
                if (title != null) title.DOKill();

        foreach (var group in _titleGroups)
            if (group != null)
            {
                group.DOKill();
                group.alpha = 1f;
            }

        if (_tabs != null) _tabs.StopEntrance();
    }

    // The panel is switched on and then filled -- the stats view, the XP track, the rewards -- all before
    // the entrance is played. The tabs put the opening tab's panel up here, as the panel comes on, so what
    // the filling touches is awake by the time it runs; where the panels sit is settled in Play.
    private void OnEnable()
    {
        Collect();
        if (_tabs != null) _tabs.ShowOpeningTab();
    }

    private Vector2 HomeOf(Piece piece, bool inRun) =>
        piece.centresOutsideRun && !inRun ? piece.home + statsCentreOffset : piece.home;

    /// <summary>The title this ending enabled, or -1 when the panel has none up.</summary>
    private int ActiveTitle()
    {
        if (titles == null) return -1;

        for (int i = 0; i < titles.Length; i++)
            if (titles[i] != null && titles[i].gameObject.activeSelf) return i;
        return -1;
    }

    private void Collect()
    {
        if (_collected) return;
        _collected = true;

        _rect = (RectTransform)transform;

        _tabs = GetComponentInChildren<GameOverTabs>(true);

        if (titles != null)
            foreach (var title in titles)
            {
                _titleHomes.Add(title != null ? title.anchoredPosition : Vector2.zero);
                _titleGroups.Add(title != null ? FadeGroup(title.gameObject) : null);
            }

        if (statsColumn != null)
            foreach (var part in statsColumn)
                Add(part, -1f, centresOutsideRun: true);

        // Added to the scene's instance of the panel, so a prefab reference to either is impossible.
        var track = GetComponentInChildren<BattleXpTrack>(true);
        if (track != null) Add(track.transform as RectTransform, -1f, centresOutsideRun: false);

        var rewards = GetComponentInChildren<BattleRewardsPanel>(true);
        if (rewards != null) Add(rewards.transform as RectTransform, 1f, centresOutsideRun: false);
    }

    /// <summary>What fades a title out as it rises. Added on demand, so nothing has to be authored for it.</summary>
    private static CanvasGroup FadeGroup(GameObject title)
    {
        var group = title.GetComponent<CanvasGroup>();
        return group != null ? group : title.AddComponent<CanvasGroup>();
    }

    private void Add(RectTransform target, float fromSide, bool centresOutsideRun)
    {
        if (target == null) return;

        _pieces.Add(new Piece
        {
            target = target,
            home = target.anchoredPosition,
            fromSide = fromSide,
            centresOutsideRun = centresOutsideRun,
        });
    }

    private void OnDestroy()
    {
        Stop();
    }
}
