using DG.Tweening;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// How a hit arrived, so the feedback for it can differ. Carried from the caller of
/// <see cref="MinionController.TakeDamage"/> to the moment the damage number appears, which is where
/// the audio rides -- see MinionView.DamageShown.
/// </summary>
public enum DamageSource
{
    /// <summary>Anything that is not a minion's attack: spells, collisions, the empty-deck clock.</summary>
    Effect = 0,

    /// <summary>A range 1 attacker's strike (its slash animation), including a melee counter-attack.</summary>
    Melee = 1,

    /// <summary>A range 2+ attacker's shot (its arrow animation), including a ranged counter-attack.</summary>
    Ranged = 2
}

public class MinionController : MonoBehaviour
{
    public SelectableEntity selectable;
    public CardModal modal;
    //public MinionModal modal;
    //public ShowInfo showInfo;

    public MinionView view;
    public CardSO card;
    public GridEntity gridEntity;
    public Animator animator;
    public MinionAnimationController animationController;
    public Agent owner;

    public SelectionType selectionType;

    public Vector3Int plannedMoveDir = Vector3Int.zero;
    public bool isMovementValidated = false;
    public int attacksMadeThisTurn = 0;
    public bool canAttack = false;
    public int age = 0;

    public MinionController LastTarget;

    [Header("Attack Ready Indicator")]
    // The sword icon shown on a minion while it still has its attack this turn. It reflects attack
    // *eligibility* only: it stays lit even when no enemy is currently in range (the minion is still
    // "ready", it just has nothing to hit yet). Being selectable/clickable still requires a target.
    public SpriteRenderer attackHighlight;
    // Source of truth for whether the sword indicator should rest lit (set by SetReadyToAttack). Transient
    // overlays that share the minion's face — the move-preview arrow, attack-target selection — hide the
    // sword renderer without clearing this, and RefreshAttackHighlight() restores it from this flag when
    // the overlay goes away.
    private bool _attackReady;

    [Header("In-Range Indicator")]
    // Lit on this unit while an ENEMY unit is hovered and this unit sits inside that unit's attack range,
    // so hovering anything reads out what it threatens. Purely range-based — it says nothing about whether
    // the hovered unit still has its attack this turn (that's the sword indicator above). Driven entirely
    // by MinionRangeHandler.ShowTargetsInRange / HideTargetsInRange; nothing here turns it on by itself.
    public SpriteRenderer inRangeIndicator;

    [Header("Move Preview")]
    public SpriteRenderer moveArrow;
    public Color moveArrowColor = Color.white;
    public Color moveArrowCollideColor = Color.yellow;
    public Color moveArrowCrippledColor = Color.red;

    [Header("Attack Preview")]
    // Shown while this minion is hovered as an attack target and the pending attacker's strike would kill it.
    public GameObject skull;
    public float skullBreathMinAlpha = 0.25f;
    public float skullBreathDuration = 0.6f;
    private SpriteRenderer _skullRenderer;
    private Color _skullBaseColor;
    private bool _skullResolved;
    private Tween _skullTween;

    [Header("Stat Change Preview")]
    // Shown while this minion is hovered as a card target and the card would change its stats WITHOUT
    // killing it (a buff, or a non-lethal debuff). Two separate labels sit above the minion's attack and
    // health numbers, each tinted for its stat (attack yellow, health red) and breathing alpha in sync.
    // The skull owns the lethal case, so the skull and these labels are never visible at once.
    public TMP_Text statChangeAttackText;
    public TMP_Text statChangeHealthText;
    // The background containers that hold each label. These are what we actually show/hide and breathe (the
    // text stays active inside), so the label's background image toggles and fades along with the number.
    public CanvasGroup statChangeAttackGroup;
    public CanvasGroup statChangeHealthGroup;
    // The number's color encodes the direction of the change: green for a buff, red for a debuff. (Which
    // stat changed is already clear from position — each label sits above its own attack/health number.)
    public Color statBuffColor = new Color(0.25f, 0.9f, 0.3f, 1f);  // green
    public Color statDebuffColor = new Color(1f, 0.3f, 0.3f, 1f);   // red
    public float statChangeBreathMinAlpha = 0.25f;
    public float statChangeBreathDuration = 0.6f;
    private Tween _statChangeTween;
    // The background image behind each number (the container's Graphic) is what breathes; the number text
    // itself stays solid so the value is always readable. Cached (with its authored color) on first show.
    private Graphic _statChangeAttackBg;
    private Graphic _statChangeHealthBg;
    private Color _statChangeAttackBgBase;
    private Color _statChangeHealthBgBase;
    private bool _statChangeBgResolved;

    [Header("Floating Text")]
    // Where status popups ("Crippled", ...) start, relative to this unit's centre. The label rises from
    // here and fades; everything else about how it looks lives in the FloatingTextConfig style it's given.
    public Vector3 floatingTextOffset = Vector3.zero;

    public SelectionType SelectionType { get => SelectionType.Minion; }
    // Set by GameManager the moment it queues this minion's OnDeath trigger, cleared once that trigger has
    // finished resolving. While it is up, DestroySelf holds off — see the comment there.
    public bool deathTriggerPending;
    private bool _destroyDeferred;
    /// <summary>The death animation has played out (the corpse is gone from view, even if the object lingers).</summary>
    public bool DeathAnimationDone { get; private set; }
    public static event Action<List<MinionController>> OnSelectingMinionForAttack;
    public static event Action<MinionController> OnDied;
    public static event Action<MinionController, MinionController> OnCollided;
    public static event Action<MinionController, int> OnTookDamage;

    /// <summary>
    /// This minion's strike landing on <c>target</c>, raised once per attack after the damage is dealt
    /// and alongside the attacker's own swing/shot animation, so a listener lands with the visual.
    /// Purely additive (audio); the counter-attack is the defender's own strike and is NOT reported here,
    /// since it plays its animation through the defender rather than going back through Attack().
    /// </summary>
    public static event Action<MinionController, MinionController> OnAttacked;

    /// <summary>
    /// This minion changing cell, raised as the move tween starts. One event for every kind of move,
    /// because they all funnel through <see cref="Move"/>: walking under its own steam, and being shoved
    /// aside by a summon or a push card. Purely additive (audio).
    /// </summary>
    public static event Action<MinionController> OnMoved;


    private void OnEnable()
    {
        GameManager.OnTurnEnd += OnTurnSwitch;
        // React to the normal selection highlight turning on/off so the sword indicator can yield to it
        // (they must never light at once) and return when the selection clears.
        if (selectable != null) selectable.SelectableChanged += OnSelectableChanged;
    }

