using System.Collections.Generic;
using UnityEngine;

/// <summary>Fog: a random column is fogged; a minion standing in it has less attack for as long as it stays.</summary>
[CreateAssetMenu(fileName = "FogWeather", menuName = "Weather/Fog")]
public class FogWeatherSO : WeatherSO
{
    [Min(0)] public int attackPenalty = 1;

    [Tooltip("Also tint the fogged column's tiles. The column fog itself is the FogColumn2D in the visual prefab.")]
    public bool markTiles = false;

    public override WeatherEffect CreateEffect() => new FogWeather();
}

public class FogWeather : WeatherEffect
{
    private FogWeatherSO Cfg => (FogWeatherSO)Config;
    private int _column;

    /// <summary>Attack actually taken from each fogged minion (never more than it had), to give back when it leaves.</summary>
    private readonly Dictionary<MinionController, int> _taken = new Dictionary<MinionController, int>();

    public override void OnBattleStart()
    {
        _column = Board.Rng.Next(Mathf.Max(1, Board.Width));

        if (Cfg.markTiles)
            for (int y = 0; y < Board.Height; y++)
                Board.SpawnTileMarker(new Vector2Int(_column, y), Config.TileSprite, Config.tileTint, "Fog");

        // The visual prefab's column fog (built centred on the board) slides over onto the fogged column.
        // Any other effects in the prefab, like the ambient full-screen fog, are left where they are.
        if (Board.Visual != null)
        {
            foreach (var band in Board.Visual.GetComponentsInChildren<FogColumn2D>(true))
            {
                Vector3 p = band.transform.position;
                band.transform.position = new Vector3(WeatherSystem.CellWorldPos(new Vector2Int(_column, 0)).x, p.y, p.z);
            }
        }
    }

    public override void OnMinionSummoned(MinionController minion) => Refresh();

    public override void OnBoardChanged() => Refresh();

    private void Refresh()
    {
        // Forget the dead; there is nothing to give back to them.
        var gone = new List<MinionController>();
        foreach (var pair in _taken) if (!WeatherSystem.IsAlive(pair.Key)) gone.Add(pair.Key);
        foreach (var m in gone) _taken.Remove(m);

        foreach (var minion in Board.AllMinions())
        {
            bool inFog = WeatherSystem.CellOf(minion).x == _column;
            bool fogged = _taken.TryGetValue(minion, out int taken);

            if (inFog && !fogged)
            {
                int penalty = Mathf.Clamp(Cfg.attackPenalty, 0, Mathf.Max(0, minion.modal.attack));
                _taken[minion] = penalty;
                if (penalty > 0) Apply(minion, -penalty);
            }
            else if (!inFog && fogged)
            {
                _taken.Remove(minion);
                if (taken > 0) Apply(minion, taken);
            }
        }
    }

    private static void Apply(MinionController minion, int delta)
    {
        minion.modal.attack += delta;
        if (minion.view != null) minion.view.UpdateView(minion.modal);
    }
}
