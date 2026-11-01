using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The end-of-match panel's two bottom-right buttons: the way from the panel into the run's next fight,
/// and the way back to the rewards it still owes.
///
/// They belong to the panel rather than to any one tab: they sit in the bottom right corner through the
/// whole review -- Result, Statistics, Hero Info, Battles -- so leaving is always in the same place,
/// whatever is being read. Neither is the rewards panel's own Proceed
/// (<see cref="BattleRewardsPanel"/>), which still gates the rows that battle paid out. They only show in
/// a run still being fought, and not at all through the run's draft (<see cref="GameOverDraft"/>), whose
/// own "click anywhere" prompt is the way on from there, nor over a reward pick's own screen
/// (<see cref="BattleRewardsPanel.PickShowing"/>), which reaches into this same corner.
///
/// One is up at a time, never both, because they answer the same question at opposite ends of it:
///   * Collect Rewards, while the battle still owes something, and only on the tabs the rewards are not
///     on: a click brings the Result tab, where they sit, back. The Result tab has them in plain view,
///     so nothing is shown there. It breathes while it is up -- its text and its image pulsing their
///     alpha together -- the way the draft's own "click anywhere" prompt pulses: it is not where the
///     player is looking, so it has to ask to be noticed.
///   * Proceed, once nothing is owed, on every tab: a click goes straight to the fight
///     (<see cref="GauntletRun.FightNextBattle"/>). The match-up is shown by the reloaded Game scene
///     itself, over its fresh board (<see cref="VersusIntro"/>).
///
/// They were one button swapping its label; they are two so each can be styled, placed and sounded on its
/// own. Left without a Collect Rewards button authored, the Proceed button falls back to the old single
/// button, standing in for both and reading <see cref="collectRewardsLabel"/> while it is the way back.
///
/// Both wait for the panel's entrance (<see cref="GameOverPanelIntro"/>) to finish before they first show.
/// Each is hidden by fading (a CanvasGroup) rather than switched off, which keeps this component -- living
/// on the Proceed button -- running to bring them back.
/// </summary>
public class GameOverVersus : MonoBehaviour
{
    [Header("Proceed")]
    [Tooltip("The button that leaves for the next fight. Defaults to this object's own.")]
    [SerializeField] private Button proceedButton;

    [Tooltip("What fades that button in and out. Left empty, the button's own object is used, which is " +
             "right for a plain button -- but not for one whose art lives on a parent, as the crossed-" +
             "swords prompt's does: there the button is only the hover zone inside it, and fading that " +
             "would fade nothing anybody can see.")]
    [SerializeField] private CanvasGroup proceedGroup;

    [Tooltip("How long a button takes to fade in or out when it comes and goes.")]
    [SerializeField] private float fadeDuration = 0.2f;

    [Header("Collect Rewards")]
    [Tooltip("The separate button that goes back to the rewards the battle still owes. Left empty, the " +
             "Proceed button does both jobs, reading Collect Rewards while the rewards are owed.")]
    [SerializeField] private Button collectRewardsButton;

    [Tooltip("What fades it, as above. Left empty, the button's own object is used.")]
    [SerializeField] private CanvasGroup collectGroup;

    [Tooltip("The pieces of that button whose alpha pulses while it is up: its text and its image. Left " +
             "empty, everything drawn under the button is used but its own background, which is the " +
             "raycast target and authored invisible.")]
    [SerializeField] private Graphic[] breathingPieces;

    [Tooltip("How far the pulse dips, as a share of each piece's authored alpha.")]
    [Range(0f, 1f)] [SerializeField] private float breathMinAlpha = 0.45f;

    [Tooltip("How long one half of the pulse takes: full down to dipped, then the same back up.")]
    [SerializeField] private float breathDuration = 0.7f;

    [SerializeField] private Ease breathEase = Ease.InOutSine;

    [Header("Label")]
    [Tooltip("The Proceed button's text. Defaults to the first TextMeshProUGUI under it.")]
    [SerializeField] private TextMeshProUGUI label;
    [Tooltip("The Collect Rewards button's text. Defaults to the first TextMeshProUGUI under it.")]
    [SerializeField] private TextMeshProUGUI collectLabel;
    [Tooltip("While the battle's rewards are still owed: what is waited on.")]
    [SerializeField] private string collectRewardsLabel = "Collect Rewards";
    [Tooltip("Once nothing is owed: the way into the next fight.")]
    [SerializeField] private string proceedLabel = "Click to Proceed";

    /// <summary>One of the buttons: what fades it, what it reads, and the alpha last asked of it.</summary>
    private class Pill
    {
        public Button button;
        public CanvasGroup group;
        public TextMeshProUGUI label;

        /// <summary>The alpha last asked of the group; -1 before the first refresh, which sets it outright.</summary>
        public float targetAlpha = -1f;
    }

