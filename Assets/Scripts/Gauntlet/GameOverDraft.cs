using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The run's draft, on the end-of-match panel's Selection tab: what the Draft scene used to do, now that
/// the whole run is played between the main menu and the Game scene. The Game scene opens straight onto
/// it whenever the run is between battles (<see cref="GauntletRun.Location"/> Draft) -- a new run, or one
/// resumed from there -- with no match behind it (<see cref="PopupManager.OpenDraft"/>).
///
/// It shows whichever step the run is on, one after another:
///   1. no hero yet: pick 1 of N heroes, shown where the card options go;
///   2. card picks owed: pick 1 of N cards in the panel's own card selection, routed there the way the
///      end-of-match rewards route theirs (<see cref="CardSelectionPanel.Route"/>);
///   3. a perk owed: pick it in the panel's own perk selection;
///   4. nothing owed: "Click anywhere to proceed" takes the options' place, and the next click anywhere
///      leaves for the fight.
/// The run's hero and deck fill in beside the options as they are picked (<see cref="BattleRewardLoadout"/>).
/// The Selection tab is the only one up for the whole draft: the top buttons stay put away and the
/// panel's Proceed (<see cref="GameOverVersus"/>) stays hidden, the prompt being the only way on.
///
/// The panel is authored disabled, so this is found the way the rest of it is (<see cref="Resolve"/>).
/// </summary>
public class GameOverDraft : MonoBehaviour
{
    [Header("Panels")]
    [Tooltip("The run's hero and deck, beside the options.")]
    [SerializeField] private BattleRewardLoadout loadout;
    [SerializeField] private CardSelectionPanel cardPanel;
    [SerializeField] private PerkSelectionPanel perkPanel;

    [Header("Hero pick")]
    [Tooltip("Shown while a hero is picked; holds the prompt and the options.")]
    [SerializeField] private GameObject heroPick;
    [SerializeField] private TextMeshProUGUI heroPrompt;
    [Tooltip("Where the hero options are spawned; drives their layout.")]
    [SerializeField] private RectTransform heroOptions;
    [Tooltip("One per hero option (the HeroPanel prefab).")]
    [SerializeField] private GameObject heroOptionPrefab;
    [SerializeField] private string heroPickPrompt = "Choose Your Hero";
    [SerializeField] private float heroOptionScale = 0.9f;
    [Tooltip("How much a hovered hero option swells, on top of its scale.")]
    [SerializeField] private float heroHoverScale = 1.05f;
    [SerializeField] private float heroHoverDuration = 0.1f;
    [SerializeField] private float optionRevealDuration = 0.25f;
    [SerializeField] private float optionRevealStagger = 0.08f;
    [Tooltip("The picked hero's passives unlock on the loadout's hero panel the way a drafted perk does: " +
             "the locked frame slides down this far, then shrinks away.")]
    [SerializeField] private float passiveUnlockSlide = 30f;
    [SerializeField] private float passiveUnlockDuration = 0.3f;
    [SerializeField] private float passiveUnlockShrinkDuration = 0.15f;
    [Tooltip("Delay between one passive unlocking and the next.")]
    [SerializeField] private float passiveUnlockStagger = 0.15f;
    [Tooltip("How far right the hero panel's locked image slides before it shrinks away; it uses the " +
             "passives' unlock durations, and they follow once it is off.")]
    [SerializeField] private float heroUnlockSlide = 60f;

    [Header("Card and perk picks")]
    [Tooltip("{0} is the pick being made, {1} the picks this step, {2} 'a New Card' or 'an Upgraded Card'.")]
    [SerializeField] private string cardPickPrompt = "Draft {2} For Your Deck {0}/{1}";
    [SerializeField] private string duplicatePickPrompt = "Choose a Card to Duplicate";
    [SerializeField] private string perkPickPrompt = "Draft a New Perk For Your Hero";

    [Header("Tabs")]
    [Tooltip("The tab this draft lives on.")]
    [SerializeField] private GameOverTabs.TabKind draftTab = GameOverTabs.TabKind.Draft;

