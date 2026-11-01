using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The in-match pause menu. Escape or the on-screen toggle opens it, either one (or Resume) closes it
/// again, and the buttons cover the only things there are to do from a paused match: carry on, start it
/// over, leave for the title screen, quit the game, or read up on how to play.
///
/// The rules and how-to-play panels are NOT part of the menu's resting state: they are authored on but
/// switched off at Awake and only come back for as long as the How To Play button is latched on, so a
/// pause is a short list of buttons rather than a wall of text. They sit in their own column left of the
/// buttons, which is why the buttons stay up alongside them — the toggle that opened them has to remain
/// clickable to shut them again, and it holds itself down while they are up.
///
/// This component lives on an ALWAYS-ACTIVE root whose <see cref="panel"/> child is the thing shown and
/// hidden. The panel has to be authored inactive — nothing should be on screen at kickoff — and Update
/// never runs on a disabled object, so the Escape key has to be read from somewhere that stays enabled.
/// Same problem <see cref="CardSelectionPanel"/> solves with a static bootstrap; an always-on parent is
/// the cheaper answer here because this menu opens on a key rather than on an event.
///
/// It stays available once the match is decided, over the end-of-match panel and the run's draft
/// (<see cref="PopupManager"/>): a run has things to do from there that the panel itself does not offer --
/// Abandon Run, the settings, the rules -- so the toggle is on screen for as long as the scene is. Opening
/// the menu there takes that panel off the screen rather than stacking over it
/// (<see cref="PopupManager.SetPopupHidden"/>), and Resume brings it back; it is faded, not switched off,
/// so what it was still doing -- its score tally, its XP track -- is only frozen by the pause, not lost.
///
/// Pausing is <see cref="Time.timeScale"/> = 0, which is enough to freeze this particular game: every
/// animation is either a DOTween tween (scaled time unless it opts out) or a coroutine yielding on
/// WaitForSeconds, and the opponent's whole turn is one of those coroutines.
///
/// What timeScale does NOT stop is input, so the three input paths are shut off separately:
///   - uGUI clicks (dragging a card out of hand, end turn) are eaten by the panel's full-screen backdrop,
///     the same trick CardSelectionPanel's backdrop plays.
///   - Legacy OnMouse* board hover is killed by <see cref="BoardInteractionGate"/>, which reads
///     <see cref="IsOpen"/>.
///   - Right-click cancel is polled directly in GameManager.Update, which also checks
///     <see cref="IsOpen"/>.
/// </summary>
public class EscMenuController : MonoBehaviour
{
    [Tooltip("The menu itself: backdrop, frame, title and buttons. Authored inactive — this component " +
             "sits on the always-active parent so it can keep reading the Escape key.")]
    [SerializeField] private GameObject panel;

    [Header("Buttons")]
    [SerializeField] private Button resumeButton;
    [SerializeField] private Button restartButton;
    [SerializeField] private Button mainMenuButton;
    [SerializeField] private Button quitButton;

    [Tooltip("Gives up the Forged in Battle run in progress (its save too) and goes to the title screen. " +
             "Only shown during a run. Left empty, one is cloned from the Main Menu button and slotted in " +
             "under it -- the buttons sit in a layout group, so it lines up on its own.")]
    [SerializeField] private Button abandonRunButton;
    [SerializeField] private string abandonRunLabel = "Abandon Run";

    [Tooltip("Sits under the other buttons and shows the rules alongside them. Not a scene change and " +
             "not a modal — it only switches the two reading panels on and off. Carries a " +
             "ToggleButton so it stays visibly held down for as long as they are up.")]
    [SerializeField] private Button howToPlayButton;

    [Tooltip("Opens the settings in the same column the rules use, and on the same terms: a latched " +
             "toggle, not a scene change and not a modal.")]
    [SerializeField] private Button settingsButton;

    [Header("Reading panels")]
    [Tooltip("Built from Docs/rules-panel.md by Tools ▸ Esc Menu ▸ Sync Rules Panel.")]
    [SerializeField] private GameObject rulesPanel;

