using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Drives the Draft scene of Forged in Battle. Every visit to the scene shows whichever step the
/// <see cref="GauntletRun"/> is on:
///   1. no hero yet: pick 1 of N heroes in the draft panel (the Draft tab);
///   2. card picks owed: pick 1 of N base cards in the draft panel, until the step's picks are done.
///      A claimed card milestone owes picks the same way (upgraded cards too); a perk milestone owes a
///      pick of 1 of N passives;
///   3. a reached milestone unclaimed: the draft panel asks for it to be claimed. Hovering a milestone
///      on the track previews it in the draft panel, clicking a reached one claims it;
///   4. nothing owed: the Draft tab slides away for the Next Match tab, the upcoming battle, with the
///      Proceed button under its skull. Proceed sits there through the whole draft, greyed out until the
///      picks are done; mid-draft, hovering that skull previews the enemy;
///   5. run over: the result in the draft panel's header, with New Run and Main Menu.
/// Entering the scene with no run active (from the menu, or opened directly in the editor) starts one.
///
/// The top bar's tabs (<see cref="DraftSceneTabs"/>) own the panels. While anything is owed (and on the
/// result step) the Draft tab is open and the others are locked; once the draft is done it switches to
/// Next Match by itself, and Next Match and Statistics can be switched between.
///
/// Card options are drawn with the same display-only card prefab the in-match card choice uses
/// (CardPreview Variant); hero options use the HeroPanel prefab, like the player's own hero panel.
/// The scene's layout is authored by hand; this only fills it in and toggles its panels.
/// </summary>
public class DraftController : MonoBehaviour
{
    /// <summary>One skull on the battle track.</summary>
    [System.Serializable]
    private class BattleNode
    {
        [Tooltip("Hover / click target. Gets an EventTrigger at runtime.")]
        public Graphic skull;
        [Tooltip("Shown on the upcoming battle.")]
        public GameObject highlight;
        [Tooltip("Shown on battles already won.")]
        public GameObject defeatedMark;
    }

    [Header("Draft panel")]
    [SerializeField] private TextMeshProUGUI draftHeaderText;
    [Tooltip("The header and option box: hidden while the draft-completed image is up.")]
    [SerializeField] private GameObject draftPickContent;
    [Tooltip("Shown in the draft panel once nothing is owed, in place of the pick box.")]
    [SerializeField] private GameObject draftCompletedImage;
    [Tooltip("Parent the option cards are spawned under; drives their layout.")]
    [SerializeField] private Transform optionsContainer;
    [Tooltip("Display-only card prefab used for each option (CardPreview Variant).")]
    [SerializeField] private GameObject optionPrefab;
    [SerializeField] private float baseCardScale = 0.8f;
    [Tooltip("Hero panel prefab used for each hero option (HeroPanel).")]
    [SerializeField] private GameObject heroOptionPrefab;
    [SerializeField] private float heroOptionScale = 0.9f;
    [Tooltip("Width each hero option takes in the options row's layout. The prefab's own rect is wider than " +
             "its art, and three of them would overflow the row.")]
    [SerializeField] private float heroOptionSlotWidth = 190f;
    [Tooltip("Scale multiplier while a hero option is hovered.")]
    [SerializeField] private float heroHoverScale = 1.05f;
    [SerializeField] private float heroHoverDuration = 0.1f;
    [SerializeField] private float hoverScale = 1.15f;
    [SerializeField] private float optionRevealDuration = 0.25f;
    [SerializeField] private float optionRevealStagger = 0.08f;

    [Header("Deck list")]
    [SerializeField] private TextMeshProUGUI deckHeaderText;
    [Tooltip("Entries are cloned from this one; the authored entries next to it are placeholders and get removed.")]
    [SerializeField] private CardButtonHandler deckEntryTemplate;

    [Header("Heroes")]
    [SerializeField] private GauntletHeroPanelView playerHero;
    [SerializeField] private GauntletHeroPanelView enemyHero;