    private void OnDisable()
    {
        GameManager.OnTurnEnd -= OnTurnSwitch;
        if (selectable != null) selectable.SelectableChanged -= OnSelectableChanged;
        _skullTween?.Kill();
        _skullTween = null;
        _statChangeTween?.Kill();
        _statChangeTween = null;
    }

    private void OnSelectableChanged(bool isSelectable) => ApplyAttackHighlight();

    protected virtual void Start()
    {
        /*if(showInfo != null)
            showInfo.card = card;*/

        EnsureSummoningSicknessIndicator();
    }

    /// <summary>
    /// Gives this minion the looping "zzz" that marks it summoning-sick (see
    /// <see cref="SummoningSicknessIndicator"/>). Attached here rather than authored into the minion
    /// prefabs so every prefab and variant gets it from one place, and because summons don't go through
    /// <see cref="Initialize"/> — GameManager.SummonMinion builds the minion inline, so Start() is the
    /// only hook every summoned minion is guaranteed to run.
    ///
    /// Heroes are excluded by <see cref="HeroController"/> overriding Start() without calling base: they
    /// also start at age 0, but they were never summoned, so they must not snore through turn one. The
    /// indicator asserts that itself too, in case that override ever changes.
    ///
    /// An instance already present on the prefab (added by hand to retune the defaults) is reused rather
    /// than doubled up.
    /// </summary>
    private void EnsureSummoningSicknessIndicator()
    {
        var indicator = GetComponent<SummoningSicknessIndicator>();
        if (indicator == null) indicator = gameObject.AddComponent<SummoningSicknessIndicator>();

        indicator.Bind(this);
    }
    public void Initialize(Agent owner, bool isPlayerMinion)
    {
        modal.UpdateModal(card, owner, isPlayerMinion);
        view.UpdateView(modal);
        PlayAppearAnimation();
    }

    protected virtual void PlayAppearAnimation()
    {
        view.PlayAppearAnimation();
    }
    private void OnTurnSwitch(GameState curState)
    {
        age++;
        attacksMadeThisTurn = 0;
        isMovementValidated = false;

        // Clear the attack indicator on every turn end; it is re-lit for the player's minions at the
        // start of their turn via SetReadyToAttack.
        _attackReady = false;
        if (attackHighlight != null)
            attackHighlight.enabled = false;

    }

    protected virtual void OnMouseDown()
    {
        if (GameManager.Instance.currentState == GameState.EndGame)
            return;

        // While a cell pick is open, a click on this minion means "pick the cell I'm standing on" —
        // that is the only thing a click can mean then, and the minion's collider covers its cell
        // exactly (same size, same z, never disabled), so the pick between the two is a coin flip we
        // don't control. Forwarding makes the outcome identical either way; previously the half that
        // landed on the minion fell through every branch below and was silently swallowed.
        // Safe to test first: cell and minion selections mutually preempt (GridCellSelectionManager
        // .BeginSelection cancels SelectionManager and vice versa), so this can never steal a click
        // from an active attack request.
        if (GridCellSelectionManager.Instance != null && gridEntity != null &&
            GridCellSelectionManager.Instance.TryClickCellAt(gridEntity.GetGridIndex()))
            return;

        // Clicking the attacker again backs out of its own in-progress attack selection.
        if (SelectionManager.Instance.ActiveAttacker == this)
        {
            SelectionManager.Instance.Cancel();
            return;
        }

        // An active selection (spell-target or attack-target) consumes the click; only when nothing
        // is being selected does a click on an attack-ready minion begin a fresh attack.
        if (SelectionManager.Instance.TryResolveClick(this))
            return;

        // Don't let a click start an attack while a card is resolving.
        if (canAttack && !GameManager.Instance.isPlayingCard)
        {
            StartAttack(GameManager.Instance.opponent);
        }
    }
    protected virtual void OnMouseEnter()
    {
        if (GameManager.Instance.currentState == GameState.EndGame)
            return;

        GameManager.Instance.player.handManager.ShowInfoCard(card, modal);

        // Mirror of the forward in OnMouseDown: during a cell pick this hover belongs to the cell
        // underneath, so the area preview and its death skulls light exactly as if the player had
        // hovered the bare cell. Returning here also suppresses the range indicator below, which would
        // otherwise compete with the area preview for the same tiles.
        if (GridCellSelectionManager.Instance != null && gridEntity != null &&
            GridCellSelectionManager.Instance.TryHoverCellAt(gridEntity.GetGridIndex()))
            return;

        // A card being dragged owns the hover; don't react unless a selection request is active.
        if (DraggableItem.AnyCardDragging && !SelectionManager.Instance.HasActiveMinionRequest)
            return;

        // What hovering this minion means right now: an active selection request dictates it (attack pick,
        // push pick, generic pick); otherwise we're just inspecting, so show the range.
        HoverIntent intent = SelectionManager.Instance.HasActiveMinionRequest
            ? SelectionManager.Instance.ActiveIntent
            : HoverIntent.SeeRange;

        ApplyHoverIntent(intent);

        // Independently of the intent-specific feedback above, light the skull if picking this minion
        // right now would kill it (an attack strike, its retaliation, or a lethal damage spell). Guarded
        // by HasActiveMinionRequest so it never shows while merely inspecting range.
        if (SelectionManager.Instance.HasActiveMinionRequest)
            ShowDeathPreview();
    }

    // Central dispatch for hover feedback. Only the SeeRange and ToPush cases do something today; the
    // others are wired here so their behavior can be filled in later without touching OnMouseEnter.
    protected virtual void ApplyHoverIntent(HoverIntent intent)
    {
        switch (intent)
        {
            case HoverIntent.SeeRange:
                MinionRangeHandler.Instance.ShowRange(gridEntity.GetGridIndex(), modal.range, modal.isPlayerMinion);
                // Alongside the range tiles, mark the enemy units actually standing in that range.
                MinionRangeHandler.Instance.ShowTargetsInRange(this);
                break;
            case HoverIntent.ToPush:
                ShowPushArrow();
                break;
            case HoverIntent.ToAttack:
                // TODO: show weapon image
                break;
            case HoverIntent.ToSelectGenerally:
                // TODO: generic select feedback
                break;
        }
    }

    // The attacker whose retaliation skull we lit while this minion was hovered, so OnMouseExit can hide
    // it too (only the hovered target itself gets an OnMouseExit).
    private MinionController _previewedAttacker;

