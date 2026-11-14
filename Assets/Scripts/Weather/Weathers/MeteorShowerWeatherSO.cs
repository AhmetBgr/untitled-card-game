using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;

/// <summary>
/// Meteor Shower: a tile is marked in advance; at the start of each of the player's turns a meteor lands
/// there, hurting whatever stands on it, and the next landing spot is marked.
/// </summary>
[CreateAssetMenu(fileName = "MeteorShowerWeather", menuName = "Weather/Meteor Shower")]
public class MeteorShowerWeatherSO : WeatherSO
{
    [Min(0)] public int damage = 3;

    [Header("Fall")]
    [Tooltip("The meteor itself (ball + trail), dropped from the sky onto the target. Built by " +
             "Tools > Weather > Build Meteor VFX. Empty = no fall, just the impact.")]
    public GameObject fallPrefab;
    [Tooltip("Where the fall starts, relative to the target tile, in world units (cells).")]
    public Vector2 fallFrom = new Vector2(2.5f, 6f);
    [Min(0.05f)] public float fallDuration = 0.85f;
    [Tooltip("How the fall speeds up. In* = starts slow and accelerates into the ground.")]
    public Ease fallEase = Ease.InSine;
    [Tooltip("On = use the curve below instead of the ease above (x: time 0..1, y: progress 0 = sky, 1 = tile).")]
    public bool useFallCurve;
    public AnimationCurve fallCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [Tooltip("How long the trail is left to burn out after the ball lands.")]
    [Min(0f)] public float trailLinger = 1f;

    [Header("Impact")]
    [Tooltip("Burst played on the tile as the meteor lands. Empty = the sprite flash below.")]
    public GameObject impactPrefab;
    [Min(0.1f)] public float impactLifetime = 2f;
    [Tooltip("Camera shake on landing, in world units. 0 = none.")]
    [Min(0f)] public float shakeStrength = 0.04f;
    [Min(0f)] public float shakeDuration = 0.2f;
    [Tooltip("Fallback impact with no prefab: sprite of the flash. Empty = the icon.")]
    public Sprite impactSprite;
    public Color impactTint = new Color(1f, 0.45f, 0.1f, 1f);

    [Header("Warning")]
    [Tooltip("Prefab laid on the target tile until the meteor lands. Its CanvasGroup breathes its alpha and " +
             "its TMP text (if any) shows the damage. Empty = the plain tile sprite marker.")]
    public GameObject warningPrefab;
    [Tooltip("Breath: the warning's canvas alpha swings between these two.")]
    [Range(0f, 1f)] public float warningMinAlpha = 0.35f;
    [Range(0f, 1f)] public float warningMaxAlpha = 1f;
    [Tooltip("Seconds for one half of the breath (max > min). A full in-and-out is twice this.")]
    [Min(0.05f)] public float warningBreathPeriod = 0.7f;
    public Ease warningBreathEase = Ease.InOutSine;
    [Tooltip("When the target moves: the old warning fades out over this long...")]
    [Min(0f)] public float warningFadeOut = 0.25f;
    [Tooltip("...then the warning fades in on the new tile over this long.")]
    [Min(0f)] public float warningFadeIn = 0.3f;

    public override WeatherEffect CreateEffect() => new MeteorShowerWeather();
}

public class MeteorShowerWeather : WeatherEffect
{
    private MeteorShowerWeatherSO Cfg => (MeteorShowerWeatherSO)Config;
    private Vector2Int _target;
    private Transform _marker;

    // Everything the warning fades: its canvas groups (which breathe) and its sprites (the cell outline),
    // each with the alpha it was authored at, so a fade-in lands back on the look the prefab was built with.
    private CanvasGroup[] _groups = new CanvasGroup[0];
    private SpriteRenderer[] _sprites = new SpriteRenderer[0];
    private float[] _spriteAlphas = new float[0];

    public override void OnBattleStart()
    {
        _target = NextTarget();
        _marker = SpawnWarning(_target);
    }

    public override string Status => "Next impact: the marked tile, at the start of your turn";

    public override IEnumerator OnTurnStarted(bool playerTurn) => playerTurn ? Fall() : null;

    private Transform SpawnWarning(Vector2Int cell)
    {
        if (Cfg.warningPrefab == null)
        {
            var sr = Board.SpawnTileMarker(cell, Config.TileSprite, Config.tileTint, "MeteorTarget");
            if (sr == null) return null;
            CaptureFaders(sr.gameObject);
            StartIdle();
            return sr.transform;
        }

        var go = Board.SpawnTilePrefab(cell, Cfg.warningPrefab, "MeteorTarget");

        var label = go.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = Cfg.damage.ToString();

        CaptureFaders(go);
        foreach (var group in _groups)
        {
            // A warning, not a button: clicks go through to the tile and the minion on it.
            group.blocksRaycasts = false;
            group.interactable = false;
        }
        StartIdle();
        return go.transform;
    }

