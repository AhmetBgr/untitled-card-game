using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The match-up every match opens on (Assets/Prefabs/UI/VersusPanel): the two heroes as they will start
/// the fight, each with its blessings -- the enemy's rolled ones above it, the player's lasting milestone
/// rewards under it -- split by the versus separator, over the freshly loaded board.
///
/// It comes in with a cascade: each hero slides on from its own edge of the screen with its blessing rows
/// following one by one, while the separator scales up through a slight turn. A click anywhere sends it
/// away: everything fades except the hero portraits and the filled passive chips, which fly onto the
/// board's own hero and passive indicators. Those are kept invisible until their copy lands and take
/// over in the same frame, so the portrait seems to settle into the board.
///
/// <see cref="GameManager"/> runs it from its match setup, once the heroes and their passives are in
/// place and before a card is dealt (<see cref="Play"/>). The root stays active so this is found in Awake;
/// what is drawn lives under <see cref="content"/>, authored disabled.
///
/// Every timing, ease and offset is an inspector field, read each time it plays: tune them in play mode
/// and use the component's "Replay" context menu to see the change at once.
/// </summary>
public class VersusIntro : MonoBehaviour
{
    public static VersusIntro Instance { get; private set; }

    [Header("Parts")]
    [Tooltip("Everything drawn while the match-up is up. Faded out as the portraits fly. Authored disabled.")]
    [SerializeField] private CanvasGroup content;

    [Tooltip("Full-screen dim behind the match-up. Blocks clicks to whatever is under it.")]
    [SerializeField] private Image backdrop;

    [SerializeField] private RectTransform playerPanel;
    [SerializeField] private RectTransform opponentPanel;
    [SerializeField] private RectTransform separator;

    [Tooltip("The player's lasting milestone rewards, top-anchored under its hero.")]
    [SerializeField] private RectTransform playerBlessings;

    [Tooltip("The enemy's blessings, bottom-anchored above its hero.")]
    [SerializeField] private RectTransform opponentBlessings;

    [SerializeField] private BlessingRow blessingRowPrefab;

    [Tooltip("The Proceed button that starts the match: the same crossed-swords button the run's draft " +
             "ends on (ProceedPromptButton). This is what is faded up and pulsed once the match-up has " +
             "finished arriving.")]
    [SerializeField] private CanvasGroup clickLabel;

    [Tooltip("The button inside that prompt. It is the only way on -- a click anywhere else does " +
             "nothing -- and it stays dead until the prompt has finished fading up.")]
    [SerializeField] private Button proceedButton;

    [Tooltip("Where the portraits and chips are moved for their flight, outside the faded content.")]
    [SerializeField] private RectTransform flyers;

    [SerializeField] private bool skipInTutorial = true;

    [Header("Blessing rows")]
    [Tooltip("Where each player row's top-left corner sits, relative to the PlayerBlessings rect, in canvas " +
             "units: the row nearest the hero first, going away from it. Rows past the list keep its last step.")]
    [SerializeField] private Vector2[] playerRowOffsets =
    {
        new Vector2(0f, 0f), new Vector2(29.30f, -60.94f), new Vector2(53.91f, -120.70f),
    };

    [Tooltip("The same for the enemy's rows, relative to EnemyBlessings: the row nearest the hero (the bottom " +
             "one) first, going up. Anchored at the bottom, so the last blessing always sits just above the hero.")]
    [SerializeField] private Vector2[] opponentRowOffsets =
    {
        new Vector2(0f, 0f), new Vector2(-29.30f, 58.59f), new Vector2(-56.25f, 119.53f),
    };

    [Header("Appear: backdrop")]
    [Tooltip("Wait after the scene has faded in before anything moves.")]
    [SerializeField] private float appearDelay = 0f;
    [SerializeField] private float backdropFadeDuration = 0.3f;
    [SerializeField] private Ease backdropEase = Ease.Linear;

    [Header("Appear: separator")]
    [SerializeField] private float separatorDelay = 0.1f;
    [SerializeField] private float separatorDuration = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float separatorStartScale = 0f;
    [SerializeField] private Ease separatorScaleEase = Ease.OutBack;
    [Tooltip("Degrees the separator turns through as it scales up; it ends at its authored rotation.")]
    [SerializeField] private float separatorSpin = -12f;
    [SerializeField] private Ease separatorRotateEase = Ease.OutCubic;