    // While this minion is hovered as a valid pick, light the skull on whatever the pick would kill. Two
    // sources: an attack (this minion takes the attacker's strike, and the attacker takes the retaliation
    // back if it is in this minion's range — so its skull can light from taking damage too), or a lethal
    // damage spell targeting this minion. Uses TakeDamage's armor math (damage after armor >= health means
    // dead). No health text is touched; the skull is the only feedback.
    protected void ShowDeathPreview()
    {
        var selection = SelectionManager.Instance;
        if (!selection.IsActiveTarget(this))
        {
            HideDeathPreview();
            return;
        }

        MinionController attacker = selection.ActiveAttacker;
        if (attacker != null)
        {
            int dmgToTarget = Mathf.Max(WeatherSystem.ModifyDamage(attacker.modal.attack) - modal.armor, 0);
            bool targetDies = dmgToTarget >= modal.health;
            SetSkull(targetDies);
            // Non-lethal hit: preview the health the target will lose (the skull owns the lethal case).
            SetHealthChangePreview(targetDies ? 0 : -dmgToTarget);

            // Retaliation: the attacker takes this minion's strike back only if it is in this minion's range
            // (mirrors Attack()'s RangeUtility.IsInRange(target, attacker) check).
            if (RangeUtility.IsInRange(this, attacker))
            {
                int dmgToAttacker = Mathf.Max(WeatherSystem.ModifyDamage(modal.attack) - attacker.modal.armor, 0);
                bool attackerDies = dmgToAttacker >= attacker.modal.health;
                attacker.SetSkull(attackerDies);
                attacker.SetHealthChangePreview(attackerDies ? 0 : -dmgToAttacker);
                _previewedAttacker = attacker;
            }
            return;
        }

        // Spell/card pick: a lethal card lights the skull; a non-lethal stat change shows the delta label.
        PreviewCardEffect(selection.ActiveCard);
    }

    // Death-preview entry points for area (cell-selection) spells, which preview every minion sitting
    // under the spell's hovered area rather than a single directly-hovered target. GridCellSelectionManager
    // calls these for each occupant of the previewed cells.
    public void ShowCardDeathPreview(CardSO sourceCard)
    {
        PreviewCardEffect(sourceCard);
    }

    public void HideCardDeathPreview()
    {
        SetSkull(false);
        HideStatChange();
    }

    // Card-hover feedback dispatch shared by single-target (ShowDeathPreview) and area (ShowCardDeathPreview)
    // previews: a card that would kill this minion lights the skull; otherwise, if it would change this
    // minion's stats, the delta label shows instead. The two are mutually exclusive.
    private void PreviewCardEffect(CardSO card)
    {
        bool lethal = card != null && WouldCardKill(card);
        SetSkull(lethal);
        SetStatChange(lethal ? null : card);
    }

    // Compute the net attack/health change this card would apply to this minion (armor mitigates a health
    // debuff, matching TakeDamage), then show the breathing label if anything actually changes. A null
    // card, or one that changes nothing, hides it.
    private void SetStatChange(CardSO card)
    {
        if (card == null) { HideStatChange(); return; }

        CardEffectInspector.StatDelta delta = CardEffectInspector.GetTargetedStatChange(card);
        int dA = delta.attack;
        int dH = delta.health;
        if (dH < 0) dH = -Mathf.Max(WeatherSystem.ModifyDamage(-dH) - modal.armor, 0); // weather + armor, like damage

        if (dA == 0 && dH == 0) { HideStatChange(); return; }

        ShowStatChange(dA, dH);
    }

    // Preview a pure health change on this minion (used by the attack hover: the damage a strike or its
    // retaliation would deal, passed as a negative delta). Zero hides the labels — e.g. a lethal hit, where
    // the skull owns the feedback instead.
    private void SetHealthChangePreview(int healthDelta)
    {
        if (healthDelta == 0) { HideStatChange(); return; }
        ShowStatChange(0, healthDelta);
    }

    // Fill each stat's label above its number (an unchanged stat's label stays hidden), then breathe the
    // labels' background images in sync for as long as the preview is up (mirrors the skull); the number
    // text stays solid. Resets alpha to full first so re-shows never inherit a mid-fade value.
    private void ShowStatChange(int dA, int dH)
    {
        ApplyStatDelta(statChangeAttackText, statChangeAttackGroup, dA);
        ApplyStatDelta(statChangeHealthText, statChangeHealthGroup, dH);
        ResolveStatChangeBgs();

        _statChangeTween?.Kill();
        SetStatChangeAlpha(1f);
        _statChangeTween = DOTween
            .To(() => 1f, SetStatChangeAlpha, statChangeBreathMinAlpha, statChangeBreathDuration)
            .SetLoops(-1, LoopType.Yoyo)
            .SetEase(Ease.InOutSine);
    }

    // Show one stat's delta ("+2" / "-1"), or hide it when unchanged. The number is tinted by direction
    // (green buff / red debuff); the background image keeps its authored color. The background container is
    // what toggles (its CanvasGroup if wired, else the text's parent), so the label's background shows/hides
    // together with the number; the text just carries the value.
    private void ApplyStatDelta(TMP_Text text, CanvasGroup group, int delta)
    {
        GameObject container = ContainerOf(text, group);
        if (container == null) return;

        if (delta == 0) { container.SetActive(false); return; }

        if (text != null)
        {
            text.color = delta > 0 ? statBuffColor : statDebuffColor;
            text.text = delta.ToString("+0;-0");
        }

        container.SetActive(true);
    }

    // The object we show/hide (and breathe) for a label: its background container — the wired CanvasGroup,
    // else the text's parent — falling back to the text itself if it has no parent.
    private static GameObject ContainerOf(TMP_Text text, CanvasGroup group)
    {
        if (group != null) return group.gameObject;
        if (text == null) return null;
        return text.transform.parent != null ? text.transform.parent.gameObject : text.gameObject;
    }

    // Breathe only the labels' background images, fading each between its authored alpha (a == 1) and
    // statChangeBreathMinAlpha of it (mirrors the skull/move-arrow scaling). The number text is untouched.
    private void SetStatChangeAlpha(float a)
    {
        FadeBg(_statChangeAttackBg, _statChangeAttackBgBase, a);
        FadeBg(_statChangeHealthBg, _statChangeHealthBgBase, a);
    }

    private static void FadeBg(Graphic bg, Color baseColor, float a)
    {
        if (bg == null) return;
        Color c = baseColor;
        c.a = baseColor.a * a;
        bg.color = c;
    }

    // Cache each label's background Graphic (the container's Image) and its authored color, so breathing
    // fades relative to that base rather than snapping to a flat alpha. Resolved once; containers are fixed.
    private void ResolveStatChangeBgs()
    {
        if (_statChangeBgResolved) return;

        _statChangeAttackBg = BgOf(statChangeAttackText, statChangeAttackGroup);
        if (_statChangeAttackBg != null) _statChangeAttackBgBase = _statChangeAttackBg.color;

        _statChangeHealthBg = BgOf(statChangeHealthText, statChangeHealthGroup);
        if (_statChangeHealthBg != null) _statChangeHealthBgBase = _statChangeHealthBg.color;

        _statChangeBgResolved = true;
    }

