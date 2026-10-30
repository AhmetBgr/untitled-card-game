using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The hero's passive indicators. Lives on Hero.prefab, so both PlayerHero and OpponentHero get it;
/// opponent passives are visible on purpose, since hidden opponent passives are exactly the feedback
/// gap this exists to close.
///
/// The component stays on the hero (HeroPassiveSystem and GameManager both reach it via For(hero)),
/// but the thing it draws does not: Agent.SpawnPassiveUI instantiates PassiveUICanvas.prefab under
/// the agent's passiveUIPos anchor and hands the indicator inside it here via AttachIndicator. That
/// keeps the row parked at a fixed board position instead of riding the hero's transform through
/// every lunge, punch and death animation.
///
/// One indicator per passive: the attached one renders the first, and further passives (a hero
/// carrying extras from the multiple-passives modifier) get clones of it, spread around the authored
/// spot at <see cref="slotSpacing"/>.
///
/// Discovery is PUSH, not pull. HeroRuntime is AddComponent'ed at runtime by HeroPassiveSystem.Register,
/// so this cannot resolve its data in Start(). It stays inert until Register hands it the runtime —
/// the one moment both prerequisites hold (hero.card is a HeroSO with passives, and the runtime exists).
/// It reads runtime.passives rather than HeroSO directly, so it can never disagree with what the
/// system actually registered.
///
/// Everything here is read-only with respect to game state. It must never enqueue ActionHolder verbs
/// or open a triggered-action scope — see the HeroPassiveSystem header for why a second writer races
/// GameManager's scope.
/// </summary>
public class HeroPassiveIndicatorView : MonoBehaviour
{
    [Tooltip("The indicator this view drives. Normally supplied at runtime by Agent.SpawnPassiveUI; an authored reference here is only a fallback for a hero used outside the agent setup.")]
    [SerializeField] private HeroPassiveIndicator indicator;

    /// <summary>
    /// Distance between neighbouring indicators, in the indicator's canvas units, when the hero carries
    /// more than one passive. The first passive stays on the authored spot and the rest step LEFT, away
    /// from the hero frame the row sits beside. The indicator's rect is 12 wide but its art is about 5.
    /// </summary>
    private const float SlotSpacing = 6f;

    /// <summary>Everything one rendered passive needs: its indicator and the per-passive display state.</summary>
    private class Slot
    {
        public HeroPassiveIndicator indicator;
        public HeroPassiveSO passive;
        public HeroPassiveDisplay lastDisplay;

        // Progress total at which the counter last completed a block and restarted at max. Owned here
        // because it is a display decision, not game state — see ResolveProgressCounter.
        public int counterBaseline;
        public bool hasCounterBaseline;
    }

    private readonly List<Slot> _slots = new List<Slot>();

    // Indicators cloned off the attached one for the second passive onwards. Kept between binds so a
    // rebind reuses them instead of accumulating.
    private readonly List<HeroPassiveIndicator> _clones = new List<HeroPassiveIndicator>();

    private HeroRuntime _runtime;

    // Where the attached indicator was authored to sit; the row is centred on it.
    private Vector2 _basePosition;
    private bool _hasBasePosition;

    /// <summary>
    /// The indicators drawing the bound passives, in passive order: where each passive sits on the board.
    /// Read-only, for something that wants to land on them (the versus intro).
    /// </summary>
    public IReadOnlyList<HeroPassiveIndicator> BoundIndicators
    {
        get
        {
            var bound = new List<HeroPassiveIndicator>(_slots.Count);
            foreach (var slot in _slots) bound.Add(slot.indicator);
            return bound;
        }
    }

    public static HeroPassiveIndicatorView For(MinionController hero)
        => hero != null ? hero.GetComponent<HeroPassiveIndicatorView>() : null;

    // The indicator ships visible in its prefab so it can be laid out in the editor. Hide it before the
    // first frame: a hero with no passives must never flash the placeholder icon.
    private void Awake() => Hide();

