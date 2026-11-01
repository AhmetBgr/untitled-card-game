using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The rewards panel of a won Forged in Battle battle: the list of what the battle paid out, opened beside
/// the end-of-match panel (<see cref="PopupManager.OpenGameOverPopup"/>), the way Slay the Spire hands out
/// a fight's rewards before the map comes back. It sits under that panel in the hierarchy, so it scales
/// in and out with it. Its Proceed replaces the end-of-match one.
///
/// It lists two kinds of reward, one row each:
///   * the new cards a win is worth (<see cref="GauntletRun.PendingBattleReward"/>): the row runs the
///     picks it owes one at a time through the match's own <see cref="CardSelectionPanel"/>, by way of
///     <see cref="CardChoice"/>, so a reward pick looks exactly like an in-match Discover -- routed to
///     the end-of-match panel's own copy of it, beside the run's loadout. On the review tabs a pick
///     fades that selection tab in over Result, and taking the reward fades Result back; the tab bar
///     stays up, so the pick can be left open while other tabs are read. Without the tabs the panel
///     hides itself while the options are up;
///   * every milestone the battle's XP just reached: the row is added the moment the track under the
///     end-of-match panel fills up to it (<see cref="BattleXpTrack.XpShown"/>), and claims it. Instant
///     rewards apply on the spot and the row says what they did, an
///     Upgraded Cards milestone runs its picks like the card row, and a New Perk milestone opens its
///     options in the <see cref="PerkSelectionPanel"/>, just as a card row opens the card panel.
///
/// The battle's score is credited here rather than on the way out (<see cref="GauntletRun.CollectScore"/>),
/// so the milestones the win reaches are already reached by the time the rows are built.
///
/// Rewards are not skippable: Proceed stays locked until nothing is owed. It then opens the end-of-match
/// panel's Battles tab, the briefing for the next battle, which the panel's Proceed leads into without the
/// Draft scene in between (<see cref="GameOverTabs"/>, <see cref="GameOverVersus"/>); with no tabs authored, or with no battle
/// left to fight, it leaves for the Draft scene (<see cref="GauntletRun.LeaveBattle"/>) as it always did.
/// The panel GameObject is authored DISABLED, like CardSelectionPanel, so it is resolved lazily
/// through <see cref="Resolve"/> rather than cached from Awake.
/// </summary>
public class BattleRewardsPanel : MonoBehaviour
{
    /// <summary>One row of the list, with what it is for.</summary>
    private class Row
    {
        public Button button;
        public TextMeshProUGUI label;
        public Image icon;

        /// <summary>Up only while the row can be clicked; the template's "Highlight" child, if it has one.</summary>
        public GameObject highlight;

        /// <summary>Index into <see cref="GauntletRun.Milestones"/>, or -1 for the card reward.</summary>
        public int milestone = -1;

        /// <summary>Taken: the row is inert and reads as what it gave.</summary>
        public bool taken;
    }

    [Header("Panel")]
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private string titleLabel = "Rewards!";
    [Tooltip("Prompt over the perk options while a New Perk milestone is being taken.")]
    [SerializeField] private string perkPickPrompt = "Draft a New Perk For Your Hero";

    [Tooltip("Parent the reward rows are spawned under; drives their layout.")]
    [SerializeField] private Transform rowsContainer;

    [Tooltip("Cloned once per reward; kept hidden. Its label is the first TextMeshProUGUI under it, its " +
             "icon the Image on a child called Icon.")]
    [SerializeField] private Button rowTemplate;

    [Tooltip("Leaves for the Draft scene. Locked until every reward has been taken.")]
    [SerializeField] private Button proceedButton;

    [Tooltip("How far the Proceed button fades while it is locked. Its sprite-swap transition has no " +
             "disabled look of its own, so the fade is what says it cannot be clicked yet.")]
    [Range(0f, 1f)] [SerializeField] private float lockedProceedAlpha = 0.45f;

    [Header("Card reward")]
    [Tooltip("Every line and icon in this section is the fallback for the one in the reward catalog " +
             "(MilestoneRewardCatalogSO): whatever the catalog leaves empty is read from here.")]
    [SerializeField] private Sprite cardIcon;
    [SerializeField] private string cardRowLabel = "Add {0} cards to your deck";
    [SerializeField] private string cardRowTakenLabel = "{0} cards added to your deck";
    [Tooltip("Shown once an Upgraded Cards milestone's picks are taken; {0} is how many.")]
    [SerializeField] private string upgradedCardsTakenLabel = "{0} upgraded cards added to your deck";
    [Tooltip("Taken label of an Upgraded Cards milestone worth a single card.")]
    [SerializeField] private string upgradedCardTakenLabel = "Upgraded card added to your deck";
    [Tooltip("Prompt over the card options; {0} is the pick being made and {1} the picks owed.")]
    [SerializeField] private string cardPickPrompt = "Choose a card for your deck ({0}/{1})";
    [Tooltip("Prompt of a Duplicate milestone's pick.")]
    [SerializeField] private string duplicatePickPrompt = "Choose a Card to Duplicate";
    [Tooltip("The run's hero and deck, shown beside the options while a card or a perk is picked.")]
    [SerializeField] private BattleRewardLoadout loadout;

