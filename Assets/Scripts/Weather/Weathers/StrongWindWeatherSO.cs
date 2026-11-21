using System.Collections;
using DG.Tweening;
using UnityEngine;

/// <summary>
/// Strong Wind: every few turns (both sides' turns count) a gust shoves every minion one tile in the
/// wind's direction. The first direction is rolled sideways at the start of the battle so it favours neither
/// side, and the wind turns around after every gust, so minions are blown back and forth.
/// </summary>
[CreateAssetMenu(fileName = "StrongWindWeather", menuName = "Weather/Strong Wind")]
public class StrongWindWeatherSO : WeatherSO
{
    [Min(1)] public int turnsBetweenGusts = 3;

    public override WeatherEffect CreateEffect() => new StrongWindWeather();
}

public class StrongWindWeather : WeatherEffect
{
    private StrongWindWeatherSO Cfg => (StrongWindWeatherSO)Config;
    private Vector3Int _dir;
    private int _turnsLeft;
    private Wind2D _wind;

    public override void OnBattleStart()
    {
        _dir = Board.Rng.Next(2) == 0 ? Vector3Int.left : Vector3Int.right;
        _turnsLeft = Cfg.turnsBetweenGusts;

        // The visual idles as a breeze blowing the way the next gust will.
        _wind = Board.Visual != null ? Board.Visual.GetComponentInChildren<Wind2D>() : null;
        if (_wind != null) _wind.SetDirection(_dir.x);
    }

    /// <summary>Where the next gust blows (left or right); flips after each gust. For the weather's visual.</summary>
    public Vector3Int Direction => _dir;

    private string DirName => _dir == Vector3Int.left ? "west" : "east";

    public override string Status => _turnsLeft <= 1
        ? $"Blowing {DirName}: gust at the end of this turn"
        : $"Blowing {DirName}: gust in {_turnsLeft} turns";

    public override int? Countdown => Mathf.Max(1, _turnsLeft);

    public override IEnumerator OnTurnEnded(bool playerTurn)
    {
        _turnsLeft--;
        if (_turnsLeft > 0) return null;

        _turnsLeft = Cfg.turnsBetweenGusts;
        return Gust();
    }

    private IEnumerator Gust()
    {
        // The breeze picks up into a gust first, so the dust visibly blows the minions along; the visual
        // then dies down, turns around and settles into a breeze the other way on its own.
        if (_wind != null)
        {
            _wind.PlayGust(_dir.x);
            yield return new WaitForSeconds(_wind.GustLeadTime);
        }

        // Downwind column first, so a whole row slides together instead of the back minion bumping into
        // the one ahead of it before that one has moved.
        int start = _dir.x > 0 ? Board.Width - 1 : 0;
        int step = _dir.x > 0 ? -1 : 1;
        bool anyMoved = false;

        for (int x = start; x >= 0 && x < Board.Width; x += step)
        {
            for (int y = 0; y < Board.Height; y++)
            {
                var minion = Board.MinionAt(new Vector2Int(x, y));
                if (minion == null) continue;

                if (minion.CanBePushedForward(_dir))
                {
                    // Move() bypasses canMove/age, so crippled and fresh minions are blown too.
                    minion.PushForward(_dir);
                    anyMoved = true;
                }
                else
                {
                    // Pinned against the edge, the rock or another unit: a nudge, never a collision.
                    minion.transform.DOPunchPosition((Vector3)_dir * 0.08f, 0.25f, 0).SetLink(minion.gameObject);
                }
            }
        }

        yield return new WaitForSeconds(anyMoved ? 0.35f : 0.25f);

        // The wind turns around after every gust; the HUD shows the new direction once this finishes.
        _dir = -_dir;
    }
}
