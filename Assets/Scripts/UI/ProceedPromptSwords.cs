using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The crossed swords on the Proceed button. Each blade swings about its own grip -- which is where its
/// RectTransform pivot sits, down at the hilt rather than in the middle of the sprite, so the blade
/// sweeps and the hand stays put the way a held sword does.
///
/// Two states, and the pointer picks between them. Both are the same motion, only wider when the pointer
/// is over the button:
///   * away -- each blade rocks <see cref="idleAngle"/> degrees either side of the angle it is authored
///     at, slowly;
///   * over -- the same rock, out to <see cref="hoverAngle"/> and quicker.
/// Changing between them eases from wherever the blade has got to into the new swing rather than
/// snapping to the end of it, so a pointer crossing the button in and out never jumps.
///
/// It also owns the pieces that are there only while the pointer is over the button
/// (<see cref="hoverOnly"/>) -- the flash behind the Proceed label -- fading them up from nothing as the
/// swing widens and back down as it settles, so the two read as one gesture.
///
/// Mirrored by default: a blade lying the other side of upright swings the opposite way, so the cross
/// opens and closes rather than both blades leaning together. Which side a blade is on is read off its
/// own authored angle, which is what lets one list drive both halves of the cross.
///
/// Home is whatever the blades are authored at, so the prefab stays the one place the resting pose
/// lives and this only ever works in degrees either side of it.
///
/// Every tween ignores timeScale, the way the prompt's own fade does: a paused screen still has the
/// button on it, and a blade frozen mid-swing would read as a bug.
/// </summary>
public class ProceedPromptSwords : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Tooltip("The blades of the cross. Each swings about its own authored angle, so the order of this " +
             "list does not matter. Their pivots belong at the grip, not the middle of the sprite.")]
    [SerializeField] private RectTransform[] swords;

    [Tooltip("A blade lying the other side of upright swings the opposite way, so the cross opens and " +
             "closes. Off, every blade swings the same way and the pair leans together.")]
    [SerializeField] private bool mirror = true;

    [Tooltip("There only while the pointer is over the button: the flash behind the Proceed label. " +
             "Each is held at nothing and faded back to the alpha it is authored at, so the prefab " +
             "stays the one place its strength is set.")]
    [SerializeField] private Graphic[] hoverOnly;

    [Header("Away")]
    [Tooltip("How far the blade rocks either side of its authored angle, in degrees.")]
    [SerializeField] private float idleAngle = 4f;
    [Tooltip("One half of the swing: one extreme across to the other.")]
    [SerializeField] private float idleDuration = 1.3f;

    [Header("Over")]
    [Tooltip("The same rock, wider, while the pointer is over the button.")]
    [SerializeField] private float hoverAngle = 14f;
    [SerializeField] private float hoverDuration = 0.45f;

    [Header("Easing")]
    [Tooltip("The swing itself, which wants to slow at each end the way a pendulum does.")]
    [SerializeField] private Ease swingEase = Ease.InOutSine;
    [Tooltip("Easing from the swing it was doing into the one it is taking up.")]
    [SerializeField] private float changeDuration = 0.18f;
    [SerializeField] private Ease changeEase = Ease.OutCubic;

    /// <summary>Each blade's authored angle, and which way round it swings.</summary>
    private float[] _home;
    private float[] _sign;

    /// <summary>The alpha each hover-only piece is authored at, which is what it fades up to.</summary>
    private float[] _hoverAlpha;

    private bool _hovered;

    private int Count => _home != null ? _home.Length : 0;

    private float Angle => _hovered ? hoverAngle : idleAngle;
    private float Duration => _hovered ? hoverDuration : idleDuration;

    private void Awake()
    {
        int count = swords != null ? swords.Length : 0;
        _home = new float[count];
        _sign = new float[count];

        for (int i = 0; i < count; i++)
        {
            if (swords[i] == null) continue;

            // 0..360 as Unity reports it, so the half it falls in says which side of upright it lies.
            float angle = swords[i].localEulerAngles.z;
            _home[i] = angle;
            _sign[i] = mirror && angle > 180f ? -1f : 1f;
        }

        int shown = hoverOnly != null ? hoverOnly.Length : 0;
        _hoverAlpha = new float[shown];
        for (int i = 0; i < shown; i++)
            if (hoverOnly[i] != null) _hoverAlpha[i] = hoverOnly[i].color.a;
    }

    // The button is switched off and on as the screen it is on comes and goes, so the pose is taken here
    // rather than once at Awake -- and the hover is dropped, since no exit will come for a pointer that
    // was over it when it went away.
    private void OnEnable()
    {
        _hovered = false;
        Apply(ease: false);
    }

    private void OnDisable()
    {
        _hovered = false;
        Home();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (_hovered) return;

        _hovered = true;
        Apply(ease: true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (!_hovered) return;

        _hovered = false;
        Apply(ease: true);
    }

    /// <summary>Everything the current state calls for: the blades' swing, and what shows with it.</summary>
    private void Apply(bool ease)
    {
        Swing(ease);
        Reveal(ease);
    }

    /// <summary>
    /// Brings the hover-only pieces up to the alpha they are authored at, or takes them back to nothing.
    /// On the same clock as the handoff between the two swings, so the flash arrives with the wider
    /// swing rather than after it.
    /// </summary>
    private void Reveal(bool ease)
    {
        int count = _hoverAlpha != null ? _hoverAlpha.Length : 0;
        for (int i = 0; i < count; i++)
        {
            var piece = hoverOnly[i];
            if (piece == null) continue;

            piece.DOKill();
            float target = _hovered ? _hoverAlpha[i] : 0f;

            if (!ease || !Application.isPlaying || changeDuration <= 0f)
            {
                SetAlpha(piece, target);
                continue;
            }

            piece.DOFade(target, changeDuration).SetEase(changeEase).SetUpdate(true);
        }
    }

    private static void SetAlpha(Graphic piece, float alpha)
    {
        var colour = piece.color;
        colour.a = alpha;
        piece.color = colour;
    }

    /// <summary>
    /// Puts every blade on the swing the current state calls for. <paramref name="ease"/> takes it there
    /// from wherever it has got to; without it the blade is simply placed at one end, which is what a
    /// fresh show wants.
    /// </summary>
    private void Swing(bool ease)
    {
        for (int i = 0; i < Count; i++)
        {
            var sword = swords[i];
            if (sword == null) continue;

            sword.DOKill();

            float from = _home[i] - _sign[i] * Angle;

            if (!ease || !Application.isPlaying || changeDuration <= 0f)
            {
                sword.localEulerAngles = new Vector3(0f, 0f, from);
                Loop(i);
                continue;
            }

            // Into the near end of the new swing first, then the swing itself. A kill on the way into
            // another state does not run this: DOKill leaves callbacks alone.
            int blade = i;
            sword.DOLocalRotate(new Vector3(0f, 0f, from), changeDuration, RotateMode.Fast)
                .SetEase(changeEase).SetUpdate(true)
                .OnComplete(() => Loop(blade));
        }
    }

    /// <summary>
    /// The swing: from the end the blade is already at, across to the other, and back for as long as the
    /// state lasts.
    /// </summary>
    private void Loop(int blade)
    {
        var sword = swords[blade];
        if (sword == null || !Application.isPlaying) return;
        if (Duration <= 0f || Mathf.Approximately(Angle, 0f)) return;

        float to = _home[blade] + _sign[blade] * Angle;
        sword.DOLocalRotate(new Vector3(0f, 0f, to), Duration, RotateMode.Fast)
            .SetEase(swingEase).SetLoops(-1, LoopType.Yoyo).SetUpdate(true);
    }

    /// <summary>Drops every tween and puts the blades back exactly as authored.</summary>
    private void Home()
    {
        for (int i = 0; i < Count; i++)
        {
            var sword = swords[i];
            if (sword == null) continue;

            sword.DOKill();
            sword.localEulerAngles = new Vector3(0f, 0f, _home[i]);
        }

        int shown = _hoverAlpha != null ? _hoverAlpha.Length : 0;
        for (int i = 0; i < shown; i++)
        {
            if (hoverOnly[i] == null) continue;

            hoverOnly[i].DOKill();
            SetAlpha(hoverOnly[i], _hoverAlpha[i]);
        }
    }
}