    private Pill _proceed;

    /// <summary>The separate Collect Rewards button, or null where Proceed stands in for both.</summary>
    private Pill _collect;

    /// <summary>What pulses while Collect Rewards is up, and the alpha each piece was authored at.</summary>
    private Graphic[] _breathing;
    private float[] _breathAlphas;
    private bool _breathingNow;

    private GameOverPanelIntro _intro;
    private bool _leaving;

    private void Awake()
    {
        if (proceedButton == null) proceedButton = GetComponent<Button>();
        _proceed = Build(proceedButton, proceedGroup, ref label);

        // Its own button, or nothing: pointed back at Proceed it would be the old single button twice over.
        if (collectRewardsButton != null && collectRewardsButton != proceedButton)
            _collect = Build(collectRewardsButton, collectGroup, ref collectLabel);

        CollectBreathingPieces();

        _intro = GetComponentInParent<GameOverPanelIntro>(true);

        if (proceedButton != null) proceedButton.onClick.AddListener(Go);
        if (_collect != null) _collect.button.onClick.AddListener(Collect);

        Refresh();
    }

    /// <summary>Gathers what a button needs to be faded and labelled, adding the group it fades by.</summary>
    private static Pill Build(Button button, CanvasGroup group, ref TextMeshProUGUI label)
    {
        if (button == null) return null;

        // Left unwired on a button whose art is elsewhere, the label simply stays null and the text is
        // the prefab's own to set -- Refresh writes nothing it was not given.
        if (label == null) label = button.GetComponentInChildren<TextMeshProUGUI>(true);

        if (group == null)
        {
            group = button.GetComponent<CanvasGroup>();
            if (group == null) group = button.gameObject.AddComponent<CanvasGroup>();
        }

        return new Pill { button = button, group = group, label = label };
    }

    /// <summary>
    /// Works out what breathes with the Collect Rewards button and the alpha each piece rests at, which
    /// is the top of its pulse. Authored empty, it is everything drawn under the button but the button's
    /// own background: that one is its raycast target, authored invisible, and pulsing it would bring a
    /// panel sprite up behind the text that is not meant to be seen at all.
    /// </summary>
    private void CollectBreathingPieces()
    {
        var pieces = new List<Graphic>();

        if (_collect != null)
        {
            if (breathingPieces != null && breathingPieces.Length > 0)
            {
                foreach (var piece in breathingPieces)
                    if (piece != null) pieces.Add(piece);
            }
            else
            {
                var own = _collect.button.gameObject;
                foreach (var piece in _collect.button.GetComponentsInChildren<Graphic>(true))
                    if (piece != null && piece.gameObject != own) pieces.Add(piece);
            }
        }

        _breathing = pieces.ToArray();
        _breathAlphas = new float[_breathing.Length];
        for (int i = 0; i < _breathing.Length; i++) _breathAlphas[i] = _breathing[i].color.a;
    }

    /// <summary>
    /// Starts or stops the pulse. Starting snaps the pieces back to full first, so a restart never
    /// inherits a mid-dip alpha -- invisible, the button being faded in from nothing around it. Stopping
    /// eases them back to full over the button's own fade, so the two finish together and nothing pops
    /// as it leaves.
    /// </summary>
    private void SetBreathing(bool on)
    {
        if (_breathing == null || _breathing.Length == 0 || _breathingNow == on) return;
        _breathingNow = on;

        for (int i = 0; i < _breathing.Length; i++)
        {
            var piece = _breathing[i];
            if (piece == null) continue;

            float full = _breathAlphas[i];
            piece.DOKill();

            if (!on)
            {
                if (fadeDuration > 0f && Application.isPlaying)
                    piece.DOFade(full, fadeDuration).SetUpdate(true);
                else SetAlpha(piece, full);
                continue;
            }

            SetAlpha(piece, full);
            if (!Application.isPlaying) continue;

            piece.DOFade(breathMinAlpha * full, breathDuration)
                .SetEase(breathEase)
                .SetLoops(-1, LoopType.Yoyo)
                .SetUpdate(true);
        }
    }

    private static void SetAlpha(Graphic piece, float alpha)
    {
        var colour = piece.color;
        colour.a = alpha;
        piece.color = colour;
    }

    private void OnDestroy()
    {
        if (_proceed != null) _proceed.group.DOKill();
        if (_collect != null) _collect.group.DOKill();

        if (_breathing == null) return;
        foreach (var piece in _breathing)
            if (piece != null) piece.DOKill();
    }

    // Polled: what they wait on -- picks, rewards, the tab being read -- changes from all over the panel.
    private void Update() => Refresh();

