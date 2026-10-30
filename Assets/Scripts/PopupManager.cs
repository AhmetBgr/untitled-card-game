using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

public class PopupManager : Singleton<PopupManager>
{
    [Header("End-game popup (the same panel is used for victory and defeat)")]
    public Transform gameOverPopup;

    [Header("Titles - only the one matching the outcome is enabled")]
    public Transform victoryTitle;
    public Transform defeatTitle;

    [Header("Buttons")]
    public Button replayButton;
    [Tooltip("The panel's 'Proceed' button: leaves the match and returns to the main menu.")]
    public Button exitButton;

    [Header("Scenes")]
    [SerializeField] private string menuSceneName = "MainMenu";

    [Header("Forged in Battle run over")]
    [Tooltip("What Replay reads once a run is over: it starts a new run instead.")]
    [SerializeField] private string newRunLabel = "New Run";
    [Tooltip("What Proceed reads once a run is over: it ends the run and goes to the main menu.")]
    [SerializeField] private string mainMenuLabel = "Main Menu";

    [Header("Timing")]
    [Tooltip("Seconds to wait before the end-game panel scales in, so the killing blow and the hero's " +
             "death get to play out on a clear board first.")]
    [SerializeField] private float gameOverDelay = 2f;

    private Transform curPopup = null;

    /// <summary>
    /// True while a popup is on screen, so <see cref="BoardInteractionGate"/> can suppress board
    /// interaction underneath it. Note this stays true until <see cref="CloseCurPopup"/>'s scale-out
    /// tween finishes and clears the field — the board should stay inert while the panel is still
    /// visibly shrinking, so that lag is the wanted behaviour rather than an oversight.
    /// </summary>
    public bool HasOpenPopup => curPopup != null;

    void Start()
    {
        if (replayButton != null) replayButton.onClick.AddListener(Replay);
        if (exitButton != null) exitButton.onClick.AddListener(ExitToMenu);
    }

    // Replay reruns the match with the decks and heroes already chosen for both sides. On a finished
    // Forged in Battle run it is New Run: the run is over for good, and a fresh one opens on its draft.
    public void Replay()
    {
        if (RunOver)
        {
            GauntletRun.End();
            GauntletRun.StartNew();
            GauntletRun.LoadScene(GauntletRun.Config.gameSceneName);
            return;
        }

        SceneTransitionManager.Instance.TransitionToScene("Game");
    }

    // Proceed leaves the match for the main menu, from which the next match can either be started
    // straight away (Quick Play) or reconfigured (Custom Game). In a Forged in Battle run still going it
    // leaves the battle for the run's draft, which the Game scene opens on; on a finished run it is Main
    // Menu, and the run goes with it. A won battle that owes rewards never gets here: BattleRewardsPanel
    // opens beside the end-game panel and its own Proceed replaces this one (see OpenGameOverPopup).
    public void ExitToMenu()
    {
        if (RunOver)
        {
            GauntletRun.End();
            SceneTransitionManager.Instance.TransitionToScene(menuSceneName);
            return;
        }

        if (GauntletRun.IsActive)
        {
            GauntletRun.LeaveBattle();
            return;
        }

        SceneTransitionManager.Instance.TransitionToScene(menuSceneName);
    }

    /// <summary>A Forged in Battle run that has been won or lost: its last end-of-match screen is its result.</summary>
    private static bool RunOver => GauntletRun.IsActive && GauntletRun.Outcome != GauntletRun.RunOutcome.InProgress;