    [Tooltip("Where a New Perk milestone's options are picked, beside the loadout. Left empty, one is " +
             "looked up in the scene; with none there at all the pick is left to the Draft scene.")]
    [SerializeField] private PerkSelectionPanel perkPanel;

    [Tooltip("Where card rewards are picked, beside the loadout: every card choice is routed here while a " +
             "pick is up. Left empty, the match's own card panel is used.")]
    [SerializeField] private CardSelectionPanel cardPanel;

    [Tooltip("The end-of-match tab the loadout and the selection panels sit on. A pick slides it in, and " +
             "taking the reward slides back to Result.")]
    [SerializeField] private GameOverTabs.TabKind pickTab = GameOverTabs.TabKind.Draft;

    [Header("Milestone rewards")]
    [Tooltip("Icon on a milestone row whose reward has none of its own in the catalog " +
             "(MilestoneRewardCatalogSO). A perk option shows the passive's own icon instead.")]
    [SerializeField] private Sprite milestoneIcon;
    [Tooltip("Tint for that shared icon: the track's own 'reached' gold, so a milestone row reads as one. " +
             "A reward with its own icon brings its own tint with it.")]
    [SerializeField] private Color milestoneIconTint = new Color(1f, 0.83f, 0.24f, 1f);
    [Tooltip("A milestone row reads as its reward and nothing else, the way a card reward row does: the " +
             "track beside the panel already says which milestone paid for it. {1} is the reward's name " +
             "and {0} the milestone's XP, so '{0} XP milestone: {1}' puts the milestone back in front.")]
    [SerializeField] private string milestoneRowLabel = "{1}";
    [Tooltip("Shown on a milestone row once claimed; {0} is what the reward did.")]
    [SerializeField] private string milestoneTakenLabel = "{0}";
    [Tooltip("Shown on the perk milestone's row once a perk is taken; {0} is the perk's name.")]
    [SerializeField] private string perkTakenLabel = "{0} learned";

    [Header("Timing")]
    [SerializeField] private float scaleInDuration = 0.4f;
    [Tooltip("How long each row takes to pop in, and the stagger between them.")]
    [SerializeField] private float rowRevealDuration = 0.25f;
    [SerializeField] private float rowRevealStagger = 0.08f;

    /// <summary>The reward rows, in the order they are listed.</summary>
    private readonly List<Row> _rows = new List<Row>();

    /// <summary>The milestone row whose perk is being picked, while the perk panel is up.</summary>
    private Row _perkRow;

    /// <summary>The row whose card picks are running: the card reward, or an Upgraded Cards milestone.</summary>
    private Row _pickingRow;

    /// <summary>A perk is being picked in the perk panel; nothing here can be taken or proceeded past.</summary>
    private bool _pickingPerk;

    /// <summary>A pick is being reopened from a save: put on its tab, but that tab is not brought up.</summary>
    private bool _pickInBackground;

    /// <summary>What that row will read once its picks are taken.</summary>
    private string _pickingTakenLabel;

    /// <summary>The options could not be put on screen; the Draft scene asks for the picks instead.</summary>
    private bool _pickBlocked;

    private bool _leaving;

    /// <summary>
    /// The panel is put back up from a saved run (<see cref="GauntletRun.Results"/>) rather than opened
    /// on a battle just won: rows already taken come back taken, and a pick left open reopens.
    /// </summary>
    private ResultsSave _restoring;

    /// <summary>Added on demand: what fades the Proceed button while it is locked.</summary>
    private CanvasGroup _proceedGroup;

    /// <summary>Proceed has handed the run over to the Battles tab; nothing here is owed any more.</summary>
    private bool _handedOver;

    /// <summary>The end-of-match panel's XP track, followed so milestone rows land as the bar reaches them.</summary>
    private BattleXpTrack _xpSource;

    /// <summary>XP the track has shown so far: milestones up to here are listed.</summary>
    private int _shownXp;

    /// <summary>The end-of-match panel beside this one; hidden along with it while a card pick is up.</summary>
    private CanvasGroup _companion;