    private static Graphic BgOf(TMP_Text text, CanvasGroup group)
    {
        GameObject container = ContainerOf(text, group);
        return container != null ? container.GetComponent<Graphic>() : null;
    }

    private void HideStatChange()
    {
        _statChangeTween?.Kill();
        _statChangeTween = null;

        GameObject atk = ContainerOf(statChangeAttackText, statChangeAttackGroup);
        if (atk != null) atk.SetActive(false);
        GameObject hp = ContainerOf(statChangeHealthText, statChangeHealthGroup);
        if (hp != null) hp.SetActive(false);
    }

    // Show/hide the skull, breathing its alpha up and down for as long as it stays shown (mirrors the move
    // arrow). Enabling resets the alpha to full first so repeated shows never drift dimmer; disabling kills
    // the tween. Idempotent when already in the requested state.
    private void SetSkull(bool willDie)
    {
        if (skull == null) return;

        if (!willDie)
        {
            _skullTween?.Kill();
            _skullTween = null;
            skull.SetActive(false);
            return;
        }

        if (skull.activeSelf && _skullTween != null) return; // already breathing

        ResolveSkullRenderer();
        skull.SetActive(true);
        if (_skullRenderer == null) return;

        _skullRenderer.color = _skullBaseColor; // reset to full so re-shows don't inherit a mid-fade alpha
        _skullTween?.Kill();
        _skullTween = _skullRenderer
            .DOFade(skullBreathMinAlpha * _skullBaseColor.a, skullBreathDuration)
            .SetLoops(-1, LoopType.Yoyo)
            .SetEase(Ease.InOutSine);
    }

    private void ResolveSkullRenderer()
    {
        if (_skullResolved) return;
        _skullResolved = true;
        _skullRenderer = skull.GetComponent<SpriteRenderer>();
        if (_skullRenderer != null) _skullBaseColor = _skullRenderer.color;
    }

    // True if playing sourceCard on this minion would kill it. A destroy/banish removes it outright;
    // otherwise the card's OnPlay damage (read from its actual serialized calls, not the AI hints, which
    // are often unset) is applied through this minion's armor to match TakeDamage.
    private bool WouldCardKill(CardSO sourceCard)
    {
        if (sourceCard.aiRemovesTarget) return true;

        int damage = CardEffectInspector.GetTargetedDamage(sourceCard);
        if (damage <= 0) return false;

        int effectiveDamage = Mathf.Max(WeatherSystem.ModifyDamage(damage) - modal.armor, 0);
        return effectiveDamage >= modal.health;
    }

    private void HideDeathPreview()
    {
        SetSkull(false);
        HideStatChange();

        if (_previewedAttacker != null)
        {
            _previewedAttacker.SetSkull(false);
            _previewedAttacker.HideStatChange();
            _previewedAttacker = null;
        }
    }

    /// <summary>
    /// Also the cleanup path when <see cref="BoardInteractionGate"/> gates the board while this minion
    /// is hovered: masking the camera makes Unity's mouse raycast miss, and it delivers this exit as if
    /// the pointer had left. Nothing extra is needed to stop a hover surviving underneath an overlay.
    /// </summary>
    protected void OnMouseExit()
    {
        GameManager.Instance.player.handManager.HideInfoCard();

        // Tear the area preview back down for the same reason OnMouseEnter raised it — otherwise it
        // stays lit after the cursor leaves a minion that was standing on a previewed cell.
        bool inCellPick = GridCellSelectionManager.Instance != null &&
                          GridCellSelectionManager.Instance.HasActiveSession;

        if (inCellPick && gridEntity != null)
            GridCellSelectionManager.Instance.TryHoverExitCellAt(gridEntity.GetGridIndex());

        MinionRangeHandler.Instance.HideRange();
        MinionRangeHandler.Instance.HideTargetsInRange();
        HideMoveArrow();

        // During a cell pick the skull on this minion belongs to the AREA preview, not to this hover:
        // OnMouseEnter returned before ShowDeathPreview, so there is nothing of ours to clear here, and
        // GridCellSelectionManager hides every skull it lit. Clearing anyway wiped a skull the NEXT
        // cell's preview had already lit, since Unity runs that cell's enter before this exit — the same
        // clobber _hoverOriginIndex guards against on the cell side.
        if (!inCellPick) HideDeathPreview();
    }

    private void OnDrawGizmosSelected()
    {
        int range = modal != null ? modal.range : (card != null ? card.range : 0);
        if (range <= 0) return;

#if UNITY_EDITOR
        UnityEditor.Handles.color = Color.cyan;
        Vector3 size = new Vector3(0.9f, 0.9f, 0.1f);
        foreach (var off in RangeUtility.RangeOffsets(range))
        {
            // grid offset (dx,dy) -> world offset (dx,-dy); cellSize is 1
            Vector3 cell = transform.position + new Vector3(off.x, -off.y, 0f);
            DrawThickWireCube(cell, size, 4f);
        }
#endif
    }

#if UNITY_EDITOR
    private static void DrawThickWireCube(Vector3 center, Vector3 size, float thickness)
    {
        Vector3 e = size * 0.5f;
        Vector3 a = center + new Vector3(-e.x, -e.y, -e.z);
        Vector3 b = center + new Vector3( e.x, -e.y, -e.z);
        Vector3 c = center + new Vector3( e.x,  e.y, -e.z);
        Vector3 d = center + new Vector3(-e.x,  e.y, -e.z);
        Vector3 a2 = center + new Vector3(-e.x, -e.y,  e.z);
        Vector3 b2 = center + new Vector3( e.x, -e.y,  e.z);
        Vector3 c2 = center + new Vector3( e.x,  e.y,  e.z);
        Vector3 d2 = center + new Vector3(-e.x,  e.y,  e.z);

        UnityEditor.Handles.DrawLine(a, b, thickness);
        UnityEditor.Handles.DrawLine(b, c, thickness);
        UnityEditor.Handles.DrawLine(c, d, thickness);
        UnityEditor.Handles.DrawLine(d, a, thickness);

        UnityEditor.Handles.DrawLine(a2, b2, thickness);
        UnityEditor.Handles.DrawLine(b2, c2, thickness);
        UnityEditor.Handles.DrawLine(c2, d2, thickness);
        UnityEditor.Handles.DrawLine(d2, a2, thickness);

        UnityEditor.Handles.DrawLine(a, a2, thickness);
        UnityEditor.Handles.DrawLine(b, b2, thickness);
        UnityEditor.Handles.DrawLine(c, c2, thickness);
        UnityEditor.Handles.DrawLine(d, d2, thickness);
    }
#endif

