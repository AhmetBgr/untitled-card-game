using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One hero panel of a Forged in Battle layout: name plate, portrait with stats, passive chips. Shared by
/// the Draft scene (<see cref="DraftController"/>) and the card picks of the end-of-match rewards
/// (<see cref="BattleRewardLoadout"/>), so both show the run's hero the same way.
/// </summary>
[System.Serializable]
public class GauntletHeroPanelView
{
    public GameObject root;
    public TextMeshProUGUI nameText;
    public HeroButtonHandler portrait;
    [Tooltip("Overwritten after the portrait fills in, with the health the hero will actually start at.")]
    public TextMeshProUGUI healthText;
    public TextMeshProUGUI attackText;
    [Tooltip("Filled in order with the hero's passives; chips past the last passive keep their empty look.")]
    public PassiveChipButton[] passiveChips;

    /// <summary>Resolves the parts of a spawned HeroPanel prefab instance. Stats keep the printed values.</summary>
    public static GauntletHeroPanelView FromInstance(GameObject go)
    {
        var nameBg = go.transform.Find("NameBG");
        return new GauntletHeroPanelView
        {
            root = go,
            nameText = nameBg != null ? nameBg.GetComponentInChildren<TextMeshProUGUI>(true) : null,
            portrait = go.GetComponentInChildren<HeroButtonHandler>(true),
            passiveChips = go.GetComponentsInChildren<PassiveChipButton>(true),
        };
    }

    /// <summary>
    /// Shows <paramref name="hero"/> ("???" and a random portrait when null) with the given stats.
    /// <paramref name="passives"/> are shown in place of the hero's own when given (a blessed enemy's).
    /// </summary>
    public void Show(HeroSO hero, int health, int attack, IReadOnlyList<HeroPassiveSO> passives = null)
    {
        if (nameText != null) nameText.text = hero != null ? hero.cardName : "???";

        if (portrait != null)
        {
            if (hero != null) portrait.SetHero(hero);
            else portrait.SetRandom(null);
        }

        if (hero != null)
        {
            if (healthText != null) healthText.text = health.ToString();
            if (attackText != null) attackText.text = attack.ToString();
        }

        if (passiveChips == null) return;
        if (passives == null && hero != null) passives = hero.passives;
        for (int i = 0; i < passiveChips.Length; i++)
        {
            var chip = passiveChips[i];
            if (chip == null) continue;
            // A slot, not a display chip: filled slots show unlocked, empty ones locked.
            chip.ShowSlot(passives != null && i < passives.Count ? passives[i] : null);
        }
    }

    /// <summary>The run's hero as it will start the next battle: carried stats, own passives and drafted perks.</summary>
    public void ShowRunHero()
    {
        var hero = GauntletRun.Hero;
        int health = 0, attack = 0;
        if (hero != null && !GauntletRun.TryGetCarriedStats(out health, out _, out attack))
        {
            health = hero.health;
            attack = hero.attack;
        }
        Show(hero, health, attack, hero != null ? GauntletRun.PlayerPassives : null);
    }
}

/// <summary>
/// The run's deck as a list of entries (cost and name), cloned from an authored template entry. Shared by
/// the Draft scene and the card picks of the end-of-match rewards.
/// </summary>
public static class GauntletDeckList
{
    /// <summary>One entry per card, by mana cost then name; duplicates get their own row.</summary>
    public static void Refresh(TextMeshProUGUI header, CardButtonHandler template, List<CardButtonHandler> entries,
        IReadOnlyList<CardSO> deck)
    {
        if (header != null) header.text = deck.Count > 0 ? $"DECK ({deck.Count})" : "DECK";
        if (template == null) return;

        var cards = new List<CardSO>(deck.Count);
        foreach (var card in deck)
            if (card != null) cards.Add(card);
        cards.Sort((a, b) =>
        {
            int byCost = a.cost.CompareTo(b.cost);
            return byCost != 0 ? byCost : string.CompareOrdinal(a.cardName, b.cardName);
        });

        while (entries.Count < cards.Count)
        {
            var entry = Object.Instantiate(template, template.transform.parent);
            entries.Add(entry);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool used = i < cards.Count;
            entry.gameObject.SetActive(used);
            if (!used) continue;

            var card = cards[i];
            entry.Card = card;
            entry.SetName(card.cardName);
            entry.SetCost(card.cost);
        }
    }

    /// <summary>Removes the example entries the layout was authored with; the template is kept, hidden, to clone from.</summary>
    public static void ClearPlaceholders(CardButtonHandler template)
    {
        if (template == null) return;

        var parent = template.transform.parent;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i).gameObject;
            if (child != template.gameObject) Object.Destroy(child);
        }
        template.gameObject.SetActive(false);
    }
}

/// <summary>
/// The upcoming battle's briefing: the enemy hero as it will actually be fielded, the encounter's label
/// and the blessings it fights with. Shared by the Draft scene's Next Match panel
/// (<see cref="DraftController"/>) and the copy of that panel on the end-of-match review tabs
/// (<see cref="DraftPanelTabs"/>), so both read the run the same way.
/// </summary>
public static class GauntletNextMatch
{
    /// <summary>
    /// Fills a Next Match panel from the run's prepared enemy. <paramref name="rulesFontSize"/> is the
    /// panel's authored size: up to three blessings can outgrow it, so the text shrinks to fit from there.
    /// </summary>
    public static void Refresh(GauntletHeroPanelView enemyHero, TextMeshProUGUI headerText,
        TextMeshProUGUI rulesText, float rulesFontSize)
    {
        var encounter = GauntletRun.CurrentEncounter;
        var enemy = GauntletRun.EnemyHero;

        RefreshHero(enemyHero, enemy, enemy != null ? GauntletRun.EnemyPassives : null);

        if (headerText != null)
            headerText.text = encounter != null && !string.IsNullOrEmpty(encounter.label)
                ? encounter.label
                : $"Battle {GauntletRun.EncounterIndex + 1}";

        if (rulesText == null) return;
        if (encounter == null)
        {
            rulesText.text = string.Empty;
            return;
        }

        rulesText.enableAutoSizing = true;
        rulesText.fontSizeMax = rulesFontSize;
        rulesText.fontSizeMin = rulesFontSize * 0.6f;
        rulesText.text = BlessingRules();
    }