    [Header("Appear: heroes and rows")]
    [Tooltip("When the player's hero starts sliding in from the left.")]
    [SerializeField] private float playerSlideDelay = 0f;
    [Tooltip("When the enemy's hero starts sliding in from the right.")]
    [SerializeField] private float opponentSlideDelay = 0f;
    [SerializeField] private float heroSlideDuration = 0.45f;
    [SerializeField] private Ease heroSlideEase = Ease.OutCubic;
    [Tooltip("Gap between a hero starting and its first blessing row starting.")]
    [SerializeField] private float rowsDelay = 0.08f;
    [Tooltip("Gap between one row starting and the next: the cascade.")]
    [SerializeField] private float rowStagger = 0.08f;
    [SerializeField] private float rowSlideDuration = 0.45f;
    [SerializeField] private Ease rowSlideEase = Ease.OutCubic;
    [Tooltip("How far past the screen edge pieces start, in canvas units.")]
    [SerializeField] private float slideMargin = 40f;

    [Header("Appear: click label")]
    [Tooltip("Wait after everything has come in before the label shows. Clicks count from when it starts showing.")]
    [SerializeField] private float labelDelay = 0f;
    [SerializeField] private float labelFadeDuration = 0.3f;

    [Header("Disappear: fade")]
    [SerializeField] private float fadeOutDelay = 0f;
    [SerializeField] private float fadeOutDuration = 0.3f;
    [SerializeField] private Ease fadeOutEase = Ease.Linear;

    [Header("Disappear: heroes")]
    [SerializeField] private float playerFlightDelay = 0f;
    [SerializeField] private float opponentFlightDelay = 0f;
    [SerializeField] private float heroFlightDuration = 0.55f;
    [SerializeField] private Ease heroMoveEase = Ease.InOutCubic;
    [SerializeField] private Ease heroScaleEase = Ease.InOutCubic;
    [Tooltip("How far the flight bends, as a fraction of its length. Positive swings out away from the " +
             "screen's centre, negative in towards it, 0 flies straight.")]
    [SerializeField] private float heroArc = 0.35f;

    [Header("Disappear: passive chips")]
    [Tooltip("When a side's first chip leaves, counted from its hero leaving.")]
    [SerializeField] private float chipDelay = 0.05f;
    [SerializeField] private float chipStagger = 0.05f;
    [SerializeField] private float chipFlightDuration = 0.55f;
    [SerializeField] private Ease chipEase = Ease.InOutCubic;
    [Tooltip("How far each chip's flight bends, as a fraction of its length. Positive swings out away from " +
             "the screen's centre, negative in towards it, 0 flies straight.")]
    [SerializeField] private float chipArc = 0.35f;
    [Tooltip("A chip and its board indicator differ in art, so they crossfade on landing.")]
    [SerializeField] private float chipCrossfade = 0.12f;

    [Header("Landing smoke")]
    [Tooltip("Played where each hero lands (Assets/Prefabs/VFX/LandingSmoke). Empty for none.")]
    [SerializeField] private ParticleSystem landingSmoke;
    [Tooltip("When the smoke starts, counted from the hero landing. Negative starts it just before.")]
    [SerializeField] private float smokeDelay = -0.05f;
    [Tooltip("World-space nudge from the centre of the hero's frame.")]
    [SerializeField] private Vector2 smokeOffset = Vector2.zero;
    [SerializeField] private float smokeScale = 1f;

    [Header("Hidden while up")]
    [Tooltip("Scene HUD kept out of sight (and out of reach of clicks) while the match-up is up: the mana " +
             "bar, the menu button. They fade back in once the heroes have landed.")]
    [SerializeField] private GameObject[] hiddenWhileUp;
    [Tooltip("When they start fading back in, counted from the last hero landing.")]
    [SerializeField] private float hiddenFadeInDelay = 0f;
    [SerializeField] private float hiddenFadeInDuration = 0.3f;
    [SerializeField] private Ease hiddenFadeInEase = Ease.Linear;

    /// <summary>A UI piece moved under <see cref="flyers"/> for its flight, and how to put it back.</summary>
    private class Flyer
    {
        public RectTransform rect;
        public Transform parent;
        public int sibling;
        public Vector3 localPosition;
        public Vector3 localScale;
        public Quaternion localRotation;
        public CanvasGroup group;
    }

