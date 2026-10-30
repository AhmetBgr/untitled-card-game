using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// State of the Forged in Battle run in progress: the hero and deck the player drafted, which battle is
/// next, and the hero's statline carried out of the last won battle. Static so it crosses the scene
/// loads between the Draft and Game scenes, like <see cref="MatchModifiers"/>.
///
/// The run is saved (<see cref="GauntletSaveStore"/>) after every change to it, so quitting at any point
/// -- the draft, mid-battle, the end-of-match rewards -- resumes there (<see cref="TryResume"/>). Every
/// roll comes from the run's own seeded stream (<see cref="RunRandom"/>), every enemy is rolled up front
/// when the hero is picked, and every rolled option is saved as rolled, so a resume never rerolls
/// anything. A battle walked out of restarts against the same enemy, with the same opening shuffle.
///
/// Gameplay asks it three things, all no-ops while no run is active: which hero and deck each side
/// plays with (<see cref="HeroFor"/>, <see cref="DeckFor"/>, read in Agent.ApplySavedSelection), how to
/// adjust the heroes at match setup (<see cref="ApplyEnemySetup"/>, <see cref="RestorePlayerHero"/>),
/// and what to do with the result (<see cref="OnBattleWon"/>, <see cref="OnBattleLost"/>).
///
/// Later enemies are rolled with blessings (<see cref="GauntletBlessing"/>), which GameManager reads
/// through the same kind of no-op-outside-a-run hooks: <see cref="OwnPassivesFor"/>,
/// <see cref="ExtraPassivesFor"/>, <see cref="StartingManaFor"/>, <see cref="ExtraStartingCardsFor"/>
/// and <see cref="SummonStartingMinion"/>.
///
/// Each won battle's end-of-match score is added to the run's <see cref="Xp"/>, which reaches the
/// milestones of the progression track (<see cref="Milestones"/>): one every
/// <see cref="GauntletConfigSO.milestoneXpStep"/> XP, then a final one at
/// <see cref="GauntletConfigSO.finalMilestoneXp"/> rolled from its own pool. The track unlocks after the first win; reached
/// milestones are claimed with <see cref="ClaimMilestone"/>, and must all be claimed before the next
/// battle.
///
/// Winning a battle is itself worth new cards (<see cref="PendingBattleReward"/>), picked on the rewards
/// panel over the end-of-match screen rather than on the track.
/// </summary>
public static class GauntletRun
{
    public enum RunOutcome { InProgress, Won, Lost }

    /// <summary>The screen the run is on, which is where a resume takes the player back to.</summary>
    public enum RunLocation { Draft, Battle, Results }

    /// <summary>One encounter's enemy, rolled when the hero is picked.</summary>
    private class EnemyRoll
    {
        public HeroSO hero;
        public readonly List<CardSO> deck = new List<CardSO>();
        public readonly List<GauntletBlessing> blessings = new List<GauntletBlessing>();
        public readonly List<HeroPassiveSO> ownPassives = new List<HeroPassiveSO>();
        public readonly List<HeroPassiveSO> extraPassives = new List<HeroPassiveSO>();
        public CardSO startingMinion;
    }

    /// <summary>One milestone of this run's track, with its Random reward already rolled.</summary>
    public class MilestoneState
    {
        public int Xp { get; }
        public MilestoneReward Reward { get; }
        public bool Claimed { get; internal set; }

        /// <summary>The first pick of a card reward, rolled once reached so a preview and the claim agree.</summary>
        internal List<CardSO> cardOptions;
        internal List<HeroPassiveSO> perkOptions;

        public MilestoneState(int xp, MilestoneReward reward)
        {
            Xp = xp;
            Reward = reward;
        }

        public bool Reached => GauntletRun.Xp >= Xp;
    }

    /// <summary>The player hero's statline and passive state at the end of the last won battle.</summary>
    private class HeroSnapshot
    {
        public int health;
        public int maxHealth;
        public int attack;
        public int appliedAttackBonus;
        public readonly Dictionary<string, int> counters = new Dictionary<string, int>();
    }

    public static bool IsActive { get; private set; }
    public static GauntletConfigSO Config { get; private set; }

    /// <summary>
    /// The tuning to read numbers off. <see cref="Config"/> outside a run is null -- the track and its
    /// reward previews are read before (and after) one -- so the asset stands in for it there.
    /// </summary>
    private static GauntletConfigSO Tuning => Config != null ? Config : GauntletConfigSO.Load();

    /// <summary>How the rewards read and look; every player-facing reward line comes from here.</summary>
    public static MilestoneRewardCatalogSO Catalog => MilestoneRewardCatalogSO.Load();

    public static HeroSO Hero { get; private set; }
    private static readonly List<CardSO> _deck = new List<CardSO>();
    public static IReadOnlyList<CardSO> Deck => _deck;

    /// <summary>Index into Config.encounters of the battle to be fought next.</summary>
    public static int EncounterIndex { get; private set; }

    /// <summary>Card picks still owed before the next battle can start.</summary>
    public static int PendingPicks { get; private set; }

    /// <summary>Picks owed in the current draft step (5 at the start, 2 for a win or a card milestone), for "pick 2 of 5".</summary>
    public static int PicksThisStep { get; private set; }

    /// <summary>The card picks owed come from an Upgraded Cards milestone.</summary>
    public static bool PickingUpgraded { get; private set; }

    /// <summary>The card pick owed comes from a Duplicate milestone: its options are cards of the deck.</summary>
    public static bool PickingDuplicate { get; private set; }

    /// <summary>A New Perk milestone's pick is owed.</summary>
    public static bool PendingPerkPick { get; private set; }

    /// <summary>
    /// Cards the battle just won still owes. They are NOT a live pick session: the reward waits its turn
    /// beside whatever milestones the win reached, and only becomes one once the player asks for it
    /// (<see cref="BeginBattleCardPicks"/>). That is what lets the rewards panel offer every reward at
    /// once and let the player take them in any order -- were the win to seize
    /// <see cref="PendingPicks"/> the moment it was won, no milestone that owes a pick of its own could
    /// be claimed until the cards had been drafted.
    /// </summary>
    public static int BattleCardsOwed { get; private set; }

    /// <summary>The battle just won still owes its cards; see <see cref="BattleCardsOwed"/>.</summary>
    public static bool PendingBattleReward => BattleCardsOwed > 0;

    /// <summary>The pick session running right now is the battle reward's, rather than a milestone's.</summary>
    private static bool _pickingBattleCards;
    public static bool PickingBattleCards => _pickingBattleCards;

    /// <summary>Index of the milestone whose picks are owed, or -1.</summary>
    public static int ActiveMilestone { get; private set; } = -1;

    /// <summary>The options of the pick owed, rolled once so leaving and re-entering the scene can't reroll them.</summary>
    private static List<CardSO> _cardOptions;
    private static List<HeroPassiveSO> _perkOptions;

    public static RunOutcome Outcome { get; private set; }

    /// <summary>The summed end-of-match scores of the battles won so far.</summary>
    public static int Xp { get; private set; }

    /// <summary>The XP the Draft scene's track last showed, so it can animate what was gained since.</summary>
    public static int XpShown
    {
        get => _xpShown;
        set
        {
            if (_xpShown == value) return;
            _xpShown = value;
            Persist();
        }
    }
    private static int _xpShown;

    /// <summary>The track's lock has slid away once already: later visits show it unlocked straight away.</summary>
    public static bool MilestonesRevealed
    {
        get => _milestonesRevealed;
        set
        {
            if (_milestonesRevealed == value) return;
            _milestonesRevealed = value;
            Persist();
        }
    }
    private static bool _milestonesRevealed;

    /// <summary>What the last instantly applied milestone did ("+5 Max Health"), or null.</summary>
    public static string LastRewardMessage { get; private set; }

    private static readonly List<MilestoneState> _milestones = new List<MilestoneState>();
    public static IReadOnlyList<MilestoneState> Milestones => _milestones;

    /// <summary>The player hero's own passives, one maybe forged by a milestone, and the perks it drafted.</summary>
    private static readonly List<HeroPassiveSO> _playerOwnPassives = new List<HeroPassiveSO>();
    private static readonly List<HeroPassiveSO> _playerExtraPassives = new List<HeroPassiveSO>();

    /// <summary>Attack from Bonus Attack milestones, re-added when the hero's attack isn't carried.</summary>
    private static int _rewardAttack;

    /// <summary>Cards Bigger Hand milestones add to the player's starting hand.</summary>
    private static int _playerExtraStartingCards;

    /// <summary>A battle was won and its score hasn't been added to the XP yet.</summary>
    private static bool _scorePending;

    private static HeroSnapshot _snapshot;

    private static HeroSO _enemyHero;
    private static readonly List<CardSO> _enemyDeck = new List<CardSO>();
    private static readonly List<GauntletBlessing> _enemyBlessings = new List<GauntletBlessing>();
    private static readonly List<HeroPassiveSO> _enemyOwnPassives = new List<HeroPassiveSO>();
    private static readonly List<HeroPassiveSO> _enemyExtraPassives = new List<HeroPassiveSO>();
    private static CardSO _enemyStartingMinion;