    // Called when this minion changes owner mid-turn (e.g. Exorcist's steal). Strip every scrap of
    // attack eligibility and clickability it carried from its previous owner: otherwise the losing
    // player keeps a clickable, "attack-ready" minion (canAttack stays true, collider stays enabled,
    // the sword indicator stays lit) and can command a minion that now belongs to the enemy. The new
    // owner re-lights it on their own turn via SetReadyToAttack. isAttackedThisTurn is forced so it
    // also can't attack for its new owner this turn (summoning-sickness on a fresh steal).
    public void ClearAttackReadiness()
    {
        canAttack = false;
        attacksMadeThisTurn = modal.attacksPerTurn;
        _attackReady = false;

        if (attackHighlight != null)
            attackHighlight.enabled = false;

        if (selectable != null)
            selectable.SetSelectable(false);
    }

    public virtual void SetReadyToAttack()
    {
        // Eligibility: does this minion still have its attack this turn? This is independent of whether
        // a target is currently in range.
        bool eligible = modal.canAttack && modal.canAttackManually && attacksMadeThisTurn < modal.attacksPerTurn && age >= 1;

        if (!eligible)
        {
            canAttack = false;
        }
        else{
            List<MinionController> targets = new List<MinionController>();
            targets.AddRange(GameManager.Instance.opponent.minions);
            targets.Add(GameManager.Instance.opponent.hero);
            bool targetExists = false;

            foreach (var minion in targets)
            {
                if (RangeUtility.IsInRange(this, minion))
                {
                    targetExists = true;
                    break;
                }
            }

            // canAttack (selectable/clickable) still requires a target to be in range.
            canAttack = targetExists;

        }

        // The attack highlight reflects eligibility, not range: it stays lit whenever the minion can
        // still attack this turn, even if there is no enemy in range to hit yet.
        _attackReady = eligible;
        ApplyAttackHighlight();

        // An attack-ready minion shows the attack highlight (above), NOT the normal selection highlight,
        // yet must stay clickable so the player can start its attack. The normal highlight is reserved
        // for other selection flows (spell / push / attack-target picking driven by SelectionManager).
        //showInfo.gameObject.SetActive(!canAttack);
        if (canAttack)
            selectable.SetClickableWithoutHighlight();
        else
            selectable.SetSelectable(false);

    }
    // Show/hide the "in range of the hovered unit" marker. Called on the TARGET, not on the hovered unit,
    // so MinionRangeHandler owns the bookkeeping of which units are currently marked.
    public void SetInRangeIndicator(bool inRange)
    {
        if (inRangeIndicator != null)
            inRangeIndicator.enabled = inRange;
    }

    // Force the attack indicator off for a transient overlay (attack-target selection, move-preview
    // arrow) without clearing _attackReady, so RefreshAttackHighlight() / SetReadyToAttack can restore it.
    public void HideAttackHighlight()
    {
        if (attackHighlight != null)
            attackHighlight.enabled = false;
    }

    // Restore the attack indicator to its resting state after an overlay that hid it goes away.
    public void RefreshAttackHighlight()
    {
        ApplyAttackHighlight();
    }

    // The sword indicator and the normal selection highlight are mutually exclusive: whenever the minion
    // is lit as a selection target (a spell / attack-target pick driven by SelectionManager), the sword
    // yields, and it returns once that highlight clears. So it shows only when the minion rests
    // attack-ready AND is not currently highlighted as a selectable target.
    private void ApplyAttackHighlight()
    {
        if (attackHighlight != null)
            attackHighlight.enabled = _attackReady && (selectable == null || !selectable.IsSelectable);
    }

    public virtual bool CanAttack(Agent opponent)
    {
        if (!modal.canAttack || !modal.canAttackManually || attacksMadeThisTurn >= modal.attacksPerTurn || age == 0) return false;

        //var grid = GridManager.Instance.GetGrid();
        List<MinionController> targets = new List<MinionController>();
        targets.AddRange(opponent.minions);
        targets.Add(opponent.hero);
        foreach (var minion in targets)
        {
            if(RangeUtility.IsInRange(this, minion))
            {
                return true;
            }
        }
        return false;
    }

    public void StartAttack(Agent opponent, MinionController target = null, bool noCounter = false)
    {
        StartCoroutine(Attack(opponent, target, noCounter));
    }