    [Header("Next match")]
    [SerializeField] private TextMeshProUGUI nextMatchHeaderText;
    [SerializeField] private TextMeshProUGUI nextMatchRulesText;
    [Tooltip("One tab per fight on the opponent panel; reset to the upcoming one on every refresh.")]
    [SerializeField] private OpponentFightTabs fightTabs;

    [Header("Tabs")]
    [Tooltip("Draft / Next Match / Statistics. Locked on Draft while a pick is owed.")]
    [SerializeField] private DraftSceneTabs tabs;

    [Header("Battle track")]
    [SerializeField] private BattleNode[] battleNodes;
    [Tooltip("Gap in canvas units between the bottom of the selected skull and the top of the Proceed button.")]
    [SerializeField] private float proceedGap = 4f;
    [Tooltip("Skull tint once its battle is won; the others are shown untinted.")]
    [SerializeField] private Color beatenSkullTint = new Color(0.396f, 0.396f, 0.396f, 1f);

    [Header("Milestones")]
    [SerializeField] private MilestoneTrack milestoneTrack;
    [Tooltip("The perk option template (PerkOption), which owns the look of an option: its size, its " +
             "chip, its info panel and its hover.")]
    [SerializeField] private PerkOptionView perkOptionPrefab;

    [Header("Buttons")]
    [Tooltip("Proceed under the upcoming skull (greyed out while a pick is owed); New Run on the result step.")]
    [SerializeField] private Button primaryButton;
    [SerializeField] private TextMeshProUGUI primaryButtonLabel;
    [Tooltip("Back to the main menu; only shown once the run is over.")]
    [SerializeField] private Button menuButton;
    [SerializeField] private TextMeshProUGUI menuButtonLabel;

    [Header("Top bar")]
    [Tooltip("Back to the main menu. A run in progress stays saved and the Forged in Battle button resumes " +
             "it; a decided run is finished with, like the result screen's Main Menu.")]
    [SerializeField] private Button backButton;

    [Tooltip("Gives the run up after a confirmation popup: the run and its save are deleted. Shown only " +
             "while the run is in progress.")]
    [SerializeField] private Button abandonButton;
    [Tooltip("On the result step, New Run and Main Menu are centred side by side in this rect.")]
    [SerializeField] private RectTransform resultButtonArea;
    [SerializeField] private float resultButtonSpacing = 130f;
    [SerializeField] private string proceedLabel = "Proceed";
    [SerializeField] private string newRunLabel = "New Run";
    [SerializeField] private string mainMenuLabel = "Main Menu";

    private readonly List<GameObject> _spawned = new List<GameObject>();
    private readonly List<CardButtonHandler> _deckEntries = new List<CardButtonHandler>();
    private bool _tabsShown;
    private bool _leaving;
    private float _rulesFontSize;

    void Start()
    {
        // The draft runs on the plain rules: no Custom Game modifier may leak into the run's matches.
        MatchModifiers.Clear();

        // Normally entered with the run already loaded (a new run, or a resume from the menu). Opened
        // straight from the editor, it picks a saved run up itself -- and hands over to the Game scene if
        // that is where the run was left.
        if (!GauntletRun.IsActive)
        {
            if (GauntletRun.TryResume(out string scene))
            {
                if (scene != GauntletRun.Config.draftSceneName)
                {
                    _leaving = true;
                    GauntletRun.LoadScene(scene);
                    return;
                }
            }
            else
            {
                GauntletRun.StartNew();
            }
        }

        if (menuButton != null) menuButton.onClick.AddListener(ExitToMenu);
        if (backButton != null) backButton.onClick.AddListener(Back);
        if (abandonButton != null) abandonButton.onClick.AddListener(AskAbandon);
        if (primaryButton != null) primaryButton.onClick.AddListener(OnPrimary);

        if (nextMatchRulesText != null) _rulesFontSize = nextMatchRulesText.fontSize;
        ClearAuthoredPlaceholders();
        HookBattleNodes();
        if (fightTabs != null) fightTabs.FightShown += ShowFight;
        if (milestoneTrack != null)
        {
            milestoneTrack.NodeHovered += OnMilestoneHover;
            milestoneTrack.NodeClicked += OnMilestoneClicked;
        }
        Refresh();
        RefreshMilestones(animate: true);
    }