    /// <summary>When the next row may pop in: after the scale-in, and a stagger after the row before.</summary>
    private float _nextRowRevealTime;

    private static BattleRewardsPanel _instance;

    /// <summary>
    /// The panel in the current scene, disabled or not. Re-found whenever the cached one has gone away, so
    /// reloading the match scene picks up the new scene's panel instead of a destroyed one -- the same
    /// lazy resolve <see cref="CardSelectionPanel"/> uses.
    /// </summary>
    public static BattleRewardsPanel Resolve()
    {
        if (_instance == null)
        {
            var found = FindObjectsOfType<BattleRewardsPanel>(true);
            _instance = found.Length > 0 ? found[0] : null;
        }
        return _instance;
    }

    /// <summary>
    /// True while the run has rewards for the battle just won that are still to be taken. A run that is
    /// over has none: a loss ends it, and the last win goes straight to the result screen -- nothing on a
    /// finished run can be claimed (<see cref="GauntletRun.CanClaim"/>), so a panel listing rewards there
    /// could never be got past.
    /// </summary>
    public static bool HasRewards =>
        GauntletRun.IsActive && GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress
        && (GauntletRun.PendingBattleReward || GauntletRun.HasUnclaimedMilestone);

    void Start()
    {
        if (proceedButton != null) proceedButton.onClick.AddListener(OnProceed);
    }

    /// <summary>
    /// Opens the panel on the rewards the won battle owes, scaling in after <paramref name="delay"/> so it
    /// lands together with the end-of-match panel. Milestone rows follow <paramref name="xpSource"/> as it
    /// fills; without one (or with it hidden) every reached milestone is listed straight away.
    /// <paramref name="companion"/> is hidden while a card pick owns the screen. Returns false, leaving the
    /// panel closed, when nothing is owed.
    /// </summary>
    public bool Show(float delay, BattleXpTrack xpSource, CanvasGroup companion)
    {
        // Must run after xpSource.Show, which reads the run's XP before this battle as its base.
        // The score the end-of-match panel is tallying becomes XP here, so a milestone the win reaches is
        // already reached below. The Draft scene's track then has nothing left to fill in.
        GauntletRun.CollectScore();
        _restoring = GauntletRun.RestoreResultsPending ? GauntletRun.Results : null;
        if (_restoring != null ? !HasRestorableRows(_restoring) : !HasRewards) return false;
        GauntletRun.XpShown = GauntletRun.Xp;

        _leaving = false;
        _pickBlocked = false;
        _handedOver = false;

        UnsubscribeXp();
        _companion = companion;
        _xpSource = xpSource != null && xpSource.IsShowing ? xpSource : null;
        _shownXp = _xpSource != null ? _xpSource.ShownXp : GauntletRun.Xp;
        _nextRowRevealTime = Time.time + delay + scaleInDuration;

        if (titleText != null) titleText.text = titleLabel;

        gameObject.SetActive(true);
        BuildRows();
        RefreshRows();

        if (_xpSource != null) _xpSource.XpShown += OnXpShown;

        // A pick the player walked out of reopens where it was, with the same options, once the panel is up.
        if (_restoring != null) DOVirtual.DelayedCall(delay + scaleInDuration, ResumeOpenPick, false).SetTarget(this);

        // No scale-in of its own: the panel lives under the end-of-match panel, which PopupManager
        // already scales in, and tweening here too would compound the two curves.
        transform.DOKill();
        transform.localScale = Vector3.one;
        return true;
    }

    private void OnXpShown(int xp)
    {
        if (xp <= _shownXp) return;
        _shownXp = xp;
        ListReachedMilestones();
        RefreshRows();
    }

    private void UnsubscribeXp()
    {
        if (_xpSource != null) _xpSource.XpShown -= OnXpShown;
        _xpSource = null;
    }

    // ---------------------------------------------------------------- Rows

    private void BuildRows()
    {
        ClearRows();

        if (rowsContainer == null || rowTemplate == null)
        {
            Debug.LogWarning("BattleRewardsPanel: not wired up (rows container / row template missing)");
            return;
        }

        rowTemplate.gameObject.SetActive(false);

        // Restored, the card row is listed if the win owed cards at all -- taken or not -- and reads as
        // what it offered then, not what is still owed.
        bool cardRow = _restoring != null ? _restoring.battleCardsAtWin > 0 : GauntletRun.PendingBattleReward;
        if (cardRow)
        {
            int picks = _restoring != null ? _restoring.battleCardsAtWin : GauntletRun.BattleCardsOwed;
            var cards = GauntletRun.Catalog.BattleCards;

            var row = SpawnRow(cards.icon != null ? cards.icon : cardIcon,
                Or(GauntletRun.Catalog.BattleCardsOffered(picks), string.Format(cardRowLabel, picks)),
                cards.icon != null ? cards.iconTint : (Color?)null);
            row.button.onClick.AddListener(() => OnCardRowClicked(row));
            _rows.Add(row);
            RevealNextRow(row);

            if (_restoring != null && !GauntletRun.PendingBattleReward && !GauntletRun.PickingBattleCards)
                MarkTaken(row, TakenLabel(-1, Or(GauntletRun.Catalog.BattleCardsAcquired(picks),
                    string.Format(cardRowTakenLabel, picks))), record: false);
        }

        ListReachedMilestones();
    }