    /// <summary>Stronger Minions: enemy summons still owed the buff this battle.</summary>
    private static int _strongerMinionsLeft;

    // ---------------------------------------------------------------- Saved run state

    /// <summary>Every encounter's enemy, rolled when the hero is picked; the fight loads its own from here.</summary>
    private static readonly List<EnemyRoll> _enemies = new List<EnemyRoll>();

    /// <summary>The opening hero pick, rolled once so a resume offers the same heroes.</summary>
    private static List<HeroSO> _heroOptions;

    private static RunRandom _rng;
    private static uint _seed;
    private static string _runId;
    private static long _startedUtcTicks;

    /// <summary>The run's random stream. Only ever null before a run starts (a stray roll still works).</summary>
    private static RunRandom Rng => _rng ?? (_rng = new RunRandom(RunRandom.NewSeed()));

    public static RunLocation Location { get; private set; }

    /// <summary>The end-of-match screen of the battle just decided, while <see cref="Location"/> is Results.</summary>
    private static ResultsSave _results;

    /// <summary>Every battle fought this run, with its stats.</summary>
    private static readonly List<BattleRecord> _battles = new List<BattleRecord>();
    public static IReadOnlyList<BattleRecord> Battles => _battles;

    /// <summary>Times the battle being fought now was walked out of and restarted.</summary>
    private static int _currentBattleRestarts;

    /// <summary>
    /// The Game scene is entered to put a saved end-of-match screen back up rather than to play: no match
    /// is set up (GameManager.Start), and the panel is rebuilt from <see cref="Results"/>.
    /// </summary>
    public static bool RestoreResultsPending { get; private set; }

    /// <summary>The saved end-of-match screen, or null while the run is not on one.</summary>
    public static ResultsSave Results => Location == RunLocation.Results ? _results : null;

    /// <summary>Set while <see cref="FromSave"/> runs, so rebuilding the run doesn't write it back half-built.</summary>
    private static bool _loading;

    /// <summary>
    /// A mana curve for a 15-card upgraded deck. Upgraded cards cost 2 to 7, so
    /// <see cref="DeckSO.ManaCurve"/> (0 to 5) doesn't fit them.
    /// </summary>
    private static readonly (int[] costs, int count)[] UpgradedManaCurve =
    {
        (new[] { 2, 3 }, 4),
        (new[] { 4 }, 4),
        (new[] { 5 }, 4),
        (new[] { 6, 7 }, 3),
    };
    private const int UpgradedManaCurveSize = 15;

    /// <summary>The enemy rolled for the next battle, or null until <see cref="PrepareNextBattle"/> runs.</summary>
    public static HeroSO EnemyHero => _enemyHero;

    /// <summary>The blessings rolled for the next battle's enemy, in roll order. Empty until it is rolled.</summary>
    public static IReadOnlyList<GauntletBlessing> EnemyBlessings => _enemyBlessings;

    /// <summary>Every passive the next enemy plays with: its own (one maybe forged), then the extras.</summary>
    public static List<HeroPassiveSO> EnemyPassives
    {
        get
        {
            var all = new List<HeroPassiveSO>(_enemyOwnPassives);
            all.AddRange(_enemyExtraPassives);
            return all;
        }
    }

    /// <summary>The minion the next enemy starts with on the board (Starting Minion), or null.</summary>
    public static CardSO EnemyStartingMinion => _enemyStartingMinion;

    /// <summary>Enemy hero health after the encounter's multiplier and Bonus Health.</summary>
    public static int EnemyStartingHealth(int printedHealth)
    {
        var encounter = CurrentEncounter;
        int hp = encounter != null
            ? Mathf.Max(1, Mathf.RoundToInt(printedHealth * encounter.enemyHealthMultiplier))
            : printedHealth;
        if (HasEnemyBlessing(GauntletBlessing.BonusHealth)) hp += Config.bonusHealth;
        return hp;
    }

    public static bool HasEnemyBlessing(GauntletBlessing blessing) => _enemyBlessings.Contains(blessing);

    /// <summary>One encounter's enemy as rolled at the hero pick, for showing any fight of the run.</summary>
    public class EnemyPreview
    {
        public HeroSO Hero { get; internal set; }
        public IReadOnlyList<GauntletBlessing> Blessings { get; internal set; }
        public List<HeroPassiveSO> Passives { get; internal set; }

        /// <summary>Enemy hero health after this encounter's multiplier and Bonus Health.</summary>
        public int StartingHealth { get; internal set; }
    }

    /// <summary>The enemy of encounter <paramref name="encounterIndex"/>, or null before the enemies are rolled.</summary>
    public static EnemyPreview PreviewEnemy(int encounterIndex)
    {
        if (Config == null || encounterIndex < 0 || encounterIndex >= Config.encounters.Count) return null;
        if (encounterIndex >= _enemies.Count || _enemies[encounterIndex]?.hero == null) return null;

        var roll = _enemies[encounterIndex];
        var passives = new List<HeroPassiveSO>(roll.ownPassives);
        passives.AddRange(roll.extraPassives);

        int printed = roll.hero.defHealth > 0 ? roll.hero.defHealth : roll.hero.health;
        int hp = Mathf.Max(1, Mathf.RoundToInt(printed * Config.encounters[encounterIndex].enemyHealthMultiplier));
        if (roll.blessings.Contains(GauntletBlessing.BonusHealth)) hp += Config.bonusHealth;

        return new EnemyPreview
        {
            Hero = roll.hero,
            Blessings = roll.blessings,
            Passives = passives,
            StartingHealth = hp,
        };
    }

    public static int EncounterCount => Config != null ? Config.encounters.Count : 0;

    public static GauntletConfigSO.Encounter CurrentEncounter =>
        Config != null && EncounterIndex >= 0 && EncounterIndex < Config.encounters.Count
            ? Config.encounters[EncounterIndex]
            : null;

    // Statics survive between play sessions when domain reload is off; start each session clean.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForNewSession() => Reset();

    private static void Reset()
    {
        IsActive = false;
        Config = null;
        Hero = null;
        _deck.Clear();
        EncounterIndex = 0;
        PendingPicks = 0;
        PicksThisStep = 0;
        PickingUpgraded = false;
        PickingDuplicate = false;
        PendingPerkPick = false;
        BattleCardsOwed = 0;
        _pickingBattleCards = false;
        ActiveMilestone = -1;
        _cardOptions = null;
        _perkOptions = null;
        Outcome = RunOutcome.InProgress;
        Xp = 0;
        _xpShown = 0;
        _milestonesRevealed = false;
        LastRewardMessage = null;
        _milestones.Clear();
        _playerOwnPassives.Clear();
        _playerExtraPassives.Clear();
        _rewardAttack = 0;
        _playerExtraStartingCards = 0;
        _scorePending = false;
        _snapshot = null;
        ClearEnemy();

        _enemies.Clear();
        _heroOptions = null;
        _rng = null;
        _seed = 0;
        _runId = null;
        _startedUtcTicks = 0;
        Location = RunLocation.Draft;
        _results = null;
        _battles.Clear();
        _currentBattleRestarts = 0;
        RestoreResultsPending = false;
        _loading = false;
    }

    private static void ClearEnemy()
    {
        _enemyHero = null;
        _enemyDeck.Clear();
        _enemyBlessings.Clear();
        _enemyOwnPassives.Clear();
        _enemyExtraPassives.Clear();
        _enemyStartingMinion = null;
        StopStrongerMinions();
    }

    /// <summary>Starts a fresh run: no hero yet, and the opening draft owed. Replaces any saved run.</summary>
    public static void StartNew()
    {
        Reset();
        IsActive = true;
        Config = GauntletConfigSO.Load();
        PendingPicks = PicksThisStep = Config.initialDeckSize;

        _seed = RunRandom.NewSeed();
        _rng = new RunRandom(_seed);
        _runId = System.Guid.NewGuid().ToString("N");
        _startedUtcTicks = System.DateTime.UtcNow.Ticks;
        Persist();
    }

    /// <summary>
    /// Finishes with the run for good: what leaving a decided run's result screen for the menu does. The
    /// save goes with it.
    /// </summary>
    public static void End()
    {
        Reset();
        GauntletSaveStore.Delete();
    }

    /// <summary>Gives the run up mid-way (the pause menu's Abandon Run): it is gone, saved copy and all.</summary>
    public static void Abandon() => End();

    /// <summary>
    /// Lets go of the run in memory but keeps it saved, so the Forged in Battle button picks it up again.
    /// What the main menu does on arrival.
    /// </summary>
    public static void Unload() => Reset();

    /// <summary>A run is saved and can be resumed.</summary>
    public static bool HasSavedRun => GauntletSaveStore.Has();