    /// <summary>
    /// Opens the panel on the run's draft rather than a match's result: the Game scene does this, with no
    /// match set up behind it, whenever the run is between battles -- a new run, or one resumed there. No
    /// outcome title and no stats: only the Selection tab, locked open until the draft owes nothing
    /// (<see cref="GameOverDraft"/>).
    /// </summary>
    public void OpenDraft()
    {
        if (gameOverPopup == null) return;

        // A run left on its draft after it was already decided (an old save): nothing to draft, it is over.
        if (RunOver)
        {
            GauntletRun.End();
            SceneTransitionManager.Instance.TransitionToScene(menuSceneName);
            return;
        }

        if (victoryTitle != null) victoryTitle.gameObject.SetActive(false);
        if (defeatTitle != null) defeatTitle.gameObject.SetActive(false);
        if (replayButton != null) replayButton.gameObject.SetActive(false);
        if (exitButton != null) exitButton.gameObject.SetActive(false);

        var draft = gameOverPopup.GetComponentInChildren<GameOverDraft>(true);
        var tabs = gameOverPopup.GetComponentInChildren<GameOverTabs>(true);
        if (tabs != null) tabs.OpenLockedTo(draft != null ? draft.Tab : GameOverTabs.TabKind.Draft);

        OpenPopup(gameOverPopup, true, 0f, null, draft: true);
        if (draft != null) draft.Begin();
    }

    // Win and loss show the identical panel (background, stats, buttons); the only difference is
    // which title transform is enabled.
    // Leave delay null to use the inspector's gameOverDelay; pass an explicit value to override it
    // (the editor debug triggers pass 0 so the preview is instant).
    public void OpenGameOverPopup(bool won, float? delay = null) => OpenGameOverPopup(won, delay, null);

    /// <summary>
    /// Puts a Forged in Battle run's saved end-of-match screen back up, as it was left: the battle's
    /// stats already tallied, the XP track already filled, and the rewards with what was taken. No
    /// delay and no outcome stinger -- the match ended in an earlier session.
    /// </summary>
    public void OpenRestoredResults()
    {
        var results = GauntletRun.Results;
        if (results == null || results.stats == null)
        {
            Debug.LogWarning("PopupManager: no saved end-of-match screen to restore; back to the draft.");
            GauntletRun.LeaveBattle();
            return;
        }

        // Left before the score was credited (the panel credits it as it opens): credit it before the
        // track is drawn, so the track shows the XP the rewards are listed against.
        GauntletRun.CollectScore();
        OpenGameOverPopup(results.won, 0f, results);
    }

    private void OpenGameOverPopup(bool won, float? delay, ResultsSave restored)
    {
        if (gameOverPopup == null) return;

        if (victoryTitle != null) victoryTitle.gameObject.SetActive(won);
        if (defeatTitle != null) defeatTitle.gameObject.SetActive(!won);

        // A run's battle can't be replayed: a loss ends the run, a win moves on to the next draft. The
        // battle that decides the run puts New Run and Main Menu up instead.
        if (replayButton != null) replayButton.gameObject.SetActive(!GauntletRun.IsActive || RunOver);
        if (RunOver)
        {
            SetLabel(replayButton, newRunLabel);
            SetLabel(exitButton, mainMenuLabel);
        }

        float resolvedDelay = delay ?? gameOverDelay;

        // Hold the outcome hook back by the same delay the panel waits, so the stinger hits on the first
        // frame of the scale-in rather than over the empty board. Scaled time, matching the tween below.
        if (restored == null)
        {
            if (resolvedDelay > 0f) DOVirtual.DelayedCall(resolvedDelay, () => GameOver?.Invoke(won), false);
            else GameOver?.Invoke(won);
        }

        float openDuration = OpenPopup(gameOverPopup, won, resolvedDelay, restored);

        // A won run battle opens its rewards beside the panel, scaling in with it, and milestone rows land
        // as the panel's XP track fills. Must follow OpenPopup: the track reads its base XP there, before
        // the rewards panel credits the battle's score. The rewards panel's Proceed is the way out, so the
        // panel's own is hidden while it is up; a loss or a finished run keeps it.
        bool rewardsShown = false;
        var rewards = BattleRewardsPanel.Resolve();
        if (won && GauntletRun.IsActive && rewards != null)
        {
            var xpTrack = gameOverPopup.GetComponentInChildren<BattleXpTrack>(true);
            // Its rows wait out the panel's entrance: the column they sit in is still sliding in.
            rewardsShown = rewards.Show(resolvedDelay + openDuration, xpTrack,
                gameOverPopup.GetComponent<CanvasGroup>());
        }
        // Nothing to hand out -- a loss, a decided run, Quick Play: the column is not left showing its
        // authored placeholder rows.
        if (!rewardsShown && rewards != null) rewards.gameObject.SetActive(false);
        if (exitButton != null) exitButton.gameObject.SetActive(!rewardsShown);
    }