    private void Refresh()
    {
        bool ready = InRun && !Owed && !_leaving;

        // Not before the panel has finished arriving: the title's rise, the bar's drop and the opening
        // tab's slide are all done first. The draft has its own way on (a click anywhere), so both stay
        // out of sight through it.
        var tabs = GameOverTabs.Resolve();
        bool entered = (_intro == null || !_intro.IsEntering)
                       && (tabs == null || (!tabs.IsBarDropping && !tabs.IsSwitching));
        bool onResult = tabs != null && tabs.Current == GameOverTabs.TabKind.Result;

        // Nor over a reward pick's own screen: the selection panel reaches into this same corner, and
        // offering a way back to rewards the player is already taking is no offer at all.
        bool up = InRun && !GameOverDraft.Active && entered && !PickShowing;

        // The way on, once nothing is owed; and, with no Collect Rewards button of its own, the way back
        // to the rewards as well -- shown then only on the tabs they are not on.
        bool proceedShown = up && (ready || _leaving || (_collect == null && !onResult));
        Show(_proceed, proceedShown);

        // The way back to the rewards: owed, and another tab being read. It breathes while it is up.
        bool collectShown = up && !ready && !_leaving && !onResult;
        Show(_collect, collectShown);
        SetBreathing(collectShown);

        if (_proceed?.label != null)
        {
            // Standing in for both, it says what it waits on; otherwise it only ever says what it does.
            string text = ready || _leaving || _collect != null ? proceedLabel : collectRewardsLabel;
            if (_proceed.label.text != text) _proceed.label.text = text;
        }

        if (_collect?.label != null && _collect.label.text != collectRewardsLabel)
            _collect.label.text = collectRewardsLabel;
    }

    /// <summary>
    /// Fades <paramref name="pill"/> in or out when that changes -- the first call landing outright -- and
    /// takes it out of the raycaster's reach while it is away. The locked Proceed standing in for Collect
    /// Rewards is clickable: the click then brings the rewards back into view (see <see cref="Go"/>).
    /// </summary>
    private void Show(Pill pill, bool shown)
    {
        if (pill == null) return;

        // Both, not just the raycast: a group left not interactable refuses every Selectable inside it,
        // so the button would take the ray and then ignore it. The crossed-swords prompt is authored
        // shut on both counts (the draft opens it the same way over in GameOverDraft), and a plain
        // button's own group is open on both, so setting them together is right either way.
        pill.group.blocksRaycasts = shown;
        pill.group.interactable = shown;
        if (pill.button != null) pill.button.interactable = shown && !_leaving;

        float alpha = shown ? 1f : 0f;
        if (Mathf.Approximately(alpha, pill.targetAlpha)) return;

        bool first = pill.targetAlpha < 0f;
        pill.targetAlpha = alpha;
        pill.group.DOKill();
        if (first || fadeDuration <= 0f || !Application.isPlaying) pill.group.alpha = alpha;
        else pill.group.DOFade(alpha, fadeDuration).SetUpdate(true);
    }

    /// <summary>A run still being fought: there is a next battle to go to.</summary>
    private static bool InRun => GauntletRun.IsActive && GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress;

    /// <summary>The draft still owes picks, or the battle just won still owes rewards.</summary>
    private static bool Owed
    {
        get
        {
            if (GameOverDraft.Owed) return true;
            if (GauntletRun.Location != GauntletRun.RunLocation.Results) return false;

            var rewards = BattleRewardsPanel.Resolve();
            return rewards != null && !rewards.Settled;
        }
    }

    /// <summary>
    /// A reward pick's options are the screen being looked at -- a card or a perk being chosen on the
    /// pick tab -- so neither button belongs over them
    /// (<see cref="BattleRewardsPanel.PickShowing"/>).
    /// </summary>
    private static bool PickShowing
    {
        get
        {
            if (!GauntletRun.IsActive || GauntletRun.Location != GauntletRun.RunLocation.Results) return false;

            var rewards = BattleRewardsPanel.Resolve();
            return rewards != null && rewards.PickShowing;
        }
    }

    /// <summary>
    /// Leaves for the run's next fight, once nothing is owed. While the rewards are still owed -- which
    /// only happens here where Proceed stands in for both buttons -- the click opens the Result tab they
    /// sit on instead.
    /// </summary>
    public void Go()
    {
        if (_leaving || !InRun) return;

        if (Owed)
        {
            ShowRewards();
            return;
        }

        _leaving = true;
        Refresh();
        GauntletRun.FightNextBattle();
    }

    /// <summary>Back to the rewards the battle still owes: what the Collect Rewards button does.</summary>
    public void Collect()
    {
        if (_leaving || !InRun) return;

        ShowRewards();
    }

    /// <summary>Back to the Result tab, where the rewards panel waits, from whichever tab is open.</summary>
    private static void ShowRewards()
    {
        var tabs = GameOverTabs.Resolve();
        if (tabs == null || tabs.Current == GameOverTabs.TabKind.Result) return;

        tabs.Select(GameOverTabs.TabKind.Result, animate: true);
    }
}