    // ---------------------------------------------------------------- Steps

    private void Refresh()
    {
        bool inProgress = GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress;

        // The enemy is rolled as soon as the hero is known (so a mirror can be avoided), and kept until
        // the battle is fought, so the preview and the fight agree.
        if (inProgress && GauntletRun.Hero != null && GauntletRun.EnemyHero == null)
            GauntletRun.PrepareNextBattle();

        RefreshDeck();
        playerHero?.ShowRunHero();
        RefreshNextMatch();
        RefreshTrack();
        if (milestoneTrack != null) milestoneTrack.RefreshStates();

        if (menuButtonLabel != null) menuButtonLabel.text = mainMenuLabel;
        if (menuButton != null) menuButton.gameObject.SetActive(!inProgress);
        if (abandonButton != null) abandonButton.gameObject.SetActive(inProgress);

        ShowStep();

        RefreshTabs();
    }

    /// <summary>
    /// Draft while anything is owed, Next Match once the draft is done. Entering the scene opens the right
    /// one outright; finishing the draft slides over to Next Match.
    /// </summary>
    private void RefreshTabs()
    {
        if (tabs == null) return;

        bool drafted = CanFight;
        var current = tabs.Current;

        if (!_tabsShown) tabs.Select(drafted ? DraftSceneTabs.TabKind.NextMatch : DraftSceneTabs.TabKind.Draft, animate: false);
        else if (!drafted && current != DraftSceneTabs.TabKind.Draft) tabs.Select(DraftSceneTabs.TabKind.Draft, animate: true);
        else if (drafted && current == DraftSceneTabs.TabKind.Draft) tabs.Select(DraftSceneTabs.TabKind.NextMatch, animate: true);
        else tabs.EndPeek();
        _tabsShown = true;

        bool open = drafted && !_leaving;
        tabs.SetInteractable(draftTab: !_leaving, nextMatchTab: open, statisticsTab: open && GauntletRun.Battles.Count > 0);
    }

    /// <summary>Fills the draft panel for the step the run is on. Also restores it after a milestone preview.</summary>
    private void ShowStep()
    {
        ClearOptions();
        SetDraftCompleted(false);

        if (GauntletRun.Outcome != GauntletRun.RunOutcome.InProgress) ShowResult();
        else if (GauntletRun.Hero == null) ShowHeroPick();
        else if (GauntletRun.PendingPerkPick) ShowPerkPick();
        // Cards a won battle still owes become a draft step here, the rewards panel having been left
        // without taking them; BeginBattleCardPicks is what turns them into one.
        else if (GauntletRun.PendingPicks > 0 || GauntletRun.BeginBattleCardPicks()) ShowCardPick();
        else if (GauntletRun.HasUnclaimedMilestone) ShowMilestonePrompt();
        else ShowBriefing();
    }

    private void ShowHeroPick()
    {
        SetDraftPanel("Choose Your Hero");
        ShowProceed();

        SpawnHeroOptions(GauntletRun.RollHeroOptions(), picked =>
        {
            GauntletRun.SetHero(picked);
            Refresh();
            RefreshMilestones(animate: false);
        });
    }

    private void ShowCardPick()
    {
        int pickNumber = GauntletRun.PicksThisStep - GauntletRun.PendingPicks + 1;
        if (GauntletRun.PickingDuplicate)
        {
            SetDraftPanel("Choose a Card to Duplicate");
        }
        else
        {
            string what = GauntletRun.PickingUpgraded ? "an Upgraded Card" : "a New Card";
            SetDraftPanel($"Draft {what} For Your Deck {pickNumber}/{GauntletRun.PicksThisStep}");
        }
        ShowProceed();

        SpawnOptions(GauntletRun.CurrentCardOptions(), picked =>
        {
            GauntletRun.AddDraftedCard(picked);
            Refresh();
        });
    }

