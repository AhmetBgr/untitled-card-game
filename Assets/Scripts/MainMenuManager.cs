using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Drives the title screen. Play opens a popup with the game modes: Quick Play drops straight into a
/// match with a random hero and a freshly rolled deck on both sides, Custom Game opens the full setup
/// scene, and Forged in Battle starts a draft gauntlet run in the Game scene, on its draft. Settings and Credits
/// open their own overlays, Quit leaves the game.
/// </summary>
public class MainMenuManager : MonoBehaviour
{
    [Header("Buttons")]
    [Tooltip("Opens the Play popup, where the game modes are chosen.")]
    [SerializeField] private Button playButton;

    [Tooltip("Sits in the column under Play, and opens the settings panel over the menu.")]
    [SerializeField] private Button settingsButton;

    [SerializeField] private Button creditsButton;
    [SerializeField] private Button quitButton;

    [Header("Play")]
    [Tooltip("Overlay shown by the Play button, holding one button per game mode. Its backdrop blocks the " +
             "menu underneath.")]
    [SerializeField] private GameObject playPanel;
    [SerializeField] private Button quickPlayButton;
    [SerializeField] private Button customGameButton;

    [Tooltip("Starts a new draft gauntlet run (see GauntletRun) in the Game scene, on its draft.")]
    [SerializeField] private Button forgedInBattleButton;

    [SerializeField] private Button closePlayButton;

    [Header("Credits")]
    [Tooltip("Overlay shown by the Credits button. Its backdrop blocks the menu underneath.")]
    [SerializeField] private GameObject creditsPanel;
    [SerializeField] private Button closeCreditsButton;

    [Header("Introduction")]
    [Tooltip("Shown over the menu on the very first launch, before the tutorial match. Its backdrop blocks " +
             "the menu underneath, and Escape does not close it: the player has to pick one of its buttons.")]
    [SerializeField] private GameObject introPanel;

    [Tooltip("Starts the tutorial match.")]
    [SerializeField] private Button introProceedButton;

    [Tooltip("Opens the settings over the introduction; closing them comes back to it.")]
    [SerializeField] private Button introSettingsButton;

    [Tooltip("Marks the tutorial as played and leaves the player on the menu.")]
    [SerializeField] private Button introSkipButton;

    [Header("Settings")]
    [Tooltip("The same panel the pause menu opens, over its own backdrop. Built by " +
             "Tools ▸ Settings ▸ Rebuild Settings Panels.")]
    [SerializeField] private SettingsPanelController settingsPanel;

    [Header("Scenes")]
    [SerializeField] private string gameSceneName = "Game";
    [SerializeField] private string customGameSceneName = "CreateCustomGame";

    void Start()
    {
        if (playButton != null)
            playButton.onClick.AddListener(() => ShowPlay(true));

        if (closePlayButton != null)
            closePlayButton.onClick.AddListener(() => ShowPlay(false));

        if (quickPlayButton != null)
            quickPlayButton.onClick.AddListener(OnQuickPlay);

        if (customGameButton != null)
            customGameButton.onClick.AddListener(() => GoToScene(customGameSceneName));

        if (forgedInBattleButton != null)
        {
            forgedInBattleButton.interactable = true;
            forgedInBattleButton.onClick.AddListener(OnForgedInBattle);
        }

        if (creditsButton != null)
            creditsButton.onClick.AddListener(() => ShowCredits(true));

        if (closeCreditsButton != null)
            closeCreditsButton.onClick.AddListener(() => ShowCredits(false));

        if (quitButton != null)
            quitButton.onClick.AddListener(Quit);

        if (settingsButton != null)
            settingsButton.onClick.AddListener(ToggleSettings);

        if (introProceedButton != null)
            introProceedButton.onClick.AddListener(OnIntroProceed);

        if (introSettingsButton != null)
            introSettingsButton.onClick.AddListener(ToggleSettings);

        if (introSkipButton != null)
            introSkipButton.onClick.AddListener(OnIntroSkip);

        // Back at the menu, no mode is being set up: Quick Play and the tutorial run on the plain rules,
        // and Custom Game re-enters its mode itself.
        MatchModifiers.Clear();

        // Coming back to the menu lets go of the Forged in Battle run in memory, but it stays saved:
        // the Forged in Battle button resumes it. Only the run's own result screen and the pause menu's
        // Abandon Run throw a run away.
        GauntletRun.Unload();

        // Always open on the menu itself, however the panels were left in the editor.
        ShowPlay(false);
        ShowCredits(false);
        if (settingsPanel != null) settingsPanel.Close();

        // First launch: the tutorial hasn't been played yet, so say what is about to happen before it does.
        ShowIntro(!SaveManager.Instance.IsTutorial);
    }

