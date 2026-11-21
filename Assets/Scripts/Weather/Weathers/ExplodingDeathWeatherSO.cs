using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Volatile Air: a minion that dies while at <see cref="ExplodingDeathWeatherSO.explodeAtHealth"/> health
/// (1 by default) explodes, damaging every minion orthogonally next to it. Minions at that health carry a
/// warning icon.
/// </summary>
[CreateAssetMenu(fileName = "ExplodingDeathWeather", menuName = "Weather/Exploding Death")]
public class ExplodingDeathWeatherSO : WeatherSO
{
    [Min(0)] public int explosionDamage = 1;
    [Tooltip("Only a minion that dies while at exactly this much health explodes (killed from 1 Health).")]
    [Min(1)] public int explodeAtHealth = 1;
    [Tooltip("Longest the blast waits for the dying minion's death animation to finish, in seconds " +
             "(a safety net for a minion with no death clip).")]
    [Min(0f)] public float maxDeathWait = 3f;
    [Tooltip("Pause after the death animation ends before the blast goes off.")]
    [Min(0f)] public float delayAfterDeath = 0.35f;

    [Header("Blast")]
    [Tooltip("Burst played on every tile next to the dying minion (the meteor's impact works well). " +
             "Empty = a sprite flash tinted below.")]
    public GameObject blastPrefab;
    [Min(0.1f)] public float blastLifetime = 2f;
    [Tooltip("Size of the burst relative to the prefab as authored (0.7 = 30% smaller).")]
    [Min(0.05f)] public float blastScale = 0.7f;
    [Tooltip("Fallback flash colour, with no prefab.")]
    public Color blastTint = new Color(1f, 0.55f, 0.15f, 1f);

    [Header("Warning")]
    [Tooltip("Shown on every minion at the explode health, following it. Its breathing comes from its sprites' " +
             "material (SpriteBreath). Empty = the weather icon as a fallback.")]
    public GameObject warningPrefab;
    [Tooltip("Offset from the minion's centre (its local space).")]
    public Vector2 warningOffset = Vector2.zero;

    public override WeatherEffect CreateEffect() => new ExplodingDeathWeather();
}

public class ExplodingDeathWeather : WeatherEffect
{
    private ExplodingDeathWeatherSO Cfg => (ExplodingDeathWeatherSO)Config;

    private static readonly Vector2Int[] Neighbours =
        { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

    // Minions at the explode health as of the last frame, each with its warning. A death is checked against
    // this, since by the time OnMinionDied runs the minion's health is already 0 or less.
    private readonly Dictionary<MinionController, GameObject> _warnings = new Dictionary<MinionController, GameObject>();
    private readonly List<MinionController> _scratch = new List<MinionController>();

    public override void Tick()
    {
        // Health changes come from many places (damage, buffs, heals), so re-check every frame.
        _scratch.Clear();
        foreach (var minion in Board.AllMinions())
            if (minion.modal.health == Cfg.explodeAtHealth) _scratch.Add(minion);

        foreach (var minion in new List<MinionController>(_warnings.Keys))
            if (minion == null || !_scratch.Contains(minion)) RemoveWarning(minion);
        foreach (var minion in _scratch)
            if (!_warnings.ContainsKey(minion)) _warnings[minion] = SpawnWarning(minion);
    }

    public override IEnumerator OnMinionDied(MinionController minion, Vector2Int cell)
    {
        // Was it marked (at the explode health) going into this hit? Then it goes off.
        if (!_warnings.ContainsKey(minion)) return null;
        RemoveWarning(minion);
        return Explode(minion, cell);
    }

    /// <summary>The warning, as a child of the minion: it moves, hides and dies with it.</summary>
    private GameObject SpawnWarning(MinionController minion)
    {
        GameObject go;
        if (Cfg.warningPrefab != null)
        {
            go = Object.Instantiate(Cfg.warningPrefab, minion.transform);
        }
        else
        {
            go = new GameObject();
            go.transform.SetParent(minion.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Config.icon;
            sr.sortingLayerName = "Layer 3";
            sr.sortingOrder = 90;
            go.transform.localScale = Vector3.one * 0.4f;
        }
        go.name = "ExplosiveWarning";
        go.transform.localPosition = new Vector3(Cfg.warningOffset.x, Cfg.warningOffset.y, 0f);
        return go;
    }

    private void RemoveWarning(MinionController minion)
    {
        if (_warnings.TryGetValue(minion, out var go) && go != null) Object.Destroy(go);
        _warnings.Remove(minion);
    }

    private IEnumerator Explode(MinionController minion, Vector2Int cell)
    {
        // Go off once the minion is gone from view, not the moment its health hits 0: the death animation
        // starts a second after the death and ends with DestroySelf (which marks it done or destroys it).
        float waited = 0f;
        while (minion != null && !minion.DeathAnimationDone && waited < Cfg.maxDeathWait)
        {
            waited += Time.deltaTime;
            yield return null;
        }
        if (Cfg.delayAfterDeath > 0f) yield return new WaitForSeconds(Cfg.delayAfterDeath);
        if (Board.IsBattleOver) yield break;

        // The blast lands on the tiles around the dead minion, not on its own tile.
        foreach (var offset in Neighbours)
        {
            var target = cell + offset;
            if (Board.InGrid(target)) PlayBlast(target);
        }
        yield return new WaitForSeconds(0.25f);

        // Snapshot first. A neighbour killed here explodes in turn, through the scheduler, after this one.
        var victims = new List<MinionController>();
        foreach (var offset in Neighbours)
        {
            var victim = Board.MinionAt(cell + offset);
            if (victim != null) victims.Add(victim);
        }

        foreach (var victim in victims) Board.Damage(victim, Cfg.explosionDamage); // No label: the burst on the tile says it.
        if (victims.Count > 0) yield return new WaitForSeconds(0.35f);
    }

    private void PlayBlast(Vector2Int cell)
    {
        if (Cfg.blastPrefab != null)
        {
            var blast = Board.SpawnEffect(Cfg.blastPrefab, WeatherSystem.CellWorldPos(cell));
            if (blast == null) return;
            blast.transform.localScale *= Cfg.blastScale; // Its particles scale with the hierarchy.
            Object.Destroy(blast, Cfg.blastLifetime);
        }
        else
        {
            Board.PlayStrike(cell, Config.TileSprite, Cfg.blastTint);
        }
    }
}
