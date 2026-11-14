using System;
using System.Collections.Generic;

/// <summary>
/// Everything a Forged in Battle run needs to carry on exactly where it was left, as plain JsonUtility
/// data (see <see cref="GauntletSaveStore"/>). Written by <see cref="GauntletRun.ToSave"/> on every change
/// to the run and read back by <see cref="GauntletRun.TryResume"/>.
///
/// Assets are stored under the keys the rest of the save already uses: cards by
/// <see cref="CardSO.cardName"/>, heroes and passives by asset name, and enums by name -- never by
/// ordinal, so reordering <see cref="GauntletBlessing"/> or <see cref="MilestoneReward"/> cannot
/// silently turn one reward into another.
///
/// JsonUtility reads a list that was null as an empty one, so every "rolled yet?" list keeps a has-flag
/// beside it: an empty list of options and options never rolled are not the same thing.
/// </summary>
[Serializable]
public class GauntletSaveData
{
    /// <summary>Bump when the shape changes in a way old saves cannot be read into; they are discarded.</summary>
    public const int CurrentVersion = 1;

    public const string LocationDraft = "Draft";
    public const string LocationBattle = "Battle";
    public const string LocationResults = "Results";

    public int version = CurrentVersion;
    public string runId;
    public long startedUtcTicks;
    public long savedUtcTicks;

    /// <summary>The run's seed, and the live state of its random stream.</summary>
    public uint seed;
    public uint rngState;

    /// <summary>Which screen the run was on: <see cref="LocationDraft"/>, <see cref="LocationBattle"/> or <see cref="LocationResults"/>.</summary>
    public string location = LocationDraft;
    public string outcome;

    // ---------------------------------------------------------------- Draft
    public List<string> heroOptions = new List<string>();
    public bool hasHeroOptions;

    public string hero;
    public List<string> deck = new List<string>();
    public int encounterIndex;

    public int pendingPicks;
    public int picksThisStep;
    public bool pickingUpgraded;
    public bool pickingDuplicate;
    public bool pendingPerkPick;
    public int battleCardsOwed;
    public bool pickingBattleCards;
    public int activeMilestone = -1;

    public List<string> cardOptions = new List<string>();
    public bool hasCardOptions;
    public List<string> perkOptions = new List<string>();
    public bool hasPerkOptions;

    // ---------------------------------------------------------------- Progression
    public int xp;
    public int xpShown;
    public bool milestonesRevealed;
    public string lastRewardMessage;
    public bool scorePending;

    public List<MilestoneSave> milestones = new List<MilestoneSave>();

    public List<string> playerOwnPassives = new List<string>();
    public List<string> playerExtraPassives = new List<string>();
    public int rewardAttack;
    public int playerExtraStartingCards;

    public bool hasSnapshot;
    public HeroSnapshotSave snapshot = new HeroSnapshotSave();

    // ---------------------------------------------------------------- Enemies
    /// <summary>One per encounter, all rolled when the hero is picked.</summary>
    public List<EnemySave> enemies = new List<EnemySave>();

    // ---------------------------------------------------------------- End of match
    /// <summary>The end-of-match screen the run is on; meaningful only while <see cref="location"/> is Results.</summary>
    public bool hasResults;
    public ResultsSave results = new ResultsSave();

    // ---------------------------------------------------------------- Stats
    /// <summary>Every battle fought this run, in order, with the stats its end-of-match panel showed.</summary>
    public List<BattleRecord> battles = new List<BattleRecord>();

    /// <summary>Times the battle being fought now was walked out of and restarted.</summary>
    public int currentBattleRestarts;
}

[Serializable]
public class MilestoneSave
{
    public int xp;
    public string reward;
    public bool claimed;
    public List<string> cardOptions = new List<string>();
    public bool hasCardOptions;
    public List<string> perkOptions = new List<string>();
    public bool hasPerkOptions;
}

[Serializable]
public class HeroSnapshotSave
{
    public int health;
    public int maxHealth;
    public int attack;
    public int appliedAttackBonus;
    public List<CounterEntry> counters = new List<CounterEntry>();
}

/// <summary>A dictionary entry, since JsonUtility cannot serialize dictionaries.</summary>
[Serializable]
public class CounterEntry
{
    public string key;
    public int value;
}

[Serializable]
public class EnemySave
{
    public string hero;
    public List<string> deck = new List<string>();
    public List<string> blessings = new List<string>();
    public List<string> ownPassives = new List<string>();
    public List<string> extraPassives = new List<string>();
    public string startingMinion;

    /// <summary>The battle's weather, by asset name; empty for a clear sky (or a save from before weather).</summary>
    public string weather;
}

[Serializable]
public class ResultsSave
{
    public bool won;

    /// <summary>The encounter the battle was, so the backdrop fields the enemy that was fought.</summary>
    public int encounterIndex;

    /// <summary>The run's XP before the battle: the track starts there and the milestones above it are this win's.</summary>
    public int baseXp;

    /// <summary>Cards the win owed when it was won; the card row is listed when above zero.</summary>
    public int battleCardsAtWin;

    public MatchStats stats;

    /// <summary>Rows already taken, with what they read once taken.</summary>
    public List<TakenRowSave> takenRows = new List<TakenRowSave>();
}

[Serializable]
public class TakenRowSave
{
    /// <summary>Index into the milestones, or -1 for the won battle's card row.</summary>
    public int milestone;
    public string label;
}

/// <summary>One fought battle, kept for the run's stats.</summary>
[Serializable]
public class BattleRecord
{
    public int encounterIndex;
    public string enemyHero;
    public List<string> enemyBlessings = new List<string>();
    public bool won;
    public int xpGained;

    /// <summary>Times this battle was walked out of mid-fight and restarted before it was decided.</summary>
    public int restarts;

    public long endedUtcTicks;
    public MatchStats stats;
}