    /// <summary>
    /// Points this view at the indicator spawned for the owning agent (see Agent.SpawnPassiveUI), which
    /// lives on a canvas anchored to the board rather than on the hero.
    ///
    /// Order-independent with respect to Bind: if passives were already bound to a previous indicator,
    /// they are rebound onto the new one, so this can arrive before or after HeroPassiveSystem.Register.
    /// </summary>
    public void AttachIndicator(HeroPassiveIndicator spawned)
    {
        if (spawned == null || spawned == indicator) return;

        // A hero that also carries an authored fallback would otherwise leave it on screen alongside
        // the spawned one, showing the same passive twice.
        if (indicator != null) indicator.gameObject.SetActive(false);
        DestroyClones();

        // Bind() opens with Hide(), which clears _runtime — so read it before rebinding, not after.
        HeroRuntime bound = _runtime;

        indicator = spawned;
        indicator.gameObject.SetActive(false); // stays hidden until something is actually bound to it
        _hasBasePosition = false;

        if (bound != null) Bind(bound);
    }

    /// <summary>
    /// Binds the hero's passives to the attached indicator (and clones of it). Idempotent: re-Registering
    /// or reloading the scene rebinds the same objects rather than accumulating icons.
    /// </summary>
    public void Bind(HeroRuntime runtime)
    {
        Hide();
        _runtime = runtime;

        if (indicator == null || runtime == null || runtime.heroSO == null) return;

        List<HeroPassiveSO> passives = runtime.passives;
        if (passives == null) return;

        for (int i = 0; i < passives.Count; i++)
        {
            HeroPassiveSO passive = passives[i];
            if (passive == null) continue;

            var slot = new Slot
            {
                indicator = _slots.Count == 0 ? indicator : CloneIndicator(_slots.Count - 1),
                passive = passive
            };

            // Apply drives SetActive off display.visible, so a passive with no icon (or one that opted
            // out) leaves its indicator hidden while still staying bound — Refresh can bring it back.
            HeroPassiveDisplay display = passive.GetDisplay(runtime);

            // Establish the reset baseline here rather than on the first Refresh, so a hero that is
            // registered already damaged starts on a full ring instead of animating a proc it never got.
            if (display.type == PassiveIndicatorType.Counter && display.counterFromProgress)
            {
                ResolveProgressCounter(slot, display.counterProgress, display.counterMax, passive.counterOverflows,
                    out int remaining, out _);
                display = display.WithCounter(remaining, display.counterMax);
            }

            slot.lastDisplay = display;
            slot.indicator.Bind(passive, slot.lastDisplay);
            _slots.Add(slot);
        }

        LayoutSlots();
    }

    /// <summary>
    /// Re-pulls GetDisplay for every bound passive. Cheap to call liberally: each slot early-outs when
    /// nothing its indicator renders has changed.
    /// </summary>
    public void Refresh()
    {
        if (_runtime == null) return;

        for (int i = 0; i < _slots.Count; i++)
            RefreshSlot(_slots[i]);
    }

    private void RefreshSlot(Slot slot)
    {
        if (slot.passive == null || slot.indicator == null) return;

        HeroPassiveDisplay display = slot.passive.GetDisplay(_runtime);

        // Progress-driven counters need resolving before anything can be compared: GetDisplay reports
        // only the running total, and the remaining count depends on this slot's reset baseline.
        int cyclesCrossed = 0;
        if (display.type == PassiveIndicatorType.Counter && display.counterFromProgress)
        {
            ResolveProgressCounter(slot, display.counterProgress, display.counterMax, slot.passive.counterOverflows,
                out int remaining, out cyclesCrossed);
            display = display.WithCounter(remaining, display.counterMax);
        }

        if (cyclesCrossed == 0 && display.SameAs(slot.lastDisplay)) return;

        bool animateCounter =
            display.type == PassiveIndicatorType.Counter &&
            slot.lastDisplay.type == PassiveIndicatorType.Counter;

        // A RuntimeCounter is written by someone else, who owns its resets — so the >0 -> 0 edge is the
        // only proc signal available for it. Edge, not level: RefreshSlot early-outs on an unchanged
        // display, so this cannot re-punch while the counter sits at zero. Progress counters never use
        // this path; their punches come from the crossings the ring animates through.
        bool countedDownToZero =
            animateCounter &&
            !display.counterFromProgress &&
            slot.lastDisplay.counterValue > 0 &&
            display.counterValue == 0;

        slot.lastDisplay = display;

        if (animateCounter) slot.indicator.ApplyAnimated(display, cyclesCrossed);
        else slot.indicator.Apply(display);

        if (countedDownToZero) slot.indicator.PlayProcFlash();
    }