    private void ShowPerkPick()
    {
        SetDraftPanel("Draft a New Perk For Your Hero");
        ShowProceed();

        SpawnPerkOptions(GauntletRun.CurrentPerkOptions, picked =>
        {
            GauntletRun.AddPerk(picked);
            Refresh();
        });
    }

    /// <summary>A reached milestone waits to be claimed: the next battle stays shut until it is.</summary>
    private void ShowMilestonePrompt()
    {
        SetDraftPanel(WithSubtitle("Milestone Reached!", "Click a glowing milestone to claim its reward"));
        ShowProceed();
    }

    /// <summary>Picks done: the Next Match tab shows the upcoming battle, and Proceed opens up.</summary>
    private void ShowBriefing()
    {
        SetDraftPanel(GauntletRun.LastRewardMessage);
        SetDraftCompleted(true);
        ShowProceed();
    }

    private void ShowResult()
    {
        bool won = GauntletRun.Outcome == GauntletRun.RunOutcome.Won;
        SetDraftPanel(won ? "Gauntlet Conquered!" : "Defeated");
        SetPrimary(newRunLabel);

        // The draft panel is empty on this step: its two buttons sit side by side in the middle of it.
        if (resultButtonArea != null)
        {
            Vector3 centre = resultButtonArea.TransformPoint(resultButtonArea.rect.center);
            Vector3 step = resultButtonArea.TransformVector(new Vector3(resultButtonSpacing * 0.5f, 0f, 0f));
            if (primaryButton != null) PlaceCentred((RectTransform)primaryButton.transform, centre + step);
            if (menuButton != null) PlaceCentred((RectTransform)menuButton.transform, centre - step);
        }
    }

    private void OnPrimary()
    {
        if (_leaving) return;

        if (GauntletRun.Outcome != GauntletRun.RunOutcome.InProgress)
        {
            GauntletRun.StartNew();
            Refresh();
            RefreshMilestones(animate: false);
            return;
        }

        if (!CanFight) return;

        _leaving = true;
        SetButtonsInteractable(false);
        // From here a quit restarts the fight rather than coming back to the draft.
        GauntletRun.BeginBattle();
        GauntletRun.LoadScene(GauntletRun.Config.gameSceneName);
    }

    private void ExitToMenu()
    {
        if (_leaving) return;

        _leaving = true;
        SetButtonsInteractable(false);
        string menu = GauntletRun.Config != null ? GauntletRun.Config.menuSceneName : "MainMenu";
        GauntletRun.End();
        GauntletRun.LoadScene(menu);
    }

    /// <summary>
    /// The top bar's Back: to the main menu. A run still in progress is only let go of -- it is saved
    /// after every step, so the Forged in Battle button picks it up where it was. A decided run has
    /// nothing left to resume, so leaving it finishes it, as the result screen's Main Menu does.
    /// </summary>
    private void Back()
    {
        if (_leaving) return;
        if (GauntletRun.Outcome != GauntletRun.RunOutcome.InProgress)
        {
            ExitToMenu();
            return;
        }

        _leaving = true;
        SetButtonsInteractable(false);
        string menu = GauntletRun.Config != null ? GauntletRun.Config.menuSceneName : "MainMenu";
        GauntletRun.Unload();
        GauntletRun.LoadScene(menu);
    }

    /// <summary>The top bar's Abandon Run: asks first, then deletes the run and its save and leaves.</summary>
    private void AskAbandon()
    {
        if (_leaving || !GauntletRun.IsActive) return;

        GauntletRunPrompts.ConfirmAbandon(() =>
        {
            _leaving = true;
            SetButtonsInteractable(false);
            GauntletRun.LoadScene(GauntletConfigSO.Load().menuSceneName);
        });
    }

    private static bool CanFight =>
        GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress
        && GauntletRun.Hero != null
        && GauntletRun.PendingPicks == 0
        && !GauntletRun.PendingPerkPick
        && !GauntletRun.PendingBattleReward
        && !GauntletRun.HasUnclaimedMilestone;

    // ---------------------------------------------------------------- Milestones