    /// <summary>
    /// Loads the saved run and says which scene carries on from where it was left: the Draft scene, or the
    /// Game scene -- to fight again a battle that was walked out of, or to put the end-of-match screen
    /// back up. A save that can't be read back (a card or hero since renamed, a changed config) is
    /// discarded, and false returned so a fresh run starts instead.
    /// </summary>
    public static bool TryResume(out string sceneName)
    {
        sceneName = null;
        var data = GauntletSaveStore.Load();
        if (data == null) return false;

        Reset();
        if (!FromSave(data))
        {
            End();
            return false;
        }

        switch (Location)
        {
            case RunLocation.Battle:
                // Walked out of mid-fight: the battle starts over, same enemy, same opening shuffle.
                _currentBattleRestarts++;
                Persist();
                sceneName = Config.gameSceneName;
                break;

            case RunLocation.Results:
                RestoreResultsPending = true;
                sceneName = Config.gameSceneName;
                break;

            default:
                // Between battles: the Game scene opens on the run's draft.
                sceneName = Config.gameSceneName;
                break;
        }
        return true;
    }

    /// <summary>The fight is starting: a quit from here on restarts it. Called as the Game scene is loaded.</summary>
    public static void BeginBattle()
    {
        if (!IsActive) return;
        if (EnemyHero == null) PrepareNextBattle();
        Location = RunLocation.Battle;
        Persist();
    }

    /// <summary>
    /// The seed the battle being fought shuffles a side's deck with, or null outside a run. Fixed per
    /// battle, so a battle restarted after a quit deals the same opening hands rather than a reroll.
    /// </summary>
    public static int? BattleShuffleSeed(SelectionSide side)
    {
        if (!IsActive || RestoreResultsPending) return null;
        return RunRandom.Derive(_seed, EncounterIndex, (int)side);
    }

    /// <summary>
    /// Remembers that a row of the end-of-match rewards was taken, and what it then read, so a resume
    /// rebuilds the panel as it was left.
    /// </summary>
    public static void RecordTakenRow(int milestone, string label)
    {
        if (!IsActive || _results == null) return;

        var row = _results.takenRows.Find(r => r.milestone == milestone);
        if (row == null) _results.takenRows.Add(row = new TakenRowSave { milestone = milestone });
        row.label = label;
        Persist();
    }

    // ---------------------------------------------------------------- Draft

    public static void SetHero(HeroSO hero)
    {
        if (!IsActive || hero == null) return;
        Hero = hero;

        _playerOwnPassives.Clear();
        _playerExtraPassives.Clear();
        foreach (var passive in hero.passives)
            if (passive != null && !_playerOwnPassives.Contains(passive)) _playerOwnPassives.Add(passive);

        // Rolled now rather than at the start of the run: whether Forge Perk can apply depends on the hero.
        RollMilestones();

        // Every enemy of the run is decided here, up front -- not at the start of the run, because
        // avoiding a mirror match needs the player's hero.
        RollAllEnemies();
        Persist();
    }

    public static void AddDraftedCard(CardSO card)
    {
        if (!IsActive || card == null || PendingPicks <= 0) return;
        _deck.Add(card);
        _cardOptions = null;
        if (PickingDuplicate)
            LastRewardMessage = Catalog.Acquired(MilestoneReward.DuplicateCard, 1, card.cardName);
        // Drained as they are taken, so walking away part-way through leaves the rest owed.
        if (_pickingBattleCards) BattleCardsOwed = PendingPicks - 1;
        if (--PendingPicks > 0)
        {
            Persist();
            return;
        }

        PickingUpgraded = false;
        PickingDuplicate = false;
        _pickingBattleCards = false;
        ActiveMilestone = -1;
        Persist();
    }

    /// <summary>
    /// Turns the cards the won battle owes into the pick session, so they can be drafted now. Refuses
    /// while another pick is already under way -- only one choice can be on screen at a time -- which is
    /// the only thing that ever holds a reward back.
    /// </summary>
    /// <returns>True when the session is now the battle reward's and its options can be shown.</returns>
    public static bool BeginBattleCardPicks()
    {
        if (!IsActive || !PendingBattleReward || PendingPicks > 0 || PendingPerkPick) return false;

        PendingPicks = PicksThisStep = BattleCardsOwed;
        PickingUpgraded = false;
        PickingDuplicate = false;
        ActiveMilestone = -1;
        _cardOptions = null;
        _pickingBattleCards = true;
        Persist();
        return true;
    }

    /// <summary>
    /// Heroes offered for the opening pick: distinct, random, never tutorial heroes. Rolled once per run,
    /// so leaving and coming back offers the same heroes.
    /// </summary>
    public static List<HeroSO> RollHeroOptions()
    {
        if (_heroOptions != null) return _heroOptions;

        var pool = new List<HeroSO>(HeroDatabase.Instance.AllHeroes);
        pool.RemoveAll(h => h == null);
        _heroOptions = TakeRandom(pool, Config.heroChoiceCount, distinct: true);
        Persist();
        return _heroOptions;
    }

    /// <summary>The cards offered for the pick owed now. Rolled once per pick.</summary>
    public static List<CardSO> CurrentCardOptions()
    {
        if (_cardOptions == null)
        {
            _cardOptions = PickingDuplicate ? RollDuplicateOptions() : RollCardOptions(PickingUpgraded);
            Persist();
        }
        return _cardOptions;
    }

    /// <summary>
    /// Cards offered for one draft pick, from the base cards (or the upgraded ones). The opening draft is
    /// capped at <see cref="GauntletConfigSO.initialDraftMaxCost"/>; milestone picks are not.
    /// </summary>
    private static List<CardSO> RollCardOptions(bool upgraded)
    {
        var pool = upgraded ? UpgradedCardPool() : BaseCardPool();
        if (!Config.allowDuplicates) pool.RemoveAll(c => _deck.Contains(c));

        if (!upgraded && EncounterIndex == 0 && Config.initialDraftMaxCost > 0)
            pool.RemoveAll(c => c.cost > Config.initialDraftMaxCost);

        return TakeRandom(pool, Config.cardChoiceCount, Config.distinctOptionsPerPick);
    }

    /// <summary>Distinct cards of the deck offered for a Duplicate pick.</summary>
    private static List<CardSO> RollDuplicateOptions()
    {
        var pool = new List<CardSO>();
        foreach (var card in _deck)
            if (card != null && !pool.Contains(card)) pool.Add(card);
        return TakeRandom(pool, Config.duplicateChoiceCount, distinct: true);
    }

    // ---------------------------------------------------------------- Milestones

    /// <summary>The track opens once the first battle is won.</summary>
    public static bool MilestonesUnlocked => IsActive && (EncounterIndex > 0 || Outcome == RunOutcome.Won);

    /// <summary>A reached milestone is still waiting to be claimed; the next battle can't start until it is.</summary>
    public static bool HasUnclaimedMilestone => _milestones.Exists(IsClaimable);

    private static bool IsClaimable(MilestoneState m) => m.Reached && !m.Claimed && m.Reward != MilestoneReward.None;

    /// <summary>
    /// The reward hands the player a choice -- cards, or a perk -- instead of applying itself. Only one
    /// such pick can be under way at a time: they all run through the same <see cref="PendingPicks"/>
    /// session, and starting a second would throw away the options of the first.
    /// </summary>
    public static bool RewardOwesPick(MilestoneReward reward) =>
        reward == MilestoneReward.UpgradedCards
        || reward == MilestoneReward.DuplicateCard
        || reward == MilestoneReward.NewPerk;

    /// <summary>
    /// The milestone can be claimed right now: reached, unclaimed, and -- only if its reward owes a pick
    /// of its own -- with no other pick already under way. A reward that applies itself (health, attack,
    /// hand size, a forged perk) has nothing to collide with, so it can be taken the moment it is listed,
    /// including while the won battle's own card picks are still owed.
    /// </summary>
    public static bool CanClaim(int index)
    {
        if (!IsActive || Outcome != RunOutcome.InProgress || !MilestonesUnlocked) return false;
        if (index < 0 || index >= _milestones.Count) return false;

        var milestone = _milestones[index];
        if (!IsClaimable(milestone)) return false;

        return !RewardOwesPick(milestone.Reward) || (PendingPicks == 0 && !PendingPerkPick);
    }