    /// <summary>
    /// Turns a monotonic running total into "how much is left on the ring". Every completed block of
    /// `max` is a proc; what happens to the leftover past the last one depends on the passive:
    ///
    /// overflow=true  — the remainder carries into the next block. The ring stays in step with a
    ///                  passive that stacks off the running total: it always reads
    ///                  max - (progress % max), so it predicts the next stack correctly. This is what
    ///                  Raging Blood wants, since ScaleAttackWithHealthLost grants healthLost / max.
    /// overflow=false — the remainder is DISCARDED and the ring restarts at max. Easier to read, but
    ///                  it drifts out of step with any passive that stacks off a total.
    ///
    /// Either way the baseline lives on the slot rather than in GetDisplay, which must stay pure.
    /// </summary>
    private static void ResolveProgressCounter(Slot slot, int progress, int rawMax, bool overflow,
        out int remaining, out int cycles)
    {
        int max = Mathf.Max(1, rawMax);

        // First read, or the total moved backwards (the hero was healed) — rebase and show a full ring.
        if (!slot.hasCounterBaseline || progress < slot.counterBaseline)
        {
            slot.counterBaseline = progress;
            slot.hasCounterBaseline = true;
            remaining = max;
            cycles = 0;
            return;
        }

        int elapsed = progress - slot.counterBaseline;
        cycles = elapsed / max;

        if (cycles > 0)
        {
            // Advance by whole blocks when overflowing, so the leftover survives into the next one;
            // jump the baseline all the way to progress when not, which throws that leftover away.
            slot.counterBaseline = overflow ? slot.counterBaseline + (cycles * max) : progress;
        }

        remaining = max - (progress - slot.counterBaseline);
    }

    /// <summary>Pops the icon when its passive fires. No-op for any passive this view isn't rendering.</summary>
    public void PlayProcFlash(HeroPassiveSO passive)
    {
        if (passive == null) return;

        for (int i = 0; i < _slots.Count; i++)
        {
            Slot slot = _slots[i];
            if (slot.passive != passive || slot.indicator == null) continue;

            // A stacking counter punches from its own ring animation, at the frame the ring empties.
            // Punching here too would fire twice per proc — once on impact, once when the ring crosses
            // zero — and would punch even on a hit that advanced the count without completing a stack.
            // Deferring to the ring means the pop lands exactly when the bonus does, and only then.
            if (slot.lastDisplay.type == PassiveIndicatorType.Counter &&
                passive.counterSource == PassiveCounterSource.HealthLostToNextStack)
                return;

            slot.indicator.PlayProcFlash();
            return;
        }
    }

    /// <summary>The indicator for the (index+1)th extra passive, cloned off the attached one on first use.</summary>
    private HeroPassiveIndicator CloneIndicator(int index)
    {
        while (_clones.Count <= index)
        {
            HeroPassiveIndicator clone = Instantiate(indicator, indicator.transform.parent);
            clone.name = $"{indicator.name}_{_clones.Count + 1}";
            clone.gameObject.SetActive(false);
            _clones.Add(clone);
        }

        return _clones[index];
    }

    /// <summary>
    /// Lines the bound indicators up from the authored spot outward (leftward), so a lone passive sits
    /// exactly where it always did and extras never creep over the hero frame to the right.
    /// </summary>
    private void LayoutSlots()
    {
        var baseRect = indicator.transform as RectTransform;
        if (baseRect == null) return;

        if (!_hasBasePosition)
        {
            _basePosition = baseRect.anchoredPosition;
            _hasBasePosition = true;
        }

        for (int i = 0; i < _slots.Count; i++)
        {
            var rect = _slots[i].indicator.transform as RectTransform;
            if (rect == null) continue;

            rect.anchoredPosition = _basePosition - new Vector2(i * SlotSpacing, 0f);
        }
    }

    private void DestroyClones()
    {
        for (int i = 0; i < _clones.Count; i++)
            if (_clones[i] != null) Destroy(_clones[i].gameObject);
        _clones.Clear();
    }

    private void Hide()
    {
        if (indicator != null) indicator.gameObject.SetActive(false);
        for (int i = 0; i < _clones.Count; i++)
            if (_clones[i] != null) _clones[i].gameObject.SetActive(false);

        // Drop the slots and their baselines too: a rebind is a different hero (or a reloaded scene),
        // and carrying a stale reset point forward would make the first Refresh animate a phantom proc.
        _slots.Clear();
        _runtime = null;
    }
}