    /// <summary>
    /// Shows the run's track. On the first visit after it unlocks the lock slides away, and any XP
    /// gained since the last visit fills in.
    /// </summary>
    private void RefreshMilestones(bool animate)
    {
        if (milestoneTrack == null) return;

        bool unlocked = GauntletRun.MilestonesUnlocked;
        bool reveal = animate && unlocked && !GauntletRun.MilestonesRevealed;
        int from = animate ? GauntletRun.XpShown : GauntletRun.Xp;

        milestoneTrack.Show(GauntletRun.Milestones, from, GauntletRun.Xp, unlocked, reveal);

        if (unlocked)
        {
            GauntletRun.MilestonesRevealed = true;
            GauntletRun.XpShown = GauntletRun.Xp;
        }
    }

    /// <summary>
    /// Previews are only for the time between picks: while a pick is owed the draft panel stays on that,
    /// and they are not shown over the Statistics tab.
    /// </summary>
    private bool CanPreviewMilestones =>
        !_leaving
        && (tabs == null || (tabs.Current != DraftSceneTabs.TabKind.Statistics && !tabs.IsSwitching))
        && GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress
        && GauntletRun.MilestonesUnlocked
        && GauntletRun.PendingPicks == 0 && !GauntletRun.PendingPerkPick
        && !GauntletRun.PendingBattleReward;

    private void OnMilestoneHover(int index, bool entered)
    {
        if (!CanPreviewMilestones) return;

        if (entered)
        {
            // The preview is shown in the draft panel, over the briefing if that is the open tab.
            tabs?.Peek(DraftSceneTabs.TabKind.Draft);
            ShowMilestonePreview(index);
        }
        else
        {
            ShowStep();
            tabs?.EndPeek();
        }
    }

    private void OnMilestoneClicked(int index)
    {
        if (_leaving || !GauntletRun.CanClaim(index)) return;

        GauntletRun.ClaimMilestone(index);
        Refresh();
    }

    /// <summary>
    /// The hovered milestone in the draft panel: what it gives, and for a reached card or perk reward, the
    /// options it offers (display only; clicking the milestone claims it and makes them pickable).
    /// </summary>
    private void ShowMilestonePreview(int index)
    {
        var milestones = GauntletRun.Milestones;
        if (index < 0 || index >= milestones.Count) return;

        var milestone = milestones[index];
        string status;
        if (milestone.Reward == MilestoneReward.None) status = GauntletRun.MilestoneRewardDescription(milestone.Reward);
        else if (milestone.Claimed) status = "Claimed";
        else if (!milestone.Reached) status = $"{GauntletRun.MilestoneRewardDescription(milestone.Reward)}. Reach {milestone.Xp} XP to unlock";
        else status = $"{GauntletRun.MilestoneRewardDescription(milestone.Reward)}. Click to claim";

        ClearOptions();
        SetDraftCompleted(false);
        SetDraftPanel(WithSubtitle($"{milestone.Xp} XP: {GauntletRun.MilestoneRewardName(milestone.Reward)}", status));

        if (!milestone.Reached || milestone.Claimed) return;

        var cards = GauntletRun.MilestoneCardOptions(index);
        if (cards != null) SpawnOptions(cards, null);

        var perks = GauntletRun.MilestonePerkOptions(index);
        if (perks != null) SpawnPerkOptions(perks, null);
    }

    private static string WithSubtitle(string title, string subtitle) => $"{title}\n<size=60%>{subtitle}</size>";

    // ---------------------------------------------------------------- Battle track

    private void HookBattleNodes()
    {
        if (battleNodes == null) return;

        for (int i = 0; i < battleNodes.Length; i++)
        {
            var skull = battleNodes[i]?.skull;
            if (skull == null) continue;

            skull.raycastTarget = true;
            var trigger = skull.GetComponent<EventTrigger>();
            if (trigger == null) trigger = skull.gameObject.AddComponent<EventTrigger>();

            int index = i;
            AddTrigger(trigger, EventTriggerType.PointerEnter, () => OnNodeHover(index, true));
            AddTrigger(trigger, EventTriggerType.PointerExit, () => OnNodeHover(index, false));
        }
    }