    /// <summary>
    /// Claims a reached milestone. Card and perk rewards owe picks (<see cref="PendingPicks"/>,
    /// <see cref="PendingPerkPick"/>) that the Draft scene then shows; the rest apply on the spot and
    /// leave a <see cref="LastRewardMessage"/>.
    /// </summary>
    public static bool ClaimMilestone(int index)
    {
        if (!CanClaim(index)) return false;

        var milestone = _milestones[index];
        milestone.Claimed = true;
        LastRewardMessage = null;
        _pickingBattleCards = false;

        switch (milestone.Reward)
        {
            case MilestoneReward.UpgradedCards:
                PickingUpgraded = true;
                PendingPicks = PicksThisStep = Config.milestoneUpgradedPicks;
                _cardOptions = MilestoneCardOptions(index);
                ActiveMilestone = index;
                break;

            case MilestoneReward.DuplicateCard:
                var options = MilestoneCardOptions(index);
                if (options == null || options.Count == 0)
                {
                    LastRewardMessage = Catalog.Unavailable(MilestoneReward.DuplicateCard);
                    break;
                }
                PickingDuplicate = true;
                PendingPicks = PicksThisStep = 1;
                _cardOptions = options;
                ActiveMilestone = index;
                break;

            case MilestoneReward.NewPerk:
                var perks = MilestonePerkOptions(index);
                if (perks.Count == 0)
                {
                    LastRewardMessage = Catalog.Unavailable(MilestoneReward.NewPerk);
                    break;
                }
                _perkOptions = perks;
                PendingPerkPick = true;
                ActiveMilestone = index;
                break;

            case MilestoneReward.ForgePerk:
                var forged = ForgeRandomPlayerPassive();
                LastRewardMessage = forged != null
                    ? Catalog.Acquired(MilestoneReward.ForgePerk, 0, forged.passiveName)
                    : Catalog.Unavailable(MilestoneReward.ForgePerk);
                break;

            case MilestoneReward.BonusHealth:
                int health = Config.milestoneBonusHealth;
                if (_snapshot != null)
                {
                    _snapshot.maxHealth += health;
                    _snapshot.health += health;
                }
                LastRewardMessage = Catalog.Acquired(MilestoneReward.BonusHealth, health);
                break;

            case MilestoneReward.BonusAttack:
                int attack = Config.milestoneBonusAttack;
                if (_snapshot != null) _snapshot.attack += attack;
                _rewardAttack += attack;
                LastRewardMessage = Catalog.Acquired(MilestoneReward.BonusAttack, attack);
                break;

            case MilestoneReward.BiggerHand:
                int cards = Config.milestoneExtraStartingCards;
                _playerExtraStartingCards += cards;
                LastRewardMessage = Catalog.Acquired(MilestoneReward.BiggerHand, cards);
                break;
        }
        Persist();
        return true;
    }

    /// <summary>
    /// The first pick an Upgraded Cards milestone offers, or the deck cards a Duplicate milestone offers;
    /// null for any other milestone.
    /// </summary>
    public static List<CardSO> MilestoneCardOptions(int index)
    {
        if (index < 0 || index >= _milestones.Count) return null;

        var milestone = _milestones[index];
        if (milestone.cardOptions != null) return milestone.cardOptions;

        switch (milestone.Reward)
        {
            case MilestoneReward.UpgradedCards: milestone.cardOptions = RollCardOptions(upgraded: true); break;
            case MilestoneReward.DuplicateCard: milestone.cardOptions = RollDuplicateOptions(); break;
            default: return null;
        }
        Persist();
        return milestone.cardOptions;
    }

    /// <summary>The passives a New Perk milestone offers, or null for any other milestone.</summary>
    public static List<HeroPassiveSO> MilestonePerkOptions(int index)
    {
        if (index < 0 || index >= _milestones.Count) return null;

        var milestone = _milestones[index];
        if (milestone.Reward != MilestoneReward.NewPerk) return null;

        if (milestone.perkOptions == null)
        {
            milestone.perkOptions = TakeRandom(PlayerPerkPool(), Config.perkChoiceCount, distinct: true);
            Persist();
        }
        return milestone.perkOptions;
    }

    /// <summary>The passives offered for the perk pick owed now.</summary>
    public static IReadOnlyList<HeroPassiveSO> CurrentPerkOptions =>
        (IReadOnlyList<HeroPassiveSO>)_perkOptions ?? System.Array.Empty<HeroPassiveSO>();

    public static void AddPerk(HeroPassiveSO passive)
    {
        if (!IsActive || !PendingPerkPick || passive == null || _perkOptions == null || !_perkOptions.Contains(passive)) return;

        _playerExtraPassives.Add(passive);
        PendingPerkPick = false;
        _perkOptions = null;
        ActiveMilestone = -1;
        Persist();
    }

    /// <summary>Every passive the player's hero plays with: its own (maybe forged), then its drafted perks.</summary>
    public static List<HeroPassiveSO> PlayerPassives
    {
        get
        {
            var all = new List<HeroPassiveSO>(_playerOwnPassives);
            all.AddRange(_playerExtraPassives);
            return all;
        }
    }

    /// <summary>Player-facing name of a milestone reward, as the catalog words it.</summary>
    public static string MilestoneRewardName(MilestoneReward reward) =>
        Catalog.Name(reward, Tuning.RewardAmount(reward));

    /// <summary>What a milestone reward does, for its preview.</summary>
    public static string MilestoneRewardDescription(MilestoneReward reward) =>
        Catalog.Description(reward, Tuning.RewardAmount(reward));

    /// <summary>
    /// Builds this run's track: <see cref="GauntletConfigSO.milestoneCount"/> milestones, one every
    /// <see cref="GauntletConfigSO.milestoneXpStep"/> XP, each rolled a reward from the random pool, then
    /// the final milestone at <see cref="GauntletConfigSO.finalMilestoneXp"/>, rolled from the final pool.
    /// Each pooled reward comes up once before any repeats; rewards that can't apply to the hero are left out.
    /// </summary>
    private static void RollMilestones()
    {
        _milestones.Clear();

        var random = OfferableRewards(Config.randomRewardPool);

        int next = 0;
        int step = Mathf.Max(1, Config.milestoneXpStep);
        for (int i = 1; i <= Config.milestoneCount; i++)
        {
            var reward = MilestoneReward.None;
            if (random.Count > 0)
            {
                if (next >= random.Count)
                {
                    Shuffle(random);
                    next = 0;
                }
                reward = random[next++];
            }
            _milestones.Add(new MilestoneState(i * step, reward));
        }

        if (Config.finalMilestoneXp > 0)
        {
            var final = OfferableRewards(Config.finalRewardPool);
            _milestones.Add(new MilestoneState(Config.finalMilestoneXp, final.Count > 0 ? final[0] : MilestoneReward.None));
        }
    }

    /// <summary>The pool's distinct rewards that can apply to the hero, shuffled.</summary>
    private static List<MilestoneReward> OfferableRewards(List<MilestoneReward> pool)
    {
        var rewards = new List<MilestoneReward>();
        foreach (var reward in pool)
            if (!rewards.Contains(reward) && CanOfferReward(reward))
                rewards.Add(reward);
        Shuffle(rewards);
        return rewards;
    }

    private static bool CanOfferReward(MilestoneReward reward)
    {
        switch (reward)
        {
            case MilestoneReward.ForgePerk: return _playerOwnPassives.Exists(p => p.forgedVersion != null);
            case MilestoneReward.NewPerk: return PlayerPerkPool().Count > 0;
            default: return true;
        }
    }

    /// <summary>Base passives the player's hero doesn't have in either form, while it has room for another perk.</summary>
    private static List<HeroPassiveSO> PlayerPerkPool()
    {
        var pool = new List<HeroPassiveSO>();
        if (HeroDatabase.Instance == null || _playerExtraPassives.Count >= MatchModifiers.MaxExtraPassives) return pool;

        var owned = PlayerPassives;
        foreach (var passive in HeroDatabase.Instance.AllPassives)
        {
            if (passive == null || passive.isForged) continue;
            if (owned.Exists(own => own == passive || own == passive.forgedVersion)) continue;
            pool.Add(passive);
        }
        return pool;
    }

