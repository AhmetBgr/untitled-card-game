using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The end-of-match panel's Proceed button: the way from the panel into the run's next fight.
///
/// Proceed belongs to the panel rather than to any one tab: it sits in the bottom right corner through
/// the whole review -- Result, Statistics, Hero Info, Battles -- so leaving is always in the same place,
/// whatever is being read. It is not the rewards panel's own Proceed
/// (<see cref="BattleRewardsPanel"/>), which still gates the rows that battle paid out. It only shows in
/// a run still being fought, and stays locked while anything is owed: after a battle, its rewards. It
/// is not shown at all through the run's draft (<see cref="GameOverDraft"/>), whose own "click anywhere"
/// prompt is the way on from there.
///
/// Locked, it reads "Collect Rewards" and shows only on the tabs the rewards are not on: a click brings
/// the Result tab, where they sit, back. Open, it reads "Click to Proceed" on every tab and a click goes
/// straight to the fight (<see cref="GauntletRun.FightNextBattle"/>). The match-up is shown by the
/// reloaded Game scene itself, over its fresh board (<see cref="VersusIntro"/>). It waits for the panel's
/// entrance (<see cref="GameOverPanelIntro"/>) to finish before it first shows.
///
/// It is hidden by fading (a CanvasGroup) rather than switched off, which keeps this component running to
/// bring it back.
/// </summary>
public class GameOverVersus : MonoBehaviour
{
    [Header("Proceed")]
    [Tooltip("The button that leaves for the next fight. Defaults to this object's own.")]
    [SerializeField] private Button proceedButton;

    [Tooltip("How long the button takes to fade in or out when it comes and goes.")]
    [SerializeField] private float fadeDuration = 0.2f;

    [Header("Label")]
    [Tooltip("The button's text. Defaults to the first TextMeshProUGUI under it.")]
    [SerializeField] private TextMeshProUGUI label;
    [Tooltip("While the battle's rewards are still owed: what the button is waiting on.")]
    [SerializeField] private string collectRewardsLabel = "Collect Rewards";
    [Tooltip("Once nothing is owed: the way into the next fight.")]
    [SerializeField] private string proceedLabel = "Click to Proceed";

    private CanvasGroup _group;
    private GameOverPanelIntro _intro;
    private bool _leaving;

    /// <summary>The alpha last asked of the group; -1 before the first refresh, which sets it outright.</summary>
    private float _targetAlpha = -1f;

    private void Awake()
    {
        if (proceedButton == null) proceedButton = GetComponent<Button>();
        if (label == null) label = GetComponentInChildren<TextMeshProUGUI>(true);
        _intro = GetComponentInParent<GameOverPanelIntro>(true);

        _group = GetComponent<CanvasGroup>();
        if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();

        if (proceedButton != null) proceedButton.onClick.AddListener(Go);
        RefreshProceed();
    }

    private void OnDestroy()
    {
        if (_group != null) _group.DOKill();
    }

    // Polled: what it waits on -- picks, rewards, the tab being read -- changes from all over the panel.
    private void Update() => RefreshProceed();

    private void RefreshProceed()
    {
        bool ready = InRun && !Owed && !_leaving;

        // Not before the panel has finished arriving: the title's rise, the bar's drop and the opening
        // tab's slide are all done first. The draft has its own way on (a click anywhere), so Proceed
        // stays out of sight through it. And while the rewards are still owed it only shows on the
        // tabs they are not on -- a way back to them -- the Result tab having them in plain view.
        var tabs = GameOverTabs.Resolve();
        bool entered = (_intro == null || !_intro.IsEntering)
                       && (tabs == null || (!tabs.IsBarDropping && !tabs.IsSwitching));
        bool onResult = tabs != null && tabs.Current == GameOverTabs.TabKind.Result;
        bool shown = InRun && !GameOverDraft.Active && entered && (ready || _leaving || !onResult);

        FadeTo(shown ? 1f : 0f);
        _group.blocksRaycasts = shown;
        // Still clickable while locked: the click then brings the rewards back into view (see Go).
        if (proceedButton != null) proceedButton.interactable = shown && !_leaving;

        // Locked, it says what it waits on; open, it says what it does.
        if (label != null)
        {
            string text = ready || _leaving ? proceedLabel : collectRewardsLabel;
            if (label.text != text) label.text = text;
        }
    }

    /// <summary>Fades the button to <paramref name="alpha"/> when that changes; the first call lands outright.</summary>
    private void FadeTo(float alpha)
    {
        if (Mathf.Approximately(alpha, _targetAlpha)) return;

        bool first = _targetAlpha < 0f;
        _targetAlpha = alpha;
        _group.DOKill();
        if (first || fadeDuration <= 0f || !Application.isPlaying) _group.alpha = alpha;
        else _group.DOFade(alpha, fadeDuration).SetUpdate(true);
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
    /// Leaves for the run's next fight, once nothing is owed. While the rewards are still owed, the
    /// click opens the Result tab they sit on instead, if another tab is being read.
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
        RefreshProceed();
        GauntletRun.FightNextBattle();
    }

    /// <summary>Back to the Result tab, where the rewards panel waits, from whichever tab is open.</summary>
    private static void ShowRewards()
    {
        var tabs = GameOverTabs.Resolve();
        if (tabs == null || tabs.Current == GameOverTabs.TabKind.Result) return;

        tabs.Select(GameOverTabs.TabKind.Result, animate: true);
    }
}
