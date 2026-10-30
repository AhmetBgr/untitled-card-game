using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

/// <summary>
/// The perk choice of a New Perk milestone, over the end-of-match screen: a prompt and a row of options
/// on a dimming backdrop, opened beside the run's loadout (<see cref="BattleRewardLoadout"/>) -- the same
/// staging a reward card pick gets from <see cref="CardSelectionPanel"/>, so taking a perk reads exactly
/// like drafting a card. <see cref="BattleRewardsPanel"/> opens it and takes the pick.
///
/// An option is the shared <see cref="PerkOptionView"/> prefab, the same template the Draft scene's perk
/// draft spawns (DraftController.SpawnPerkOptions), so the same perk looks the same in both places.
///
/// Holds no rules of its own, like the card panel: it draws the perks it is handed and reports the click.
/// The panel GameObject is authored DISABLED, so it is only up while a pick is open, and is resolved
/// lazily through <see cref="Resolve"/> rather than cached from Awake.
/// </summary>
public class PerkSelectionPanel : MonoBehaviour
{
    [Tooltip("Parent the options are spawned under; drives their layout.")]
    [SerializeField] private Transform optionsContainer;

    [SerializeField] private TextMeshProUGUI promptText;

    [Tooltip("A copy kept for one screen's own picks (the end-of-match rewards), wired to it directly: " +
             "never found as the scene's panel.")]
    [SerializeField] private bool routedOnly;

    [Tooltip("The perk option template: Assets/Prefabs/UI/PerkOption.prefab, which owns the look of an " +
             "option (its size, its fonts and its hover).")]
    [SerializeField] private PerkOptionView optionPrefab;

    [Tooltip("The stagger between options popping in. How long one takes belongs to the prefab.")]
    [SerializeField] private float optionRevealStagger = 0.08f;

    [Header("Taking a perk")]
    [Tooltip("How long the panel stays up after a perk is taken, so the unlock can play out before the " +
             "screen moves on. 0 hands the pick back the instant it is clicked, as it used to. Keep it " +
             "at or above the option's punch plus the unlock slide + shrink + hold below, or the screen moves on " +
             "before the perk has finished appearing on the hero.")]
    [SerializeField] private float closeDelay = 1f;

    [Header("Unlocking it on the hero")]
    [Tooltip("The hero panel's locked frame sliding down to reveal the new perk: how far down it goes " +
             "(in chip units -- the chip itself is 40) and how long it takes.")]
    [SerializeField] private float unlockSlide = 30f;
    [SerializeField] private float unlockDuration = 0.3f;

    [Tooltip("How long the locked frame takes to scale down to nothing once it has slid down.")]
    [SerializeField] private float unlockShrinkDuration = 0.15f;

    [Tooltip("A beat on the unlocked chip before the screen moves on, so the reveal is read.")]
    [SerializeField] private float unlockHold = 0.2f;

    private readonly List<PerkOptionView> _options = new List<PerkOptionView>();

    private Action<HeroPassiveSO> _onPicked;

    /// <summary>
    /// The locked slot on the hero panel this pick fills, whose locked frame slides off to reveal it. Cleared by every <see cref="Open"/>, so a slot that
    /// belonged to one pick can never be reused by the next.
    /// </summary>
    private PassiveChipButton _unlockSlot;

    /// <summary>The wait between the click and handing the perk back. Killed if the panel goes away first.</summary>
    private Tween _closeCall;

    /// <summary>One choice gives one perk: a second click, on the option beside it, is not a second pick.</summary>
    private bool _taken;

    private static PerkSelectionPanel _instance;

    /// <summary>
    /// The panel in the current scene, disabled or not. Re-found whenever the cached one has gone away, so
    /// reloading the match scene picks up the new scene's panel instead of a destroyed one -- the same lazy
    /// resolve <see cref="CardSelectionPanel"/> and <see cref="BattleRewardsPanel"/> use.
    /// </summary>
    public static PerkSelectionPanel Resolve()
    {
        if (_instance == null)
            foreach (var panel in FindObjectsByType<PerkSelectionPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!panel.routedOnly)
                {
                    _instance = panel;
                    break;
                }
        return _instance;
    }