    /// <summary>A restored panel lists anything at all: the win's cards, or a milestone this win reached.</summary>
    private static bool HasRestorableRows(ResultsSave results)
    {
        if (!results.won || GauntletRun.Outcome != GauntletRun.RunOutcome.InProgress) return false;
        if (results.battleCardsAtWin > 0) return true;

        foreach (var milestone in GauntletRun.Milestones)
            if (ReachedByThisWin(milestone, results)) return true;
        return false;
    }

    /// <summary>A milestone the restored battle's XP reached, claimed since or not.</summary>
    private static bool ReachedByThisWin(GauntletRun.MilestoneState milestone, ResultsSave results) =>
        milestone.Reward != MilestoneReward.None && milestone.Xp > results.baseXp && milestone.Reached;

    /// <summary>The label a row was saved with once taken, else <paramref name="fallback"/>.</summary>
    private string TakenLabel(int milestone, string fallback)
    {
        var saved = _restoring != null ? _restoring.takenRows.Find(r => r.milestone == milestone) : null;
        return saved != null && !string.IsNullOrEmpty(saved.label) ? saved.label : fallback;
    }

    /// <summary>The claimed milestone whose pick is still owed: its row is not taken yet, and its pick reopens.</summary>
    private static bool PickStillOwed(int milestone) =>
        GauntletRun.ActiveMilestone == milestone && (GauntletRun.PendingPicks > 0 || GauntletRun.PendingPerkPick);

    /// <summary>
    /// Restored with a pick under way -- the player left with the card or perk options on screen: put the
    /// same options back up for the row they belong to. On the review tabs they go back on the pick tab
    /// without bringing it up: the panel still opens on Result, and the pick's row leads to it, as it
    /// does to any pick left open.
    /// </summary>
    private void ResumeOpenPick()
    {
        if (_restoring == null || _leaving || _pickingPerk || _pickingRow != null) return;

        _pickInBackground = true;
        try
        {
            ReopenPick();
        }
        finally
        {
            _pickInBackground = false;
        }
    }

    private void ReopenPick()
    {
        if (GauntletRun.PendingPerkPick)
        {
            var perkRow = _rows.Find(r => r.milestone == GauntletRun.ActiveMilestone);
            if (perkRow != null) ShowPerkOptions(perkRow);
            return;
        }

        if (GauntletRun.PendingPicks <= 0) return;

        var row = GauntletRun.PickingBattleCards
            ? _rows.Find(r => r.milestone < 0)
            : _rows.Find(r => r.milestone == GauntletRun.ActiveMilestone);
        if (row == null) return;

        _pickingRow = row;
        _pickingTakenLabel = GauntletRun.PickingBattleCards
            ? CardsTakenLabel()
            : GauntletRun.PickingDuplicate
                ? null
                : string.Format(milestoneTakenLabel, GauntletRun.PickingUpgraded ? UpgradedCardsTakenLabel() : CardsTakenLabel());
        OpenNextCardPick();
    }

    /// <summary>
    /// Adds a row for every claimable milestone the track has filled up to and that is not listed yet. The
    /// bar only climbs, so rows arrive in milestone order.
    /// </summary>
    private void ListReachedMilestones()
    {
        if (rowsContainer == null || rowTemplate == null) return;

        var milestones = GauntletRun.Milestones;
        for (int i = 0; i < milestones.Count; i++)
        {
            var milestone = milestones[i];
            if (milestone.Xp > _shownXp || IsListed(i)) continue;

            // Restored, a milestone this win reached is listed even when claimed since: it comes back taken.
            bool taken = false;
            if (!IsOwed(milestone))
            {
                if (_restoring == null || !ReachedByThisWin(milestone, _restoring)) continue;
                taken = !PickStillOwed(i);
            }

            string name = GauntletRun.MilestoneRewardName(milestone.Reward);

            // The reward's own icon when the catalog gives it one, the shared milestone icon otherwise.
            var catalog = GauntletRun.Catalog;
            var icon = catalog.Icon(milestone.Reward);
            var tint = catalog.IconTint(milestone.Reward, milestoneIconTint);

            var row = SpawnRow(icon != null ? icon : milestoneIcon,
                string.Format(milestoneRowLabel, milestone.Xp, name), tint);
            row.milestone = i;
            int index = i;
            row.button.onClick.AddListener(() => OnMilestoneRowClicked(row, index));
            _rows.Add(row);
            RevealNextRow(row);

            if (taken) MarkTaken(row, TakenLabel(i, string.Format(milestoneTakenLabel, name)), record: false);
        }
    }