    private static void AddTrigger(EventTrigger trigger, EventTriggerType type, System.Action action)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(_ => action());
        trigger.triggers.Add(entry);
    }

    private static bool IsUpcoming(int nodeIndex) => GauntletBattleTrack.IsUpcoming(nodeIndex);

    /// <summary>Mid-draft the upcoming skull previews the enemy; once drafted, the briefing has its own tab.</summary>
    private void OnNodeHover(int index, bool entered)
    {
        if (_leaving || CanFight || !IsUpcoming(index) || tabs == null) return;

        if (entered) tabs.Peek(DraftSceneTabs.TabKind.NextMatch);
        else tabs.EndPeek();
    }

    private void RefreshTrack()
    {
        if (battleNodes == null) return;

        for (int i = 0; i < battleNodes.Length; i++)
        {
            var node = battleNodes[i];
            if (node == null) continue;

            GauntletBattleTrack.RefreshNode(node.skull, node.highlight, node.defeatedMark, i, beatenSkullTint);
        }
    }

    /// <summary>Centres the primary button under <paramref name="target"/>, its top edge just below it.</summary>
    private void PlacePrimary(RectTransform target)
    {
        if (primaryButton == null || target == null) return;

        var rt = (RectTransform)primaryButton.transform;
        var corners = new Vector3[4];
        target.GetWorldCorners(corners);
        Vector3 bottomCentre = rt.parent.InverseTransformPoint((corners[0] + corners[3]) * 0.5f);

        float height = rt.rect.height * rt.localScale.y;
        PlaceCentred(rt, rt.parent.TransformPoint(bottomCentre - new Vector3(0f, proceedGap + height * 0.5f, 0f)));
    }

    /// <summary>Moves <paramref name="rt"/> so its centre lands on a world point, whatever its pivot.</summary>
    private static void PlaceCentred(RectTransform rt, Vector3 worldCentre)
    {
        Vector3 local = rt.parent.InverseTransformPoint(worldCentre);
        Vector2 size = Vector2.Scale(rt.rect.size, rt.localScale);
        rt.localPosition = new Vector3(
            local.x + (rt.pivot.x - 0.5f) * size.x,
            local.y + (rt.pivot.y - 0.5f) * size.y,
            rt.localPosition.z);
    }

    // ---------------------------------------------------------------- View

    /// <summary>Swaps the draft panel's pick box for the draft-completed image, or back.</summary>
    private void SetDraftCompleted(bool completed)
    {
        if (draftCompletedImage != null) draftCompletedImage.SetActive(completed);
        if (draftPickContent != null) draftPickContent.SetActive(!completed);
    }

    /// <summary>The draft panel's header; the panel itself is shown and hidden by its tab.</summary>
    private void SetDraftPanel(string header)
    {
        if (header != null && draftHeaderText != null) draftHeaderText.text = header;
    }

    /// <summary>Proceed under the upcoming skull; it only works once the picks are done.</summary>
    private void ShowProceed()
    {
        SetPrimary(proceedLabel, CanFight);

        int upcoming = GauntletRun.EncounterIndex;
        if (battleNodes != null && upcoming >= 0 && upcoming < battleNodes.Length && battleNodes[upcoming] != null)
            PlacePrimary(battleNodes[upcoming].skull != null ? battleNodes[upcoming].skull.rectTransform : null);
    }

    /// <summary>Shows the primary button with this label, or hides it for null.</summary>
    private void SetPrimary(string label, bool interactable = true)
    {
        if (primaryButton == null) return;

        primaryButton.gameObject.SetActive(label != null);
        primaryButton.interactable = interactable && !_leaving;
        if (label != null && primaryButtonLabel != null) primaryButtonLabel.text = label;
    }

    private void SetButtonsInteractable(bool interactable)
    {
        if (primaryButton != null) primaryButton.interactable = interactable;
        if (menuButton != null) menuButton.interactable = interactable;
        if (backButton != null) backButton.interactable = interactable;
        if (abandonButton != null) abandonButton.interactable = interactable;
        if (!interactable) tabs?.SetInteractable(false, false, false);
        foreach (var go in _spawned)
            if (go != null)
                foreach (var b in go.GetComponentsInChildren<Button>(true)) b.interactable = interactable;
    }

    private void RefreshNextMatch()
    {
        if (fightTabs != null) fightTabs.ResetToNext(GauntletRun.EncounterIndex);
        else GauntletNextMatch.Refresh(enemyHero, nextMatchHeaderText, nextMatchRulesText, _rulesFontSize);
    }

    /// <summary>The opponent panel filled with one fight of the run, from its fight tabs.</summary>
    private void ShowFight(int encounterIndex) =>
        GauntletNextMatch.RefreshEncounter(encounterIndex, enemyHero, nextMatchHeaderText, nextMatchRulesText, _rulesFontSize);

    private void RefreshDeck() =>
        GauntletDeckList.Refresh(deckHeaderText, deckEntryTemplate, _deckEntries, GauntletRun.Deck);

    /// <summary>
    /// Removes the stand-ins the layout was authored with: the example option cards and the example
    /// deck entries (the template is kept, hidden, to clone from).
    /// </summary>
    private void ClearAuthoredPlaceholders()
    {
        if (optionsContainer != null)
            for (int i = optionsContainer.childCount - 1; i >= 0; i--)
                Destroy(optionsContainer.GetChild(i).gameObject);

        GauntletDeckList.ClearPlaceholders(deckEntryTemplate);
    }

    private void SpawnOptions<T>(List<T> options, System.Action<CardSO> onPicked) where T : CardSO
    {
        if (optionsContainer == null || optionPrefab == null)
        {
            Debug.LogWarning("[Gauntlet] DraftController is not wired up (options container / prefab missing).");
            return;
        }

        var baseScale = Vector3.one * baseCardScale;

        for (int i = 0; i < options.Count; i++)
        {
            CardSO option = options[i];
            if (option == null) continue;

            GameObject go = Instantiate(optionPrefab, optionsContainer);
            go.SetActive(true);
            _spawned.Add(go);

            var modal = go.GetComponent<CardModal>();
            var view = go.GetComponent<CardView>();
            if (modal != null)
            {
                modal.UpdateModal(option, null, true);
                if (view != null)
                {
                    view.UpdateView(modal);
                    view.SetPlayableOutline(true);
                    view.SetHoveredOutline(false);

                    var hover = go.GetComponent<CardHoverOutline>();
                    if (hover == null) hover = go.AddComponent<CardHoverOutline>();
                    hover.ConfigureHoverScale(hoverScale, baseScale);
                }
            }

            if (go.TryGetComponent(out Button button))
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    if (_leaving) return;
                    onPicked?.Invoke(option);
                });
            }

            go.transform.localScale = Vector3.zero;
            go.transform.DOScale(baseCardScale, optionRevealDuration).SetDelay(i * optionRevealStagger);
        }
    }

    private void SpawnHeroOptions(List<HeroSO> heroes, System.Action<HeroSO> onPicked)
    {
        if (optionsContainer == null || heroOptionPrefab == null)
        {
            Debug.LogWarning("[Gauntlet] DraftController is not wired up (options container / hero prefab missing).");
            return;
        }

        for (int i = 0; i < heroes.Count; i++)
        {
            HeroSO hero = heroes[i];
            if (hero == null) continue;

            GameObject go = Instantiate(heroOptionPrefab, optionsContainer);
            go.SetActive(true);
            var rt = (RectTransform)go.transform;
            rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
            rt.sizeDelta = new Vector2(heroOptionSlotWidth, rt.sizeDelta.y);
            _spawned.Add(go);

            var view = GauntletHeroPanelView.FromInstance(go);
            GauntletNextMatch.RefreshHero(view, hero);

            System.Action pick = () =>
            {
                if (_leaving) return;
                onPicked(hero);
            };

            // The portrait is a button of its own; the rest of the panel picks through the trigger below.
            var button = view.portrait != null ? view.portrait.GetComponent<Button>() : null;
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => pick());
            }

            // Hovering anywhere on the panel lights its Highlighted image and scales it up a touch.
            var highlighted = go.transform.Find("Highlighted");
            if (highlighted != null) highlighted.gameObject.SetActive(false);

            // The passive text shows for the whole panel, under the first chip, so the chips' own
            // beside-the-chip tooltips are switched off here.
            RectTransform tooltipAnchor = null;
            var passiveText = new System.Text.StringBuilder();
            if (view.passiveChips != null)
            {
                foreach (var chip in view.passiveChips)
                {
                    if (chip == null || !chip.TryGetComponent(out UITooltipTrigger chipTooltip)) continue;
                    if (chip.Passive != null && !string.IsNullOrEmpty(chipTooltip.Message))
                    {
                        if (tooltipAnchor == null) tooltipAnchor = (RectTransform)chip.transform;
                        if (passiveText.Length > 0) passiveText.Append("\n\n");
                        passiveText.Append(chipTooltip.Message);
                    }
                    chipTooltip.enabled = false;
                }
            }
            string tooltip = passiveText.ToString();

            var trigger = go.GetComponent<EventTrigger>();
            if (trigger == null) trigger = go.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerEnter, () => SetHeroOptionHovered(go, highlighted, true, tooltip, tooltipAnchor));
            AddTrigger(trigger, EventTriggerType.PointerExit, () => SetHeroOptionHovered(go, highlighted, false, tooltip, tooltipAnchor));
            AddTrigger(trigger, EventTriggerType.PointerClick, pick);

            go.transform.localScale = Vector3.zero;
            go.transform.DOScale(heroOptionScale, optionRevealDuration).SetDelay(i * optionRevealStagger);
        }
    }

    /// <summary>
    /// One <see cref="PerkOptionView"/> per passive -- the shared PerkOption prefab, which the
    /// end-of-match perk milestone (<see cref="PerkSelectionPanel"/>) spawns too, so a perk looks the
    /// same wherever it is offered. Null <paramref name="onPicked"/> shows them for display only
    /// (a milestone preview), which the prefab reads as inert.
    /// </summary>
    private void SpawnPerkOptions(IReadOnlyList<HeroPassiveSO> perks, System.Action<HeroPassiveSO> onPicked)
    {
        if (optionsContainer == null || perkOptionPrefab == null)
        {
            Debug.LogWarning("[Gauntlet] DraftController is not wired up (options container / perk prefab missing).");
            return;
        }

        for (int i = 0; i < perks.Count; i++)
        {
            HeroPassiveSO perk = perks[i];
            if (perk == null) continue;

            PerkOptionView option = Instantiate(perkOptionPrefab, optionsContainer);
            option.name = perk.passiveName;
            option.Show(perk);
            _spawned.Add(option.gameObject);

            // No handler on a perk that is only being shown (the run's own perks on the briefing step),
            // which is also what leaves it inert.
            if (onPicked != null)
                option.OnClicked = _ =>
                {
                    if (_leaving) return;
                    onPicked(perk);
                };

            option.PlayReveal(i * optionRevealStagger);
        }
    }

    private void SetHeroOptionHovered(GameObject option, Transform highlighted, bool hovered, string tooltip, RectTransform tooltipAnchor)
    {
        if (_leaving && hovered) return;

        if (highlighted != null) highlighted.gameObject.SetActive(hovered);

        if (UITooltip.Instance != null)
        {
            if (hovered && tooltipAnchor != null) UITooltip.Instance.ShowBelow(tooltip, tooltipAnchor);
            else UITooltip.Instance.Hide();
        }

        option.transform.DOKill();
        float scale = heroOptionScale * (hovered ? heroHoverScale : 1f);
        option.transform.DOScale(scale, heroHoverDuration);
    }

    private void ClearOptions()
    {
        // A hovered hero option's tooltip would otherwise outlive the option.
        if (_spawned.Count > 0 && UITooltip.Instance != null) UITooltip.Instance.Hide();

        foreach (var go in _spawned)
        {
            if (go == null) continue;
            go.transform.DOKill();
            Destroy(go);
        }
        _spawned.Clear();
    }
}
