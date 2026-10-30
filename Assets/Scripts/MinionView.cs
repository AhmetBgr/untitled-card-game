using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
public class MinionView : MonoBehaviour
{
    [SerializeField] private Sprite[] meleeFrames;
    [SerializeField] private Sprite[] rangedFrames;

    [SerializeField] private SpriteRenderer frame;
    [SerializeField] private SpriteRenderer art;

    /// <summary>The frame sprite: where the hero's portrait sits on the board, and in what tint.</summary>
    public SpriteRenderer Frame => frame;

    [SerializeField] private TextMeshProUGUI attacktext;
    [SerializeField] private TextMeshProUGUI healthtext;

    [SerializeField] private SpriteRenderer enemyInline;
    [SerializeField] private SpriteRenderer friendlyInline;

    [Tooltip("Melee attack icon (range 1). Assigned on the Hero prefab; left null on minion prefabs.")]
    [SerializeField] private GameObject attackIconSword;
    [Tooltip("Ranged attack icon (range >= 2). Assigned on the Hero prefab; left null on minion prefabs.")]
    [SerializeField] private GameObject attackIconBow;

    [SerializeField] private Transform damageIndicator;
    [SerializeField] private SpriteRenderer damageIndicatorBg;
    [SerializeField] private TextMeshProUGUI damageIndicatorText;
    [SerializeField] private CanvasGroup damageIndicatorGroup;
    [SerializeField] private float damageIndicatorHoldDuration = 0.4f;

    [Tooltip("Overlay flashed over the attack stat when the minion's attack is buffed or debuffed.")]
    [SerializeField] private CanvasGroup attackStatChangeGroup;
    [SerializeField] private Image attackStatChangeImage;
    [SerializeField] private TextMeshProUGUI attackStatChangeText;
    [SerializeField] private Color attackBuffColor = new Color(0.28f, 0.9f, 0.35f, 1f);
    [SerializeField] private Color attackDebuffColor = new Color(0.9f, 0.25f, 0.25f, 1f);
    [SerializeField] private float attackStatChangeFadeInDuration = 0.1f;
    [SerializeField] private float attackStatChangeFadeOutDuration = 0.7f;

    [Tooltip("Ranged variant, used instead of the fields above when the bow icon is showing (range >= 2). Assigned on the Hero prefab; left null on minion prefabs.")]
    [SerializeField] private CanvasGroup attackStatChangeBowGroup;
    [SerializeField] private Image attackStatChangeBowImage;
    [SerializeField] private TextMeshProUGUI attackStatChangeBowText;

    [Tooltip("Overlay flashed over the health stat when the minion is healed. Health loss shows the damage indicator instead.")]
    [SerializeField] private CanvasGroup healthStatChangeGroup;
    [SerializeField] private Image healthStatChangeImage;
    [SerializeField] private TextMeshProUGUI healthStatChangeText;
    [SerializeField] private Color healthBuffColor = new Color(0.28f, 0.9f, 0.35f, 1f);
    [SerializeField] private float healthStatChangeFadeInDuration = 0.1f;
    [SerializeField] private float healthStatChangeFadeOutDuration = 0.7f;

    [Header("Frame Tint")]
    [SerializeField] private Color playerFrameTint = new Color(0.3f, 0.6f, 1f, 1f);
    [SerializeField] private Color opponentFrameTint = new Color(1f, 0.35f, 0.35f, 1f);

    [Tooltip("Inline colour for a minion summoned from a FORGED (upgraded) card, replacing whatever colour that team's inline is authored with. Only the TINT changes -- which inline shows is still the team's, so a forged minion still reads as friendly or enemy.")]
    [SerializeField] private Color forgedInlineTint = new Color(1f, 0.82f, 0.2f, 1f);

    [Tooltip("Purely visual lag: buff/debuff flashes and their stat numbers land this long after the value actually changed. Health loss uses damageIndicatorVisualDelay instead.")]
    [SerializeField] private float statChangeVisualDelay = 0.35f;

    [Tooltip("Same idea, but for health loss only: the damage indicator and the health number land this long after the damage was dealt. This is the whole damage lag — MinionController.TakeDamage pushes the value immediately — so it's what syncs the number with the strike animation. Kept separate so damage can read faster (or slower) than buff/debuff flashes.")]
    [SerializeField] private float damageIndicatorVisualDelay = 0.35f;

    // Which team's inline this minion should show, and whether the spawn animation has finished. The
    // inline stays hidden until the pop-in completes, so it doesn't flash at full size mid scale-up.
    private bool _isPlayerMinion;
    private bool _hasAppeared;

    // Whether this minion came from a forged (upgraded) card, which recolours its inline. Pushed from
    // the modal on every UpdateView rather than latched, so a view reused for a different minion --
    // or a card that becomes upgraded while on the board -- ends up with the right inline either way.
    private bool _isForged;

