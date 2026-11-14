using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Overgrowth: a few random tiles are brambled; a minion moving onto one takes damage.</summary>
[CreateAssetMenu(fileName = "BrambleWeather", menuName = "Weather/Bramble")]
public class BrambleWeatherSO : WeatherSO
{
    [Min(1)] public int brambleTiles = 2;
    [Min(0)] public int damage = 1;

    public override WeatherEffect CreateEffect() => new BrambleWeather();
}

public class BrambleWeather : WeatherEffect
{
    private BrambleWeatherSO Cfg => (BrambleWeatherSO)Config;
    private readonly HashSet<Vector2Int> _brambles = new HashSet<Vector2Int>();

    public override void OnBattleStart()
    {
        foreach (var cell in Board.RandomCells(Cfg.brambleTiles))
        {
            _brambles.Add(cell);
            Board.SpawnTileMarker(cell, Config.TileSprite, Config.tileTint, "Bramble");
        }
    }

    public override void OnMinionEnteredCell(MinionController minion, Vector2Int cell)
    {
        if (_brambles.Contains(cell)) Board.Damage(minion, Cfg.damage, "Bramble");
    }

    // Being summoned onto a bramble hurts too, not just walking onto one.
    public override IEnumerator AfterMinionSummoned(MinionController minion, Vector2Int cell)
    {
        if (!_brambles.Contains(cell)) return null;
        return Prick(minion);
    }

    private IEnumerator Prick(MinionController minion)
    {
        // Let the summon's appear animation land before the thorns bite.
        yield return new WaitForSeconds(0.3f);
        Board.Damage(minion, Cfg.damage, "Bramble");
        yield return new WaitForSeconds(0.3f);
    }
}