    /// <summary>
    /// Fills the panel with encounter <paramref name="encounterIndex"/>: any fight of the run, its enemy as
    /// rolled at the hero pick. The upcoming one reads the same as <see cref="Refresh"/>.
    /// </summary>
    public static void RefreshEncounter(int encounterIndex, GauntletHeroPanelView enemyHero,
        TextMeshProUGUI headerText, TextMeshProUGUI rulesText, float rulesFontSize)
    {
        var config = GauntletRun.Config;
        var encounter = config != null && encounterIndex >= 0 && encounterIndex < config.encounters.Count
            ? config.encounters[encounterIndex]
            : null;
        var enemy = GauntletRun.PreviewEnemy(encounterIndex);

        if (enemyHero != null)
            enemyHero.Show(enemy?.Hero, enemy?.StartingHealth ?? 0, enemy?.Hero != null ? enemy.Hero.attack : 0, enemy?.Passives);

        if (headerText != null)
            headerText.text = encounter != null && !string.IsNullOrEmpty(encounter.label)
                ? encounter.label
                : $"Battle {encounterIndex + 1}";

        if (rulesText == null) return;
        if (encounter == null || enemy == null)
        {
            rulesText.text = string.Empty;
            return;
        }

        rulesText.enableAutoSizing = true;
        rulesText.fontSizeMax = rulesFontSize;
        rulesText.fontSizeMin = rulesFontSize * 0.6f;
        rulesText.text = BlessingRules(enemy.Blessings);
    }

    /// <summary>
    /// The enemy's blessings under one header, one line each; stacked ones (Extra Passive) once, with a count.
    /// </summary>
    public static string BlessingRules() => BlessingRules(GauntletRun.EnemyBlessings);

    /// <summary>The same, for any enemy's blessings.</summary>
    public static string BlessingRules(IReadOnlyList<GauntletBlessing> blessings)
    {
        var rules = new System.Text.StringBuilder();

        //if (blessings.Count > 0) rules.Append("> Enemy blessings:");

        var listed = new List<GauntletBlessing>();
        foreach (var blessing in blessings)
        {
            if (listed.Contains(blessing)) continue;
            listed.Add(blessing);

            int stacks = 0;
            foreach (var b in blessings) if (b == blessing) stacks++;

            rules.Append($"<color=#FFD36B>{GauntletRun.BlessingName(blessing)}{(stacks > 1 ? $" x{stacks}" : "")}</color>: " +
                         GauntletRun.BlessingDescription(blessing) + "\n");
        }

        return rules.ToString();
    }

    /// <summary>A hero as an enemy would field it this battle (the encounter's health scaling).</summary>
    /// <param name="passives">Shown in place of the hero's own passives when given (a blessed enemy's).</param>
    public static void RefreshHero(GauntletHeroPanelView panel, HeroSO hero,
        IReadOnlyList<HeroPassiveSO> passives = null)
    {
        if (panel == null) return;
        panel.Show(hero, hero != null ? EnemyStartingHealth(hero) : 0, hero != null ? hero.attack : 0, passives);
    }

    /// <summary>Same scaling (and Bonus Health) <see cref="GauntletRun.ApplyEnemySetup"/> applies in the match.</summary>
    private static int EnemyStartingHealth(HeroSO hero) =>
        GauntletRun.EnemyStartingHealth(hero.defHealth > 0 ? hero.defHealth : hero.health);
}

/// <summary>
/// The row of encounter skulls: which battles are already won, and which one is up next. Shared by the
/// Draft scene's track (<see cref="DraftController"/>, which also makes its skulls hoverable) and the
/// read-only copy on the end-of-match review tabs (<see cref="DraftPanelTabs"/>).
/// </summary>
public static class GauntletBattleTrack
{
    /// <summary>True while <paramref name="nodeIndex"/> is the battle the run is about to fight.</summary>
    public static bool IsUpcoming(int nodeIndex) =>
        GauntletRun.Outcome == GauntletRun.RunOutcome.InProgress
        && GauntletRun.Hero != null
        && nodeIndex == GauntletRun.EncounterIndex;

    /// <summary>
    /// Shows one skull as the run leaves it: tinted with a defeated mark once its battle is won (a won
    /// run has beaten them all), highlighted while it is the one up next.
    /// </summary>
    public static void RefreshNode(Graphic skull, GameObject highlight, GameObject defeatedMark, int index,
        Color beatenSkullTint)
    {
        bool defeated = GauntletRun.Outcome == GauntletRun.RunOutcome.Won || index < GauntletRun.EncounterIndex;

        if (defeatedMark != null) defeatedMark.SetActive(defeated);
        if (skull != null) skull.color = defeated ? beatenSkullTint : Color.white;
        if (highlight != null) highlight.SetActive(IsUpcoming(index));
    }
}