    [Tooltip("Built from Docs/how-to-play-panel.md by Tools ▸ Esc Menu ▸ Sync How To Play Panel.")]
    [SerializeField] private GameObject howToPlayPanel;

    [Header("Settings")]
    [Tooltip("Shares the left column with the reading panels — only ever one of the two is up. Built by " +
             "Tools ▸ Settings ▸ Rebuild Settings Panel.")]
    [SerializeField] private SettingsPanelController settingsPanel;

    [Header("On-screen toggle")]
    [Tooltip("Corner button that opens and closes the menu, for players who don't reach for Escape. It is " +
             "a SIBLING of the panel rather than a child of it, ordered after it — that is what keeps it " +
             "drawn above the backdrop and clickable while the menu is open.")]
    [SerializeField] private Button toggleButton;

    [SerializeField] private string openLabel = "Menu";
    [SerializeField] private string closeLabel = "Close";

    [Header("Scenes")]
    [SerializeField] private string gameSceneName = "Game";
    [SerializeField] private string menuSceneName = "MainMenu";

    /// <summary>
    /// True while the pause menu is on screen. Static so the input sources timeScale cannot reach can ask
    /// without holding a reference to a menu that only exists in the match scene.
    /// </summary>
    public static bool IsOpen { get; private set; }

    /// <summary>
    /// What the game was running at before we paused. Read rather than assumed to be 1 so a slow-motion or
    /// fast-forward debug setting survives a trip through the menu.
    /// </summary>
    private float _resumeTimeScale = 1f;

    /// <summary>
    /// The toggle's caption. Resolved rather than serialised, the way CardSelectionPanel picks up its See
    /// Board label, so restyling the button in the prefab needs no extra inspector wiring.
    /// </summary>
    private TMPro.TextMeshProUGUI _toggleLabel;

    /// <summary>
    /// The latch on the How To Play button, when it is the authored toggle rather than a plain Button.
    /// Same arrangement as <see cref="CardSelectionPanel"/>'s See Board button: the toggle owns the click
    /// and reports the state it moved to, so nothing here has to invert anything.
    /// </summary>
    private ToggleButton _howToPlayToggle;

    /// <summary>The latch on the Settings button, on the same terms as <see cref="_howToPlayToggle"/>.</summary>
    private ToggleButton _settingsToggle;

    /// <summary>Whether the rules and how-to-play panels are currently showing.</summary>
    private bool _infoOpen;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ResetState()
    {
        // Statics survive between play sessions when Enter Play Mode Options skips the domain reload, so a
        // session that ended paused would otherwise start the next one with the board inert and the
        // right-click cancel dead. Same reasoning as BoardInteractionGate.Bootstrap.
        IsOpen = false;
    }

    private void Awake()
    {
        if (resumeButton != null) resumeButton.onClick.AddListener(Resume);
        if (restartButton != null) restartButton.onClick.AddListener(Restart);
        if (mainMenuButton != null) mainMenuButton.onClick.AddListener(ExitToMenu);
        if (quitButton != null) quitButton.onClick.AddListener(Quit);

        // No restarting a Forged in Battle battle: it would be a free retry of a lost fight.
        if (restartButton != null && GauntletRun.IsActive) restartButton.gameObject.SetActive(false);

        SetUpAbandonRun();

        if (howToPlayButton != null)
        {
            _howToPlayToggle = howToPlayButton.GetComponent<ToggleButton>();

            // The toggle has already flipped itself by the time it reports in, so take the state it hands
            // over — routing that through ToggleHowToPlay would flip a second time and cancel it out.
            if (_howToPlayToggle != null) _howToPlayToggle.onValueChanged.AddListener(ShowInfo);
            else howToPlayButton.onClick.AddListener(ToggleHowToPlay);
        }

        if (settingsButton != null)
        {
            _settingsToggle = settingsButton.GetComponent<ToggleButton>();

            if (_settingsToggle != null) _settingsToggle.onValueChanged.AddListener(ShowSettings);
            else settingsButton.onClick.AddListener(ToggleSettings);
        }

        // The panel can also close itself — its own Close button — and the latch has to come back up
        // when it does.
        if (settingsPanel != null) settingsPanel.Closed += OnSettingsClosed;

        if (toggleButton != null)
        {
            toggleButton.onClick.AddListener(Toggle);
            _toggleLabel = toggleButton.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
        }

        // Always start the match unpaused, however the panel was left in the editor. The reading panels
        // are authored ON so they can be laid out in the prefab; this is where they get put away.
        if (panel != null) panel.SetActive(false);
        IsOpen = false;
        ShowInfo(false);
        ShowSettings(false);

        RefreshToggle();
    }

