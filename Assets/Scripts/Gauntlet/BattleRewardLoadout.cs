using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

/// <summary>
/// The run's hero and deck, shown beside the card options while a reward card is picked over the
/// end-of-match screen, laid out like the Draft scene's panels so the player knows what the deck needs.
/// <see cref="BattleRewardsPanel"/> shows it when a pick opens and hides it when the panel comes back;
/// it is authored disabled.
/// </summary>
public class BattleRewardLoadout : MonoBehaviour
{
    [SerializeField] private GauntletHeroPanelView hero;
    [Tooltip("Covers the hero panel while the run has no hero yet; slides off when one is picked " +
             "(PlayHeroUnlock).")]
    [SerializeField] private RectTransform heroLockedImage;

    [Header("Deck list")]
    [SerializeField] private TextMeshProUGUI deckHeaderText;
    [Tooltip("Entries are cloned from this one; the authored entries next to it are placeholders and get removed.")]
    [SerializeField] private CardButtonHandler deckEntryTemplate;

    [Header("Room for the panels")]
    [Tooltip("Moved sideways while shown (the selection panel's options and prompt), so they clear the " +
             "panels; put back on hide. The selection panel also serves in-match choices, which keep its layout.")]
    [SerializeField] private RectTransform[] shiftedWhileShown;
    [SerializeField] private float shiftX = -150f;

    private readonly List<CardButtonHandler> _deckEntries = new List<CardButtonHandler>();
    private bool _placeholdersCleared;
    private bool _shifted;
    private Sequence _heroUnlock;

    /// <summary>
    /// Shows the run as it stands, so a pick just taken is already in the deck.
    /// Pass <paramref name="shiftPanels"/> false when nothing is in the way: the end-of-match review tabs
    /// (<see cref="DraftPanelTabs"/>) show the loadout on its own, with no card options to make room for.
    /// Pass <paramref name="activate"/> false to fill it in where it stands without switching it on: a
    /// pick reopened from a save behind a tab that is not up (<see cref="BattleRewardsPanel"/>).
    /// </summary>
    public void Show(bool shiftPanels = true, bool activate = true)
    {
        if (!GauntletRun.IsActive) return;

        if (activate)
        {
            gameObject.SetActive(true);
            if (shiftPanels) Shift(true);
        }
        if (!_placeholdersCleared)
        {
            GauntletDeckList.ClearPlaceholders(deckEntryTemplate);
            _placeholdersCleared = true;
        }

        hero?.ShowRunHero();
        // Left alone while it is sliding off: the pick that starts the slide redraws the loadout too.
        if (heroLockedImage != null && (_heroUnlock == null || !_heroUnlock.IsActive()))
            heroLockedImage.gameObject.SetActive(GauntletRun.Hero == null);
        GauntletDeckList.Refresh(deckHeaderText, deckEntryTemplate, _deckEntries, GauntletRun.Deck);
    }

    /// <summary>
    /// The hero panel's lock coming off once a hero is picked: the locked image slides right, then
    /// shrinks to nothing -- the passive chips' unlock (<see cref="PassiveChipButton.PlayUnlock"/>),
    /// sideways. Afterwards it is switched off and put back where it was authored. Returned so the
    /// caller can time what follows; null when there is no locked image.
    /// </summary>
    public Sequence PlayHeroUnlock(float slideDistance, float slideDuration, float shrinkDuration)
    {
        if (heroLockedImage == null) return null;
        if (_heroUnlock != null && _heroUnlock.IsActive()) _heroUnlock.Complete();

        var image = heroLockedImage;
        Vector2 home = image.anchoredPosition;
        Vector3 size = image.localScale;
        image.gameObject.SetActive(true);

        _heroUnlock = DOTween.Sequence();
        _heroUnlock.Append(image.DOAnchorPosX(home.x + slideDistance, slideDuration).SetEase(Ease.OutQuad));
        _heroUnlock.Append(image.DOScale(Vector3.zero, shrinkDuration).SetEase(Ease.InQuad));
        _heroUnlock.OnComplete(() =>
        {
            if (image == null) return;
            image.gameObject.SetActive(false);
            image.anchoredPosition = home;
            image.localScale = size;
        });
        return _heroUnlock;
    }

    /// <summary>
    /// Where a card taken on the rewards screen should fly: the deck list it is about to join. Handed to
    /// <see cref="CardSelectionPanel.SetDeckTarget"/> when a pick opens, because the panel is a prefab
    /// and this is a scene object.
    /// </summary>
    public RectTransform DeckAnchor =>
        deckHeaderText != null ? (RectTransform)deckHeaderText.transform : (RectTransform)transform;

    /// <summary>The hero's passive row, in slot order; null when the panel has none wired.</summary>
    public IReadOnlyList<PassiveChipButton> PassiveChips => hero?.passiveChips;

    /// <summary>
    /// The chip a perk taken on the rewards screen is about to fill: the first free slot on the hero's
    /// passive row. This is what the perk flies to AND what unlocks when it gets there, so the player
    /// sees the perk appear on the hero. Falls back to the last chip when the row is already full, and
    /// to null when there are no chips at all.
    /// </summary>
    public PassiveChipButton PassiveSlot
    {
        get
        {
            var chips = hero?.passiveChips;
            if (chips == null) return null;

            PassiveChipButton last = null;
            foreach (var chip in chips)
            {
                if (chip == null) continue;
                if (chip.Passive == null) return chip;
                last = chip;
            }
            return last;
        }
    }

    public void Hide()
    {
        Shift(false);
        gameObject.SetActive(false);
    }

    private void OnDestroy() => Shift(false);

    private void Shift(bool on)
    {
        if (_shifted == on || shiftedWhileShown == null) return;
        _shifted = on;

        float dx = on ? shiftX : -shiftX;
        foreach (var rt in shiftedWhileShown)
            if (rt != null) rt.anchoredPosition += new Vector2(dx, 0f);
    }
}
