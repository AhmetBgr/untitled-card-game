using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The card selection panel: draws whatever choice <see cref="CardChoice"/> currently has open and routes
/// a click on an option back to it. Holds no game rules of its own — it is a view over the request, so a
/// different presentation can be swapped in without touching the core.
///
/// Options are spawned from a display-only card prefab (CardPreview Variant: Button + CardModal +
/// CardView). Deliberately NOT the full Card prefab, which carries CardController + DraggableItem and
/// would let the player drag a mid-selection option onto the board.
///
/// The panel GameObject is authored DISABLED and is only enabled while a selection is open. That rules
/// out subscribing from Awake/OnEnable — neither runs on a disabled object — so the wiring lives in a
/// static bootstrap instead: <see cref="Bootstrap"/> subscribes once per play session and resolves the
/// (possibly disabled) panel lazily when a request actually opens.
/// </summary>
public class CardSelectionPanel : MonoBehaviour
{
    [Tooltip("Parent the option cards are spawned under; drives their layout.")]
    [SerializeField] private Transform optionsContainer;

    [SerializeField] private TextMeshProUGUI promptText;

    [Tooltip("A copy kept for one screen's own picks (the end-of-match rewards): opened only while " +
             "routed to (Route), and never found as the scene's panel for in-match choices.")]
    [SerializeField] private bool routedOnly;

    [Tooltip("Display-only card prefab used for each option (CardPreview Variant).")]
    [SerializeField] private GameObject optionPrefab;

    [Tooltip("How long each option takes to pop in, and the stagger between them.")]
    [SerializeField] private float optionRevealDuration = 0.25f;
    [SerializeField] private float optionRevealStagger = 0.08f;

    [Tooltip("Resting size of every option card. The reveal tween lands here and hover swells out from here.")]
    [SerializeField] private float baseCardScale = 1f;

    [Tooltip("How much a hovered option swells, on top of the base scale. The layout group doesn't control child size, so growing a card shifts nothing around it.")]
    [SerializeField] private float hoverScale = 1.2f;

    [Header("Forged preview")]
    [Tooltip("Shows the forged (upgraded) version of a hovered option under it, so a draft or reward pick " +
             "can be weighed with what it forges into. Off for in-match choices, which have no forging.")]
    [SerializeField] private bool showForgedOnHover;
    [SerializeField] private float forgedPreviewScale = 0.8f;
    [Tooltip("Space between the bottom of the hovered option (swollen) and the forged preview, in the panel's units.")]
    [SerializeField] private float forgedPreviewGap = 12f;
    [Tooltip("Sliced panel drawn behind the forged preview, the way the perk tooltip is boxed. None for a bare card.")]
    [SerializeField] private Sprite forgedBackdropSprite;
    [SerializeField] private Color forgedBackdropColor = Color.white;
    [Tooltip("How far the panel reaches past the preview's visible frame, in the panel's units.")]
    [SerializeField] private float forgedBackdropPadding = 24f;
    [Tooltip("The least the preview (backdrop included) stays inside the screen's edges, in the panel's units.")]
    [SerializeField] private float forgedScreenMargin = 8f;

    [Header("Taking a card")]
    [Tooltip("OFF: a taken card resolves the choice outright, with nothing played first. On, the taken " +
             "card is punched and flown into the deck while the others bow out, and closeDelay below is " +
             "what holds the panel up for it.")]
    [SerializeField] private bool playTakeAnimation = false;

    [Tooltip("How long the panel stays up after a card is taken. Only worth anything with " +
             "playTakeAnimation on -- with it off this is a dead pause, so it is 0. Keep it at or above " +
             "punch + fly, or the panel goes away mid-flight.")]
    [SerializeField] private float closeDelay = 0f;

    [Tooltip("Where a taken card flies to when nothing else says otherwise. The rewards screen points " +
             "this at the loadout's deck list instead (see SetDeckTarget).")]
    [SerializeField] private RectTransform deckTarget;