    // The inlines' authored colours, read once before anything overwrites them, so a minion that isn't
    // forged can be tinted BACK without the plain colour having to be duplicated in the inspector.
    private Color _enemyInlineBaseColor = Color.white;
    private Color _friendlyInlineBaseColor = Color.white;
    private bool _inlineBaseColorsCaptured;

    // Last attack value pushed through UpdateView, used to detect buffs/debuffs.
    private int _lastAttack;
    private bool _hasAttackValue;

    // Same for health: gains flash the overlay, losses fall through to the damage indicator.
    private int _lastHealth;
    private bool _hasHealthValue;

    // Whether the bow icon is showing, which also selects the ranged attack-flash overlay.
    private bool _isRanged;

    /// <summary>
    /// The damage number landing, which is the moment a hit reads as having happened -- the value is
    /// pushed immediately but shown <see cref="damageIndicatorVisualDelay"/> later, and feedback tied to
    /// the push instead would fire while the strike is still swinging. Purely additive (audio).
    /// </summary>
    public static event Action<MinionController, int, DamageSource> DamageShown;

    /// <summary>
    /// A stat GAIN landing on screen -- the green overlay and its "+n", for attack or for health.
    /// Raised with the visual rather than when the number changed, for the same reason DamageShown is:
    /// the flash is deferred, and a sound cued to the mutation would arrive before anything is visible.
    /// </summary>
    public static event Action<MinionController, int> BuffShown;

    /// <summary>
    /// A stat LOSS landing on screen -- the red attack overlay and its "-n". Health losses are not this:
    /// they show the damage indicator instead and are announced by <see cref="DamageShown"/>.
    /// </summary>
    public static event Action<MinionController, int> DebuffShown;

    /// <summary>
    /// The kind of hit the next health loss belongs to. Set by MinionController.TakeDamage right before
    /// it pushes the new value, and consumed by that same push -- a loss arriving through any other
    /// UpdateView (a spell writing health directly) correctly reads Effect.
    /// </summary>
    [NonSerialized] public DamageSource nextDamageSource = DamageSource.Effect;

    // Resolved once: MinionView shares the root GameObject with MinionController (see
    // PlayAbsorbAnimation), so this is the minion the number belongs to.
    private MinionController _owner;
    private bool _ownerResolved;

    public void UpdateView(CardModal modal)
    {
        if (modal == null) return;

        // Recorded before the stat updates, so an attack flash picks the overlay matching the icon
        // that's about to be shown rather than the previous frame's.
        _isRanged = modal.range >= 2;

        UpdateAttackText(modal.attack);
        UpdateHealthText(modal.health);
        UpdateFrame(modal);
        UpdateAttackIcon(modal.range);

        art.sprite = modal.minionArt;
        _isPlayerMinion = modal.isPlayerMinion;
        _isForged = modal.isUpgraded;
        ApplyFrameTint();
        ApplyInlineTint();
        ApplyInlineVisibility();
    }

    /// <summary>
    /// UpdateView, but the attack/health stats are re-seeded as the new baseline instead of being
    /// diffed against the old one — so no buff/debuff flash plays. For standing modifiers stamped
    /// during setup (a hero passive's printed -2 Attack), which are part of the statline the player
    /// starts with, not something that just happened to them.
    /// </summary>
    public void UpdateViewWithoutStatFlash(CardModal modal)
    {
        _hasAttackValue = false;
        _hasHealthValue = false;
        UpdateView(modal);
    }

    // The team inline is only visible once the spawn animation has finished; before then both stay
    // hidden. Later UpdateView calls (e.g. after taking damage) keep it shown, since _hasAppeared sticks.
    private void ApplyFrameTint()
    {
        if (frame == null) return;
        frame.color = _isPlayerMinion ? playerFrameTint : opponentFrameTint;
    }

    // Forged minions wear a different inline colour; everyone else wears the one the prefab authored.
    // Applied to both inlines, since only one of them is ever visible anyway and this way the tint is
    // already right if visibility flips later.
    private void ApplyInlineTint()
    {
        if (!_inlineBaseColorsCaptured)
        {
            if (enemyInline != null) _enemyInlineBaseColor = enemyInline.color;
            if (friendlyInline != null) _friendlyInlineBaseColor = friendlyInline.color;
            _inlineBaseColorsCaptured = true;
        }

        TintInline(enemyInline, _isForged ? forgedInlineTint : _enemyInlineBaseColor);
        TintInline(friendlyInline, _isForged ? forgedInlineTint : _friendlyInlineBaseColor);
    }