    private void OnDisable()
    {
        // Never leave the game frozen behind us. This runs on scene unload and on leaving play mode, both
        // of which can happen with the menu still open.
        if (IsOpen) Resume();
    }

    private void OnDestroy()
    {
        if (settingsPanel != null) settingsPanel.Closed -= OnSettingsClosed;
    }

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;

        // A confirmation popup over the menu owns Escape: it cancels the question, not the pause.
        if (ConfirmationPopup.ConsumesEscape) return;

        // Escape backs out one layer at a time: it closes whatever is up in the left column first and the
        // menu second, so reading the rules — or setting the volume — is never a one-way trip into
        // leaving the match by accident.
        if (IsOpen && _infoOpen) ShowInfo(false);
        else if (IsOpen && settingsPanel != null && settingsPanel.IsOpen) ShowSettings(false);
        else Toggle();
    }

    /// <summary>Open the menu if it is shut, shut it if it is open. What both Escape and the corner button do.</summary>
    public void Toggle()
    {
        if (IsOpen) Resume();
        else Open();
    }

    public void Open()
    {
        if (IsOpen || panel == null) return;

        _resumeTimeScale = Time.timeScale;
        Time.timeScale = 0f;

        panel.SetActive(true);

        // Every pause starts on the buttons, whatever was left open last time.
        ShowInfo(false);
        ShowSettings(false);
        RefreshAbandonRun();

        // The end-of-match panel steps aside rather than being stacked under: it is a screen of its own,
        // and a pause over the top of it would read as two at once. Resume brings it back.
        if (PopupManager.Instance != null) PopupManager.Instance.SetPopupHidden(true);

        // Above everything else on the canvas, including a card selection panel left open mid-card. Done
        // on open rather than authored, because sibling order is a property of whatever else the scene has
        // added to the canvas since.
        transform.SetAsLastSibling();

        IsOpen = true;
        RefreshToggle();
    }

    public void Resume()
    {
        if (panel != null) panel.SetActive(false);
        ShowInfo(false);
        ShowSettings(false);

        // Guarded, so a Resume that closes nothing — OnDisable firing on an already-closed menu — cannot
        // overwrite a timeScale somebody else owns, or put back a panel it never took away.
        if (IsOpen)
        {
            Time.timeScale = _resumeTimeScale;
            if (PopupManager.Instance != null) PopupManager.Instance.SetPopupHidden(false);
        }

        IsOpen = false;
        RefreshToggle();
    }

    private void RefreshToggle()
    {
        if (_toggleLabel != null) _toggleLabel.text = IsOpen ? closeLabel : openLabel;
    }

    /// <summary>Show the rules next to the buttons, or put them away again. What the How To Play button does.</summary>
    public void ToggleHowToPlay() => ShowInfo(!_infoOpen);

    private void ShowInfo(bool show)
    {
        _infoOpen = show;
        if (rulesPanel != null) rulesPanel.SetActive(show);
        if (howToPlayPanel != null) howToPlayPanel.SetActive(show);

        // Silent, because every other way of closing the panels — Escape, Resume, a fresh pause — has to
        // put the latch back up without being reported straight back in here.
        if (_howToPlayToggle != null) _howToPlayToggle.SetIsOn(show, notify: false);

        // The rules and the settings share the left column, so opening one puts the other away rather
        // than drawing both into the same space.
        if (show) ShowSettings(false);
    }

    /// <summary>Show the settings next to the buttons, or put them away again. What the Settings button does.</summary>
    public void ToggleSettings() => ShowSettings(settingsPanel != null && !settingsPanel.IsOpen);

    private void ShowSettings(bool show)
    {
        if (settingsPanel != null) settingsPanel.SetOpen(show);

        if (_settingsToggle != null) _settingsToggle.SetIsOn(show, notify: false);

        if (show) ShowInfo(false);
    }

    /// <summary>The panel closed itself (its own Close button). Nothing to shut, just the latch to raise.</summary>
    private void OnSettingsClosed()
    {
        if (_settingsToggle != null) _settingsToggle.SetIsOn(false, notify: false);
    }

    /// <summary>Rerun the match with the decks and heroes already chosen, exactly as Play Again does.</summary>
    public void Restart() => LeaveTo(gameSceneName);

    /// <summary>
    /// To the title screen. A Forged in Battle run is NOT given up by this: it stays saved, and the
    /// Forged in Battle button carries it on (a battle left mid-fight starts over). Abandon Run is the
    /// way to throw one away.
    /// </summary>
    public void ExitToMenu() => LeaveTo(menuSceneName);

    // ---------------------------------------------------------------- Abandon Run

    /// <summary>A run that can still be thrown away: a finished one is already gone.</summary>
    private static bool RunInProgress =>
        GauntletRun.IsActive && GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress;

    /// <summary>
    /// Builds the button and wires it, once. Whether it SHOWS is settled per open
    /// (<see cref="RefreshAbandonRun"/>): the menu is reachable from the end-of-match panel now, where
    /// the run may have ended since this ran.
    /// </summary>
    private void SetUpAbandonRun()
    {
        bool runInProgress = RunInProgress;

        if (abandonRunButton == null && runInProgress && mainMenuButton != null)
        {
            abandonRunButton = Instantiate(mainMenuButton, mainMenuButton.transform.parent);
            abandonRunButton.name = "AbandonRunButton";
            abandonRunButton.transform.SetSiblingIndex(mainMenuButton.transform.GetSiblingIndex() + 1);
            // A fresh event: the clone must not carry the Main Menu button's own wiring.
            abandonRunButton.onClick = new Button.ButtonClickedEvent();
        }

        if (abandonRunButton == null) return;

        abandonRunButton.gameObject.SetActive(runInProgress);
        if (!runInProgress) return;

        var label = abandonRunButton.GetComponentInChildren<TMPro.TMP_Text>(true);
        if (label != null) label.text = abandonRunLabel;
        abandonRunButton.onClick.AddListener(OnAbandonRunClicked);
    }

    /// <summary>
    /// Shows or hides Abandon Run for this pause. Separate from <see cref="SetUpAbandonRun"/>, which must
    /// not run twice -- it would add its click listener again.
    /// </summary>
    private void RefreshAbandonRun()
    {
        if (abandonRunButton != null) abandonRunButton.gameObject.SetActive(RunInProgress);
    }

    private void OnAbandonRunClicked() =>
        GauntletRunPrompts.ConfirmAbandon(() => LeaveTo(menuSceneName));

    public void Quit()
    {
        // Application.Quit still runs a frame or two on the way out; don't hand it a frozen clock.
        Resume();

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// <summary>
    /// Unpausing FIRST is not tidiness: SceneTransitionManager's fade is a coroutine driven by
    /// Time.deltaTime, so kicking off a transition at timeScale 0 leaves the screen stuck part-faded with
    /// the next scene never loading.
    /// </summary>
    private void LeaveTo(string sceneName)
    {
        Resume();

        // Same optional-transition handling as MainMenuManager: a scene entered without a
        // SceneTransitionManager still has to be able to leave.
        if (SceneTransitionManager.Instance != null)
            SceneTransitionManager.Instance.TransitionToScene(sceneName);
        else
            SceneManager.LoadScene(sceneName);
    }

}