    [Tooltip("How hard the taken card is punched the moment it is clicked.")]
    [SerializeField] private float pickPunchScale = 0.18f;
    [SerializeField] private float pickPunchDuration = 0.08f;

    [Tooltip("How long the taken card takes to fly into the deck, and how small it gets on the way.")]
    [SerializeField] private float flyDuration = 0.15f;
    [SerializeField] private float flyEndScale = 0.3f;

    [Tooltip("How the cards beside the taken one bow out.")]
    [SerializeField] private float discardDuration = 0.08f;
    [SerializeField] private float discardScale = 0.85f;

    [Header("See Board")]
    [Tooltip("Off for a panel with no backdrop to look past (the end-of-match rewards' copy): the " +
             "button is never shown.")]
    [SerializeField] private bool offerSeeBoard = true;

    [Tooltip("Optional. Toggles the options out of the way so the board underneath can be read. Left " +
             "unassigned, one is built at runtime — same self-installing approach as BoardInteractionGate, " +
             "so the feature needs no scene wiring. Assign your own to restyle it.")]
    [SerializeField] private Button seeBoardButton;

    [SerializeField] private string seeBoardLabel = "See Board";
    [SerializeField] private string showCardsLabel = "Show Cards";

    private Vector3 BaseScale => Vector3.one * baseCardScale;

    private readonly List<GameObject> _spawned = new List<GameObject>();

    /// <summary>One click takes one card: a second, on the option beside it, is not a second pick.</summary>
    private bool _taken;

    /// <summary>
    /// Where this request's card should fly, handed over by whoever opened it. Cleared by every
    /// <see cref="Open"/>, so a target that belonged to a reward pick can never be reused by an
    /// in-match choice that has no deck to go into.
    /// </summary>
    private RectTransform _deckTargetOverride;

    /// <summary>The wait between the click and resolving the choice. Killed if the panel goes away first.</summary>
    private Tween _closeCall;

    /// <summary>The take / discard animations, killed together when the options are cleared.</summary>
    private readonly List<Sequence> _pickAnims = new List<Sequence>();

    /// <summary>The forged preview under a hovered option, built on first use and reused.</summary>
    private RectTransform _forgedPreview;
    private CardModal _forgedModal;
    private CardView _forgedView;
    private RectTransform _forgedBackdrop;

    // True while the options are tucked away and the player is reading the board.
    private bool _peeking;

    private TextMeshProUGUI _seeBoardButtonLabel;

    /// <summary>
    /// Present when the assigned See Board button is the authored toggle (Button_Toggle) rather than a
    /// plain Button. When it is, the toggle owns the click and reports the new state through
    /// onValueChanged; the runtime fallback button has none and falls back to Button.onClick.
    /// </summary>
    private ToggleButton _seeBoardToggle;

    /// <summary>
    /// The panel's own full-screen Image: the dimming backdrop, and — because it is a raycast target
    /// covering the whole canvas — the thing that stops uGUI clicks (dragging a card out of hand, the
    /// end-turn button) reaching anything underneath. Peeking drops its alpha to 0 but deliberately
    /// leaves it enabled and raycasting, which is what keeps the player locked out while they look.
    /// </summary>
    private Image _backdrop;
    private Color _backdropColor;
    private bool _backdropCaptured;

