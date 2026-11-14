using UnityEngine;

/// <summary>Sandstorm: spells are cheaper and minions dearer.</summary>
[CreateAssetMenu(fileName = "SandstormWeather", menuName = "Weather/Sandstorm")]
public class SandstormWeatherSO : WeatherSO
{
    public int spellCostChange = -1;
    public int minionCostChange = 1;

    public override WeatherEffect CreateEffect() => new SandstormWeather();
}

public class SandstormWeather : WeatherEffect
{
    // A card with no health is a spell: the same test GameManager.PlayCard uses.
    public override int AdjustCost(CardSO card, int cost)
    {
        var cfg = (SandstormWeatherSO)Config;
        return cost + (card.health > 0 ? cfg.minionCostChange : cfg.spellCostChange);
    }
}