    private static void SetLabel(Button button, string label)
    {
        var text = button != null ? button.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) : null;
        if (text != null) text.text = label;
    }

    /// <summary>
    /// Fired when the end-game panel opens, with the outcome. Purely additive: consumed by the audio
    /// system, no core logic depends on it. This is the single funnel every ending goes through -- the
    /// real win check and both editor debug triggers -- which is why the hook lives here rather than in
    /// GameManager.CheckWinCondition.
    ///
    /// Note this fires after the panel's open delay has elapsed, not when the match ends, so the stinger
    /// lands together with the scale-in animation.
    /// </summary>
    public static event System.Action<bool> GameOver;

    /// <summary>Opens <paramref name="popup"/>, and returns how long it takes to finish arriving.</summary>
    public float OpenPopup(Transform popup, bool won = false, float delay = 0f) => OpenPopup(popup, won, delay, null);

    /// <param name="restored">A saved end-of-match screen to show as it was, instead of this match's live stats.</param>
    /// <param name="draft">The run's draft (<see cref="OpenDraft"/>): no match, so no stats and no XP to show.</param>
    private float OpenPopup(Transform popup, bool won, float delay, ResultsSave restored, bool draft = false)
    {
        CloseCurPopup();
        curPopup = popup;

        curPopup.gameObject.SetActive(true);

        // Populate the end-game stats/score on this panel, if it has a view. Additive: does nothing on
        // panels without a MatchStatsView. Covers both real wins/losses and the editor debug triggers.
        const float scaleInDuration = 0.5f;

        // A panel with an entrance of its own -- the end-of-match panel, whose title and columns each
        // arrive separately -- opens at full size and poses itself; everything else scales in whole.
        var intro = popup.GetComponent<GameOverPanelIntro>();
        float openDuration = intro != null ? intro.Duration : scaleInDuration;
        curPopup.localScale = intro != null ? Vector3.one : Vector3.zero;

        var statsView = popup.GetComponentInChildren<MatchStatsView>(true);

        // In a run, the panel also carries the milestone track, which fills with the score being tallied.
        // Wired before the reveal starts so it is on screen from the first number, and left to itself on
        // panels without one.
        var xpTrack = popup.GetComponentInChildren<BattleXpTrack>(true);
        if (draft)
        {
            // Nothing was fought: the stats and the track are left as they are, on the Result tab no one can open.
        }
        else if (restored != null)
        {
            // Already tallied in the session the battle was fought: the panel lands finished.
            if (xpTrack != null) xpTrack.ShowStatic(won, restored.baseXp, GauntletRun.Xp);
            if (statsView != null) statsView.Show(restored.stats, animate: false);
        }
        else
        {
            if (xpTrack != null) xpTrack.Show(won, statsView);

            // The stats view reveals its rows one by one; hold them until the panel has finished arriving.
            if (statsView != null) statsView.Show(won, delay + openDuration);
        }

        // Posed for the entrance now, played after the delay, so the board is clear of the panel until then.
        // The run's draft has no entrance: the scene opens with it already up.
        if (intro != null && draft) intro.ShowAtOnce();
        else if (intro != null) intro.Play(delay);
        else curPopup.DOScale(1f, scaleInDuration).SetDelay(delay);

        return openDuration;
    }

    public void CloseCurPopup()
    {
        if (curPopup == null) return;

        var intro = curPopup.GetComponent<GameOverPanelIntro>();
        if (intro != null) intro.Stop();

        curPopup.DOScale(0f, 0.5f).OnComplete(() =>
        {
            curPopup.gameObject.SetActive(false);
            curPopup = null;
        });
    }
}