    // Alpha is left alone rather than taken from the tint: the inline is faded out on death
    // (FadeOutArtImage), and an UpdateView arriving mid-fade must not snap it back to full opacity.
    private static void TintInline(SpriteRenderer inline, Color tint)
    {
        if (inline == null) return;
        tint.a = inline.color.a;
        inline.color = tint;
    }

    private void ApplyInlineVisibility()
    {
        enemyInline.gameObject.SetActive(_hasAppeared && !_isPlayerMinion);
        if (friendlyInline != null)
            friendlyInline.gameObject.SetActive(_hasAppeared && _isPlayerMinion);
    }

    private void UpdateAttackText(int value)
    {
        // The first UpdateView only seeds the baseline (spawning at 3 attack isn't a buff), and shows
        // the number straight away — a spawning minion shouldn't sit blank for the delay.
        if (!_hasAttackValue)
        {
            attacktext.text = value.ToString();
            _lastAttack = value;
            _hasAttackValue = true;
            return;
        }

        int delta = value - _lastAttack;

        // Bookkeeping is immediate so back-to-back changes each get the right delta; only the number
        // and its flash are deferred, since all attack mutations route back through here.
        _lastAttack = value;

        DeferVisual(statChangeVisualDelay, () =>
        {
            attacktext.text = value.ToString();
            if (delta != 0)
                PlayAttackStatChange(delta);
        });
    }

    // Runs a visual-only update after the given delay. The callback is dropped if this view was
    // destroyed in the meantime (the minion died mid-delay), since DOTween outlives the GameObject.
    private void DeferVisual(float delay, TweenCallback action)
    {
        if (delay <= 0f) { action(); return; }

        DOVirtual.DelayedCall(delay, () =>
        {
            if (this == null) return;
            action();
        });
    }

    // Green for a buff, red for a debuff: a fast fade in to full alpha, then a slow fade back out.
    // The label shows the signed delta ("+2" / "-1"), not the new total.
    private void PlayAttackStatChange(int delta)
    {
        RaiseStatChange(delta);

        // Ranged heroes show the attack stat under the bow icon, which sits elsewhere on the card and
        // so needs its own overlay. Minion prefabs leave the bow trio null and always use the sword one.
        bool useBow = _isRanged && attackStatChangeBowGroup != null;

        CanvasGroup group = useBow ? attackStatChangeBowGroup : attackStatChangeGroup;
        Image image = useBow ? attackStatChangeBowImage : attackStatChangeImage;
        TextMeshProUGUI label = useBow ? attackStatChangeBowText : attackStatChangeText;

        if (group == null) return;

        group.DOKill();

        Color tint = delta > 0 ? attackBuffColor : attackDebuffColor;

        if (image != null)
            image.color = tint;

        if (label != null)
        {
            label.text = (delta > 0 ? "+" : "") + delta;
        }

        group.alpha = 0f;
        DOTween.Sequence().SetTarget(group)
            .Append(group.DOFade(1f, attackStatChangeFadeInDuration).SetEase(Ease.OutQuad))
            .AppendInterval(0.4f)
            .Append(group.DOFade(0f, attackStatChangeFadeOutDuration).SetEase(Ease.InQuad));
    }
    private void UpdateHealthText(int value)
    {
        if (!_hasHealthValue)
        {
            healthtext.text = value.ToString();
            _lastHealth = value;
            _hasHealthValue = true;
            return;
        }

        int delta = value - _lastHealth;
        _lastHealth = value;

        // Health is asymmetric: a gain flashes the green overlay, a loss reuses the existing damage
        // indicator. The number always lands with whichever visual it belongs to, so a loss follows
        // the damage delay and a gain (or a no-op re-push) follows the stat-change one.
        float delay = delta < 0 ? damageIndicatorVisualDelay : statChangeVisualDelay;

        // Read (and cleared) now rather than inside the delayed callback: by the time that runs, a later
        // hit may have re-latched the field.
        DamageSource source = nextDamageSource;
        nextDamageSource = DamageSource.Effect;

        DeferVisual(delay, () =>
        {
            healthtext.text = value.ToString();
            if (delta > 0)
                PlayHealthStatChange(delta);
            else if (delta < 0)
                PlayDamageIndicator(-delta, source);
        });
    }

    // Heals only — there is no red variant, since losing health shows the damage indicator instead.
    private void PlayHealthStatChange(int delta)
    {
        RaiseStatChange(delta);

        if (healthStatChangeGroup == null) return;

        healthStatChangeGroup.DOKill();

        if (healthStatChangeImage != null)
            healthStatChangeImage.color = healthBuffColor;

        if (healthStatChangeText != null)
            healthStatChangeText.text = "+" + delta;

        healthStatChangeGroup.alpha = 0f;
        DOTween.Sequence().SetTarget(healthStatChangeGroup)
            .Append(healthStatChangeGroup.DOFade(1f, healthStatChangeFadeInDuration).SetEase(Ease.OutQuad))
            .AppendInterval(0.4f)
            .Append(healthStatChangeGroup.DOFade(0f, healthStatChangeFadeOutDuration).SetEase(Ease.InQuad));
    }