    public virtual IEnumerator Attack(Agent opponent, MinionController target = null, bool noCounter = false)
    {
        Debug.Log("starting Attack");

        // Cards flagged canAttack = false can never attack (manual selection or triggered/auto attacks).
        if (!modal.canAttack) yield break;

        List<MinionController> selectableminions = new List<MinionController>();
        List<MinionController> targets = new List<MinionController>();
        targets.AddRange(opponent.minions);
        targets.Add(opponent.hero);
        foreach (var minion in targets)
        {
            if (RangeUtility.IsInRange(this, minion))
            {
                selectableminions.Add(minion);
            }
        }

        Debug.Log("selectiable minion count: " + selectableminions.Count);

        MinionController chosen = null;

        if (target != null)
        {
            // Triggered / AI attack: the target is already decided, so bypass the interactive
            // selection request entirely (no highlights, no wait).
            chosen = target;
        }
        else
        {
            if (selectableminions.Count == 0) yield break; // nothing in range — don't open a dangling selection

            // Interactive attack: the SelectionManager lights the valid targets and routes the click.
            SelectionManager.Instance.BeginAttackRequest(this, selectableminions, picked => chosen = picked);
            OnSelectingMinionForAttack?.Invoke(selectableminions);

            while (chosen == null && SelectionManager.Instance.HasActiveMinionRequest && !GameManager.Instance.isTesting)
            {
                yield return null;
            }

            if (chosen == null)
            {
                // Cancelled (right-click / attacker re-click / spell preempt / turn end).
                SelectionManager.Instance.Cancel();
                yield break;
            }
        }

        Debug.Log("damaging minion");

        Vector3 dir = (chosen.transform.position - transform.position).normalized;
        // Snapshot the defender's attack BEFORE the strike lands, because the counter-attack below must
        // retaliate with the value the player committed the trade against. TakeDamage dispatches the
        // defender's took-damage triggers synchronously — outside a card play _executingTriggeredActions
        // is false, so EnqueueTriggeredAction invokes inline, and _ScaleAttackWithHealthLost mutates
        // modal.attack before its first yield — so a passive like the Berserker's Raging Blood ("+1 Attack
        // per 4 Health lost") has already raised the defender's attack by the time control returns here.
        // Reading it live let a 2-attack Berserker counter for 3 on the very blow that pushed it over the
        // threshold, killing a 3 HP attacker the player had every reason to expect would survive.
        int counterAttack = chosen.modal.attack;
        // Lunge plays 20% faster (duration / 1.2).
        transform.DOPunchPosition(dir*0.2f, 0.5f / 1.2f, vibrato: 1).SetEase(Ease.InOutBack).SetDelay(0.5f);
        chosen.TakeDamage(modal.attack, modal.range >= 2 ? DamageSource.Ranged : DamageSource.Melee);
        chosen.transform.DOPunchPosition(dir * 0.03f, 0.15f, vibrato: 5).SetDelay(0.75f);

        // A struck hero may carry a passive that cancels its counter-attack (e.g. the defensive summoner).
        // noCounter is set by triggered "reaction" shots (e.g. the Repaired Crossbow's auto-attack) that
        // fire without exposing the attacker to retaliation.
        bool chosenIsHero = chosen.owner != null && chosen == chosen.owner.hero;
        bool suppressCounter = noCounter || (chosenIsHero && GameManager.Instance.HeroSuppressesCounterAttack(chosen));

        if (RangeUtility.IsInRange(chosen, this) && !suppressCounter) // target retaliates if attacker is in ITS range
        {
            // The counter is the DEFENDER's strike, so its reach decides the sound, not this minion's.
            TakeDamage(counterAttack, chosen.modal.range >= 2 ? DamageSource.Ranged : DamageSource.Melee);
            // Counter-attack is delayed an extra 0.25s so it reads as a response to the strike
            // rather than overlapping it.
            if (chosen.modal.range < 2)
            {
                StartCoroutine(chosen.animationController.PlaySlashAnimation(-dir, 0.75f));
            }
            else
            {
                StartCoroutine(chosen.animationController.PlayArrowAnimation(-dir, transform.position, 0.9f, animationController.PlayArrowHitAnimation));
            }
        }

        // A minion strike on a hero is an "attack" — distinct from the took-damage a spell deals — so it
        // drives the HeroAttacked passive trigger. Skip if the strike was lethal: the hero is dying and
        // GameManager.CheckWinCondition owns the game-over, so a reinforcement summon would be pointless.
        if (chosenIsHero && chosen.modal.health > 0)
            GameManager.Instance.OnHeroAttacked(chosen, this);

        if(modal.range < 2)
        {
            StartCoroutine(animationController.PlaySlashAnimation(dir, 0.5f));
        }
        else
        {
            StartCoroutine(animationController.PlayArrowAnimation(dir, chosen.transform.position, 0.65f, chosen.animationController.PlayArrowHitAnimation));
        }

        OnAttacked?.Invoke(this, chosen);
        LastTarget = chosen;
        attacksMadeThisTurn++;
        canAttack = false;

        // Tear down the selection (de-highlights every lit target) now that the attack resolved, then
        // restore resting attack-readiness for the remaining minions. State is set above first so this
        // minion is correctly recomputed as no-longer-attackable.
        SelectionManager.Instance.Complete();
        GameManager.Instance.SetPlayerMinionsReadyToAttack();

        yield break;
    }
    /// <param name="source">
    /// What kind of hit this is. Defaults to <see cref="DamageSource.Effect"/> so the many spell and
    /// effect call sites need no change -- only an attack knows it is one, and only the two call sites
    /// inside <see cref="Attack"/> say so.
    /// </param>
    public bool TakeDamage(int damage, DamageSource source = DamageSource.Effect)
    {
        // Weather scales the raw hit before armor (Blood Moon doubles it).
        damage = WeatherSystem.ModifyDamage(damage);
        int effectiveDamage = Mathf.Max(damage - modal.armor, 0);
        modal.health -= effectiveDamage;

        // UpdateView drives the damage indicator itself now: it sees health drop and plays it, so every
        // source of health loss (not just combat damage) shows a number without each one wiring it up.
        // Pushed immediately, NOT on a delay: the view reads the delta between successive UpdateView
        // calls to tell damage from a buff, so deferring the call itself would let a heal landing in the
        // gap be misread as a loss (and would merge two quick hits into one number). The visual lag that
        // syncs the number with the strike lives in MinionView.damageIndicatorVisualDelay instead.
        //
        // The source is latched immediately before the push and consumed by the loss it describes, both
        // inside this one synchronous call -- so no other UpdateView can slip between and pick up the
        // wrong kind of hit.
        view.nextDamageSource = source;
        view.UpdateView(modal);
        Debug.Log("minion take damage: " + modal.name);

        if (modal.health <= 0)
        {
            // Lethal hit: fire OnTookDamage for the killing blow FIRST, then die, so "whenever this takes
            // damage" effects (e.g. Blessing Touch) still resolve on the fatal hit instead of being
            // silently skipped. This is safe because the took-damage and death triggered actions are
            // serialized by the scheduler: EnqueueTriggeredAction defers the death behind the took-damage
            // action (we're now mid-triggered-action), and FinishTriggeredAction runs in each action's
            // finally OUTSIDE its ActionHolder scope — so the death trigger takes a fresh selection
            // snapshot AFTER the took-damage scope restores, never clobbering its own OnDeath state.
            Debug.Log("minion died: " + modal.name);
            if (effectiveDamage > 0)
            {
                OnTookDamage?.Invoke(this, effectiveDamage);
            }
            Die();
            return true;
        }

        if (effectiveDamage > 0)
        {
            Debug.Log("on took damage invoke");
            OnTookDamage?.Invoke(this, effectiveDamage);
        }

        return false;
    }

    protected virtual void Die()
    {
        OnDied?.Invoke(this);
        selectable.SetSelectable(false);
        // Detach (not just remove) from the grid: the GameObject lingers to play its death animation,
        // so it must also stop re-registering on GridChanged, or its stale reference reclaims the cell
        // a subsequent mover advances into — corrupting occupancy (e.g. the enemy then summons on top).
        gridEntity.DetachFromGrid();
        // Remove from the ACTUAL owner's roster, not whoever's turn it currently is. A minion that
        // dies on the other agent's turn would otherwise be removed from the wrong list and linger as
        // a destroyed reference, later crashing range/targeting code that walks owner.minions.
        if (owner != null)
        {
            owner.minions.Remove(this);
        }
        else
        {
            GameManager.Instance.player.minions.Remove(this);
            GameManager.Instance.opponent.minions.Remove(this);
        }
        Invoke("PlayDeathAnimation", 1f);

        GridManager.Instance.GetCell(transform.position).cellObj.GetComponent<CellController>().AddToMinionsDiedHere(card, modal.isPlayerMinion);
    }

