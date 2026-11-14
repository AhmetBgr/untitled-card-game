using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Thunderstorm: from the player's 3rd turn (configurable), at the end of each of their turns, lightning strikes the minion with the most health.</summary>
[CreateAssetMenu(fileName = "ThunderstormWeather", menuName = "Weather/Thunderstorm")]
public class ThunderstormWeatherSO : WeatherSO
{
    [Min(0)] public int damage = 1;
    [Tooltip("Your turn number (1 = your first turn) whose end brings the first bolt. Before it the storm only rains.")]
    [Min(1)] public int firstStrikeTurn = 3;
    public Color boltTint = new Color(0.75f, 0.85f, 1f, 1f);

    [Header("Warning")]
    [Tooltip("Shown on every minion the next bolt could hit (the highest health; ties all get one). " +
             "Follows the minion. Its breathing comes from its sprites' material (SpriteBreath). " +
             "Empty = the weather icon as a fallback.")]
    public GameObject warningPrefab;
    [Tooltip("Offset from the minion's position (world units).")]
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

    /// <summary>Your turns started so far this battle (1 during your first turn).</summary>
    private static int PlayerTurn => GameManager.Instance != null ? GameManager.Instance.PlayerTurnsStarted : 0;

    /// <summary>The storm has started striking: from the first strike turn on (and the enemy turns after it).</summary>
    private bool Active => PlayerTurn >= Cfg.firstStrikeTurn;

    public override IEnumerator OnTurnEnded(bool playerTurn) => playerTurn && Active ? Strike() : null;

    public override string Status
    {
        get
        {
            int turnsLeft = Cfg.firstStrikeTurn - PlayerTurn;
            if (turnsLeft <= 0) return "Strikes at the end of your turn";
            return turnsLeft == 1 ? "First strike: end of your next turn" : $"First strike: end of your turn {Cfg.firstStrikeTurn}";
        }
    }

    public override void Tick()
    {
        // Health changes come from many places (damage, buffs, heals, deaths), so just re-check every frame:
        // a 3x3 board is a handful of minions.
        if (_striking) { FollowMinions(); return; }

        // No target marked before the storm can strike.
        if (Active) Tallest(_scratch);
        else _scratch.Clear();
        foreach (var minion in new List<MinionController>(_warnings.Keys))
            if (!_scratch.Contains(minion)) RemoveWarning(minion);
        foreach (var minion in _scratch)
            if (!_warnings.ContainsKey(minion)) _warnings[minion] = SpawnWarning();
        FollowMinions();
    }

    private void FollowMinions()
    {
        foreach (var pair in _warnings)
        {
            if (pair.Key == null || pair.Value == null) continue;
            Vector3 p = pair.Key.transform.position;
            pair.Value.transform.position = new Vector3(p.x + Cfg.warningOffset.x, p.y + Cfg.warningOffset.y, pair.Value.transform.position.z);
        }
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

    private GameObject SpawnWarning()
    {
        GameObject go;
        if (Cfg.warningPrefab != null)
        {
            go = Object.Instantiate(Cfg.warningPrefab, Board.transform);
        }
        else
        {
            go = new GameObject("ThunderWarning");
            go.transform.SetParent(Board.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Config.icon;
            sr.sortingLayerName = "Layer 3";
            sr.sortingOrder = 90;
            go.transform.localScale = Vector3.one * 0.4f;
        }
        go.name = "ThunderWarning";
        return go;
    }

    private void RemoveWarning(MinionController minion)
    {
        if (_warnings.TryGetValue(minion, out var go) && go != null) Object.Destroy(go);
        _warnings.Remove(minion);
    }

    private IEnumerator Strike()
    {
        // Every minion tied for the most health is hit, all by the same strike.
        var targets = new List<MinionController>();
        Tallest(targets);
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
