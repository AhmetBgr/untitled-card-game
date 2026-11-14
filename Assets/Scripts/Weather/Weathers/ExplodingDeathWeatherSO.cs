using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Volatile Air: a dying minion explodes, damaging every minion orthogonally next to it.</summary>
[CreateAssetMenu(fileName = "ExplodingDeathWeather", menuName = "Weather/Exploding Death")]
public class ExplodingDeathWeatherSO : WeatherSO
{
    [Min(0)] public int explosionDamage = 1;
    public Color blastTint = new Color(1f, 0.55f, 0.15f, 1f);

    public override WeatherEffect CreateEffect() => new ExplodingDeathWeather();
}

public class ExplodingDeathWeather : WeatherEffect
{
    private ExplodingDeathWeatherSO Cfg => (ExplodingDeathWeatherSO)Config;

    private static readonly Vector2Int[] Neighbours =
        { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

    public override IEnumerator OnMinionDied(MinionController minion, Vector2Int cell) => Explode(cell);

    private IEnumerator Explode(Vector2Int cell)
    {
        Board.PlayStrike(cell, Config.TileSprite, Cfg.blastTint);
        yield return new WaitForSeconds(0.25f);

        // Snapshot first. A neighbour killed here explodes in turn, through the scheduler, after this one.
        var victims = new List<MinionController>();
        foreach (var offset in Neighbours)
        {
            var victim = Board.MinionAt(cell + offset);
            if (victim != null) victims.Add(victim);
        }

        foreach (var victim in victims) Board.Damage(victim, Cfg.explosionDamage, "Blast");
        if (victims.Count > 0) yield return new WaitForSeconds(0.35f);
    }
}
