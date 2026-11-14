using UnityEngine;

/// <summary>Restless Earth: every minion deals extra clash (collision) damage, and clashes hurt allies too.</summary>
[CreateAssetMenu(fileName = "ClashWeather", menuName = "Weather/Clash")]
public class ClashWeatherSO : WeatherSO
{
    [Min(0)] public int bonusClashDamage = 1;
    public bool damagesFriendlyMinions = true;

    public override WeatherEffect CreateEffect() => new ClashWeather();
}

public class ClashWeather : WeatherEffect
{
    private ClashWeatherSO Cfg => (ClashWeatherSO)Config;

    public override bool FriendlyCollisionDamage => Cfg.damagesFriendlyMinions;

    public override void OnMinionSummoned(MinionController minion)
    {
        if (minion.modal != null) minion.modal.collisionDamage += Cfg.bonusClashDamage;
    }
}
