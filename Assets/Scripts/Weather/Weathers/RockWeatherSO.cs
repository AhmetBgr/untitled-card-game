using UnityEngine;

/// <summary>Landslide: a boulder sits on the centre tile, and no minion can move, be pushed or be summoned onto it.</summary>
[CreateAssetMenu(fileName = "RockWeather", menuName = "Weather/Rock")]
public class RockWeatherSO : WeatherSO
{
    public override WeatherEffect CreateEffect() => new RockWeather();
}

public class RockWeather : WeatherEffect
{
    public override void OnBattleStart()
    {
        var centre = new Vector2Int(Board.Width / 2, Board.Height / 2);
        var rock = Board.SpawnTileMarker(centre, Config.TileSprite, Config.tileTint, "Rock");

        // Holding the cell's obj slot is all a blocker needs: moves, pushes and summons all refuse an
        // occupied cell with no minion on it, like a wall. GridEntity re-registers it on every
        // GridChanged, the way minions keep their cells.
        var entity = rock.gameObject.AddComponent<GridEntity>();
        entity.type = GridEntityType.Obj;
        entity.WorldPos = WeatherSystem.CellWorldPos(centre);
        entity.AddToGridCell();
    }
}
