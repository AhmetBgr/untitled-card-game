using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The Hero Info tab of the end-of-match panel: the run's hero as it will start its next battle, the
/// deck it fights with, and the lasting milestone rewards it has claimed -- the player's own "blessings",
/// worded the way the versus intro words them (<see cref="GauntletPlayerBlessings"/>).
///
/// The hero panel and deck list are the Draft scene's own layouts (Assets/Prefabs/UI/Draft) nested under
/// the panel, filled with the same helpers the run's loadout fills its own with
/// (<see cref="GauntletHeroPanelView.ShowRunHero"/>, <see cref="GauntletDeckList"/>). The blessings block
/// is one <see cref="BlessingRow"/> per reward, cloned from the Draft scene's row prefab; a run with no
/// lasting reward yet hides the block altogether. Anything authored under the deck list or the blessings
/// list is a preview and is cleared on the first fill.
///
/// Read-only: nothing here is picked. <see cref="GameOverTabs"/> refreshes it as its tab opens.
/// </summary>
public class GameOverHeroInfo : MonoBehaviour
{
    [Header("Hero")]
    [SerializeField] private GauntletHeroPanelView hero;

    [Header("Deck list")]
    [SerializeField] private TextMeshProUGUI deckHeaderText;
    [Tooltip("Entries are cloned from this one; the authored entries next to it are placeholders and get removed.")]
    [SerializeField] private CardButtonHandler deckEntryTemplate;

    [Header("Blessings")]
    [Tooltip("The whole blessings block, header included: hidden when the run has no lasting reward yet.")]
    [SerializeField] private GameObject blessingsRoot;
    [Tooltip("Where the reward rows go, one per reward. Anything authored under it is a preview, cleared on the first fill.")]
    [SerializeField] private RectTransform blessingList;
    [SerializeField] private BlessingRow blessingRowPrefab;

    private readonly List<CardButtonHandler> _deckEntries = new List<CardButtonHandler>();
    private readonly List<BlessingRow> _rows = new List<BlessingRow>();
    private bool _initialised;

    /// <summary>
    /// The panel is authored disabled and its tab fills it on the way up, before it is switched on, so
    /// none of this can wait for Awake.
    /// </summary>
    private void Init()
    {
        if (_initialised) return;
        _initialised = true;

        GauntletDeckList.ClearPlaceholders(deckEntryTemplate);

        if (blessingList != null)
            for (int i = blessingList.childCount - 1; i >= 0; i--)
                Destroy(blessingList.GetChild(i).gameObject);
    }

    /// <summary>Brings the hero, its deck and its rewards up to date with the run as it stands.</summary>
    public void Refresh()
    {
        Init();

        if (!GauntletRun.IsActive)
        {
            if (blessingsRoot != null) blessingsRoot.SetActive(false);
            return;
        }

        hero?.ShowRunHero();
        GauntletDeckList.Refresh(deckHeaderText, deckEntryTemplate, _deckEntries, GauntletRun.Deck);

        int rows = GauntletPlayerBlessings.Fill(blessingList, blessingRowPrefab, _rows);
        if (blessingsRoot != null) blessingsRoot.SetActive(rows > 0);
    }
}
