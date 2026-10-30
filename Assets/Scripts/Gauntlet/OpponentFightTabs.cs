using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

/// <summary>
/// The fight tabs on the Draft scene's opponent panel: one per encounter of the run. The upcoming fight's
/// tab reads "Upcoming" and is the one selected whenever the panel is refreshed; the others read "First",
/// "Second", "Third" and so on.
/// Any fight can be looked at, past or future: every enemy is rolled at the hero pick.
///
/// Switching slides the panel's contents -- every sibling of this tab bar -- off the left edge of the
/// screen, refills them for the chosen fight (<see cref="FightShown"/>), then slides them back in from
/// the left. The tab bar stays where it is. The slide's timing, easing and cascade spread are the top
/// tabs' own (<see cref="DraftSceneTabs"/>), so both move alike. Cascading, the contents go one after
/// another by their place left to right, as the top tabs' cascading panels do.
/// </summary>
public class OpponentFightTabs : MonoBehaviour
{
    [Tooltip("One per encounter, in order.")]
    [SerializeField] private TabButton[] fights;

    [SerializeField] private string nextLabel = "Upcoming";
    [Tooltip("One per encounter, in order. A fight past the end of the list falls back to its number.")]
    [SerializeField] private string[] fightLabels = { "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth", "Tenth" };

    [Tooltip("The top tabs, whose slide timing and easing these tabs use.")]
    [SerializeField] private DraftSceneTabs slideFrom;
    [Tooltip("Slide the contents one after another, left to right, instead of all at once.")]
    [SerializeField] private bool cascade = true;

    /// <summary>Raised with an encounter index when the panel should be filled with that fight.</summary>
    public event System.Action<int> FightShown;

    public int Selected { get; private set; } = -1;

    private readonly List<RectTransform> _content = new List<RectTransform>();
    private readonly List<Vector2> _homes = new List<Vector2>();
    private Sequence _slide;
    private bool _initialised;

    private void Awake() => Init();

    private void Init()
    {
        if (_initialised) return;
        _initialised = true;

        // Everything on the panel but this bar moves.
        var parent = transform.parent;
        if (parent != null)
            foreach (Transform child in parent)
                if (child != transform && child is RectTransform rect)
                {
                    _content.Add(rect);
                    _homes.Add(rect.anchoredPosition);
                }

        if (fights == null) return;
        for (int i = 0; i < fights.Length; i++)
        {
            if (fights[i] == null) continue;

            int index = i;
            fights[i].Button.onClick.AddListener(() => Select(index, animate: true));
        }
    }

    private void OnDisable() => FinishSlide();

    /// <summary>Labels the tabs for where the run is and selects the upcoming fight, without a slide.</summary>
    public void ResetToNext(int nextIndex)
    {
        Init();
        if (fights == null || fights.Length == 0) return;

        for (int i = 0; i < fights.Length; i++)
            if (fights[i] != null)
                fights[i].SetLabel(i == nextIndex ? nextLabel : FightLabel(i));

        // A won run has no next fight: its last one stays up.
        Select(Mathf.Clamp(nextIndex, 0, fights.Length - 1), animate: false);
    }

    private string FightLabel(int index) =>
        fightLabels != null && index < fightLabels.Length ? fightLabels[index] : (index + 1).ToString();

    public void Select(int index, bool animate)
    {
        Init();
        if (animate && index == Selected && !IsSwitching) return;

        FinishSlide();
        Selected = index;

        if (fights != null)
            for (int i = 0; i < fights.Length; i++)
                if (fights[i] != null) fights[i].SetActive(i == index);

        if (!animate || !Application.isPlaying || !gameObject.activeInHierarchy || _content.Count == 0)
        {
            FightShown?.Invoke(index);
            return;
        }

        float exitDuration = slideFrom != null ? slideFrom.ExitDuration : 0.25f;
        float enterAt = exitDuration + (slideFrom != null ? slideFrom.EnterGap : 0f);
        float enterDuration = slideFrom != null ? slideFrom.EnterDuration : 0.3f;
        var exitEase = slideFrom != null ? slideFrom.ExitEase : Ease.Linear;
        var enterEase = slideFrom != null ? slideFrom.EnterEase : Ease.Linear;
        float spread = cascade && slideFrom != null ? slideFrom.CascadeSpread : 0f;
        var order = PanelSlide.CascadeOrder(_content);
        float dx = PanelSlide.OffscreenLeftOffset(_content, slideFrom != null ? slideFrom.OffscreenMargin : 50f);

        _slide = DOTween.Sequence();
        for (int i = 0; i < _content.Count; i++)
        {
            var rect = _content[i];
            var home = _homes[i];
            var parked = home + new Vector2(dx, 0f);

            // Left-most out first and right-most in first, so the right-most leaves and the left-most arrives last.
            _slide.Insert(spread * order[i], rect.DOAnchorPos(parked, exitDuration).SetEase(exitEase));
            _slide.Insert(spread + enterAt + spread * (1f - order[i]),
                rect.DOAnchorPos(home, enterDuration).SetEase(enterEase));
        }

        // Refilled while off-screen, so the new fight is what comes back in.
        _slide.InsertCallback(spread + exitDuration, () => FightShown?.Invoke(index));
        // Under the panel's id, so the top tabs can finish it before moving the same pieces themselves.
        _slide.SetId(transform.parent);
        _slide.OnKill(() => _slide = null);
    }

    private bool IsSwitching => _slide != null && _slide.IsActive();

    /// <summary>Jumps a running switch to its end: contents home, filled with the selected fight.</summary>
    private void FinishSlide()
    {
        if (_slide != null && _slide.IsActive()) _slide.Complete(withCallbacks: true);
        _slide = null;

        for (int i = 0; i < _content.Count; i++)
            if (_content[i] != null) _content[i].anchoredPosition = _homes[i];
    }
}
