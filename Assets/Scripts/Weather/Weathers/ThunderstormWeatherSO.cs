using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Thunderstorm: at the end of each of the player's turns, from the first, every minion tied for the most
/// health rolls on its own to be struck by lightning. The chance starts low and climbs after every turn's
/// rolls, strike or not, so the storm grows more dangerous the longer the battle runs.
/// </summary>
[CreateAssetMenu(fileName = "ThunderstormWeather", menuName = "Weather/Thunderstorm")]
public class ThunderstormWeatherSO : WeatherSO
{
    [Min(0)] public int damage = 1;

    [Header("Chance")]
    [Tooltip("Percent chance of a strike at the end of your first turn.")]
    [Range(0, 100)] public int startChance = 30;
    [Tooltip("Percent added to the chance after every end of your turn, whether anything was struck or not.")]
    [Min(0)] public int chanceIncrease = 10;
    [Tooltip("The chance never climbs past this.")]
    [Range(0, 100)] public int maxChance = 90;

    public Color boltTint = new Color(0.75f, 0.85f, 1f, 1f);

    [Header("Warning")]
    [Tooltip("Shown on every minion the next bolt could hit (the highest health; ties all get one). " +
             "Follows the minion. Its breathing comes from its sprites' material (SpriteBreath). " +
             "Empty = the weather icon as a fallback.")]
    public GameObject warningPrefab;
    [Tooltip("Offset from the minion's centre (its local space).")]
    public Vector2 warningOffset = Vector2.zero;

    public override WeatherEffect CreateEffect() => new ThunderstormWeather();
}

public class ThunderstormWeather : WeatherEffect
{
    private ThunderstormWeatherSO Cfg => (ThunderstormWeatherSO)Config;

    // Warning marker per minion currently in line for the next bolt.
    private readonly Dictionary<MinionController, GameObject> _warnings = new Dictionary<MinionController, GameObject>();
    private readonly List<MinionController> _scratch = new List<MinionController>();
    private bool _striking;

    /// <summary>Percent chance each target is struck at the end of your next turn.</summary>
    private int _chance;

    private int MaxChance => Mathf.Clamp(Cfg.maxChance, 0, 100);

    public override void OnBattleStart() => _chance = Mathf.Clamp(Cfg.startChance, 0, MaxChance);

    public override IEnumerator OnTurnEnded(bool playerTurn)
    {
        if (!playerTurn) return null;

        // The rolls happen when the strike runs (the targets are picked then); the odds rise now, once per turn.
        int chance = _chance;
        _chance = Mathf.Min(MaxChance, _chance + Cfg.chanceIncrease);
        return Strike(chance);
    }

    public override string Badge => $"{_chance}%";

    public override bool BadgeUrgent => _chance >= MaxChance;

    public override string Status => _chance >= MaxChance
        ? $"Each marked minion: {_chance}% chance to be struck at the end of your turn (maximum)"
        : $"Each marked minion: {_chance}% chance to be struck at the end of your turn (+{Cfg.chanceIncrease}% each turn, up to {MaxChance}%)";

    public override void Tick()
    {
        // Health changes come from many places (damage, buffs, heals, deaths), so just re-check every frame:
        // a 3x3 board is a handful of minions.
        if (_striking) return;

        // Every minion the next bolt would hit is marked, whatever the odds.
        Tallest(_scratch);
        foreach (var minion in new List<MinionController>(_warnings.Keys))
            if (!_scratch.Contains(minion)) RemoveWarning(minion);
        foreach (var minion in _scratch)
            if (!_warnings.ContainsKey(minion)) _warnings[minion] = SpawnWarning(minion);
    }

    private void Tallest(List<MinionController> into)
    {
        into.Clear();
        int best = int.MinValue;
        foreach (var minion in Board.AllMinions())
        {
            if (minion.modal.health > best) { best = minion.modal.health; into.Clear(); }
            if (minion.modal.health == best) into.Add(minion);
        }
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
            go = new GameObject("ThunderWarning");
            go.transform.SetParent(minion.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Config.icon;
            sr.sortingLayerName = "Layer 3";
            sr.sortingOrder = 90;
            go.transform.localScale = Vector3.one * 0.4f;
        }
        go.name = "ThunderWarning";
        go.transform.localPosition = new Vector3(Cfg.warningOffset.x, Cfg.warningOffset.y, 0f);
        return go;
    }

    private void RemoveWarning(MinionController minion)
    {
        if (_warnings.TryGetValue(minion, out var go) && go != null) Object.Destroy(go);
        _warnings.Remove(minion);
    }

    private IEnumerator Strike(int chance)
    {
        // Every minion tied for the most health rolls on its own; the ones that fail are all hit by one strike.
        // Rolled on the battle's own stream, so a restarted Forged in Battle fight gets the same storm.
        var targets = new List<MinionController>();
        Tallest(targets);
        targets.RemoveAll(_ => Board.Rng.Next(100) >= chance);
        if (targets.Count == 0) yield break;

        _striking = true;
        try
        {
            var flash = Board.Visual != null ? Board.Visual.GetComponentInChildren<LightningFlash2D>() : null;
            if (flash != null)
            {
                // Bolts, radial flash and delayed thunder live on the weather's visual, so they are tuned there.
                var points = new List<Vector3>(targets.Count);
                foreach (var target in targets) points.Add(target.transform.position);
                flash.Strike(points);
                yield return new WaitForSeconds(flash.ImpactTime);
            }
            else
            {
                foreach (var target in targets)
                    Board.PlayStrike(WeatherSystem.CellOf(target), Config.icon, Cfg.boltTint, 0.5f);
                yield return new WaitForSeconds(0.2f);
            }

            // Snapshot taken above, so one bolt's damage (a death, an on-damage effect) cannot change who
            // the others hit; just skip any that died in the meantime.
            foreach (var target in targets)
                if (WeatherSystem.IsAlive(target)) Board.Damage(target, Cfg.damage, "Lightning");
            yield return new WaitForSeconds(0.45f);
        }
        finally
        {
            _striking = false;
        }
    }
}
