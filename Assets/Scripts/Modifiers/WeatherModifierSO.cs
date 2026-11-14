using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Picks the match's weather: Clear, Random (rolled from the weather pool), or one specific weather.
/// Stored as an index like any whole-number modifier -- 0 = Clear, 1 = Random, 2.. = the weather
/// database's list in order -- so it rides the slider row and the saved profile with no new plumbing.
/// The range follows the database, so a weather added there shows up here without touching this asset.
/// </summary>
[CreateAssetMenu(fileName = "Weather", menuName = "Game Modes/Modifiers/Weather")]
public class WeatherModifierSO : GameModifierSO
{
    public const int Clear = 0;
    public const int Random = 1;
    private const int FirstWeather = 2;

    [Tooltip("Clear, Random, or the database index + 2 of the weather a fresh profile starts on.")]
    [Min(0)] public int defaultValue = Clear;

    public override ModifierValueKind Kind => ModifierValueKind.Int;

    public int Min => Clear;
    public int Max => FirstWeather + Weathers().Count - 1;

    public override float DefaultValue => Mathf.Clamp(defaultValue, Min, Max);
    public override float Clamp(float value) => Mathf.Clamp(Mathf.RoundToInt(value), Min, Max);

    public override string FormatValue(float value)
    {
        int index = Mathf.RoundToInt(value);
        if (index <= Clear) return "Clear";
        if (index == Random) return "Random";

        List<WeatherSO> weathers = Weathers();
        int i = index - FirstWeather;
        return i < weathers.Count ? weathers[i].DisplayName : "Clear";
    }

    public override bool TryChooseWeather(float value, out WeatherSO weather)
    {
        int index = Mathf.RoundToInt(Clamp(value));
        weather = null;

        if (index == Random)
        {
            List<WeatherSO> pool = WeatherDatabaseSO.Load().Pool();
            if (pool.Count > 0) weather = pool[UnityEngine.Random.Range(0, pool.Count)];
        }
        else if (index >= FirstWeather)
        {
            List<WeatherSO> weathers = Weathers();
            int i = index - FirstWeather;
            if (i < weathers.Count) weather = weathers[i];
        }

        return true;
    }

    /// <summary>Every enabled weather in the database, blanks and disabled (out of the random pool) ones skipped.</summary>
    private static List<WeatherSO> Weathers()
    {
        var list = new List<WeatherSO>();
        foreach (WeatherSO weather in WeatherDatabaseSO.Load().weathers)
            if (weather != null && weather.inRandomPool && !list.Contains(weather)) list.Add(weather);
        return list;
    }
}