    [Header("Done")]
    [Tooltip("\"Click anywhere to proceed\": shown where the options were once nothing is owed, beside the " +
             "deck and the hero. A click anywhere on the screen then leaves for the fight; the panel's Proceed " +
             "and the top buttons stay away for the whole draft.")]
    [SerializeField] private CanvasGroup proceedPrompt;
    [Tooltip("Wait after the last pick before the prompt shows and clicks count, so the pick's card can land.")]
    [SerializeField] private float promptDelay = 0.4f;
    [SerializeField] private float promptFadeDuration = 0.3f;
    [Range(0f, 1f)] [SerializeField] private float promptPulseAlpha = 0.35f;
    [SerializeField] private float promptPulseDuration = 0.8f;

    private readonly List<GameObject> _heroOptions = new List<GameObject>();
    private bool _leaving;

    /// <summary>Nothing owed: the prompt is up (or on its way), and a click anywhere goes to the fight.</summary>
    private bool _done;
    private float _clicksFrom;

    /// <summary>
    /// True from <see cref="Begin"/> until the draft leaves for the fight: the panel's Proceed
    /// (<see cref="GameOverVersus"/>) stays hidden, since the draft's own prompt is the way on.
    /// </summary>
    public static bool Active { get; private set; }

    /// <summary>The tab this draft lives on, which the panel opens locked to while it is owed.</summary>
    public GameOverTabs.TabKind Tab => draftTab;

    private static GameOverDraft _instance;

    /// <summary>The draft in the current scene, disabled or not.</summary>
    public static GameOverDraft Resolve()
    {
        if (_instance == null)
        {
            var found = FindObjectsByType<GameOverDraft>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _instance = found.Length > 0 ? found[0] : null;
        }
        return _instance;
    }

    /// <summary>True while the draft still owes something: the way on to the fight stays shut.</summary>
    public static bool Owed =>
        GauntletRun.IsActive && GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress
        && (GauntletRun.Hero == null || GauntletRun.PendingPicks > 0 || GauntletRun.PendingPerkPick);

    /// <summary>
    /// Opens the draft on the step the run is on. Call once the panel is up; the Selection tab is
    /// already locked open by then (<see cref="GameOverTabs.OpenLockedTo"/>).
    /// </summary>
    public void Begin()
    {
        _leaving = false;
        Active = true;
        ShowStep();
    }

    private void OnDestroy()
    {
        Active = false;
        if (CardSelectionPanel.Resolve() == cardPanel) CardSelectionPanel.Route(null);
        ClearHeroOptions();
        HideProceedPrompt();
    }

    // Polled: the prompt's click can land on anything, a deck card or the hero included, so it is read
    // off the input rather than through a raycast target (the way the match-up's own prompt is).
    private void Update()
    {
        if (!_done || _leaving || Time.unscaledTime < _clicksFrom) return;
        if (ClickedAnywhere()) Go();
    }

    private static bool ClickedAnywhere()
    {
        if (EscMenuController.IsOpen || ConfirmationPopup.IsOpen) return false;
        if (Input.GetMouseButtonDown(0)) return true;
        return Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began;
    }

    private void ShowStep()
    {
        ClearHeroOptions();
        _done = false;
        HideProceedPrompt();
        if (heroPick != null) heroPick.SetActive(false);

        if (loadout != null) loadout.Show(shiftPanels: false);

        if (GauntletRun.Hero == null) ShowHeroPick();
        else if (GauntletRun.PendingPerkPick) ShowPerkPick();
        // Cards a won battle still owes become a draft step here, the rewards having been left without
        // taking them; BeginBattleCardPicks is what turns them into one.
        else if (GauntletRun.PendingPicks > 0 || GauntletRun.BeginBattleCardPicks()) ShowCardPick();
        else Finish();
    }

    // ---------------------------------------------------------------- Hero