    /// <summary>Swaps a random forgeable passive of the player's hero (own or perk) for its forged version.</summary>
    private static HeroPassiveSO ForgeRandomPlayerPassive()
    {
        var candidates = new List<(List<HeroPassiveSO> list, int index)>();
        foreach (var list in new[] { _playerOwnPassives, _playerExtraPassives })
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].forgedVersion != null) candidates.Add((list, i));
        if (candidates.Count == 0) return null;

        var (target, at) = candidates[Rng.Range(0, candidates.Count)];
        var forged = target[at].forgedVersion;
        target[at] = forged;
        return forged;
    }

    // ---------------------------------------------------------------- Battles

    /// <summary>
    /// Puts the next battle's enemy -- rolled with the rest when the hero was picked -- in place for the
    /// preview and the fight. The Draft scene calls it once the player has a hero, so the upcoming enemy
    /// can be previewed before the fight.
    /// </summary>
    public static void PrepareNextBattle()
    {
        var encounter = CurrentEncounter;
        if (!IsActive || encounter == null) return;

        // Every encounter is rolled at the hero pick; only a run that somehow lacks this one rolls it now.
        while (_enemies.Count <= EncounterIndex)
        {
            RollEnemy(Config.encounters[_enemies.Count]);
            _enemies.Add(TakeRolledEnemy());
        }

        LoadEnemy(_enemies[EncounterIndex]);
    }

    /// <summary>Rolls every encounter's enemy, in order, from the run's stream.</summary>
    private static void RollAllEnemies()
    {
        _enemies.Clear();
        foreach (var encounter in Config.encounters)
        {
            RollEnemy(encounter);
            _enemies.Add(TakeRolledEnemy());
        }
        ClearEnemy();
    }

    /// <summary>The enemy just rolled into the live fields, copied out.</summary>
    private static EnemyRoll TakeRolledEnemy()
    {
        var roll = new EnemyRoll { hero = _enemyHero, startingMinion = _enemyStartingMinion };
        roll.deck.AddRange(_enemyDeck);
        roll.blessings.AddRange(_enemyBlessings);
        roll.ownPassives.AddRange(_enemyOwnPassives);
        roll.extraPassives.AddRange(_enemyExtraPassives);
        return roll;
    }

    private static void LoadEnemy(EnemyRoll roll)
    {
        ClearEnemy();
        if (roll == null) return;

        _enemyHero = roll.hero;
        _enemyStartingMinion = roll.startingMinion;
        _enemyDeck.AddRange(roll.deck);
        _enemyBlessings.AddRange(roll.blessings);
        _enemyOwnPassives.AddRange(roll.ownPassives);
        _enemyExtraPassives.AddRange(roll.extraPassives);
    }

    /// <summary>
    /// Rolls one encounter's enemy into the live enemy fields: random hero, its blessings, then the deck,
    /// passives and starting minion those blessings call for.
    /// </summary>
    private static void RollEnemy(GauntletConfigSO.Encounter encounter)
    {
        ClearEnemy();

        var heroes = new List<HeroSO>(HeroDatabase.Instance.AllHeroes);
        heroes.RemoveAll(h => h == null);
        if (Config.avoidMirrorHero && heroes.Count > 1) heroes.Remove(Hero);
        _enemyHero = heroes.Count > 0 ? heroes[Rng.Range(0, heroes.Count)] : null;

        if (_enemyHero != null)
            foreach (var passive in _enemyHero.passives)
                if (passive != null && !_enemyOwnPassives.Contains(passive)) _enemyOwnPassives.Add(passive);

        _enemyBlessings.AddRange(RollBlessings(encounter.enemyBlessings));

        if (HasEnemyBlessing(GauntletBlessing.ForgedPassive)) ForgeRandomOwnPassive();

        int extraPassives = _enemyBlessings.FindAll(b => b == GauntletBlessing.ExtraPassive).Count;
        if (extraPassives > 0) _enemyExtraPassives.AddRange(RollExtraPassives(extraPassives));

        if (HasEnemyBlessing(GauntletBlessing.StartingMinion))
        {
            var minions = BaseCardPool().FindAll(c => c.cost == Config.startingMinionCost && c.health > 0);
            _enemyStartingMinion = minions.Count > 0 ? minions[Rng.Range(0, minions.Count)] : null;
        }

        _enemyDeck.AddRange(HasEnemyBlessing(GauntletBlessing.UpgradedDeck)
            ? GenerateEnemyDeck(Config.upgradedDeckSize, UpgradedCardPool(), UpgradedManaCurve, UpgradedManaCurveSize, allowDuplicates: true)
            : GenerateEnemyDeck(encounter.enemyDeckSize, BaseCardPool(), DeckSO.ManaCurve, 10, allowDuplicates: false));
    }

    // ---------------------------------------------------------------- Blessings

    /// <summary>
    /// <paramref name="count"/> distinct blessings from the pool, except that Extra Passive can come up
    /// to <see cref="GauntletConfigSO.extraPassiveMaxStacks"/> times. Blessings that would do nothing on
    /// the rolled enemy (Forged Passive on a hero with nothing to forge) are left out.
    /// </summary>
    private static List<GauntletBlessing> RollBlessings(int count)
    {
        var bag = new List<GauntletBlessing>();
        foreach (var blessing in Config.blessingPool)
        {
            if (bag.Contains(blessing) || !CanBless(blessing)) continue;

            int copies = blessing == GauntletBlessing.ExtraPassive ? Config.extraPassiveMaxStacks : 1;
            for (int i = 0; i < copies; i++) bag.Add(blessing);
        }

        return TakeRandom(bag, count, distinct: true);
    }

    private static bool CanBless(GauntletBlessing blessing)
    {
        switch (blessing)
        {
            case GauntletBlessing.ForgedPassive:
                return _enemyOwnPassives.Exists(p => p.forgedVersion != null);
            case GauntletBlessing.ExtraPassive:
                return ExtraPassivePool().Count > 0;
            case GauntletBlessing.StartingMinion:
                return BaseCardPool().Exists(c => c.cost == Config.startingMinionCost && c.health > 0);
            default:
                return true;
        }
    }

    private static void ForgeRandomOwnPassive()
    {
        var forgeable = new List<int>();
        for (int i = 0; i < _enemyOwnPassives.Count; i++)
            if (_enemyOwnPassives[i].forgedVersion != null) forgeable.Add(i);
        if (forgeable.Count == 0) return;

        int index = forgeable[Rng.Range(0, forgeable.Count)];
        _enemyOwnPassives[index] = _enemyOwnPassives[index].forgedVersion;
    }

    /// <summary>Base passives the enemy hero doesn't already have, in either its base or forged form.</summary>
    private static List<HeroPassiveSO> ExtraPassivePool()
    {
        var pool = new List<HeroPassiveSO>();
        if (HeroDatabase.Instance == null) return pool;

        foreach (var passive in HeroDatabase.Instance.AllPassives)
        {
            if (passive == null || passive.isForged) continue;
            if (_enemyOwnPassives.Exists(own => own == passive || own == passive.forgedVersion)) continue;
            pool.Add(passive);
        }
        return pool;
    }

    private static List<HeroPassiveSO> RollExtraPassives(int count) =>
        TakeRandom(ExtraPassivePool(), Mathf.Min(count, MatchModifiers.MaxExtraPassives), distinct: true);

    /// <summary>
    /// The passives a side's hero plays with in place of its HeroSO's own list, or null to keep that
    /// list: one of them may be forged (the enemy's Forged Passive blessing, the player's Forge Perk).
    /// </summary>
    public static IReadOnlyList<HeroPassiveSO> OwnPassivesFor(SelectionSide side)
    {
        if (!IsActive) return null;
        if (side == SelectionSide.Player) return Hero != null ? _playerOwnPassives : null;
        return _enemyHero != null ? _enemyOwnPassives : null;
    }

    /// <summary>
    /// The extra passives a side plays with this battle (the enemy's Extra Passive blessings, the
    /// player's drafted perks), or null for none.
    /// </summary>
    public static IReadOnlyList<HeroPassiveSO> ExtraPassivesFor(SelectionSide side)
    {
        if (!IsActive) return null;
        var extras = side == SelectionSide.Player ? _playerExtraPassives : _enemyExtraPassives;
        return extras.Count > 0 ? extras : null;
    }

    /// <summary>A side's max mana on its first turn: <paramref name="standard"/>, or more with Starting Mana.</summary>
    public static int StartingManaFor(SelectionSide side, int standard)
    {
        if (!IsActive || side != SelectionSide.Opponent || !HasEnemyBlessing(GauntletBlessing.StartingMana)) return standard;
        return Mathf.Max(standard, Config.blessedStartingMana);
    }

    /// <summary>Cards a side draws on top of the normal starting hand (the Bigger Hand blessing or milestones).</summary>
    public static int ExtraStartingCardsFor(SelectionSide side)
    {
        if (!IsActive) return 0;
        if (side == SelectionSide.Player) return _playerExtraStartingCards;
        return HasEnemyBlessing(GauntletBlessing.BiggerHand) ? Config.extraStartingCards : 0;
    }

    /// <summary>
    /// Starting Minion: puts the rolled minion on a random free tile of the enemy's spawn row. Runs at
    /// match setup, after the heroes' passives register so their auras stamp it like any other summon.
    /// </summary>
    public static void SummonStartingMinion(Agent enemy)
    {
        if (!IsActive || enemy == null || _enemyStartingMinion == null || GameManager.Instance == null) return;

        // Same row convention as ActionHolder.SpawnRowOf: the player summons on row 2, the opponent on 0.
        int spawnRow = enemy == GameManager.Instance.player ? 2 : 0;
        var free = new List<Transform>();
        foreach (var cell in GridManager.Instance.GetGrid())
            if (cell.index.y == spawnRow && cell.cellObj != null && cell.obj == null)
                free.Add(cell.cellObj.transform);
        if (free.Count == 0) return;

        GameManager.Instance.SummonMinion(_enemyStartingMinion, free[Random.Range(0, free.Count)].position, enemy);
    }

    /// <summary>Player-facing name of a blessing.</summary>
    public static string BlessingName(GauntletBlessing blessing)
    {
        switch (blessing)
        {
            case GauntletBlessing.StrongerMinions: return "Stronger Minions";
            case GauntletBlessing.UpgradedDeck: return "Upgraded Deck";
            case GauntletBlessing.ExtraPassive: return "Extra Passive";
            case GauntletBlessing.BonusHealth: return "Bonus Health";
            case GauntletBlessing.StartingMinion: return "Vanguard";
            case GauntletBlessing.BiggerHand: return "Bigger Hand";
            case GauntletBlessing.StartingMana: return "Head Start";
            case GauntletBlessing.ForgedPassive: return "Forged Passive";
            default: return blessing.ToString();
        }
    }

    /// <summary>What a blessing does, with the config's numbers (and the rolled minion) filled in.</summary>
    public static string BlessingDescription(GauntletBlessing blessing)
    {
        var c = Config;
        switch (blessing)
        {
            case GauntletBlessing.StrongerMinions:
                return $"Its first {c.strongerMinionsCount} minions get +{c.strongerMinionsBonus}/+{c.strongerMinionsBonus}";
            case GauntletBlessing.UpgradedDeck:
                return $"Its deck is {c.upgradedDeckSize} upgraded cards";
            case GauntletBlessing.ExtraPassive:
                return "Its hero has a random extra passive";
            case GauntletBlessing.BonusHealth:
                return $"Its hero has +{c.bonusHealth} health";
            case GauntletBlessing.StartingMinion:
                return _enemyStartingMinion != null
                    ? $"It starts with {_enemyStartingMinion.cardName} on the board"
                    : $"It starts with a {c.startingMinionCost}-mana minion on the board";
            case GauntletBlessing.BiggerHand:
                return $"Its starting hand has {c.extraStartingCards} extra cards";
            case GauntletBlessing.StartingMana:
                return $"It starts at {c.blessedStartingMana} mana";
            case GauntletBlessing.ForgedPassive:
                return "Its hero's passive is forged";
            default:
                return string.Empty;
        }
    }

    /// <summary>The hero a side plays with this battle, or null to leave the saved selection alone.</summary>
    public static HeroSO HeroFor(SelectionSide side)
    {
        if (!IsActive) return null;
        if (side != SelectionSide.Opponent) return Hero;

        // Putting a saved end-of-match screen back up: the enemy that was fought stands behind it.
        var fought = ResultsEnemy;
        return fought != null ? fought.hero : _enemyHero;
    }

    /// <summary>The enemy of the battle whose end-of-match screen is being restored, or null.</summary>
    private static EnemyRoll ResultsEnemy =>
        RestoreResultsPending && _results != null && _results.encounterIndex >= 0 && _results.encounterIndex < _enemies.Count
            ? _enemies[_results.encounterIndex]
            : null;

    /// <summary>
    /// The deck a side plays with this battle: always the drafted base cards for the player, since
    /// upgrades earned during a battle are not kept.
    /// </summary>
    public static IReadOnlyList<CardSO> DeckFor(SelectionSide side)
    {
        if (!IsActive) return System.Array.Empty<CardSO>();
        if (side != SelectionSide.Opponent) return _deck;
        var fought = ResultsEnemy;
        return fought != null ? fought.deck : _enemyDeck;
    }

    /// <summary>
    /// Scales the enemy hero's health by the encounter's multiplier (plus Bonus Health), and arms
    /// Stronger Minions for this battle. Runs before passives register.
    /// </summary>
    public static void ApplyEnemySetup(MinionController hero)
    {
        StopStrongerMinions();

        var encounter = CurrentEncounter;
        if (!IsActive || encounter == null || hero == null || hero.modal == null) return;

        int hp = EnemyStartingHealth(hero.modal.defHealth);
        hero.modal.defHealth = hp;
        hero.modal.health = hp;

        if (hero.view != null) hero.view.UpdateViewWithoutStatFlash(hero.modal);

        if (HasEnemyBlessing(GauntletBlessing.StrongerMinions))
        {
            _strongerMinionsLeft = Config.strongerMinionsCount;
            GameManager.OnMinionSummoned += BuffEnemySummon;
        }
    }

    private static void StopStrongerMinions()
    {
        _strongerMinionsLeft = 0;
        GameManager.OnMinionSummoned -= BuffEnemySummon;
    }

    /// <summary>Stronger Minions: +N/+N on each of the enemy's first summons this battle.</summary>
    private static void BuffEnemySummon(MinionController minion)
    {
        var game = GameManager.Instance;
        if (!IsActive || game == null || minion == null || minion.modal == null || minion.owner != game.opponent) return;

        int bonus = Config.strongerMinionsBonus;
        minion.modal.attack += bonus;
        minion.modal.health += bonus;
        minion.modal.defHealth += bonus;
        if (minion.view != null) minion.view.UpdateView(minion.modal);

        if (--_strongerMinionsLeft <= 0) StopStrongerMinions();
    }

    /// <summary>
    /// Puts the statline and passive state carried from the last won battle back onto the player's
    /// hero. Runs AFTER passives register, so it overwrites their one-time self-stat stamp (the
    /// Summoner's -2 Attack) with the carried value, which already has the stamp in it, instead of
    /// stacking it a second time.
    /// </summary>
    public static void RestorePlayerHero(MinionController hero)
    {
        if (!IsActive || _snapshot == null || hero == null || hero.modal == null) return;

        hero.modal.defHealth = _snapshot.maxHealth;
        hero.modal.health = Mathf.Clamp(_snapshot.health, 1, _snapshot.maxHealth);
        if (Config.keepHeroAttack) hero.modal.attack = _snapshot.attack;
        else hero.modal.attack += _rewardAttack;

        var runtime = HeroRuntime.For(hero);
        if (runtime != null && Config.keepPassiveState)
        {
            // The Berserker's granted bonus only travels with the attack it was granted into; with the
            // attack reset, the passive must be free to grant it again.
            if (Config.keepHeroAttack) runtime.appliedAttackBonus = _snapshot.appliedAttackBonus;
            foreach (var pair in _snapshot.counters) runtime.SetCounter(pair.Key, pair.Value);
        }

        if (hero.view != null) hero.view.UpdateViewWithoutStatFlash(hero.modal);
        HeroPassiveIndicatorView.For(hero)?.Refresh();
    }

    /// <summary>Records the player's hero as it ended the battle and advances the run.</summary>
    public static void OnBattleWon(MinionController playerHero)
    {
        if (!IsActive || Outcome != RunOutcome.InProgress) return;

        int fought = EncounterIndex;
        int baseXp = Xp;

        Capture(playerHero);
        EncounterIndex++;

        // The score becomes XP on the way out of the battle (LeaveBattle), when the end-of-match panel
        // has finished tallying it, so the XP gained is exactly the score the player was shown.
        _scorePending = true;
        LastRewardMessage = null;

        // The next battle gets a freshly rolled enemy.
        ClearEnemy();

        if (EncounterIndex >= Config.encounters.Count)
        {
            Outcome = RunOutcome.Won;
        }
        else
        {
            HealSnapshot();
            GrantBattleReward();
        }

        RecordBattle(won: true, fought, baseXp, BattleCardsOwed);
    }

    /// <summary>
    /// Keeps the decided battle: its stats for the run's record, and its end-of-match screen as the
    /// place a resume comes back to until the player leaves it. The stats are built here, as the match
    /// ends -- the same moment, and the same object, the end-of-match panel then shows -- so the score
    /// that becomes XP is exactly the one on screen.
    /// </summary>
    private static void RecordBattle(bool won, int encounterIndex, int baseXp, int battleCardsAtWin)
    {
        var tracker = StatsTracker.Instance;
        var stats = tracker != null ? CopyStats(tracker.BuildStats(won)) : null;

        var enemy = encounterIndex >= 0 && encounterIndex < _enemies.Count ? _enemies[encounterIndex] : null;
        var record = new BattleRecord
        {
            encounterIndex = encounterIndex,
            enemyHero = enemy != null ? HeroKey(enemy.hero) : HeroKey(_enemyHero),
            won = won,
            restarts = _currentBattleRestarts,
            endedUtcTicks = System.DateTime.UtcNow.Ticks,
            stats = stats,
        };
        foreach (var blessing in enemy != null ? enemy.blessings : _enemyBlessings)
            record.enemyBlessings.Add(blessing.ToString());
        _battles.Add(record);
        _currentBattleRestarts = 0;

        _results = new ResultsSave
        {
            won = won,
            encounterIndex = encounterIndex,
            baseXp = baseXp,
            battleCardsAtWin = battleCardsAtWin,
            stats = stats,
        };
        Location = RunLocation.Results;
        Persist();
    }

    /// <summary>A detached copy: the tracker's own object keeps being refinalized while the panel is up.</summary>
    private static MatchStats CopyStats(MatchStats stats) =>
        stats != null ? JsonUtility.FromJson<MatchStats>(JsonUtility.ToJson(stats)) : null;

    /// <summary>
    /// Owes the won battle's card picks. They are taken on the rewards panel over the end-of-match screen;
    /// should the player get to the Draft scene with them still owed, it asks for them as a draft step.
    /// </summary>
    private static void GrantBattleReward()
    {
        BattleCardsOwed = Mathf.Max(1, Config.battleCardPicks);
    }

    /// <summary>
    /// Adds the won battle's end-of-match score (never below zero) to the run's XP. Idempotent: whoever
    /// gets there first collects it. The rewards panel does, so the milestones the win reaches are
    /// already reached while it lists them; <see cref="LeaveBattle"/> catches every other way out.
    /// </summary>
    public static void CollectScore()
    {
        if (!_scorePending) return;
        _scorePending = false;

        // The score captured as the battle was won (RecordBattle); the live tracker only stands in for a
        // battle decided without one.
        var stats = _results != null ? _results.stats : null;
        if (stats == null)
        {
            var tracker = StatsTracker.Instance;
            stats = tracker != null ? tracker.BuildStats(won: true) : null;
        }

        int gained = stats != null ? Mathf.Max(0, stats.TotalScore) : 0;
        Xp += gained;
        if (_battles.Count > 0) _battles[_battles.Count - 1].xpGained = gained;
        Persist();
    }

    public static void OnBattleLost()
    {
        if (!IsActive || Outcome != RunOutcome.InProgress) return;
        Outcome = RunOutcome.Lost;
        StopStrongerMinions();
        RecordBattle(won: false, EncounterIndex, Xp, 0);
    }

    /// <summary>
    /// Leaves a finished battle for the run's draft, which the Game scene opens on with no match behind
    /// it (<see cref="GameOverDraft"/>). What the end-of-match Proceed button does during a run.
    /// </summary>
    public static void LeaveBattle()
    {
        CollectScore();

        // Off the end-of-match screen: a resume from here on comes back to the Draft scene.
        if (IsActive)
        {
            Location = RunLocation.Draft;
            _results = null;
            RestoreResultsPending = false;
            Persist();
        }

        LoadScene(Config != null ? Config.gameSceneName : "Game");
    }

    /// <summary>
    /// Goes from a finished battle straight into the next one, with no Draft scene in between: what the
    /// Fight button on the end-of-match panel's Battles tab does once the win's rewards have been taken.
    /// The score is credited and the saved end-of-match screen dropped exactly as <see cref="LeaveBattle"/>
    /// does, so a quit from the next battle restarts that battle rather than coming back to a screen that
    /// has already been proceeded past.
    ///
    /// A run with nothing left to fight -- won, or lost -- has no next battle, so it leaves for the Draft
    /// scene instead, which is where a run's result is shown.
    /// </summary>
    public static void FightNextBattle()
    {
        CollectScore();

        if (!IsActive || Outcome != RunOutcome.InProgress)
        {
            LeaveBattle();
            return;
        }

        _results = null;
        RestoreResultsPending = false;

        // Puts the rolled enemy in place and marks the run as being in a battle again, saving as it goes.
        BeginBattle();
        LoadScene(Config != null ? Config.gameSceneName : "Game");
    }

    public static void LoadScene(string sceneName)
    {
        if (SceneTransitionManager.Instance != null)
            SceneTransitionManager.Instance.TransitionToScene(sceneName);
        else
            SceneManager.LoadScene(sceneName);
    }

    /// <summary>Current health / max health / attack the player's hero will start the next battle with.</summary>
    public static bool TryGetCarriedStats(out int health, out int maxHealth, out int attack)
    {
        if (_snapshot != null)
        {
            health = _snapshot.health;
            maxHealth = _snapshot.maxHealth;
            attack = Config.keepHeroAttack || Hero == null ? _snapshot.attack : Hero.attack + _rewardAttack;
            return true;
        }

        health = maxHealth = attack = 0;
        if (Hero == null) return false;

        health = Hero.health;
        maxHealth = Hero.defHealth > 0 ? Hero.defHealth : Hero.health;
        attack = Hero.attack;
        return true;
    }

    // ---------------------------------------------------------------- Save

    /// <summary>Writes the run as it stands now. A no-op outside a run, and while a save is being read back.</summary>
    private static void Persist()
    {
        if (!IsActive || _loading) return;
        GauntletSaveStore.Write(ToSave());
    }

    /// <summary>
    /// Upgraded cards are keyed apart from base ones: DeckDatabase lets a card and its upgraded version
    /// share a cardName, so the name alone could come back as the wrong one.
    /// </summary>
    private const string UpgradedCardPrefix = "^";

    private static string CardKey(CardSO card) =>
        card == null ? null : (card.isUpgraded ? UpgradedCardPrefix : string.Empty) + card.cardName;

    private static string HeroKey(HeroSO hero) => hero != null ? hero.name : null;

    private static string PassiveKey(HeroPassiveSO passive) => passive != null ? passive.name : null;

    private static List<string> CardKeys(IEnumerable<CardSO> cards)
    {
        var keys = new List<string>();
        if (cards != null) foreach (var card in cards) keys.Add(CardKey(card));
        return keys;
    }

    private static List<string> PassiveKeys(IEnumerable<HeroPassiveSO> passives)
    {
        var keys = new List<string>();
        if (passives != null) foreach (var passive in passives) keys.Add(PassiveKey(passive));
        return keys;
    }

    /// <summary>The run as plain data.</summary>
    public static GauntletSaveData ToSave()
    {
        var data = new GauntletSaveData
        {
            runId = _runId,
            startedUtcTicks = _startedUtcTicks,
            savedUtcTicks = System.DateTime.UtcNow.Ticks,
            seed = _seed,
            rngState = Rng.State,
            location = Location.ToString(),
            outcome = Outcome.ToString(),

            hasHeroOptions = _heroOptions != null,
            hero = HeroKey(Hero),
            deck = CardKeys(_deck),
            encounterIndex = EncounterIndex,

            pendingPicks = PendingPicks,
            picksThisStep = PicksThisStep,
            pickingUpgraded = PickingUpgraded,
            pickingDuplicate = PickingDuplicate,
            pendingPerkPick = PendingPerkPick,
            battleCardsOwed = BattleCardsOwed,
            pickingBattleCards = _pickingBattleCards,
            activeMilestone = ActiveMilestone,
            hasCardOptions = _cardOptions != null,
            cardOptions = CardKeys(_cardOptions),
            hasPerkOptions = _perkOptions != null,
            perkOptions = PassiveKeys(_perkOptions),

            xp = Xp,
            xpShown = _xpShown,
            milestonesRevealed = _milestonesRevealed,
            lastRewardMessage = LastRewardMessage,
            scorePending = _scorePending,

            playerOwnPassives = PassiveKeys(_playerOwnPassives),
            playerExtraPassives = PassiveKeys(_playerExtraPassives),
            rewardAttack = _rewardAttack,
            playerExtraStartingCards = _playerExtraStartingCards,

            hasSnapshot = _snapshot != null,
            hasResults = _results != null,
            results = _results ?? new ResultsSave(),
            currentBattleRestarts = _currentBattleRestarts,
        };

        if (_heroOptions != null)
            foreach (var hero in _heroOptions) data.heroOptions.Add(HeroKey(hero));

        foreach (var milestone in _milestones)
        {
            data.milestones.Add(new MilestoneSave
            {
                xp = milestone.Xp,
                reward = milestone.Reward.ToString(),
                claimed = milestone.Claimed,
                hasCardOptions = milestone.cardOptions != null,
                cardOptions = CardKeys(milestone.cardOptions),
                hasPerkOptions = milestone.perkOptions != null,
                perkOptions = PassiveKeys(milestone.perkOptions),
            });
        }

        if (_snapshot != null)
        {
            data.snapshot.health = _snapshot.health;
            data.snapshot.maxHealth = _snapshot.maxHealth;
            data.snapshot.attack = _snapshot.attack;
            data.snapshot.appliedAttackBonus = _snapshot.appliedAttackBonus;
            foreach (var pair in _snapshot.counters)
                data.snapshot.counters.Add(new CounterEntry { key = pair.Key, value = pair.Value });
        }

        foreach (var enemy in _enemies)
        {
            var save = new EnemySave
            {
                hero = HeroKey(enemy.hero),
                deck = CardKeys(enemy.deck),
                ownPassives = PassiveKeys(enemy.ownPassives),
                extraPassives = PassiveKeys(enemy.extraPassives),
                startingMinion = CardKey(enemy.startingMinion),
            };
            foreach (var blessing in enemy.blessings) save.blessings.Add(blessing.ToString());
            data.enemies.Add(save);
        }

        data.battles.AddRange(_battles);
        return data;
    }

    /// <summary>
    /// Thrown while reading a save back when something in it no longer resolves -- a card, hero or
    /// passive since renamed or removed, an enum value that is gone. The save is then discarded whole:
    /// a run with a hole in it is worse than a fresh one.
    /// </summary>
    private class SaveMismatchException : System.Exception
    {
        public SaveMismatchException(string message) : base(message) { }
    }

    /// <summary>Rebuilds the run from <paramref name="data"/>. False (and the run reset) if it can't be.</summary>
    private static bool FromSave(GauntletSaveData data)
    {
        Reset();
        _loading = true;
        try
        {
            ReadSave(data);
            _loading = false;
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"GauntletRun: the saved run can't be resumed and is discarded. {e.Message}");
            Reset();
            return false;
        }
    }

    private static void ReadSave(GauntletSaveData data)
    {
        if (data.version != GauntletSaveData.CurrentVersion)
            throw new SaveMismatchException($"save version {data.version}, expected {GauntletSaveData.CurrentVersion}");

        if (HeroDatabase.Instance == null) throw new SaveMismatchException("no HeroDatabase to resolve heroes with");

        IsActive = true;
        Config = GauntletConfigSO.Load();
        if (Config == null) throw new SaveMismatchException("no GauntletConfig");

        var cards = new Dictionary<string, CardSO>();
        foreach (var card in CardPool(upgraded: false)) if (!cards.ContainsKey(CardKey(card))) cards.Add(CardKey(card), card);
        foreach (var card in CardPool(upgraded: true)) if (!cards.ContainsKey(CardKey(card))) cards.Add(CardKey(card), card);

        CardSO Card(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (!cards.TryGetValue(key, out var card)) throw new SaveMismatchException($"unknown card '{key}'");
            return card;
        }

        HeroSO HeroOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var hero = HeroDatabase.Instance.GetHeroByName(key);
            if (hero == null) throw new SaveMismatchException($"unknown hero '{key}'");
            return hero;
        }

        HeroPassiveSO Passive(string key)
        {
            var passive = HeroDatabase.Instance.GetPassiveByName(key);
            if (passive == null) throw new SaveMismatchException($"unknown passive '{key}'");
            return passive;
        }

        void Cards(List<string> keys, List<CardSO> into)
        {
            foreach (var key in keys) into.Add(Card(key));
        }

        void Passives(List<string> keys, List<HeroPassiveSO> into)
        {
            foreach (var key in keys) into.Add(Passive(key));
        }

        T Enum<T>(string name) where T : struct
        {
            if (!System.Enum.TryParse(name, out T value)) throw new SaveMismatchException($"unknown {typeof(T).Name} '{name}'");
            return value;
        }

        _runId = data.runId;
        _startedUtcTicks = data.startedUtcTicks;
        _seed = data.seed;
        _rng = RunRandom.FromState(data.rngState);
        Location = Enum<RunLocation>(data.location);
        Outcome = Enum<RunOutcome>(data.outcome);

        if (data.hasHeroOptions)
        {
            _heroOptions = new List<HeroSO>();
            foreach (var key in data.heroOptions) _heroOptions.Add(HeroOf(key));
        }

        Hero = HeroOf(data.hero);
        Cards(data.deck, _deck);
        EncounterIndex = data.encounterIndex;
        if (EncounterIndex < 0 || EncounterIndex > Config.encounters.Count)
            throw new SaveMismatchException($"encounter {EncounterIndex} out of range");

        PendingPicks = data.pendingPicks;
        PicksThisStep = data.picksThisStep;
        PickingUpgraded = data.pickingUpgraded;
        PickingDuplicate = data.pickingDuplicate;
        PendingPerkPick = data.pendingPerkPick;
        BattleCardsOwed = data.battleCardsOwed;
        _pickingBattleCards = data.pickingBattleCards;
        ActiveMilestone = data.activeMilestone;
        if (data.hasCardOptions) Cards(data.cardOptions, _cardOptions = new List<CardSO>());
        if (data.hasPerkOptions) Passives(data.perkOptions, _perkOptions = new List<HeroPassiveSO>());

        Xp = data.xp;
        _xpShown = data.xpShown;
        _milestonesRevealed = data.milestonesRevealed;
        LastRewardMessage = string.IsNullOrEmpty(data.lastRewardMessage) ? null : data.lastRewardMessage;
        _scorePending = data.scorePending;

        foreach (var save in data.milestones)
        {
            var milestone = new MilestoneState(save.xp, Enum<MilestoneReward>(save.reward)) { Claimed = save.claimed };
            if (save.hasCardOptions) Cards(save.cardOptions, milestone.cardOptions = new List<CardSO>());
            if (save.hasPerkOptions) Passives(save.perkOptions, milestone.perkOptions = new List<HeroPassiveSO>());
            _milestones.Add(milestone);
        }

        Passives(data.playerOwnPassives, _playerOwnPassives);
        Passives(data.playerExtraPassives, _playerExtraPassives);
        _rewardAttack = data.rewardAttack;
        _playerExtraStartingCards = data.playerExtraStartingCards;

        if (data.hasSnapshot)
        {
            _snapshot = new HeroSnapshot
            {
                health = data.snapshot.health,
                maxHealth = data.snapshot.maxHealth,
                attack = data.snapshot.attack,
                appliedAttackBonus = data.snapshot.appliedAttackBonus,
            };
            foreach (var counter in data.snapshot.counters)
                if (!string.IsNullOrEmpty(counter.key)) _snapshot.counters[counter.key] = counter.value;
        }

        foreach (var save in data.enemies)
        {
            var enemy = new EnemyRoll { hero = HeroOf(save.hero), startingMinion = Card(save.startingMinion) };
            Cards(save.deck, enemy.deck);
            foreach (var blessing in save.blessings) enemy.blessings.Add(Enum<GauntletBlessing>(blessing));
            Passives(save.ownPassives, enemy.ownPassives);
            Passives(save.extraPassives, enemy.extraPassives);
            _enemies.Add(enemy);
        }

        // A run whose hero is picked was rolled against this many encounters; a config that has since
        // changed the count can't carry it on.
        if (Hero != null && _enemies.Count != Config.encounters.Count)
            throw new SaveMismatchException($"{_enemies.Count} enemies saved for {Config.encounters.Count} encounters");

        _results = data.hasResults ? data.results : null;
        if (Location == RunLocation.Results && _results == null) Location = RunLocation.Draft;
        if (_results != null && _results.takenRows == null) _results.takenRows = new List<TakenRowSave>();

        if (data.battles != null) _battles.AddRange(data.battles);
        _currentBattleRestarts = data.currentBattleRestarts;

        // The enemy the next battle is against, for the Draft preview and the fight.
        if (Outcome == RunOutcome.InProgress && Hero != null && CurrentEncounter != null)
            LoadEnemy(_enemies[EncounterIndex]);
    }

    // ---------------------------------------------------------------- Internals

    private static void Capture(MinionController hero)
    {
        if (hero == null || hero.modal == null) return;

        var snapshot = new HeroSnapshot
        {
            maxHealth = Mathf.Max(1, hero.modal.defHealth),
            health = hero.modal.health,
            attack = hero.modal.attack,
        };

        var runtime = HeroRuntime.For(hero);
        if (runtime != null)
        {
            snapshot.appliedAttackBonus = runtime.appliedAttackBonus;
            foreach (var pair in runtime.Counters) snapshot.counters[pair.Key] = pair.Value;
        }

        _snapshot = snapshot;
    }

    private static void HealSnapshot()
    {
        if (_snapshot == null) return;

        int heal = Config.healAfterWin + Mathf.RoundToInt(_snapshot.maxHealth * Config.healAfterWinPercent);
        _snapshot.health = Mathf.Clamp(_snapshot.health + heal, 1, _snapshot.maxHealth);
    }

    private static List<CardSO> BaseCardPool() => CardPool(upgraded: false);

    private static List<CardSO> UpgradedCardPool() => CardPool(upgraded: true);

    private static List<CardSO> CardPool(bool upgraded)
    {
        var all = DeckDatabase.Instance != null
            ? DeckDatabase.Instance.AllCards
            : new List<CardSO>(Resources.LoadAll<CardSO>("Cards"));

        var pool = new List<CardSO>(all.Count);
        foreach (var card in all)
            if (card != null && card.isUpgraded == upgraded && !(card is HeroSO)) pool.Add(card);
        return pool;
    }

    /// <summary>
    /// A random deck of <paramref name="size"/> out of <paramref name="all"/>, shaped like
    /// SaveManager.GenerateRandomDeck: <paramref name="curve"/> (authored for a
    /// <paramref name="curveSize"/>-card deck) scaled to the size, then padded at random. Without
    /// <paramref name="allowDuplicates"/>, a card repeats only once the pool runs out.
    /// </summary>
    private static List<CardSO> GenerateEnemyDeck(int size, List<CardSO> all, (int[] costs, int count)[] curve,
        int curveSize, bool allowDuplicates)
    {
        var pool = new List<CardSO>(all);
        var deck = new List<CardSO>(size);
        float scale = size / (float)curveSize;

        foreach (var tier in curve)
        {
            int want = Mathf.RoundToInt(tier.count * scale);
            var tierCards = pool.FindAll(c => System.Array.IndexOf(tier.costs, c.cost) >= 0);
            while (want > 0 && deck.Count < size && tierCards.Count > 0)
            {
                int index = Rng.Range(0, tierCards.Count);
                CardSO card = tierCards[index];
                deck.Add(card);
                want--;

                if (allowDuplicates) continue;
                tierCards.RemoveAt(index);
                pool.Remove(card);
            }
        }

        while (deck.Count < size && all.Count > 0)
        {
            if (!allowDuplicates && pool.Count > 0)
            {
                int index = Rng.Range(0, pool.Count);
                deck.Add(pool[index]);
                pool.RemoveAt(index);
            }
            else
            {
                deck.Add(all[Rng.Range(0, all.Count)]);
            }
        }

        return deck;
    }

    private static List<T> TakeRandom<T>(List<T> pool, int count, bool distinct)
    {
        var result = new List<T>(count);
        if (pool.Count == 0) return result;

        if (distinct)
        {
            var copy = new List<T>(pool);
            Shuffle(copy);
            for (int i = 0; i < count && i < copy.Count; i++) result.Add(copy[i]);
        }
        else
        {
            for (int i = 0; i < count; i++) result.Add(pool[Rng.Range(0, pool.Count)]);
        }
        return result;
    }

    private static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Rng.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
