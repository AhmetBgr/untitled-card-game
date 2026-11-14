using UnityEngine;

/// <summary>What shape of value a modifier holds. Drives which row the popup builds for it.</summary>
public enum ModifierValueKind { Bool, Int, Float }

/// <summary>
/// One tunable match rule, authored as its own asset so a game mode can list whichever set it wants
/// (see <see cref="GameModeSO"/>). Every value is stored as a float (bools as 0/1) so one profile
/// shape covers every kind -- see <see cref="ModifierProfile"/>.
///
/// Effects are the virtual hooks below, each a no-op by default: a modifier overrides only the hook it
/// cares about, and gameplay calls them through <see cref="MatchModifiers"/> rather than knowing any
/// modifier by name. A new kind of rule (mana, hand size, ...) adds a hook here and one call site.
/// </summary>
public abstract class GameModifierSO : ScriptableObject
{
    [Tooltip("Stable key the saved value is stored under. Never rename once shipped.")]
    public string id;

    public string displayName;
    [TextArea] public string description;

    public abstract ModifierValueKind Kind { get; }

    public abstract float DefaultValue { get; }

    /// <summary>Snaps a raw value onto what this modifier accepts (range, step, 0/1).</summary>
    public abstract float Clamp(float value);

    /// <summary>What the row shows beside the control for this value.</summary>
    public virtual string FormatValue(float value) => value.ToString();

    // ------------------------------------------------------------------ effect hooks

    /// <summary>Relaxes or tightens the deck-building rules. Runs on top of the mode's base rules.</summary>
    public virtual void ModifyDeckRules(ref DeckRules rules, float value) { }

    /// <summary>
    /// Called from GameManager.SetupGame after the hero's modal has been seeded from its CardSO and
    /// BEFORE passives are registered, so stat changes here are the base the passives stamp onto.
    /// </summary>
    public virtual void OnHeroInitialized(MinionController hero, SelectionSide side, float value) { }

    /// <summary>True when this modifier lets the setup screen pick extra passives for each side.</summary>
    public virtual bool EnablesExtraPassives(float value) => false;

    /// <summary>
    /// True when this modifier decides the match's weather; <paramref name="weather"/> is then the pick
    /// (null = clear sky). Called once, as the Game scene loads.
    /// </summary>
    public virtual bool TryChooseWeather(float value, out WeatherSO weather)
    {
        weather = null;
        return false;
    }
}