    private static bool IsOwed(GauntletRun.MilestoneState milestone) =>
        milestone.Reached && !milestone.Claimed && milestone.Reward != MilestoneReward.None;

    private bool IsListed(int milestone) => _rows.Exists(r => r.milestone == milestone);

    /// <summary>Pops the row in at its turn: not before the panel has scaled in, one stagger after the last.</summary>
    private void RevealNextRow(Row row)
    {
        float at = Mathf.Max(Time.time, _nextRowRevealTime);
        _nextRowRevealTime = at + rowRevealStagger;
        RevealRow(row, at - Time.time);
    }

    /// <summary>Rows only invite a click while what they give can be taken right now.</summary>
    private void RefreshRows()
    {
        // A pick left open on its tab: the rest wait for it, and its own row is the way back to it.
        bool picking = _pickingRow != null || _pickingPerk;

        foreach (var row in _rows)
        {
            if (row.taken) continue;
            if (picking)
            {
                SetClickable(row, row == _pickingRow || row == _perkRow);
                continue;
            }

            SetClickable(row, row.milestone < 0
                ? GauntletRun.PendingBattleReward && GauntletRun.PendingPicks == 0
                  && !GauntletRun.PendingPerkPick
                : GauntletRun.CanClaim(row.milestone));
        }
        RefreshProceed();
    }

    /// <param name="wrap">Lets the label run onto a second line: a perk option carries its description.</param>
    private Row SpawnRow(Sprite icon, string label, Color? iconTint = null, bool wrap = false)
    {
        var button = Instantiate(rowTemplate, rowsContainer);
        button.gameObject.SetActive(true);
        button.onClick.RemoveAllListeners();

        var iconTransform = button.transform.Find("Icon");
        var highlight = button.transform.Find("Highlight");
        var row = new Row
        {
            button = button,
            label = button.GetComponentInChildren<TextMeshProUGUI>(true),
            icon = iconTransform != null ? iconTransform.GetComponent<Image>() : null,
            highlight = highlight != null ? highlight.gameObject : null,
        };

        if (row.label != null)
        {
            row.label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            row.label.text = label;
        }
        if (row.icon != null)
        {
            row.icon.sprite = icon;
            row.icon.color = iconTint ?? Color.white;
            row.icon.enabled = icon != null;
        }

        // However the template was left, the highlight starts out saying whether the row can be clicked.
        SetClickable(row, button.interactable);
        return row;
    }

    /// <summary>
    /// A row invites a click, or it does not: the highlight behind it is up exactly while the button is
    /// live, so a reward waiting to be taken is the only one that looks like it.
    /// </summary>
    private static void SetClickable(Row row, bool clickable)
    {
        if (row == null || row.button == null) return;

        row.button.interactable = clickable;
        if (row.highlight != null) row.highlight.SetActive(clickable);
    }

    private void RevealRow(Row row, float delay)
    {
        row.button.transform.localScale = Vector3.zero;
        row.button.transform.DOScale(1f, rowRevealDuration).SetDelay(delay);
    }

    /// <summary>The row keeps its place in the list, reading as what it gave, and stops taking clicks.</summary>
    /// <param name="record">Saves the row as taken with this label (off when restoring one already saved).</param>
    private void MarkTaken(Row row, string label, bool record = true)
    {
        if (row == null) return;

        row.taken = true;
        SetClickable(row, false);
        if (row.label != null && !string.IsNullOrEmpty(label)) row.label.text = label;
        if (record) GauntletRun.RecordTakenRow(row.milestone, row.label != null ? row.label.text : label);
    }

    private void ClearRows()
    {
        foreach (var row in _rows) DestroyRow(row);
        _rows.Clear();
    }

    private static void DestroyRow(Row row)
    {
        if (row == null || row.button == null) return;
        row.button.transform.DOKill();
        Destroy(row.button.gameObject);
    }

    // ---------------------------------------------------------------- Card picks

