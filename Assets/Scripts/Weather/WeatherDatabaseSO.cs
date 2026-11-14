using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every weather in the game (Resources/Weather/WeatherDatabase). Forged in Battle rolls each battle's
/// weather from <see cref="Pool"/>; saves store a weather by asset name and resolve it back through
/// <see cref="GetByName"/>.
/// </summary>
[CreateAssetMenu(fileName = "WeatherDatabase", menuName = "Weather/Weather Database")]
public class WeatherDatabaseSO : ScriptableObject
{
    public const string ResourcePath = "Weather/WeatherDatabase";

    public List<WeatherSO> weathers = new List<WeatherSO>();

    [Header("Outside Forged in Battle")]
    [Tooltip("Played in every match that is not part of a Forged in Battle run (Quick Play, custom games). " +
             "Meant for testing a weather; leave empty for clear skies.")]
    public WeatherSO forceWeatherOutsideRuns;

    [Tooltip("Matches outside a run roll a random weather from the pool. Ignored when a weather is forced above.")]
    public bool randomWeatherOutsideRuns = false;

    [Header("Board Markers")]
    [Tooltip("Sorting order of tile markers, relative to the highest order on the board cell they sit on.")]
    public int tileMarkerOrderOffset = 1;
    [Range(0f, 1f)] public float tileMarkerAlpha = 0.85f;

    [Header("HUD")]
    [Tooltip("Where the weather badge sits on the game canvas (anchor and pivot share this, 0..1).")]
    public Vector2 hudAnchor = new Vector2(0f, 1f);
    public Vector2 hudOffset = new Vector2(24f, -190f);
    public float hudIconSize = 72f;
    public float hudWidth = 320f;
    public float hudNameFontSize = 30f;
    public float hudTextFontSize = 20f;
    public Color hudNameColor = new Color(1f, 0.83f, 0.42f, 1f);
    public Color hudTextColor = Color.white;
    public Color hudBackgroundColor = new Color(0f, 0f, 0f, 0.55f);

    private static WeatherDatabaseSO _cached;

    public static WeatherDatabaseSO Load()
    {
        if (_cached != null) return _cached;

        _cached = Resources.Load<WeatherDatabaseSO>(ResourcePath);
        if (_cached == null)
        {
            Debug.LogWarning($"[Weather] No database at Resources/{ResourcePath}; battles get no weather.");
            _cached = CreateInstance<WeatherDatabaseSO>();
        }
        return _cached;
    }

    /// <summary>The weathers a battle can roll.</summary>
    public List<WeatherSO> Pool()
    {
        var pool = new List<WeatherSO>();
        foreach (var weather in weathers)
            if (weather != null && weather.inRandomPool && !pool.Contains(weather)) pool.Add(weather);
        return pool;
    }

    /// <summary>The weather saved under <paramref name="assetName"/>, or null (clear sky) if there is none.</summary>
    public WeatherSO GetByName(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return null;
        foreach (var weather in weathers)
            if (weather != null && weather.name == assetName) return weather;

        Debug.LogWarning($"[Weather] Unknown weather '{assetName}'; playing with a clear sky.");
        return null;
    }

    /// <summary>The weather of a match that is not part of a run.</summary>
    public WeatherSO WeatherOutsideRuns()
    {
        if (forceWeatherOutsideRuns != null) return forceWeatherOutsideRuns;
        if (!randomWeatherOutsideRuns) return null;

        var pool = Pool();
        return pool.Count > 0 ? pool[Random.Range(0, pool.Count)] : null;
    }
}