    private void ShowHeroPick()
    {
        if (heroPick != null) heroPick.SetActive(true);
        if (heroPrompt != null) heroPrompt.text = heroPickPrompt;

        if (heroOptions == null || heroOptionPrefab == null)
        {
            Debug.LogWarning("GameOverDraft: the hero pick is not wired up (options / hero prefab missing).");
            return;
        }

        var heroes = GauntletRun.RollHeroOptions();
        for (int i = 0; i < heroes.Count; i++)
        {
            HeroSO hero = heroes[i];
            if (hero == null) continue;

            var go = Instantiate(heroOptionPrefab, heroOptions);
            go.SetActive(true);
            _heroOptions.Add(go);

            var view = GauntletHeroPanelView.FromInstance(go);
            GauntletNextMatch.RefreshHero(view, hero);

            System.Action pick = () => PickHero(hero);

            // The portrait is a button of its own; the rest of the panel picks through the trigger below.
            var button = view.portrait != null ? view.portrait.GetComponent<Button>() : null;
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => pick());
            }

            var highlighted = go.transform.Find("Highlighted");
            if (highlighted != null) highlighted.gameObject.SetActive(false);

            // The passive text shows for the whole panel, under the first chip, so the chips' own
            // beside-the-chip tooltips are switched off here -- as the Draft scene did.
            RectTransform tooltipAnchor = null;
            var passiveText = new System.Text.StringBuilder();
            if (view.passiveChips != null)
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
            string tooltip = passiveText.ToString();