    private void OnCardRowClicked(Row row)
    {
        if (BackToOpenPick(row)) return;
        if (_leaving || !GauntletRun.BeginBattleCardPicks()) return;

        _pickingRow = row;
        _pickingTakenLabel = CardsTakenLabel();
        OpenNextCardPick();
    }

    /// <summary>
    /// Puts the next owed pick on screen, or brings the panel back once they are all taken. The panel is
    /// hidden while the options are up: the selection panel owns the screen (and the clicks) for as long
    /// as a choice is open.
    /// </summary>
    private void OpenNextCardPick()
    {
        if (GauntletRun.PendingPicks <= 0)
        {
            FinishCardPicks();
            return;
        }

        int pickNumber = GauntletRun.PicksThisStep - GauntletRun.PendingPicks + 1;
        string prompt = GauntletRun.PickingDuplicate
            ? duplicatePickPrompt
            : Or(GauntletRun.Catalog.BattleCardsPrompt(pickNumber, GauntletRun.PicksThisStep),
                string.Format(cardPickPrompt, pickNumber, GauntletRun.PicksThisStep));

        ShowPickScreen();
        if (cardPanel != null) CardSelectionPanel.Route(cardPanel);
        CardChoice.Instance.Begin(GauntletRun.CurrentCardOptions(), prompt, OnCardPicked, requirePlayerTurn: false);

        // Nothing to pick from (no options rolled, or the match is in a state that refuses a choice): the
        // picks stay owed and the Draft scene asks for them, so Proceed must not stay locked here.
        if (CardChoice.Instance.HasActiveRequest)
        {
            if (loadout != null)
            {
                loadout.Show(activate: !_pickInBackground || PickTabs == null);

                // Still handed over, so turning the card animation back on needs no rewiring here.
                var cardPanel = CardSelectionPanel.Resolve();
                if (cardPanel != null) cardPanel.SetDeckTarget(loadout.DeckAnchor);
            }
        }
        else
        {
            Debug.LogWarning("BattleRewardsPanel: no card choice could be opened; the picks are left to the Draft scene.");
            _pickBlocked = true;
            _pickingRow = null;
            _pickingTakenLabel = null;
            ReturnToPanel();
        }
    }

    private void OnCardPicked(CardSO card)
    {
        GauntletRun.AddDraftedCard(card);
        OpenNextCardPick();
    }

    private void FinishCardPicks()
    {
        if (_pickingRow != null)
        {
            // A Duplicate pick names the card it copied, only known once it is picked.
            MarkTaken(_pickingRow, _pickingTakenLabel ?? string.Format(milestoneTakenLabel, GauntletRun.LastRewardMessage));
            _pickingRow = null;
            _pickingTakenLabel = null;
        }
        ReturnToPanel();
    }

    /// <summary>Back from a pick: the panel comes straight up, already scaled in.</summary>
    private void ReturnToPanel()
    {
        CardSelectionPanel.Route(null);

        var tabs = PickTabs;
        if (tabs != null)
        {
            // The loadout is the pick tab's panel: it goes with the tab rather than vanishing. The
            // pick tab is a fadeSwitch tab, so this fades -- and the selection panel, still on because
            // its own close left it so, fades out with the rest of the tab before the park switches the
            // whole thing off.
            SetPanelsKeptOnClose(false);
            tabs.Select(GameOverTabs.TabKind.Result, animate: true);
        }
        else
        {
            if (loadout != null) loadout.Hide();
            SetCompanionVisible(true);
        }

        gameObject.SetActive(true);
        transform.DOKill();
        transform.localScale = Vector3.one;
        RefreshRows();
    }

    /// <summary>
    /// Puts a pick's screen up. With the review tabs, the pick tab -- the loadout and the selection
    /// panels -- fades in over Result, which fades out and goes on tallying off screen. A quick fade
    /// rather than the review's slide: the options have to be there at once. The tab bar stays: the player
    /// can leave the pick open for any other tab and come back to it through its row (the other rows
    /// wait for it). Without the tabs, this panel steps off and the end-of-match panel fades.
    /// </summary>
    private void ShowPickScreen()
    {
        var tabs = PickTabs;
        if (tabs != null)
        {
            // The selection panels leave with the tab's fade from here on, not by switching themselves
            // off the moment the choice resolves.
            SetPanelsKeptOnClose(true);

            if (!_pickInBackground) tabs.Select(pickTab, animate: true);
            RefreshRows();
            return;
        }

        gameObject.SetActive(false);
        SetCompanionVisible(false);
    }