    protected virtual void PlayDeathAnimation()
    {
        //view.FadeOutArtImage(1.35f);
        animator.enabled = true;
        animator.speed = 3.34f;
        if((modal.range == 1  || modal.range == 1) && modal.defHealth < 5)
        {
            animator.Play("MeleeBronzeDeath");
        }
        else if ((modal.range == 1 || modal.range == 1) && modal.defHealth < 10)
        {
            animator.Play("MeleeSilverDeath");
        }
        else if ((modal.range == 1 || modal.range == 1) && modal.defHealth < 15)
        {
            animator.Play("MeleeGoldDeath");
        }
        else if (modal.range == 2 && modal.defHealth < 5)
        {
            animator.Play("RangedBronzeDeath");
        }
        else if (modal.range == 2 && modal.defHealth < 10)
        {
            animator.Play("RangedSilverDeath");
        }
        else if (modal.range == 2 && modal.defHealth < 15)
        {
            animator.Play("RangedGoldDeath");
        }
    }
    // Animation event at the end of the death clip.
    public void DestroySelf()
    {
        DeathAnimationDone = true;

        // The death animation runs on a wall clock (Invoke in Die() + clip length) that is completely
        // independent of the triggered-action scheduler, which defers this minion's own OnDeath behind the
        // play that killed it. A column-clearing spell that kills two minions spends a full second per
        // death inside _ChangeMinionHealth, so the clip routinely ends first. Destroying here would leave
        // ActionHolder.thisMinion pointing at a destroyed object, and Unity's overloaded == reports that as
        // null: every `thisMinion != null` check in the deathrattle then takes its turn-relative fallback
        // and the effect resolves for whoever's turn it is instead of the dead minion's owner (Blessed Seed
        // buffing the caster's minions when Ray of Ruin killed it). Wait for the trigger to resolve; the
        // object is already detached from the grid and off its owner's roster, so lingering is harmless.
        if (deathTriggerPending)
        {
            _destroyDeferred = true;
            HideCorpse();
            return;
        }
        Destroy(gameObject);
    }

    /// <summary>
    /// Retire the visuals at the exact moment a non-deferred death would have destroyed the object.
    /// The death clips loop and their states have no exit transition, so the DestroySelf event above is
    /// the ONLY thing that ever ends one: holding the destroy without this would send the animator back
    /// to frame one and replay the whole death — art fade included — on repeat for as long as the hold
    /// lasts. Spell kills always take that path (a card play owns the triggered-action lock for its whole
    /// resolution, so the OnDeath trigger can't run until the card is done, long after the ~0.3s clip
    /// ends), which is why looping corpses only ever showed up on spells. Disabling the animator also
    /// stops further DestroySelf/FadeOutArtImage events, so this runs once. The GameObject itself stays
    /// alive and active — it is still ActionHolder.thisMinion for the pending deathrattle.
    /// </summary>
    private void HideCorpse()
    {
        if (animator != null) animator.enabled = false;

        // Renderers cover the sprites and world-space TMP; Canvases cover the UI-space stat labels,
        // which draw through a CanvasRenderer instead. Includes inactive children so nothing that gets
        // re-enabled later (e.g. a skull left mid-preview) can pop back into view.
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.enabled = false;
        foreach (var c in GetComponentsInChildren<Canvas>(true))
            c.enabled = false;
    }

    /// <summary>
    /// Called by GameManager once this minion's OnDeath trigger has fully resolved. Releases the hold
    /// DestroySelf takes above, and completes the destruction if the death clip already asked for it.
    /// </summary>
    public void OnDeathTriggerResolved()
    {
        deathTriggerPending = false;
        if (_destroyDeferred)
        {
            _destroyDeferred = false;
            Destroy(gameObject);
        }
    }
    public virtual void Move(Vector3Int pos)
    {
        gridEntity.WorldPos = pos;

        // A move is authoritative over this transform's position, so cancel any position tween still
        // running on it before starting the DOMove. Without this, a minion relocated mid-animation — e.g.
        // the attacker of a summon-on-attacked hero, pushed to make room for the reinforcement while its
        // own attack lunge (DOPunchPosition) is still in flight — has that lunge fight and then override
        // the push, snapping it back onto the newly-summoned minion. Only position tweens target this
        // transform (scale/appear live on the child MinionView), so killing them here is safe. complete:
        // false leaves it at its current spot so the DOMove interpolates smoothly from there.
        transform.DOKill(false);

        // Recompute attack-readiness once the move finishes: a changed position changes which targets
        // are in range (both for this minion and for any minion that could reach it). We hook OnComplete
        // because RangeUtility reads transform.position, which only reaches `pos` when the tween ends.
        transform.DOMove(pos, 0.25f).OnComplete(() =>
        {
            if (GameManager.Instance != null) GameManager.Instance.SetPlayerMinionsReadyToAttack();
            // Landed: the weather sees it arrive (the Overgrowth brambles bite here). Skipped if another
            // move has since sent it on, so only the cell it actually stopped on counts.
            if (Vector3Int.RoundToInt(gridEntity.WorldPos) == pos) WeatherSystem.NotifyMinionEnteredCell(this);
        });
        plannedMoveDir = Vector3Int.zero;
        isMovementValidated = false;

        OnMoved?.Invoke(this);

        GridManager.Instance.InvokeGridChanged();
    }

    // True if the cell one step along `pushDir` is in-grid and empty. `pushDir` is the summoning
    // agent's forward direction (up for the player, down for the opponent), so any occupant — friendly
    // or enemy — can be pushed out of the way as long as the cell ahead of it is free.
    // Used by spawn-on-occupied-cell logic: a start cell is only valid if its occupant can be pushed.
    public bool CanBePushedForward(Vector3Int pushDir)
    {
        Vector3Int target = Vector3Int.RoundToInt(gridEntity.WorldPos) + pushDir;
        Vector2Int idx = GridManager.Instance.PosToGridIndex(target);
        if (GridManager.Instance.IsOutSideOfGrid(idx)) return false;
        return GridManager.Instance.GetCell(idx).obj == null;
    }

    // Move this minion one cell along `pushDir`, bypassing age/canMove gating (Move() does no checks).
    public void PushForward(Vector3Int pushDir)
    {
        Vector3Int target = Vector3Int.RoundToInt(gridEntity.WorldPos) + pushDir;
        Move(target);
    }

    // Swap world positions with another minion in one grid refresh so neither slot is briefly vacant.
    public void SwapPositionsWith(MinionController other)
    {
        Vector3 posA = gridEntity.WorldPos;
        Vector3 posB = other.gridEntity.WorldPos;

        gridEntity.WorldPos = posB;
        other.gridEntity.WorldPos = posA;

        transform.DOMove(posB, 0.25f).OnComplete(() =>
        {
            if (GameManager.Instance != null) GameManager.Instance.SetPlayerMinionsReadyToAttack();
            WeatherSystem.NotifyMinionEnteredCell(this);
        });
        other.transform.DOMove(posA, 0.25f).OnComplete(() => WeatherSystem.NotifyMinionEnteredCell(other));

        plannedMoveDir = Vector3Int.zero;
        other.plannedMoveDir = Vector3Int.zero;

        GridManager.Instance.InvokeGridChanged();
    }

