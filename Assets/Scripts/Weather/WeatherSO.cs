using System.Collections;
using UnityEngine;

/// <summary>
/// One weather a battle can be fought in: its name, rules text and icon, plus the numbers its effect
/// reads. Purely a config asset -- a battle's live state (which tiles are brambled, where the next meteor
/// lands, how many turns until the wind) lives on the <see cref="WeatherEffect"/> that
/// <see cref="CreateEffect"/> builds for it, so one asset can be shared by every battle that rolls it.
///
/// Each kind of weather is a subclass with its effect class next to it in the same file. The pool a
/// battle rolls from is <see cref="WeatherDatabaseSO"/>.
/// </summary>
public abstract class WeatherSO : ScriptableObject
{
    [Header("Display")]
    public string weatherName;
    [TextArea(2, 4)] public string description;
    public Sprite icon;

    [Header("Board")]
    [Tooltip("Sprite laid over the board tiles this weather marks (brambles, fog, the rock, the meteor's " +
             "target). Empty = the icon. Weathers that mark no tiles ignore it.")]
    public Sprite tileSprite;
    public Color tileTint = Color.white;

    [Header("Visuals")]
    [Tooltip("World-space prefab spawned at the centre of the board when the battle starts (rain, fog " +
             "clouds, lighting...) and destroyed with the battle. Empty = no ambient visual.")]
    public GameObject visualPrefab;

    [Header("Audio")]
    [Tooltip("Looping bed (rain, wind...) played over the match ambience for the whole battle, on the " +
             "Ambient bus. Empty = none.")]
    public SoundEffect ambience;
    [Tooltip("Seconds to fade the weather bed in at the start of the battle and out at its end.")]
    [Min(0f)] public float ambienceFade = 3f;

    [Header("Pool")]
    [Tooltip("Off = never rolled for a battle, but still resolvable by name (old saves, forced test weather).")]
    public bool inRandomPool = true;

    public string DisplayName => string.IsNullOrEmpty(weatherName) ? name : weatherName;

    public Sprite TileSprite => tileSprite != null ? tileSprite : icon;

    /// <summary>A fresh effect for one battle.</summary>
    public abstract WeatherEffect CreateEffect();
}

/// <summary>
/// The live half of a weather, for one battle. Every hook is a no-op by default; a weather overrides only
/// what it touches. Hooks returning an IEnumerator run inside GameManager's triggered-action scheduler
/// (so the damage they deal serializes with death / took-damage triggers); returning null skips them.
/// </summary>
public abstract class WeatherEffect
{
    public WeatherSO Config { get; private set; }
    protected WeatherSystem Board { get; private set; }

    internal void Bind(WeatherSO config, WeatherSystem system)
    {
        Config = config;
        Board = system;
    }

    /// <summary>The board is up (cells registered) and nothing has been dealt yet.</summary>
    public virtual void OnBattleStart() { }

    /// <summary>Raw damage about to hit a unit, before armor.</summary>
    public virtual int ModifyDamage(int damage) => damage;

    /// <summary>A card's cost as it enters play (drawn, created, transformed).</summary>
    public virtual int AdjustCost(CardSO card, int cost) => cost;

    /// <summary>Whether a minion colliding into an ally deals it its collision damage.</summary>
    public virtual bool FriendlyCollisionDamage => false;

    /// <summary>A minion was just summoned, its modal fully set up.</summary>
    public virtual void OnMinionSummoned(MinionController minion) { }

    /// <summary>
    /// A minion's summon has fully resolved (its own on-summon effects included) on <paramref name="cell"/>.
    /// Runs on the scheduler, so it is safe to damage it here, unlike in <see cref="OnMinionSummoned"/>.
    /// </summary>
    public virtual IEnumerator AfterMinionSummoned(MinionController minion, Vector2Int cell) => null;

    /// <summary>A minion arrived on <paramref name="cell"/> by moving (its own advance, a push, the wind).</summary>
    public virtual void OnMinionEnteredCell(MinionController minion, Vector2Int cell) { }

    /// <summary>Something on the grid moved or changed.</summary>
    public virtual void OnBoardChanged() { }

    /// <summary>A minion died on <paramref name="cell"/>.</summary>
    public virtual IEnumerator OnMinionDied(MinionController minion, Vector2Int cell) => null;

    /// <summary>A side's turn begins: after its draw, before its own turn-start triggers.</summary>
    public virtual IEnumerator OnTurnStarted(bool playerTurn) => null;

    /// <summary>A side's turn ends: after its minions have moved.</summary>
    public virtual IEnumerator OnTurnEnded(bool playerTurn) => null;

    /// <summary>Every frame while the battle runs, for visuals that track live board state (e.g. a warning).</summary>
    public virtual void Tick() { }

    /// <summary>A short live line under the weather's description in its HUD tooltip, or null.</summary>
    public virtual string Status => null;

    /// <summary>
    /// Turns until this weather next fires, shown as a number on its HUD icon (1 = this turn), or null for
    /// a weather with no timer. Units are the weather's own (Strong Wind counts every turn, Thunderstorm
    /// only yours); <see cref="Status"/> spells them out in the tooltip.
    /// </summary>
    public virtual int? Countdown => null;

    /// <summary>
    /// What the badge on the HUD icon reads, or null for no badge. Defaults to <see cref="Countdown"/>;
    /// override to show something else (Thunderstorm shows its strike chance, "30%").
    /// </summary>
    public virtual string Badge => Countdown?.ToString();

    /// <summary>The badge is drawn in the warning colour: the weather fires this turn (or is certain to).</summary>
    public virtual bool BadgeUrgent => Countdown.HasValue && Countdown.Value <= 1;
}
