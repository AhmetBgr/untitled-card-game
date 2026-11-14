using UnityEngine;

/// <summary>Blood Moon: every hit (attacks, spells, collisions, fatigue) deals multiplied damage.</summary>
[CreateAssetMenu(fileName = "DamageMultiplierWeather", menuName = "Weather/Damage Multiplier")]
public class DamageMultiplierWeatherSO : WeatherSO
{
    [Min(1)] public int multiplier = 2;

    public override WeatherEffect CreateEffect() => new DamageMultiplierWeather();
}

public class DamageMultiplierWeather : WeatherEffect
{
    public override int ModifyDamage(int damage) => damage * ((DamageMultiplierWeatherSO)Config).multiplier;
}