    /// <summary>
    /// A row clicked while a pick is open, after the player left its tab: the row being picked brings the
    /// pick tab back, and any other does nothing until that pick is taken.
    /// </summary>
    private bool BackToOpenPick(Row row)
    {
        bool picking = _pickingRow != null || _pickingPerk;
        if (!picking) return false;

        var tabs = PickTabs;
        if (tabs != null && (row == _pickingRow || row == _perkRow)) tabs.Select(pickTab, animate: true);
        return true;
    }

    /// <summary>The end-of-match review tabs, when a run has them up; null for the plain panel.</summary>
    private static GameOverTabs PickTabs
    {
        get
        {
            if (!GauntletRun.IsActive) return null;
            var tabs = GameOverTabs.Resolve();
            return tabs != null && tabs.isActiveAndEnabled ? tabs : null;
        }
    }

    /// <summary>
    /// Hands the selection panels' going-away over to the pick tab's fade, or gives it back to them.
    /// A panel that switches its own object off on close snaps out a frame before that fade starts --
    /// it closes before the reward is marked taken and the tab is switched -- so only the loadout
    /// beside it would be seen to fade (<see cref="CardSelectionPanel.KeepOnClose"/>).
    /// </summary>
    private void SetPanelsKeptOnClose(bool kept)
    {
        if (cardPanel != null) cardPanel.KeepOnClose = kept;
        if (perkPanel != null) perkPanel.KeepOnClose = kept;
    }

    /// <summary>
    /// Hides the end-of-match panel with this one while a pick is up, by fading rather than disabling it so
    /// its score tally (and the track feeding milestone rows here) keeps running underneath.
    /// </summary>
    private void SetCompanionVisible(bool visible)
    {
        if (_companion == null) return;
        _companion.alpha = visible ? 1f : 0f;
        _companion.blocksRaycasts = visible;
    }

    /// <summary>What the won-battle card row reads once its picks are taken.</summary>
    private string CardsTakenLabel()
    {
        int picks = GauntletRun.PicksThisStep;
        return Or(GauntletRun.Catalog.BattleCardsAcquired(picks), string.Format(cardRowTakenLabel, picks));
    }

    /// <summary>What an Upgraded Cards milestone's row reads once its picks are taken.</summary>
    private string UpgradedCardsTakenLabel()
    {
        int picks = GauntletRun.PicksThisStep;
        return Or(GauntletRun.Catalog.Acquired(MilestoneReward.UpgradedCards, picks),
            string.Format(picks == 1 ? upgradedCardTakenLabel : upgradedCardsTakenLabel, picks));
    }

    /// <summary>The catalog's wording when it has any, this panel's own inspector wording otherwise.</summary>
    private static string Or(string catalogLine, string fallback) =>
        !string.IsNullOrEmpty(catalogLine) ? catalogLine : fallback;

    // ---------------------------------------------------------------- Milestones

    private void OnMilestoneRowClicked(Row row, int index)
    {
        if (BackToOpenPick(row)) return;
        if (_leaving || !GauntletRun.CanClaim(index)) return;

        var reward = GauntletRun.Milestones[index].Reward;
        GauntletRun.ClaimMilestone(index);
        // The milestone is claimed the moment it is clicked, picks owed or not, so its node can mark now.
        if (_xpSource != null) _xpSource.RefreshMilestoneStates();

        // Upgraded Cards owes picks, New Perk owes a perk; the rest have already applied themselves.
        if (GauntletRun.PendingPicks > 0)
        {
            _pickingRow = row;
            _pickingTakenLabel = GauntletRun.PickingDuplicate
                ? null
                : string.Format(milestoneTakenLabel,
                    GauntletRun.PickingUpgraded ? UpgradedCardsTakenLabel() : CardsTakenLabel());
            OpenNextCardPick();
            return;
        }

        if (GauntletRun.PendingPerkPick)
        {
            ShowPerkOptions(row);
            return;
        }

        string did = GauntletRun.LastRewardMessage;
        if (string.IsNullOrEmpty(did)) did = GauntletRun.MilestoneRewardName(reward);
        MarkTaken(row, string.Format(milestoneTakenLabel, did));
        RefreshRows();
    }