    private void CaptureFaders(GameObject root)
    {
        _groups = root.GetComponentsInChildren<CanvasGroup>(true);
        _sprites = root.GetComponentsInChildren<SpriteRenderer>(true);
        _spriteAlphas = new float[_sprites.Length];
        for (int i = 0; i < _sprites.Length; i++) _spriteAlphas[i] = _sprites[i].color.a;
    }

    /// <summary>The looping "about to hit here" animation: the canvas breathes; a bare sprite marker pulses.</summary>
    private void StartIdle()
    {
        foreach (var group in _groups)
            WeatherSystem.Breathe(group, Cfg.warningMinAlpha, Cfg.warningMaxAlpha, Cfg.warningBreathPeriod, Cfg.warningBreathEase);
        if (_groups.Length == 0)
            foreach (var sr in _sprites) WeatherSystem.Pulse(sr);
    }

    /// <summary>Fades the whole warning to <paramref name="t"/> (0 = gone, 1 = as authored) over <paramref name="duration"/>.</summary>
    private Sequence FadeWarning(float t, float duration)
    {
        var seq = DOTween.Sequence();
        foreach (var group in _groups)
        {
            group.DOKill();
            seq.Join(group.DOFade(t * Cfg.warningMaxAlpha, duration));
        }
        for (int i = 0; i < _sprites.Length; i++)
        {
            _sprites[i].DOKill();
            seq.Join(_sprites[i].DOFade(_spriteAlphas[i] * t, duration));
        }
        if (_marker != null) seq.SetLink(_marker.gameObject);
        return seq;
    }

    /// <summary>Old tile fades out, the warning jumps, then fades back in on the new tile and starts breathing again.</summary>
    private IEnumerator MoveWarning(Vector2Int cell)
    {
        if (_marker == null) yield break;

        yield return FadeWarning(0f, Cfg.warningFadeOut).WaitForCompletion();
        if (_marker == null) yield break;

        _marker.DOKill();
        _marker.position = WeatherSystem.CellWorldPos(cell);
        _marker.name = $"MeteorTarget {cell.x},{cell.y}";

        yield return FadeWarning(1f, Cfg.warningFadeIn).WaitForCompletion();
        if (_marker == null) yield break;

        StartIdle();
    }

    private IEnumerator Fall()
    {
        if (Cfg.fallPrefab != null) yield return Drop();
        Land();
        yield return new WaitForSeconds(Cfg.impactPrefab != null ? 0.25f : 0.3f);

        var victim = Board.MinionAt(_target);
        if (victim != null)
        {
            Board.Damage(victim, Cfg.damage, "Meteor");
            yield return new WaitForSeconds(0.4f);
        }

        _target = NextTarget();
        yield return MoveWarning(_target);
    }

    /// <summary>The ball streaks in from the sky onto the target, its position snapped to the art's pixel grid.</summary>
    private IEnumerator Drop()
    {
        Vector3 end = WeatherSystem.CellWorldPos(_target);
        Vector3 start = end + (Vector3)Cfg.fallFrom;

        var meteor = Board.SpawnEffect(Cfg.fallPrefab, start);
        if (meteor == null) yield break;

        var tween = DOVirtual.Float(0f, 1f, Cfg.fallDuration, t =>
            {
                if (meteor != null) meteor.transform.position = WeatherSystem.SnapToPixel(Vector3.LerpUnclamped(start, end, t));
            })
            .SetLink(meteor);
        if (Cfg.useFallCurve && Cfg.fallCurve != null && Cfg.fallCurve.length > 1) tween.SetEase(Cfg.fallCurve);
        else tween.SetEase(Cfg.fallEase);
        yield return tween.WaitForCompletion();
        if (meteor == null) yield break;

        // The rock is gone into the ground; its trail stops feeding and burns out where it hangs.
        foreach (var sr in meteor.GetComponentsInChildren<SpriteRenderer>()) sr.enabled = false;
        foreach (var ps in meteor.GetComponentsInChildren<ParticleSystem>())
            ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        Object.Destroy(meteor, Cfg.trailLinger);
    }

    private void Land()
    {
        if (Cfg.impactPrefab != null)
        {
            var impact = Board.SpawnEffect(Cfg.impactPrefab, WeatherSystem.CellWorldPos(_target));
            if (impact != null) Object.Destroy(impact, Cfg.impactLifetime);
        }
        else
        {
            var sprite = Cfg.impactSprite != null ? Cfg.impactSprite : Config.icon;
            Board.PlayStrike(_target, sprite, Cfg.impactTint, 0.7f);
        }

        WeatherSystem.ShakeCamera(Cfg.shakeStrength, Cfg.shakeDuration);
    }

    private Vector2Int NextTarget()
    {
        var cells = Board.RandomCells(1);
        return cells.Count > 0 ? cells[0] : Vector2Int.zero;
    }
}