    public virtual void FailedMove(Vector3 dir, MinionController  collidedEntity = null)
    {
        Debug.Log("failed move");
        transform.DOPunchPosition(dir/20, 0.25f, vibrato: 0).SetEase(Ease.OutCubic);
        plannedMoveDir = Vector3Int.zero;
        isMovementValidated = false;

        if (collidedEntity != null) {
            OnCollided?.Invoke(this, collidedEntity);
        }
    }
    /// <summary>
    /// Floats a one-off message off this unit — "Crippled" when it loses its move, and anything else worth
    /// announcing on the board. <paramref name="styleId"/> names a preset in the FloatingTextConfig asset
    /// (colour, size, rise, timing); an unknown or empty id falls back to that config's default style.
    /// </summary>
    public void ShowFloatingText(string text, string styleId = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        FloatingTextManager.ShowOn(transform, text, styleId, floatingTextOffset);
    }

    // Movement-preview arrow shown while the player hovers the turn-switch button. The arrow points in
    // this minion's forward direction (up for the player, down for the opponent) and is enabled only when
    // the minion would actually try to advance next turn: white when the cell ahead is free (it will
    // move or will follow the column forward), yellow only when the cell ahead stays blocked, red when the
    // minion is crippled (canMove == false). It stays hidden when summoned this turn (age < 1) or facing
    // the board edge. Collision is resolved via CellAheadClears, which walks the column so a minion tucked
    // behind an ally that is itself advancing shows white (it follows into the vacated cell), and only a
    // real blocker (edge, wall, enemy, or a stuck ally) shows yellow.
    public void ShowMoveArrow()
    {
        if (moveArrow == null) return;

        if (age < 1)
        {
            HideMoveArrow();
            return;
        }

        if (!modal.canMove)
        {
            EnableMoveArrow(moveArrowCrippledColor);
            return;
        }

        Vector3Int dir = modal.isPlayerMinion ? Vector3Int.up : Vector3Int.down;
        Vector3Int dest = Vector3Int.RoundToInt(transform.position) + dir;
        Vector2Int idx = GridManager.Instance.PosToGridIndex(dest);

        if (GridManager.Instance.IsOutSideOfGrid(idx))
        {
            HideMoveArrow();
            return;
        }

        bool willCollide = !CellAheadClears(dir);
        EnableMoveArrow(willCollide);
    }

    // Whether the cell one step along `dir` will be empty by the time this minion resolves its move
    // at turn end. The turn-end movement loops resolve minions front-first and each Move updates the
    // grid immediately, so an occupied cell isn't necessarily a collision: if the blocker is a
    // same-side minion that will itself advance, this minion follows it into the vacated cell (the
    // whole column shifts in one step). Only a permanent blocker keeps the cell filled — the board
    // edge, a wall/non-minion, an enemy (which doesn't move on this side's phase), or an ally that is
    // itself blocked. Walks the column recursively, matching the resolution order exactly.
    private bool CellAheadClears(Vector3Int dir)
    {
        Vector3Int dest = Vector3Int.RoundToInt(transform.position) + dir;
        Vector2Int idx = GridManager.Instance.PosToGridIndex(dest);

        if (GridManager.Instance.IsOutSideOfGrid(idx)) return false;

        GameObject occupant = GridManager.Instance.GetCell(idx).obj;
        if (occupant == null) return true;

        if (!occupant.TryGetComponent(out MinionController ahead)) return false;
        if (ahead.modal.isPlayerMinion != modal.isPlayerMinion) return false;
        if (!ahead.modal.canMove || ahead.age < 1) return false;

        return ahead.CellAheadClears(dir);
    }

    // Push preview shown while hovering a push card's candidate: the arrow always shows (the intent IS to
    // push this minion), pointing in the summoner's forward direction. White when the cell ahead is free
    // (the push lands), yellow when it's blocked by the board edge or an occupant (the push will fail /
    // collide). Ignores canMove/age because a push bypasses them. Pushes only happen on the player's turn,
    // so the direction is always up and the arrow needs no rotation.
    public void ShowPushArrow()
    {
        if (moveArrow == null) return;
        bool willCollide = !CanBePushedForward(ActionHolder.SummonerPushDir());
        EnableMoveArrow(willCollide);
    }

    // Enable the arrow in white/yellow for as long as it stays shown.
    private void EnableMoveArrow(bool willCollide)
    {
        EnableMoveArrow(willCollide ? moveArrowCollideColor : moveArrowColor);
    }

    private void EnableMoveArrow(Color c)
    {
        if (moveArrow == null) return;

        moveArrow.color = c;
        moveArrow.gameObject.SetActive(true);

        HideAttackHighlight();
    }

    public void HideMoveArrow()
    {
        if (moveArrow != null) moveArrow.gameObject.SetActive(false);

        // Bring the attack indicator back to its resting state now that the arrow is gone.
        RefreshAttackHighlight();
    }

    public virtual MoveInfo CanMove(Vector3Int pos)
    {
        var moveInfo = new MoveInfo();

        moveInfo.CanMove = false;
        moveInfo.CollidedEntity = null;

        if(!modal.canMove) return moveInfo;

        if(age < 1)
        {
            Debug.Log("cant move because age: " + age);
            return moveInfo;
        }

        if (GridManager.Instance.IsOutSideOfGrid(GridManager.Instance.PosToGridIndex(pos)))
        {
            Debug.Log("-2: " + GridManager.Instance.PosToGridIndex(pos));

            plannedMoveDir = Vector3Int.zero;
            return moveInfo;
        }

        GameObject objectAtDest = GridManager.Instance.GetCell(GridManager.Instance.PosToGridIndex(pos)).obj;

        if (objectAtDest != null) {
            // The minion wanted to advance (it passed the can-move / age / edge gates) but the cell
            // ahead is taken — a real failed move. Blocked distinguishes this from the earlier returns,
            // where the minion simply isn't trying to move. CollidedEntity is the blocker when it's a
            // minion, null when it's a wall (still blocked).
            moveInfo.Blocked = true;
            moveInfo.CollidedEntity = objectAtDest.GetComponent<MinionController>();
            return moveInfo;
        }

        moveInfo.CanMove= true;
        return moveInfo;
    }

    public struct MoveInfo
    {
        public bool CanMove;
        // True when the minion tried to advance but was blocked by an occupant (minion or wall), as
        // opposed to not moving at all (can't-move flag, summoned this turn, or facing the board edge).
        public bool Blocked;
        public MinionController CollidedEntity;
    }
}