    /// <summary>
    /// Puts these perks on screen as the choice. Returns false, leaving the panel closed, when there is
    /// nothing to show (no perks, or the panel is not wired up); the caller then leaves the pick to the
    /// Draft scene, exactly as a card pick that cannot be opened does.
    /// </summary>
    public bool Open(IReadOnlyList<HeroPassiveSO> perks, string prompt, Action<HeroPassiveSO> onPicked)
    {
        if (optionsContainer == null || optionPrefab == null)
        {
            Debug.LogWarning("PerkSelectionPanel: not wired up (options container / option prefab missing)");
            return false;
        }

        Clear();
        _unlockSlot = null;
        if (promptText != null) promptText.text = prompt;

        int shown = 0;
        if (perks != null)
            for (int i = 0; i < perks.Count; i++)
                if (perks[i] != null) SpawnOption(perks[i], shown++);

        if (shown == 0)
        {
            Clear();
            return false;
        }

        _onPicked = onPicked;
        _taken = false;
        gameObject.SetActive(true);
        return true;
    }

    public void Close()
    {
        Clear();
        _onPicked = null;
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Names the locked slot on the hero panel this pick is going to fill. Nothing about it changes
    /// until a perk is taken: it is already authored locked, and stays that way while the choice is
    /// open. Set after <see cref="Open"/>, which clears it -- a prefab cannot hold the reference itself,
    /// because the slot is a scene object. Null leaves the pick with nothing to unlock.
    /// </summary>
    public void SetUnlockTarget(PassiveChipButton slot) => _unlockSlot = slot;

    /// <param name="order">Where the option sits in the row, which is also its turn to pop in.</param>
    private void SpawnOption(HeroPassiveSO perk, int order)
    {
        PerkOptionView option = Instantiate(optionPrefab, optionsContainer);
        option.name = perk.passiveName;
        option.Show(perk);
        option.OnClicked = _ => Pick(perk);
        option.PlayReveal(order * optionRevealStagger);
        _options.Add(option);
    }

    private void Pick(HeroPassiveSO perk)
    {
        if (_taken || _onPicked == null) return;

        _taken = true;

        var picked = _onPicked;
        _onPicked = null;

        PlayUnlock(perk);

        if (closeDelay <= 0f)
        {
            picked(perk);
            return;
        }

        // Unscaled, so a paused game can never leave the pick hanging and the run stuck on this screen.
        _closeCall = DOVirtual.DelayedCall(closeDelay, () =>
        {
            _closeCall = null;
            picked(perk);
        }, ignoreTimeScale: true);
    }

    /// <summary>
    /// The perk being taken: the option punches where it stands and the others bow out, then the
    /// hero panel's slot unlocks.
    /// </summary>
    private void PlayUnlock(HeroPassiveSO perk)
    {
        foreach (var option in _options)
        {
            if (option == null) continue;
            if (option.Perk == perk) option.PlayTaken(() => UnlockSlot(perk));
            else option.PlayDiscarded();
        }
    }

    /// <summary>The hero panel's locked frame sliding down to reveal the new perk.</summary>
    private void UnlockSlot(HeroPassiveSO perk)
    {
        if (_unlockSlot != null) _unlockSlot.PlayUnlock(perk, unlockSlide, unlockDuration, unlockShrinkDuration);
    }

    private void Clear()
    {
        if (_closeCall != null && _closeCall.IsActive()) _closeCall.Kill();
        _closeCall = null;

        _options.Clear();

        // Everything in the container, not only what this panel spawned -- the same sweep
        // DraftController does. An option authored into the scene as a design-time preview of the
        // PerkOption prefab would otherwise stay on screen as an extra, unpickable choice.
        if (optionsContainer != null)
            for (int i = optionsContainer.childCount - 1; i >= 0; i--)
                Destroy(optionsContainer.GetChild(i).gameObject);

        _taken = false;
    }

    void OnDestroy() => Clear();
}