    /// <summary>One hero's half of the match-up: its panel, its rows, and what it hides on the board.</summary>
    private class Side
    {
        public Agent agent;
        public RectTransform panel;
        public RectTransform list;
        public bool fromLeft;

        public GauntletHeroPanelView view;
        public int passiveCount;
        public readonly List<BlessingRow> rows = new List<BlessingRow>();
        public int rowCount;

        public readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        public readonly List<Canvas> hiddenCanvases = new List<Canvas>();
        public readonly List<HeroPassiveIndicator> indicators = new List<HeroPassiveIndicator>();
        public readonly List<CanvasGroup> indicatorGroups = new List<CanvasGroup>();
    }

    private Side _player;
    private Side _opponent;
    private readonly List<Flyer> _flyers = new List<Flyer>();
    private readonly List<(Graphic graphic, Color color)> _tinted = new List<(Graphic, Color)>();
    private readonly List<RectTransform> _slid = new List<RectTransform>();
    private readonly List<Vector2> _slidHomes = new List<Vector2>();
    private Sequence _sequence;
    private Quaternion _separatorRotation;
    private float _backdropAlpha;
    private bool _dismissed;
    private bool _playing;
    private readonly List<(CanvasGroup group, bool blocked)> _hidden = new List<(CanvasGroup, bool)>();

    /// <summary>
    /// True from the moment the match-up goes up until the heroes have landed. The board reads it to stay
    /// quiet under it (<see cref="BoardInteractionGate"/>).
    /// </summary>
    public static bool IsUp => Instance != null && Instance._playing;