            var trigger = go.GetComponent<EventTrigger>();
            if (trigger == null) trigger = go.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerEnter, () => SetHovered(go, highlighted, true, tooltip, tooltipAnchor));
            AddTrigger(trigger, EventTriggerType.PointerExit, () => SetHovered(go, highlighted, false, tooltip, tooltipAnchor));
            AddTrigger(trigger, EventTriggerType.PointerClick, pick);

            go.transform.localScale = Vector3.zero;
            go.transform.DOScale(heroOptionScale, optionRevealDuration).SetDelay(i * optionRevealStagger);
        }
    }

    private void PickHero(HeroSO hero)
    {
        if (_leaving || hero == null || GauntletRun.Hero != null) return;

        GauntletRun.SetHero(hero);
        ShowStep();

        // The hero panel's lock comes off first, then the hero's passives unlock one by one.
        var heroUnlock = loadout != null
            ? loadout.PlayHeroUnlock(heroUnlockSlide, passiveUnlockDuration, passiveUnlockShrinkDuration)
            : null;
        PlayHeroPassiveUnlocks(heroUnlock != null ? heroUnlock.Duration() : 0f);
    }

    /// <summary>
    /// The picked hero's own passives unlocking on the loadout's hero panel, one after another -- the
    /// same locked-frame slide a drafted perk plays (<see cref="PassiveChipButton.PlayUnlock"/>).
    /// ShowStep has already drawn the slots filled; PlayUnlock puts the locked frame back over each one
    /// before sliding it off, so a slot waiting its turn still reads as locked.
    /// </summary>
    private void PlayHeroPassiveUnlocks(float delay)
    {
        var chips = loadout != null ? loadout.PassiveChips : null;
        if (chips == null) return;

        int order = 0;
        foreach (var chip in chips)
        {
            if (chip == null || chip.Passive == null) continue;
            var unlock = chip.PlayUnlock(chip.Passive, passiveUnlockSlide, passiveUnlockDuration, passiveUnlockShrinkDuration);
            if (unlock != null) unlock.SetDelay(delay + order++ * passiveUnlockStagger);
        }
    }

    private void SetHovered(GameObject option, Transform highlighted, bool hovered, string tooltip, RectTransform tooltipAnchor)
    {
        if (option == null || (_leaving && hovered)) return;
        if (highlighted != null) highlighted.gameObject.SetActive(hovered);

        if (UITooltip.Instance != null)
        {
            if (hovered && tooltipAnchor != null && !string.IsNullOrEmpty(tooltip)) UITooltip.Instance.ShowBelow(tooltip, tooltipAnchor);
            else UITooltip.Instance.Hide();
        }

        option.transform.DOKill();
        option.transform.DOScale(heroOptionScale * (hovered ? heroHoverScale : 1f), heroHoverDuration);
    }

    private static void AddTrigger(EventTrigger trigger, EventTriggerType type, System.Action action)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(_ => action());
        trigger.triggers.Add(entry);
    }

    private void ClearHeroOptions()
    {
        // The hovered option goes with the rest: its tooltip must not outlive it.
        if (_heroOptions.Count > 0 && UITooltip.Instance != null) UITooltip.Instance.Hide();

        foreach (var go in _heroOptions)
            if (go != null)
            {
                go.transform.DOKill();
                Destroy(go);
            }
        _heroOptions.Clear();
    }

    // ---------------------------------------------------------------- Cards

    private void ShowCardPick()
    {
        int pickNumber = GauntletRun.PicksThisStep - GauntletRun.PendingPicks + 1;
        string prompt = GauntletRun.PickingDuplicate
            ? duplicatePickPrompt
            : string.Format(cardPickPrompt, pickNumber, GauntletRun.PicksThisStep,
                GauntletRun.PickingUpgraded ? "an Upgraded Card" : "a New Card");

        if (cardPanel != null) CardSelectionPanel.Route(cardPanel);
        CardChoice.Instance.Begin(GauntletRun.CurrentCardOptions(), prompt, OnCardPicked, requirePlayerTurn: false);

        if (!CardChoice.Instance.HasActiveRequest)
        {
            // Nothing could be offered: leave the picks owed rather than hang on an empty panel.
            Debug.LogWarning("GameOverDraft: no card choice could be opened.");
            CardSelectionPanel.Route(null);
            return;
        }

        var target = CardSelectionPanel.Resolve();
        if (target != null && loadout != null) target.SetDeckTarget(loadout.DeckAnchor);
    }

    private void OnCardPicked(CardSO card)
    {
        if (_leaving) return;

        GauntletRun.AddDraftedCard(card);
        CardSelectionPanel.Route(null);
        ShowStep();
    }

    // ---------------------------------------------------------------- Perks

    private void ShowPerkPick()
    {
        if (perkPanel == null) return;

        bool opened = perkPanel.Open(GauntletRun.CurrentPerkOptions, perkPickPrompt, TakePerk);
        if (opened && loadout != null) perkPanel.SetUnlockTarget(loadout.PassiveSlot);
    }

    private void TakePerk(HeroPassiveSO perk)
    {
        if (_leaving || perk == null) return;

        GauntletRun.AddPerk(perk);
        perkPanel.Close();
        ShowStep();
    }

    // ---------------------------------------------------------------- Done

    /// <summary>
    /// Nothing owed: "Click anywhere to proceed" takes the options' place, and the next click leaves for
    /// the fight. The tabs stay locked and the top buttons put away -- there is nothing else to read here,
    /// and the panel's Proceed stays hidden (<see cref="Active"/>).
    /// </summary>
    private void Finish()
    {
        _done = true;
        _clicksFrom = Time.unscaledTime + promptDelay;
        ShowProceedPrompt();
    }

    private void ShowProceedPrompt()
    {
        if (proceedPrompt == null) return;

        proceedPrompt.DOKill();
        proceedPrompt.gameObject.SetActive(true);
        proceedPrompt.alpha = 0f;
        proceedPrompt.blocksRaycasts = false;
        proceedPrompt.interactable = false;

        // The pulse is its own tween: DOTween won't loop forever inside a Sequence.
        proceedPrompt.DOFade(1f, promptFadeDuration).SetDelay(promptDelay).SetUpdate(true).OnComplete(() =>
            proceedPrompt.DOFade(promptPulseAlpha, promptPulseDuration).SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo).SetUpdate(true));
    }

    private void HideProceedPrompt()
    {
        if (proceedPrompt == null) return;

        proceedPrompt.DOKill();
        proceedPrompt.alpha = 0f;
        proceedPrompt.gameObject.SetActive(false);
    }

    /// <summary>Leaves for the run's next fight, once nothing is owed.</summary>
    private void Go()
    {
        if (_leaving || Owed || !GauntletRun.IsActive || GauntletRun.Outcome != GauntletRun.RunOutcome.InProgress) return;

        _leaving = true;
        // Active stays set until the panel goes with the scene: dropping it here would let the panel's
        // Proceed show for the frames the fight takes to load.
        HideProceedPrompt();
        GauntletRun.FightNextBattle();
    }
}