    /// <summary>
    /// Opens the perk milestone's options the way a card row opens its pick: this panel steps off the
    /// screen, the end-of-match panel beside it fades, and the options come up in the perk panel with the
    /// run's loadout next to them.
    /// </summary>
    private void ShowPerkOptions(Row row)
    {
        _perkRow = row;
        _pickingPerk = true;

        if (perkPanel == null) perkPanel = PerkSelectionPanel.Resolve();

        ShowPickScreen();

        bool opened = perkPanel != null
            && perkPanel.Open(GauntletRun.CurrentPerkOptions, perkPickPrompt, TakePerk);

        // No panel, or nothing to pick from: the perk stays owed and the Draft scene asks for it, so
        // Proceed must not stay locked here -- the same hand-over an unopenable card pick gets.
        if (opened)
        {
            if (loadout != null)
            {
                loadout.Show(activate: !_pickInBackground || PickTabs == null);

                // Shown first, so the passive row is filled in and its first free slot can be found:
                // the locked slot that unlocks to reveal the perk once it is taken.
                perkPanel.SetUnlockTarget(loadout.PassiveSlot);
            }
            return;
        }

        Debug.LogWarning("BattleRewardsPanel: no perk choice could be opened; the pick is left to the Draft scene.");
        _pickingPerk = false;
        _perkRow = null;
        _pickBlocked = true;
        ReturnToPanel();
    }

    /// <summary>Back from the perk panel, which closes the way a finished card pick does.</summary>
    private void TakePerk(HeroPassiveSO perk)
    {
        if (_leaving || perk == null) return;

        GauntletRun.AddPerk(perk);
        if (perkPanel != null) perkPanel.Close();
        _pickingPerk = false;

        MarkTaken(_perkRow, Or(GauntletRun.Catalog.Acquired(MilestoneReward.NewPerk, 0, perk.passiveName),
            string.Format(perkTakenLabel, perk.passiveName)));
        _perkRow = null;
        ReturnToPanel();
    }


    // ---------------------------------------------------------------- Leaving

    private void RefreshProceed()
    {
        if (proceedButton == null) return;

        bool can = CanProceed;
        proceedButton.interactable = can;

        if (_proceedGroup == null)
        {
            _proceedGroup = proceedButton.GetComponent<CanvasGroup>();
            if (_proceedGroup == null) _proceedGroup = proceedButton.gameObject.AddComponent<CanvasGroup>();
        }
        _proceedGroup.alpha = can ? 1f : lockedProceedAlpha;
    }

    /// <summary>
    /// Rewards are not skippable: every row the panel lists has to be taken before the run moves on. Read
    /// off the rows rather than the run, so what unlocks Proceed is exactly what the player can see -- and
    /// stays locked while the track is still filling toward a milestone that has no row yet.
    /// </summary>
    private bool CanProceed
    {
        get
        {
            if (_leaving || _pickingPerk) return false;

            var milestones = GauntletRun.Milestones;
            for (int i = 0; i < milestones.Count; i++)
                if (IsOwed(milestones[i]) && !IsListed(i)) return false;

            if (_pickBlocked) return true;

            foreach (var row in _rows)
                if (!row.taken) return false;
            return true;
        }
    }

    /// <summary>
    /// True while nothing the battle paid out is still owed: what unlocks Proceed, and with it anything
    /// else that leaves for the next battle. It stays true once Proceed has handed over, so the way on is
    /// not locked behind the very click that brought it up.
    /// </summary>
    public bool Settled => _handedOver || CanProceed;

    /// <summary>
    /// True while a reward pick's options are the screen the player is on: a card or a perk pick is open
    /// and the tab it sits on is the one up. A pick left open while another tab is read does not count,
    /// nor does one reopened from a save into the background.
    ///
    /// What the end-of-match panel's Collect Rewards button stays out of the way of
    /// (<see cref="GameOverVersus"/>): it sits in the same bottom-right corner the selection panel
    /// reaches into, and it is a way back to rewards the player is already in the middle of taking.
    /// </summary>
    public bool PickShowing
    {
        get
        {
            if (_pickingRow == null && !_pickingPerk) return false;

            // Without the tabs the pick owns the whole screen -- this panel steps off and the
            // end-of-match panel fades (ShowPickScreen) -- so it is up either way.
            var tabs = PickTabs;
            return tabs == null || tabs.Current == pickTab;
        }
    }

    private void OnProceed()
    {
        if (!CanProceed) return;

        UnsubscribeXp();
        if (proceedButton != null) proceedButton.interactable = false;

        // In a run with a battle still to fight, Proceed hands over to the end-of-match panel's Battles
        // tab, its briefing; the Draft scene is only loaded when there is nothing left to fight,
        // or when this panel is the whole end-of-match screen (no tabs authored).
        var tabs = GameOverTabs.Resolve();
        if (tabs != null && GauntletRun.IsActive && GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress)
        {
            _handedOver = true;
            tabs.Select(GameOverTabs.TabKind.Battles, animate: true);
            return;
        }

        _leaving = true;
        GauntletRun.LeaveBattle();
    }

    void OnDestroy()
    {
        UnsubscribeXp();
        DOTween.Kill(this);
        transform.DOKill();
        foreach (var row in _rows)
            if (row.button != null) row.button.transform.DOKill();
    }
}
