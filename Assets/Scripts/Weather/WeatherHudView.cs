using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The battle's weather on the HUD: its icon, a badge (a turn countdown, a strike chance...) and the
/// shared <see cref="UITooltip"/> with its name, rules text and live status while hovered.
///
/// Authored in the Game scene under UICanvas, inactive, just above CardPlayArea: above it so the icon gets
/// the pointer for its tooltip (CardPlayArea is a full-screen drop target), below every panel and popup.
/// <see cref="WeatherSystem"/> shows it when the battle has a weather and keeps it up to date; a clear-sky
/// battle leaves it hidden.
/// </summary>
public class WeatherHudView : MonoBehaviour
{
    [Header("Icon")]
    [SerializeField] private Image icon;
    [Tooltip("Shown in place of the icon, with the weather's initials, for a weather that has no icon yet.")]
    [SerializeField] private TextMeshProUGUI initials;

    [Header("Badge")]
    [SerializeField] private RectTransform badge;
    [SerializeField] private TextMeshProUGUI badgeText;
    [SerializeField] private Color badgeTextColor = new Color(1f, 0.83f, 0.42f, 1f);
    [Tooltip("Badge text colour when the weather is about to fire, or at its most dangerous.")]
    [SerializeField] private Color badgeUrgentColor = new Color(1f, 0.35f, 0.25f, 1f);

    [Header("Tooltip")]
    [SerializeField] private UITooltipTrigger tooltipTrigger;
    [SerializeField] private Color nameColor = new Color(1f, 0.83f, 0.42f, 1f);
    [SerializeField] private Color statusColor = new Color(1f, 0.83f, 0.42f, 1f);

    [Header("Pop In")]
    [SerializeField] private float popInDelay = 0.2f;
    [SerializeField] private float popInDuration = 0.35f;

    private WeatherSO _weather;
    private string _shownBadge;

    /// <summary>The weather view in the open scene (it starts inactive), or null.</summary>
    public static WeatherHudView Find() =>
        FindFirstObjectByType<WeatherHudView>(FindObjectsInactive.Include);

    /// <summary>Puts <paramref name="weather"/> on the HUD and pops it in.</summary>
    public void Show(WeatherSO weather)
    {
        _weather = weather;
        _shownBadge = null;

        // The icon / initials catch the pointer for the tooltip themselves, so hovering works whether or
        // not the backing plate (this object's own Image) is enabled.
        bool hasIcon = weather.icon != null;
        if (icon != null)
        {
            icon.sprite = weather.icon;
            icon.raycastTarget = true;
            icon.gameObject.SetActive(hasIcon);
        }
        if (initials != null)
        {
            initials.text = Initials(weather.DisplayName);
            initials.raycastTarget = true;
            initials.gameObject.SetActive(!hasIcon);
        }
        if (badge != null) badge.gameObject.SetActive(false);

        gameObject.SetActive(true);
        transform.DOKill();
        transform.localScale = Vector3.zero;
        transform.DOScale(1f, popInDuration).SetEase(Ease.OutBack).SetDelay(popInDelay).SetLink(gameObject);
    }

    public void Hide()
    {
        transform.DOKill();
        gameObject.SetActive(false);
    }

    /// <summary>Shows the weather's live badge (null = none) and status line.</summary>
    public void Refresh(string badgeValue, bool urgent, string status)
    {
        if (_weather == null) return;

        if (tooltipTrigger != null) tooltipTrigger.Message = TooltipText(status);

        bool hasBadge = !string.IsNullOrEmpty(badgeValue);
        if (badge != null)
        {
            badge.gameObject.SetActive(hasBadge);
            if (hasBadge && badgeText != null)
            {
                badgeText.text = badgeValue;
                badgeText.color = urgent ? badgeUrgentColor : badgeTextColor;

                // Square for a digit or two, widening evenly for longer text ("90%").
                float height = badge.sizeDelta.y;
                badge.sizeDelta = new Vector2(
                    Mathf.Max(height, badgeText.GetPreferredValues(badgeValue).x + height * 0.35f), height);

                // A tick reads as a tick: the badge pops whenever it changes.
                if (_shownBadge != null && _shownBadge != badgeValue)
                {
                    badge.DOComplete();
                    badge.DOPunchScale(Vector3.one * 0.35f, 0.3f, 6, 0.5f).SetLink(badge.gameObject);
                }
            }
        }
        _shownBadge = hasBadge ? badgeValue : null;
    }

    /// <summary>The weather's name, its rules text (keywords highlighted like card text) and its live status.</summary>
    private string TooltipText(string status)
    {
        var text = new System.Text.StringBuilder();
        text.Append("<b><color=#").Append(ColorUtility.ToHtmlStringRGBA(nameColor)).Append('>')
            .Append(_weather.DisplayName).Append("</color></b>");

        if (!string.IsNullOrEmpty(_weather.description))
            text.Append('\n').Append(CardTextFormatter.Format(_weather.description, HighlightConfig));

        if (!string.IsNullOrEmpty(status))
            text.Append("\n<i><color=#").Append(ColorUtility.ToHtmlStringRGBA(statusColor)).Append('>')
                .Append(status).Append("</color></i>");

        return text.ToString();
    }

    /// <summary>"Blood Moon" -> "BM".</summary>
    private static string Initials(string name)
    {
        var letters = new System.Text.StringBuilder();
        foreach (var word in name.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            letters.Append(char.ToUpperInvariant(word[0]));
            if (letters.Length == 2) break;
        }
        return letters.ToString();
    }

    // Same Resources config card and hero-passive text use. A missing asset renders plain text.
    private static CardTextHighlightConfig _highlightConfig;
    private static CardTextHighlightConfig HighlightConfig =>
        _highlightConfig != null ? _highlightConfig : (_highlightConfig = Resources.Load<CardTextHighlightConfig>("CardTextHighlightConfig"));
}
