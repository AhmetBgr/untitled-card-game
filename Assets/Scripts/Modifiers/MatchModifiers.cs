using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The mode and modifier values the NEXT match is played with. Static so it crosses the scene load
/// from the setup screen into the Game scene, like GameManager.IsTutorialMatch; a mode's setup screen
/// calls <see cref="Begin"/>, plain modes (Quick Play, the tutorial) call <see cref="Clear"/> so no
/// modifier leaks into them.
///
/// Gameplay never reads a modifier by name. It asks this facade -- <see cref="CurrentDeckRules"/>,
/// <see cref="ApplyHeroSetup"/>, <see cref="ExtraPassivesEnabled"/> -- which folds every listed
/// modifier's hook over its saved value. With no mode set, every hook is a no-op and the classic rules
/// apply, so the Game scene still runs when opened directly.
/// </summary>
public static class MatchModifiers
{
    public static GameModeSO Mode { get; private set; }
    public static ModifierProfile Profile { get; private set; }

    /// <summary>Raised after Begin, Clear, a value change and a reset.</summary>
    public static event Action Changed;

    public static bool IsActive => Mode != null;

    // Statics survive between play sessions when domain reload is off; start each run clean.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForNewSession()
    {
        Mode = null;
        Profile = null;
        Changed = null;
    }

    public static void Begin(GameModeSO mode)
    {
        Mode = mode;
        Profile = mode != null && SaveManager.Instance != null
            ? SaveManager.Instance.GetOrCreateModifierProfile(mode.modeId)
            : null;

        Changed?.Invoke();
    }

    public static void Clear()
    {
        Mode = null;
        Profile = null;
        Changed?.Invoke();
    }

    public static float GetValue(GameModifierSO modifier)
    {
        if (modifier == null) return 0f;
        return Profile != null ? Profile.Get(modifier) : modifier.DefaultValue;
    }

    public static bool GetBool(GameModifierSO modifier) => BoolModifierSO.IsOn(GetValue(modifier));

    public static void SetValue(GameModifierSO modifier, float value)
    {
        if (modifier == null || Profile == null) return;

        float clamped = modifier.Clamp(value);
        if (Mathf.Approximately(Profile.Get(modifier), clamped)) return;

        Profile.Set(modifier, clamped);
        if (SaveManager.Instance != null) SaveManager.Instance.SaveData();
        Changed?.Invoke();
    }

    public static void ResetToDefaults()
    {
        if (Profile == null) return;

        Profile.Clear();
        if (SaveManager.Instance != null) SaveManager.Instance.SaveData();
        Changed?.Invoke();
    }

    /// <summary>The mode's base rules with every modifier folded over them.</summary>
    public static DeckRules CurrentDeckRules
    {
        get
        {
            if (Mode == null)
            {
                int size = SaveManager.Instance != null ? SaveManager.Instance.DeckSize : 10;
                return DeckRules.Standard(size);
            }

            DeckRules rules = Mode.baseDeckRules;
            List<GameModifierSO> modifiers = Mode.modifiers;
            for (int i = 0; i < modifiers.Count; i++)
                if (modifiers[i] != null) modifiers[i].ModifyDeckRules(ref rules, GetValue(modifiers[i]));

            return rules;
        }
    }

    /// <summary>Lets every modifier adjust a freshly initialised hero. See GameModifierSO.OnHeroInitialized.</summary>
    public static void ApplyHeroSetup(MinionController hero, SelectionSide side)
    {
        if (Mode == null || hero == null) return;

        List<GameModifierSO> modifiers = Mode.modifiers;
        for (int i = 0; i < modifiers.Count; i++)
            if (modifiers[i] != null) modifiers[i].OnHeroInitialized(hero, side, GetValue(modifiers[i]));

        // Re-seed rather than diff, as a passive's own stat stamp does: this lands during SetupGame, and
        // a "+4" / "-9" flash over the hero before the first turn reads as a buff or a hit, not a rule.
        if (hero.view != null && hero.modal != null) hero.view.UpdateViewWithoutStatFlash(hero.modal);
    }

    /// <summary>
    /// How many passives a side may add on top of its hero's own while the multiple-passives modifier
    /// is on. The picker refuses the pick past this, and GameManager trims to it when the match reads
    /// the save — so a save written before the cap (or edited by hand) cannot smuggle extras in.
    /// </summary>
    public const int MaxExtraPassives = 2;

    /// <summary>
    /// True when the mode has a modifier that picks the weather; <paramref name="weather"/> is its pick
    /// (null = clear sky). False with no mode, or a mode without one: the caller's own default applies.
    /// </summary>
    public static bool TryChooseWeather(out WeatherSO weather)
    {
        weather = null;
        if (Mode == null) return false;

        List<GameModifierSO> modifiers = Mode.modifiers;
        for (int i = 0; i < modifiers.Count; i++)
            if (modifiers[i] != null && modifiers[i].TryChooseWeather(GetValue(modifiers[i]), out weather)) return true;

        return false;
    }

    public static bool ExtraPassivesEnabled
    {
        get
        {
            if (Mode == null) return false;

            List<GameModifierSO> modifiers = Mode.modifiers;
            for (int i = 0; i < modifiers.Count; i++)
                if (modifiers[i] != null && modifiers[i].EnablesExtraPassives(GetValue(modifiers[i]))) return true;

            return false;
        }
    }
}