    private void Awake()
    {
        Instance = this;
        if (content != null) content.gameObject.SetActive(false);
        if (backdrop != null) _backdropAlpha = backdrop.color.a;
        if (separator != null) _separatorRotation = separator.localRotation;

        // The way on. Dismiss is what the old click-anywhere set too, so the wait loop is unchanged.
        if (proceedButton != null)
        {
            proceedButton.onClick.AddListener(Dismiss);
            proceedButton.interactable = false;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Kill();
    }

    /// <summary>
    /// Shows the match-up, waits for a click, and lands the heroes on the board. Returns once the board's
    /// own hero and passives have taken over. Read from the live match, so call it after the heroes are
    /// set up and their passives registered.
    /// </summary>
    public IEnumerator Play(Agent player, Agent opponent)
    {
        if (content == null || _playing || (skipInTutorial && GameManager.IsTutorialMatch)) yield break;
        _playing = true;
        HideHud();

        _player = Prepare(player, playerPanel, playerBlessings, fromLeft: true);
        _opponent = Prepare(opponent, opponentPanel, opponentBlessings, fromLeft: false);

        FillPlayerRows(_player);
        FillOpponentRows(_opponent);

        content.alpha = 1f;
        content.blocksRaycasts = true;
        content.gameObject.SetActive(true);
        Canvas.ForceUpdateCanvases();
        Layout(_player, playerRowOffsets, nearestLast: false);
        Layout(_opponent, opponentRowOffsets, nearestLast: true);

        // Posed before the scene finishes fading in, so nothing is seen at rest first.
        Pose();

        while (SceneTransitionManager.Instance != null && SceneTransitionManager.Instance.IsTransitioning)
            yield return null;

        _sequence = Appear();
        yield return _sequence.WaitForCompletion();

        if (labelDelay > 0f) yield return new WaitForSeconds(labelDelay);
        StartLabelPulse();

        // The Proceed button is the way on, and Dismiss is what it calls; a click anywhere else on the
        // match-up does nothing.
        _dismissed = false;
        yield return null;
        while (!_dismissed) yield return null;

        if (proceedButton != null) proceedButton.interactable = false;
        SetPromptLive(false);

        if (clickLabel != null) clickLabel.DOKill();
        _sequence = Disappear();
        yield return _sequence.WaitForCompletion();

        Finish();
        _playing = false;
        ShowHud();
    }

    /// <summary>The HUD that would sit on top of the match-up, out of sight and click-through.</summary>
    private void HideHud()
    {
        _hidden.Clear();
        if (hiddenWhileUp == null) return;

        foreach (var go in hiddenWhileUp)
        {
            if (go == null) continue;

            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();
            group.DOKill();
            _hidden.Add((group, group.blocksRaycasts));
            group.alpha = 0f;
            group.blocksRaycasts = false;
        }
    }

    private void ShowHud()
    {
        foreach (var (group, blocked) in _hidden)
        {
            if (group == null) continue;

            group.blocksRaycasts = blocked;
            group.DOFade(1f, hiddenFadeInDuration).SetDelay(hiddenFadeInDelay).SetEase(hiddenFadeInEase);
        }
        _hidden.Clear();
    }

    /// <summary>
    /// Plays the intro again over the match in progress, for tuning its fields in play mode. Purely visual:
    /// the board's heroes and passives are hidden and landed again, nothing in the match changes.
    /// </summary>
    [ContextMenu("Replay")]
    private void Replay()
    {
        var game = GameManager.Instance;
        if (!Application.isPlaying || _playing || game == null) return;
        StartCoroutine(Play(game.player, game.opponent));
    }

    // ---------------------------------------------------------------- Fill

    private Side Prepare(Agent agent, RectTransform panel, RectTransform list, bool fromLeft)
    {
        var side = new Side { agent = agent, panel = panel, list = list, fromLeft = fromLeft };
        var hero = agent != null ? agent.hero : null;
        if (panel == null || hero == null) return side;

        side.view = WithStats(panel.gameObject);
        var runtime = HeroRuntime.For(hero);
        var passives = runtime != null ? runtime.passives : null;
        side.passiveCount = passives != null ? passives.Count : 0;
        side.view.Show(hero.card as HeroSO, hero.modal.health, hero.modal.attack, passives);

        HideBoard(side);
        return side;
    }

    /// <summary>
    /// A HeroPanel instance with its stat texts too, so it shows the health and attack the hero will
    /// actually fight with, not the printed ones.
    /// </summary>
    private static GauntletHeroPanelView WithStats(GameObject heroPanel)
    {
        var view = GauntletHeroPanelView.FromInstance(heroPanel);
        var portrait = view.portrait != null ? view.portrait.transform : null;
        if (portrait != null)
        {
            view.healthText = portrait.Find("HealthText (TMP)")?.GetComponent<TextMeshProUGUI>();
            view.attackText = portrait.Find("AttackText (TMP) (1)")?.GetComponent<TextMeshProUGUI>();
        }
        return view;
    }

    /// <summary>
    /// The run's claimed milestone rewards that still shape the fight, one row each, stacked
    /// (<see cref="GauntletPlayerBlessings"/>).
    /// </summary>
    private void FillPlayerRows(Side side) =>
        side.rowCount = GauntletPlayerBlessings.Fill(side.list, blessingRowPrefab, side.rows);

    private void FillOpponentRows(Side side)
    {
        side.rowCount = BlessingRow.Fill(side.list, blessingRowPrefab, side.rows,
            GauntletRun.IsActive ? GauntletRun.EnemyBlessings : null);
    }

    /// <summary>
    /// Places the rows at their authored offsets, the row nearest the hero first. The player's rows read
    /// down from their hero, so the nearest is the first; the enemy's read down towards theirs, so the
    /// nearest is the last (<paramref name="nearestLast"/>).
    /// </summary>
    private static void Layout(Side side, Vector2[] offsets, bool nearestLast)
    {
        if (side.list == null) return;

        side.list.gameObject.SetActive(side.rowCount > 0);
        var group = side.list.GetComponent<LayoutGroup>();
        if (group != null) group.enabled = false;

        int count = Mathf.Min(side.rowCount, side.rows.Count);
        for (int i = 0; i < count; i++)
        {
            var rect = (RectTransform)side.rows[i].transform;
            int fromHero = nearestLast ? count - 1 - i : i;

            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.localScale = Vector3.one;
            rect.anchoredPosition = RowOffset(offsets, fromHero);
        }
    }

    /// <summary>The authored offset of the n-th row from the hero; past the list, its last step repeated.</summary>
    private static Vector2 RowOffset(Vector2[] offsets, int n)
    {
        if (offsets == null || offsets.Length == 0) return Vector2.zero;
        if (n < offsets.Length) return offsets[n];

        int last = offsets.Length - 1;
        Vector2 step = last > 0 ? offsets[last] - offsets[last - 1] : Vector2.zero;
        return offsets[last] + step * (n - last);
    }

    // ---------------------------------------------------------------- Board

    /// <summary>
    /// Hides the board's hero and its passive indicators until their copies land. The hero's renderers
    /// are switched off rather than the hero itself, which has to stay live through the match setup;
    /// the indicators are faded, since their own Apply drives SetActive.
    /// </summary>
    private static void HideBoard(Side side)
    {
        var hero = side.agent.hero;

        foreach (var renderer in hero.GetComponentsInChildren<Renderer>(true))
            if (renderer.enabled)
            {
                renderer.enabled = false;
                side.hiddenRenderers.Add(renderer);
            }

        foreach (var canvas in hero.GetComponentsInChildren<Canvas>(true))
            if (canvas.enabled)
            {
                canvas.enabled = false;
                side.hiddenCanvases.Add(canvas);
            }

        var view = HeroPassiveIndicatorView.For(hero);
        if (view == null) return;

        foreach (var indicator in view.BoundIndicators)
        {
            if (indicator == null) continue;

            var group = indicator.GetComponent<CanvasGroup>();
            if (group == null) group = indicator.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            side.indicators.Add(indicator);
            side.indicatorGroups.Add(group);
        }
    }

    private static void ShowHero(Side side)
    {
        foreach (var renderer in side.hiddenRenderers)
            if (renderer != null) renderer.enabled = true;
        foreach (var canvas in side.hiddenCanvases)
            if (canvas != null) canvas.enabled = true;

        side.hiddenRenderers.Clear();
        side.hiddenCanvases.Clear();
    }

    private static void ShowIndicators(Side side)
    {
        foreach (var group in side.indicatorGroups)
            if (group != null)
            {
                group.DOKill();
                group.alpha = 1f;
            }
    }

    // ---------------------------------------------------------------- Appear

    /// <summary>Everything at its starting pose: off the screen, the separator shrunk, the dim clear.</summary>
    private void Pose()
    {
        _slid.Clear();
        _slidHomes.Clear();

        foreach (var side in new[] { _player, _opponent })
        {
            if (side.panel == null) continue;

            PoseOffscreen(side.panel, side.fromLeft);
            foreach (var row in RowsFromHero(side))
                PoseOffscreen(row, side.fromLeft);
        }

        if (separator != null)
        {
            separator.localScale = Vector3.one * separatorStartScale;
            separator.localRotation = _separatorRotation * Quaternion.Euler(0f, 0f, separatorSpin);
        }

        if (backdrop != null) SetAlpha(backdrop, 0f);
        if (clickLabel != null) clickLabel.alpha = 0f;
        SetPromptLive(false);
    }

    private void PoseOffscreen(RectTransform rect, bool fromLeft)
    {
        var one = new[] { rect };
        float offset = fromLeft
            ? PanelSlide.OffscreenLeftOffset(one, slideMargin)
            : PanelSlide.OffscreenRightOffset(one, slideMargin);

        _slid.Add(rect);
        _slidHomes.Add(rect.anchoredPosition);
        rect.anchoredPosition += new Vector2(offset, 0f);
    }

    /// <summary>A side's showing rows, the one nearest its hero first: the order they cascade in.</summary>
    private static List<RectTransform> RowsFromHero(Side side)
    {
        var rows = new List<RectTransform>();
        int count = Mathf.Min(side.rowCount, side.rows.Count);
        for (int i = 0; i < count; i++)
            rows.Add((RectTransform)side.rows[side.fromLeft ? i : count - 1 - i].transform);
        return rows;
    }

    private Sequence Appear()
    {
        var sequence = DOTween.Sequence();
        float start = Mathf.Max(0f, appearDelay);

        if (backdrop != null)
            sequence.Insert(start, backdrop.DOFade(_backdropAlpha, backdropFadeDuration).SetEase(backdropEase));

        if (separator != null)
        {
            sequence.Insert(start + separatorDelay,
                separator.DOScale(1f, separatorDuration).SetEase(separatorScaleEase));
            sequence.Insert(start + separatorDelay,
                separator.DOLocalRotateQuaternion(_separatorRotation, separatorDuration).SetEase(separatorRotateEase));
        }

        // Each hero, then its own rows one by one, nearest the hero first. Pose recorded them in that order.
        int index = 0;
        foreach (var side in new[] { _player, _opponent })
        {
            if (side.panel == null) continue;

            float heroStart = start + (side.fromLeft ? playerSlideDelay : opponentSlideDelay);
            sequence.Insert(heroStart,
                _slid[index].DOAnchorPos(_slidHomes[index], heroSlideDuration).SetEase(heroSlideEase));
            index++;

            int rows = Mathf.Min(side.rowCount, side.rows.Count);
            for (int turn = 0; turn < rows; turn++, index++)
                sequence.Insert(heroStart + rowsDelay + turn * rowStagger,
                    _slid[index].DOAnchorPos(_slidHomes[index], rowSlideDuration).SetEase(rowSlideEase));
        }

        return sequence;
    }

    /// <summary>
    /// Whether the prompt can be reached by the pointer at all. Its group is authored shut -- the
    /// game-over panel's draft opens it in code when it shows the prompt there -- so the match-up has to
    /// open it here too, or the button sits under a group that refuses every ray and the hover never
    /// fires either. Shut again as the match-up leaves, so an invisible prompt cannot eat a click.
    /// </summary>
    private void SetPromptLive(bool live)
    {
        if (clickLabel == null) return;

        clickLabel.blocksRaycasts = live;
        clickLabel.interactable = live;
    }

    private void StartLabelPulse()
    {
        if (clickLabel == null) return;

        clickLabel.DOKill();
        SetPromptLive(true);

        // Dead until it is up, so the match-up cannot be dismissed by a click that lands while the
        // button is still fading in.
        if (proceedButton != null) proceedButton.interactable = false;

        // Up once and stays up. No alpha pulse: the swords swinging are what says the button is live,
        // and a pulse over the top of them only made the blades fade in and out as they swung.
        clickLabel.DOFade(1f, labelFadeDuration).OnComplete(() =>
        {
            if (proceedButton != null) proceedButton.interactable = true;
        });
    }

    /// <summary>Sends the match-up away as a click would, once it has finished coming in.</summary>
    public void Dismiss() => _dismissed = true;

    // ---------------------------------------------------------------- Disappear

    private Sequence Disappear()
    {
        var sequence = DOTween.Sequence();
        var camera = Camera.main;

        foreach (var side in new[] { _player, _opponent })
            if (side.view != null) Launch(sequence, side, camera);

        sequence.Insert(fadeOutDelay, content.DOFade(0f, fadeOutDuration).SetEase(fadeOutEase));
        content.blocksRaycasts = false;
        return sequence;
    }

    /// <summary>The portrait onto the board's hero frame, each filled chip onto its passive's indicator.</summary>
    private void Launch(Sequence sequence, Side side, Camera camera)
    {
        var portrait = side.view.portrait != null ? (RectTransform)side.view.portrait.transform : null;
        var minion = side.agent.hero;
        var boardFrame = BoardFrame(minion);
        float leave = side.fromLeft ? playerFlightDelay : opponentFlightDelay;

        if (portrait != null && boardFrame != null && camera != null)
        {
            var flyer = Lift(portrait);
            var uiFrame = portrait.Find("Frame") as RectTransform;
            FlyTo(sequence, leave, heroFlightDuration, heroMoveEase, heroScaleEase, heroArc, flyer,
                uiFrame != null ? uiFrame : portrait, ScreenRect(boardFrame, camera));

            var frameImage = uiFrame != null ? uiFrame.GetComponent<Image>() : null;
            if (frameImage != null)
            {
                _tinted.Add((frameImage, frameImage.color));
                sequence.Insert(leave, frameImage.DOColor(boardFrame.color, heroFlightDuration).SetEase(heroMoveEase));
            }

            float landed = leave + heroFlightDuration;
            sequence.InsertCallback(landed, () =>
            {
                flyer.rect.gameObject.SetActive(false);
                ShowHero(side);
            });

            if (landingSmoke != null)
                sequence.InsertCallback(Mathf.Max(0f, landed + smokeDelay), () => Smoke(boardFrame));
        }
        else sequence.InsertCallback(fadeOutDelay + fadeOutDuration, () => ShowHero(side));

        var chips = side.view.passiveChips;
        int flown = 0;
        float lastLanding = leave + heroFlightDuration;
        for (int i = 0; chips != null && i < chips.Length; i++)
        {
            var chip = chips[i];
            if (chip == null || i >= side.passiveCount || i >= side.indicators.Count || camera == null) continue;

            var indicator = side.indicators[i];
            var group = side.indicatorGroups[i];
            if (indicator == null || !indicator.gameObject.activeInHierarchy) continue;

            var target = indicator.transform.Find("BG") as RectTransform;
            if (target == null) target = (RectTransform)indicator.transform;

            float delay = leave + chipDelay + flown++ * chipStagger;
            var flyer = Lift((RectTransform)chip.transform);
            // The chip's frame and the indicator's BG are the same panel sprite: match those, not the rects.
            var chipFrame = flyer.rect.Find("DefaultFrame") as RectTransform;
            FlyTo(sequence, delay, chipFlightDuration, chipEase, chipEase, chipArc, flyer,
                chipFrame != null ? chipFrame : flyer.rect, ScreenRect(target, camera));

            float landed = delay + chipFlightDuration;
            lastLanding = Mathf.Max(lastLanding, landed);
            sequence.Insert(landed, flyer.group.DOFade(0f, chipCrossfade));
            sequence.Insert(landed, group.DOFade(1f, chipCrossfade));
        }

        // Passives past the panel's chips, or ones no chip could reach, come in as the flights end.
        sequence.InsertCallback(lastLanding, () =>
        {
            foreach (var g in side.indicatorGroups)
                if (g != null && g.alpha <= 0f) g.DOFade(1f, chipCrossfade);
        });
    }

    /// <summary>A puff of smoke where a hero has just landed. It cleans itself up once its particles are gone.</summary>
    private void Smoke(SpriteRenderer boardFrame)
    {
        var local = boardFrame.sprite != null ? boardFrame.sprite.bounds.center : Vector3.zero;
        Vector3 at = boardFrame.transform.TransformPoint(local) + (Vector3)smokeOffset;

        var smoke = Instantiate(landingSmoke, at, landingSmoke.transform.rotation);
        smoke.transform.localScale = landingSmoke.transform.localScale * smokeScale;
        smoke.Play(true);

        var main = smoke.main;
        Destroy(smoke.gameObject, main.duration + main.startLifetime.constantMax + 0.5f);
    }

    /// <summary>The hero's frame sprite on the board: its view's, else the one the Hero prefab names Frame.</summary>
    private static SpriteRenderer BoardFrame(MinionController hero)
    {
        if (hero.view != null && hero.view.Frame != null) return hero.view.Frame;

        foreach (var sprite in hero.GetComponentsInChildren<SpriteRenderer>(true))
            if (sprite.name == "Frame") return sprite;
        return null;
    }

    /// <summary>Moves a piece out of the content, keeping where it is on screen, so the fade leaves it.</summary>
    private Flyer Lift(RectTransform rect)
    {
        var flyer = new Flyer
        {
            rect = rect,
            parent = rect.parent,
            sibling = rect.GetSiblingIndex(),
            localPosition = rect.localPosition,
            localScale = rect.localScale,
            localRotation = rect.localRotation,
        };
        flyer.group = rect.GetComponent<CanvasGroup>();
        if (flyer.group == null) flyer.group = rect.gameObject.AddComponent<CanvasGroup>();
        flyer.group.alpha = 1f;

        rect.SetParent(flyers, true);
        _flyers.Add(flyer);
        return flyer;
    }

    /// <summary>
    /// Flies <paramref name="flyer"/> so that <paramref name="visual"/> (it, or a part of it centred on it)
    /// ends up covering <paramref name="target"/>, a screen rect.
    /// </summary>
    private void FlyTo(Sequence sequence, float delay, float duration, Ease moveEase, Ease scaleEase, float arc,
        Flyer flyer, RectTransform visual, Rect target)
    {
        var canvas = flyers.GetComponentInParent<Canvas>().rootCanvas;
        var uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

        Rect now = ScreenRect(visual, uiCamera);
        float scale = now.width > 0f ? target.width / now.width : 1f;

        RectTransformUtility.ScreenPointToWorldPointInRectangle(flyers, target.center, uiCamera, out var targetWorld);
        RectTransformUtility.ScreenPointToWorldPointInRectangle(flyers, now.center, uiCamera, out var visualWorld);

        // The visual is centred on the flyer's pivot, so its offset from the pivot scales with it.
        Vector3 pivotToVisual = visualWorld - flyer.rect.position;
        Vector3 end = targetWorld - pivotToVisual * scale;

        var rect = flyer.rect;
        Vector3 start = rect.position;
        if (Mathf.Approximately(arc, 0f))
            sequence.Insert(delay, rect.DOMove(end, duration).SetEase(moveEase));
        else
        {
            Vector3 control = ArcControl(start, end, arc);
            sequence.Insert(delay, DOVirtual.Float(0f, 1f, duration, t =>
            {
                // A quadratic Bezier through the control point: the eased t still lands exactly on end.
                float u = 1f - t;
                rect.position = u * u * start + 2f * u * t * control + t * t * end;
            }).SetEase(moveEase));
        }
        sequence.Insert(delay, rect.DOScale(rect.localScale * scale, duration).SetEase(scaleEase));
    }

    /// <summary>
    /// The control point that bends a flight from <paramref name="start"/> to <paramref name="end"/> by
    /// <paramref name="arc"/> times its length, sideways from the straight line: positive away from the
    /// screen's centre, negative towards it. The same value bends both heroes alike, mirrored.
    /// </summary>
    private Vector3 ArcControl(Vector3 start, Vector3 end, float arc)
    {
        Vector3 mid = (start + end) * 0.5f;
        Vector3 along = end - start;
        Vector3 side = new Vector3(-along.y, along.x, 0f).normalized;

        flyers.GetWorldCorners(Corners);
        Vector3 centre = (Corners[0] + Corners[2]) * 0.5f;
        if (Vector3.Dot(side, mid - centre) < 0f) side = -side;

        return mid + side * (arc * along.magnitude);
    }

    private static readonly Vector3[] Corners = new Vector3[4];

    private static Rect ScreenRect(RectTransform rect, Camera camera)
    {
        rect.GetWorldCorners(Corners);
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, Corners[0]);
        Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, Corners[2]);
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    /// <summary>
    /// From the sprite's own bounds rather than the renderer's: the renderer is switched off while the
    /// intro is up, and a disabled renderer's bounds can't be trusted.
    /// </summary>
    private static Rect ScreenRect(SpriteRenderer sprite, Camera camera)
    {
        var local = sprite.sprite != null ? sprite.sprite.bounds : new Bounds(Vector3.zero, Vector3.one);
        var transform = sprite.transform;
        Vector2 min = camera.WorldToScreenPoint(transform.TransformPoint(local.min));
        Vector2 max = camera.WorldToScreenPoint(transform.TransformPoint(local.max));
        return Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y),
            Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
    }

    // ---------------------------------------------------------------- Reset

    /// <summary>Puts every piece back where it was authored, ready for the next match.</summary>
    private void Finish()
    {
        Kill();

        foreach (var side in new[] { _player, _opponent })
        {
            if (side == null) continue;
            ShowHero(side);
            ShowIndicators(side);
        }

        for (int i = _flyers.Count - 1; i >= 0; i--)
        {
            var flyer = _flyers[i];
            if (flyer.rect == null) continue;

            flyer.rect.SetParent(flyer.parent, false);
            flyer.rect.SetSiblingIndex(flyer.sibling);
            flyer.rect.localPosition = flyer.localPosition;
            flyer.rect.localScale = flyer.localScale;
            flyer.rect.localRotation = flyer.localRotation;
            flyer.rect.gameObject.SetActive(true);
            flyer.group.alpha = 1f;
        }
        _flyers.Clear();

        foreach (var (graphic, color) in _tinted)
            if (graphic != null) graphic.color = color;
        _tinted.Clear();

        for (int i = 0; i < _slid.Count; i++)
            if (_slid[i] != null) _slid[i].anchoredPosition = _slidHomes[i];

        if (separator != null)
        {
            separator.localScale = Vector3.one;
            separator.localRotation = _separatorRotation;
        }
        if (backdrop != null) SetAlpha(backdrop, _backdropAlpha);

        // A chip hovered as the click landed leaves its tooltip switched on; it would be up next time.
        foreach (var tooltip in content.GetComponentsInChildren<UITooltip>(true)) tooltip.Hide();

        content.gameObject.SetActive(false);
        content.alpha = 1f;
    }

    private void Kill()
    {
        if (_sequence != null)
        {
            _sequence.Kill();
            _sequence = null;
        }
        if (clickLabel != null) clickLabel.DOKill();
        SetPromptLive(false);
    }

    private static void SetAlpha(Graphic graphic, float alpha)
    {
        var color = graphic.color;
        color.a = alpha;
        graphic.color = color;
    }
}