    // Melee (range 1) shows the sword icon; ranged (range >= 2) shows the bow. Both refs are only
    // assigned on the Hero prefab, so this is a no-op for minion prefabs that leave them null.
    private void UpdateAttackIcon(int range)
    {
        if (attackIconSword == null || attackIconBow == null) return;

        attackIconBow.SetActive(_isRanged);
        attackIconSword.SetActive(!_isRanged);
    }

    private void UpdateFrame(CardModal modal)
    {
        /*if(frame == null) return;

        if(modal.range == 1)
        {
            int index = Mathf.Min((int)(modal.defHealth / 5f), meleeFrames.Length - 1);
            frame.sprite = meleeFrames[index];
        }
        else
        {
            int index = Mathf.Min((int)(modal.defHealth / 5f), rangedFrames.Length - 1);
            frame.sprite = rangedFrames[index];

        }*/
    }

    public void PlayAppearAnimation()
    {
        transform.localScale = Vector3.zero;
        transform.DOScale(Vector3.one, 0.5f).SetDelay(0.25f).SetEase(Ease.OutBack)
            .OnComplete(RevealInline);
    }

    // The minion is being consumed by another (Angry Soul's on-summon absorb): it slides into the
    // absorber while shrinking away, instead of dying in place. Both tweens target this transform —
    // MinionView shares the root GameObject with MinionController, so this is the same transform
    // Move() animates; killing tweens here first stops a still-running appear/move from fighting it.
    public void PlayAbsorbAnimation(Vector3 absorberPos, float duration)
    {
        transform.DOKill(false);
        transform.DOMove(absorberPos, duration).SetEase(Ease.InQuad);
        transform.DOScale(Vector3.zero, duration).SetEase(Ease.InBack);
    }

    // Heroes have no entrance animation: they start the match already in place, so there is nothing to
    // wait on and the inline is revealed straight away. (Minions still pop in via PlayAppearAnimation.)
    public void ShowHeroImmediately()
    {
        RevealInline();
    }

    // Called when a spawn animation finishes: the inline becomes eligible to show and is applied now.
    private void RevealInline()
    {
        _hasAppeared = true;
        ApplyInlineVisibility();
    }

    public void FadeOutArtImage(float dur)
    {
        art.DOFade(0f, dur);
        enemyInline.DOFade(0f, dur);

    }

    // Fast pop-in of the damage number, held briefly, then faded out (via CanvasGroup, with the
    // world-space background synced to the same alpha) and hidden.
    // Announces a stat change to whoever is listening (audio). Sits ahead of the per-stat null checks
    // on purpose: whether a particular overlay is wired on this prefab is a question about the CARD's
    // layout, not about whether the unit was buffed.
    private void RaiseStatChange(int delta)
    {
        if (delta == 0) return;

        if (!_ownerResolved)
        {
            _owner = GetComponent<MinionController>();
            _ownerResolved = true;
        }

        if (delta > 0) BuffShown?.Invoke(_owner, delta);
        else DebuffShown?.Invoke(_owner, -delta);
    }

    public void PlayDamageIndicator(int damage, DamageSource source = DamageSource.Effect)
    {
        if (!_ownerResolved)
        {
            _owner = GetComponent<MinionController>();
            _ownerResolved = true;
        }

        DamageShown?.Invoke(_owner, damage, source);

        damageIndicator.DOKill();
        damageIndicatorGroup.DOKill();

        damageIndicatorText.text = "-" + damage;
        damageIndicatorGroup.alpha = 1f;
        damageIndicatorBg.color = new Color(damageIndicatorBg.color.r, damageIndicatorBg.color.g, damageIndicatorBg.color.b, 1f);

        damageIndicator.gameObject.SetActive(true);
        damageIndicator.localScale = Vector3.zero;
        damageIndicator.DOScale(Vector3.one, 0.12f).SetEase(Ease.OutBack)
            .OnComplete(() =>
            {
                DOVirtual.DelayedCall(damageIndicatorHoldDuration, () =>
                {
                    damageIndicatorGroup.DOFade(0f, 0.18f)
                        .OnUpdate(() => damageIndicatorBg.color = new Color(
                            damageIndicatorBg.color.r, damageIndicatorBg.color.g, damageIndicatorBg.color.b, damageIndicatorGroup.alpha))
                        .OnComplete(() => damageIndicator.gameObject.SetActive(false));
                });
            });
    }

}