    private static CardSelectionPanel _instance;
    private static CardSelectionPanel _routed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        // Remove-then-add is idempotent (removing an unsubscribed handler is a no-op), so this stays
        // correct if the bootstrap runs again without a domain reload — e.g. with Enter Play Mode Options
        // set to skip it, where the statics survive between sessions.
        CardChoice.Opened -= HandleOpened;
        CardChoice.Closed -= HandleClosed;
        CardChoice.Opened += HandleOpened;
        CardChoice.Closed += HandleClosed;
    }

    private static void HandleOpened(IReadOnlyList<CardSO> options, string prompt, bool faceDown)
    {
        var panel = Resolve();
        if (panel != null) panel.Open(options, prompt, faceDown);
    }

    private static void HandleClosed()
    {
        var panel = Resolve();
        if (panel != null) panel.Close();
    }

    /// <summary>
    /// The panel in the current scene, disabled or not. Re-found whenever the cached one has gone away,
    /// so reloading the match scene (Replay) picks up the new scene's panel instead of a destroyed one —
    /// Unity's overloaded == reports a destroyed object as null, which is what drives the re-find.
    /// </summary>
    public static CardSelectionPanel Resolve()
    {
        if (_routed != null) return _routed;

        if (_instance == null)
            foreach (var panel in FindObjectsOfType<CardSelectionPanel>(true))
                if (!panel.routedOnly)
                {
                    _instance = panel;
                    break;
                }
        return _instance;
    }

    /// <summary>
    /// Sends every choice to <paramref name="panel"/> until routed back with null: the end-of-match rewards
    /// open their picks in their own copy of the panel, beside the run's loadout, rather than the match's.
    /// </summary>
    public static void Route(CardSelectionPanel panel) => _routed = panel;

    /// <summary>Show the panel with these options. Public so the panel can be driven directly, the way
    /// PopupManager exposes OpenPopup/CloseCurPopup.
    ///
    /// <paramref name="faceDown"/> draws the options as card backs and makes them inert: that is the
    /// spectator view of the OPPONENT choosing, where the player is being told a choice is happening but
    /// must not see what is on offer or be able to pick from it.</summary>
    public void Open(IReadOnlyList<CardSO> options, string prompt, bool faceDown = false)
    {
        Clear();
        _taken = false;
        _deckTargetOverride = null;

        if (optionsContainer == null || optionPrefab == null)
        {
            Debug.LogWarning("CardSelectionPanel: not wired up (options container / prefab missing)");
            return;
        }

        if (promptText != null)
            promptText.text = string.IsNullOrEmpty(prompt) ? "Choose a card" : prompt;

        // Shown before spawning so the layout group is active and positions the options as they arrive.
        gameObject.SetActive(true);

        CaptureBackdrop();

        // Every request starts with the cards up, however the last one ended.
        _peeking = false;

        // Only an interactive request gets the toggle: a face-down spectator panel closes itself after a
        // beat and asks nothing of the player, so a button to tuck it away would be dead weight.
        SetUpSeeBoardButton(interactive: !faceDown);
        ApplyPeekState();

        var owner = GameManager.Instance != null ? GameManager.Instance.player : null;

        for (int i = 0; i < options.Count; i++)
        {
            CardSO option = options[i]; // captured per iteration for the click closure
            if (option == null) continue;

            GameObject go = Instantiate(optionPrefab, optionsContainer);
            go.SetActive(true);
            _spawned.Add(go);

            var modal = go.GetComponent<CardModal>();
            var view = go.GetComponent<CardView>();
            if (modal != null)
            {
                // isPlayerMinion drives face vs. back in CardView.UpdateView: true renders the full face
                // (cost, stats, art), false renders the card back — which is exactly what the face-down
                // spectator view wants, upgraded back sprite and all.
                modal.UpdateModal(option, owner, !faceDown);
                if (view != null)
                {
                    view.UpdateView(modal);

                    // Same outlines as a card in hand. Playable is set once rather than per-frame: the
                    // panel blocks play while it is open, so available mana can't move underneath it.
                    // A face-down card is not the player's to play, so it gets no playable outline.
                    view.SetPlayableOutline(!faceDown && CardView.IsPlayableNow(modal));
                    view.SetHoveredOutline(false);

                    // CardPreview Variant has no EventTrigger/CardController, so hover needs its own
                    // source here (see CardHoverOutline). Skipped while face down: swelling on hover
                    // would advertise these as pickable when they are not.
                    if (!faceDown)
                    {
                        var hover = go.GetComponent<CardHoverOutline>();
                        if (hover == null) hover = go.AddComponent<CardHoverOutline>();
                        // Rest scale passed explicitly: the reveal tween below starts the card at zero, so
                        // the component must not infer its resting size from the transform.
                        hover.ConfigureHoverScale(hoverScale, BaseScale);

                        if (showForgedOnHover)
                        {
                            GameObject hovered = go; // captured per iteration, like the option itself
                            hover.HoverChanged += on => ShowForged(on ? option : null, hovered);
                        }
                    }
                }
            }

            var button = go.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                // Belt and braces: CardChoice.TryResolveClick already refuses face-down requests, but an
                // inert button also drops the hover/press tinting that would make these look clickable.
                button.interactable = !faceDown;
                if (!faceDown)
                {
                    GameObject card = go; // captured per iteration, like the option itself
                    button.onClick.AddListener(() => Pick(option, card));
                }
            }

            // Stagger the reveal so the options read as being dealt out rather than appearing at once.
            go.transform.localScale = Vector3.zero;
            go.transform.DOScale(baseCardScale, optionRevealDuration).SetDelay(i * optionRevealStagger);
        }
    }

    /// <summary>
    /// Points this request's flight at a spot in the scene -- the deck list the card is about to join.
    /// Set after the request opens, which clears it: a panel serving an in-match choice has no deck to
    /// fly to and must not inherit the last reward pick's. Null falls back to the authored
    /// <see cref="deckTarget"/>.
    /// </summary>
    public void SetDeckTarget(RectTransform target) => _deckTargetOverride = target;

    /// <summary>
    /// Takes a card: the choice is held open for <see cref="closeDelay"/> while the card is seen going
    /// into the deck, then resolved. Resolving is what closes the panel (CardChoice ends the request,
    /// which fires Closed), so the delay here IS the delay before the panel goes away.
    /// </summary>
    private void Pick(CardSO card, GameObject picked)
    {
        if (_taken) return;
        _taken = true;

        if (playTakeAnimation) PlayTaken(picked);

        if (closeDelay <= 0f)
        {
            CardChoice.Instance.TryResolveClick(card);
            return;
        }

        // Unscaled, so a paused game can never leave the choice hanging open.
        _closeCall = DOVirtual.DelayedCall(closeDelay, () =>
        {
            _closeCall = null;
            CardChoice.Instance.TryResolveClick(card);
        }, ignoreTimeScale: true);
    }

    /// <summary>
    /// The card going into the deck: the one taken is punched and then flies into the deck list,
    /// shrinking and fading, while the cards beside it bow out. Hover and clicks are switched off on all
    /// of them first, so nothing swells or picks again while this plays.
    /// </summary>
    private void PlayTaken(GameObject picked)
    {
        RectTransform target = _deckTargetOverride != null ? _deckTargetOverride : deckTarget;

        // Switched off before anything moves. A layout group re-flows its row whenever a child changes
        // -- SetAsLastSibling below is enough to dirty it -- and the rebuild runs after the tweens each
        // frame, so it would drag the flying card straight back into line.
        if (optionsContainer != null && optionsContainer.TryGetComponent(out LayoutGroup layout))
            layout.enabled = false;

        foreach (var go in _spawned)
        {
            if (go == null) continue;

            if (go.TryGetComponent(out Button button)) button.interactable = false;
            if (go.TryGetComponent(out CardHoverOutline hover)) hover.enabled = false;
            go.transform.DOKill();

            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();

            var seq = DOTween.Sequence();
            _pickAnims.Add(seq);

            if (go != picked)
            {
                seq.Append(go.transform.DOScale(baseCardScale * discardScale, discardDuration).SetEase(Ease.InQuad));
                seq.Join(group.DOFade(0f, discardDuration));
                continue;
            }

            // Over the cards beside it on the way out.
            go.transform.SetAsLastSibling();

            seq.Append(go.transform.DOPunchScale(BaseScale * pickPunchScale, pickPunchDuration, 6, 0.6f));
            if (target != null)
            {
                seq.Append(go.transform.DOMove(target.position, flyDuration).SetEase(Ease.InBack));
                seq.Join(go.transform.DOScale(baseCardScale * flyEndScale, flyDuration));
                seq.Join(group.DOFade(0f, flyDuration).SetEase(Ease.InQuad));
            }
        }
    }

    // ---------------------------------------------------------------- Forged preview

    /// <summary>
    /// Shows what <paramref name="option"/> forges into, under the option's card; null hides it. An
    /// option that is already forged, or has nothing to forge into, shows nothing.
    /// </summary>
    private void ShowForged(CardSO option, GameObject card)
    {
        CardSO forged = option != null && !option.isUpgraded ? option.upgradedVersion : null;
        if (forged == null || card == null || !EnsureForgedPreview())
        {
            HideForged();
            return;
        }

        var owner = GameManager.Instance != null ? GameManager.Instance.player : null;
        _forgedModal.UpdateModal(forged, owner, true);
        _forgedView.UpdateView(_forgedModal);
        _forgedView.SetPlayableOutline(false);
        _forgedView.SetHoveredOutline(false);

        // Centred under the option as it stands swollen, in this panel's space (the options row sits
        // unscaled in it), so it never overlaps the options beside the hovered one.
        var self = (RectTransform)transform;
        var cardRect = (RectTransform)card.transform;
        Vector3 local = self.InverseTransformPoint(cardRect.position);
        float cardHalf = cardRect.rect.height * baseCardScale * hoverScale * 0.5f;
        float previewHalf = _forgedPreview.rect.height * forgedPreviewScale * 0.5f;
        _forgedPreview.localPosition = new Vector3(local.x, local.y - cardHalf - forgedPreviewGap - previewHalf, 0f);
        _forgedPreview.gameObject.SetActive(true);

        var visual = _forgedView.VisualRect != null ? _forgedView.VisualRect : _forgedPreview;
        if (_forgedBackdrop != null) CardTooltipBackdrop.Fit(_forgedBackdrop, new[] { visual }, forgedBackdropPadding);

        // Kept wholly on screen: an option at the edge of a row, or a low row, would otherwise push its
        // preview off the bottom or the side.
        var parentCanvas = GetComponentInParent<Canvas>();
        if (parentCanvas != null)
        {
            Rect bounds = CardTooltipBackdrop.LocalBounds(_forgedBackdrop != null ? new[] { _forgedBackdrop } : new[] { visual }, self);
            Rect screen = CardTooltipBackdrop.LocalBounds(new[] { (RectTransform)parentCanvas.rootCanvas.transform }, self);
            var delta = (Vector3)CardTooltipBackdrop.ShiftInside(bounds, screen, forgedScreenMargin);
            _forgedPreview.localPosition += delta;
            if (_forgedBackdrop != null) _forgedBackdrop.localPosition += delta;
        }
    }

    private void HideForged()
    {
        if (_forgedPreview != null) _forgedPreview.gameObject.SetActive(false);
        if (_forgedBackdrop != null) _forgedBackdrop.gameObject.SetActive(false);
    }

    /// <summary>
    /// The forged preview: one more display-only card, on a canvas of its own sorted above the root so
    /// it draws over the backdrop, with no raycasts so it never takes the hover from the option.
    /// </summary>
    private bool EnsureForgedPreview()
    {
        if (_forgedPreview != null) return true;
        if (optionPrefab == null) return false;

        var go = Instantiate(optionPrefab, transform, false);
        go.name = "ForgedPreview";
        _forgedPreview = (RectTransform)go.transform;
        _forgedPreview.anchorMin = _forgedPreview.anchorMax = _forgedPreview.pivot = new Vector2(0.5f, 0.5f);
        _forgedPreview.localScale = Vector3.one * forgedPreviewScale;
        _forgedModal = go.GetComponent<CardModal>();
        _forgedView = go.GetComponent<CardView>();
        if (_forgedModal == null || _forgedView == null)
        {
            Debug.LogWarning("CardSelectionPanel: the option prefab needs a CardModal and a CardView for the forged preview.");
            Destroy(go);
            _forgedPreview = null;
            return false;
        }

        if (go.TryGetComponent(out Button button)) button.interactable = false;

        var parentCanvas = GetComponentInParent<Canvas>();
        var root = parentCanvas != null ? parentCanvas.rootCanvas : null;
        var canvas = go.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        if (root != null)
        {
            canvas.sortingLayerID = root.sortingLayerID;
            canvas.sortingOrder = root.sortingOrder + 100;
        }

        var group = go.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        // Just under the preview's canvas, so it draws over the options but behind the card.
        if (forgedBackdropSprite != null)
            _forgedBackdrop = CardTooltipBackdrop.Build(transform, "ForgedPreviewBackdrop", forgedBackdropSprite, forgedBackdropColor, root, canvas.sortingOrder - 1);

        go.SetActive(false);
        return true;
    }

    /// <summary>
    /// While set, <see cref="Close"/> clears the options but leaves the panel's object on, because
    /// something around it is fading it away: the end-of-match tab a reward pick sits on, whose fade
    /// switch takes the whole tab out (<see cref="GameOverTabs"/>). The panel closes before the reward is
    /// marked taken and the tab is switched (<see cref="CardChoice.TryResolveClick"/> ends the request
    /// first), so switching off here would snap it away a frame before that fade even started, leaving
    /// only the loadout beside it to be seen fading.
    ///
    /// Whoever sets it owns clearing it and owns putting the panel away -- which for a tab is the park at
    /// the end of the switch (<see cref="BattleRewardsPanel"/>).
    /// </summary>
    public bool KeepOnClose { get; set; }

    /// <summary>Clear the options and hide the panel again.</summary>
    public void Close()
    {
        Clear();

        // Restore the backdrop before going away: Open() resets _peeking, but a panel left transparent
        // would flash the board through on the next request's first frame.
        _peeking = false;
        ApplyPeekState();

        if (!KeepOnClose) gameObject.SetActive(false);
    }

    /// <summary>
    /// Tuck the options away to read the board, or bring them back. The REQUEST stays open throughout —
    /// this only moves pixels. That is what keeps the player locked out while peeking: BoardInteractionGate
    /// blocks the board on CardChoice.HasActiveRequest (minions, hero, hover feedback), the backdrop keeps
    /// eating uGUI clicks (card drags, end turn), and GameManager.CancelPlayingCard is already inert while
    /// a request is open. Nothing but this button responds.
    /// </summary>
    public void ToggleSeeBoard()
    {
        SetPeeking(!_peeking);
    }

    /// <summary>Peek state as an explicit value, so the toggle can report what it just switched to.</summary>
    public void SetPeeking(bool peeking)
    {
        _peeking = peeking;
        ApplyPeekState();
    }

    private void ApplyPeekState()
    {
        if (promptText != null) promptText.gameObject.SetActive(!_peeking);
        if (optionsContainer != null) optionsContainer.gameObject.SetActive(!_peeking);

        if (_backdrop != null && _backdropCaptured)
        {
            // Alpha only — enabled and raycastTarget stay untouched so the invisible backdrop still
            // swallows every click that is not the button itself.
            Color c = _backdropColor;
            _backdrop.color = _peeking ? new Color(c.r, c.g, c.b, 0f) : c;
        }

        if (_seeBoardButtonLabel != null)
            _seeBoardButtonLabel.text = _peeking ? showCardsLabel : seeBoardLabel;

        // notify: false — the toggle is usually the thing that told us to get here, and Open()/Close()
        // reset the peek without a click at all. Either way it must not echo the change back.
        if (_seeBoardToggle != null)
            _seeBoardToggle.SetIsOn(_peeking, notify: false);
    }

    private void CaptureBackdrop()
    {
        if (_backdrop == null) _backdrop = GetComponent<Image>();

        // Captured once, and only from a non-peeking state (Open resets _peeking before this runs), so a
        // re-open mid-session can never bake the transparent colour in as the "original".
        if (_backdrop != null && !_backdropCaptured)
        {
            _backdropColor = _backdrop.color;
            _backdropCaptured = true;
        }
    }

    private void SetUpSeeBoardButton(bool interactive)
    {
        if (!interactive || !offerSeeBoard)
        {
            if (seeBoardButton != null) seeBoardButton.gameObject.SetActive(false);
            return;
        }

        if (seeBoardButton == null) seeBoardButton = BuildSeeBoardButton();
        if (seeBoardButton == null) return;

        _seeBoardButtonLabel = seeBoardButton.GetComponentInChildren<TextMeshProUGUI>(true);
        _seeBoardToggle = seeBoardButton.GetComponent<ToggleButton>();

        seeBoardButton.gameObject.SetActive(true);
        // Last sibling so it draws above the backdrop and stays clickable while everything else is hidden.
        seeBoardButton.transform.SetAsLastSibling();

        seeBoardButton.onClick.RemoveAllListeners();

        if (_seeBoardToggle != null)
        {
            // The toggle already flipped itself on the click, so take its state rather than inverting
            // ours — going through ToggleSeeBoard here would flip a second time and cancel it out.
            _seeBoardToggle.onValueChanged.RemoveAllListeners();
            _seeBoardToggle.onValueChanged.AddListener(SetPeeking);
        }
        else
        {
            seeBoardButton.onClick.AddListener(ToggleSeeBoard);
        }
    }

    /// <summary>
    /// Builds the fallback toggle. Deliberately plain — it borrows the prompt's font so it matches, and
    /// exists so the feature works without anyone opening the scene. Assign <see cref="seeBoardButton"/>
    /// to replace it with an authored one.
    /// </summary>
    private Button BuildSeeBoardButton()
    {
        var go = new GameObject("SeeBoardButton", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(transform, false);

        var rt = (RectTransform)go.transform;
        // Top-right: the one corner of the board that stays empty. Anchored bottom-centre it sat on top
        // of the player's hand, which is exactly what they peek at the board to look at.
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-32f, -32f);
        rt.sizeDelta = new Vector2(240f, 68f);

        var background = go.GetComponent<Image>();
        background.color = new Color(0.10f, 0.10f, 0.12f, 0.95f);

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(go.transform, false);

        var lrt = (RectTransform)labelGo.transform;
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;

        var label = labelGo.GetComponent<TextMeshProUGUI>();
        label.text = seeBoardLabel;
        label.alignment = TextAlignmentOptions.Center;
        label.enableAutoSizing = true;
        label.fontSizeMin = 18f;
        label.fontSizeMax = 34f;
        label.color = Color.white;
        label.raycastTarget = false; // the Button owns the click; the label must not intercept it
        if (promptText != null && promptText.font != null) label.font = promptText.font;

        return go.GetComponent<Button>();
    }

    private void Clear()
    {
        if (_closeCall != null && _closeCall.IsActive()) _closeCall.Kill();
        _closeCall = null;

        // Back on for the next request: PlayTaken switches it off to let a card fly out of the row.
        if (optionsContainer != null && optionsContainer.TryGetComponent(out LayoutGroup layout))
            layout.enabled = true;

        foreach (var seq in _pickAnims)
            if (seq != null && seq.IsActive()) seq.Kill();
        _pickAnims.Clear();

        foreach (var go in _spawned)
        {
            if (go == null) continue;
            go.transform.DOKill();
            Destroy(go);
        }
        _spawned.Clear();
        _taken = false;
        HideForged();
    }

    // The close delay is a free-standing tween, not bound to this object, so a panel that goes away
    // mid-delay (scene reload) would otherwise leave it running with nothing to resolve into.
    private void OnDestroy() => Clear();
}