    /// <summary>
    /// Proceed on the introduction: into the tutorial match. The match-up itself is applied in the Game
    /// scene by <see cref="Agent.ApplySavedSelection"/>, not written into the save here, so it can't
    /// overwrite the player's own hero and deck choices. <see cref="GameManager"/> flips the save flag when
    /// that match ends, so the menu behaves normally from the next visit onwards.
    /// </summary>
    void OnIntroProceed()
    {
        // The fade takes a moment; don't let a second click (or a Skip) land during it.
        SetIntroInteractable(false);
        GoToScene(gameSceneName);
    }

    /// <summary>Skip on the introduction: the tutorial counts as played, and the menu is left as it is.</summary>
    void OnIntroSkip()
    {
        SaveManager.Instance.SetTutorial(true);
        ShowIntro(false);
    }

    void ShowIntro(bool show)
    {
        if (introPanel != null)
            introPanel.SetActive(show);
    }

    void SetIntroInteractable(bool interactable)
    {
        if (introProceedButton != null) introProceedButton.interactable = interactable;
        if (introSettingsButton != null) introSettingsButton.interactable = interactable;
        if (introSkipButton != null) introSkipButton.interactable = interactable;
    }

    void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;

        // One layer at a time, and the settings first: they are the panel that can be opened from on top
        // of the others, so they are the one Escape has to reach first. Play and Credits never overlap --
        // each opens from the column the other's backdrop covers -- so their order is moot. The
        // introduction is left alone: it asks a question, and Escape is not an answer to it.
        if (settingsPanel != null && settingsPanel.IsOpen)
            settingsPanel.Close();
        else if (playPanel != null && playPanel.activeSelf)
            ShowPlay(false);
        else if (creditsPanel != null && creditsPanel.activeSelf)
            ShowCredits(false);
    }

    /// <summary>Open the settings over the menu, or put them away. What the Settings button does.</summary>
    public void ToggleSettings()
    {
        if (settingsPanel == null) return;

        // The play and credits popups are modals of their own; never leave two stacked.
        if (!settingsPanel.IsOpen)
        {
            ShowPlay(false);
            ShowCredits(false);
        }

        settingsPanel.Toggle();
    }

    void ShowPlay(bool show)
    {
        if (playPanel != null)
            playPanel.SetActive(show);
    }

    void ShowCredits(bool show)
    {
        if (creditsPanel == null) return;

        creditsPanel.SetActive(show);

        // The body scrolls; always open it at the top rather than wherever it was left.
        if (show)
        {
            var scroll = creditsPanel.GetComponentInChildren<ScrollRect>();
            if (scroll != null) scroll.verticalNormalizedPosition = 1f;
        }
    }

    /// <summary>
    /// Sets both sides to the mystery deck and the "Random Hero" sentinel, which is exactly the
    /// selection CreateCustomGame produces when the mystery slot and the random hero slot are picked
    /// for player and opponent alike. The mystery deck is re-rolled here because the deck panel that
    /// normally regenerates it never runs in Quick Play; the hero sentinel resolves per side in
    /// <see cref="HeroDatabase.GetSelectedHero"/> once the Game scene loads, so the two sides roll
    /// independently.
    /// </summary>
    void OnQuickPlay()
    {
        var saveManager = SaveManager.Instance;

        Randomize(saveManager, SelectionSide.Player);
        Randomize(saveManager, SelectionSide.Opponent);

        saveManager.SaveData();

        GoToScene(gameSceneName);
    }

    /// <summary>
    /// Forged in Battle: carries on the saved run where it was left (the draft, a battle to fight again,
    /// or the end-of-match rewards), or starts a fresh one with the hero pick on the Game scene's draft.
    /// </summary>
    void OnForgedInBattle()
    {
        if (GauntletRun.HasSavedRun && GauntletRun.TryResume(out string scene))
        {
            GoToScene(scene);
            return;
        }

        // The whole run is played in the Game scene: it opens on the run's draft (GameOverDraft).
        GauntletRun.StartNew();
        GoToScene(GauntletRun.Config.gameSceneName);
    }

    static void Randomize(SaveManager saveManager, SelectionSide side)
    {
        saveManager.GenerateRandomDeck(SaveManager.MysteryDeckIndex, side);
        saveManager.SetSelectedDeckIndex(side, SaveManager.MysteryDeckIndex);
        saveManager.SetSelectedHeroIndex(side, HeroDatabase.RandomHeroIndex);
    }

    // The fade transition is optional: a scene entered without a SceneTransitionManager (or one whose
    // transition object is disabled) still needs its buttons to work.
    void GoToScene(string sceneName)
    {
        if (SceneTransitionManager.Instance != null)
            SceneTransitionManager.Instance.TransitionToScene(sceneName);
        else
            SceneManager.LoadScene(sceneName);
    }

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
