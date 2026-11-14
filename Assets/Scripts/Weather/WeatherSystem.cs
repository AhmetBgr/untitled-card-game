using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The weather of the battle being fought: owns its <see cref="WeatherEffect"/>, the markers it lays on
/// the board and the HUD badge naming it. GameManager creates it in Awake and drives the turn hooks; the
/// rest of the game asks it through the static facade (<see cref="ModifyDamage"/>,
/// <see cref="AdjustCost"/>, <see cref="FriendlyCollisionDamage"/>, ...), which is a no-op with no
/// weather, so a clear-sky match plays exactly as before.
///
/// Board tiles are addressed by grid index; a cell's world position is (x, -y), as in GridManager.
/// </summary>
public class WeatherSystem : MonoBehaviour
{
    public static WeatherSystem Current { get; private set; }

    /// <summary>The live weather, or null for a clear sky.</summary>
    public static WeatherEffect Effect => Current != null ? Current._effect : null;

    public WeatherSO Weather { get; private set; }
    public WeatherDatabaseSO Database { get; private set; }

    /// <summary>The battle's own random stream: a restarted Forged in Battle fight gets the same weather rolls.</summary>
    public System.Random Rng { get; private set; }

    private WeatherEffect _effect;
    private bool _started;
    private readonly List<GameObject> _spawned = new List<GameObject>();

    private TextMeshProUGUI _hudStatus;
    private RectTransform _hudRoot;

    // ---------------------------------------------------------------- Setup

    /// <summary>Puts <paramref name="weather"/> on this match (null = clear sky). Called from GameManager.Awake.</summary>
    public static WeatherSystem Create(GameObject host, WeatherSO weather, int? seed)
    {
        var system = host.AddComponent<WeatherSystem>();
        system.Database = WeatherDatabaseSO.Load();
        system.Weather = weather;
        system.Rng = new System.Random(seed ?? Environment.TickCount);

        if (weather != null)
        {
            system._effect = weather.CreateEffect();
            system._effect?.Bind(weather, system);
        }

        Current = system;
        return system;
    }

    private void OnEnable()
    {
        MinionController.OnDied += HandleMinionDied;
        GridManager.GridChanged += HandleGridChanged;
    }

    private void OnDisable()
    {
        MinionController.OnDied -= HandleMinionDied;
        GridManager.GridChanged -= HandleGridChanged;
    }

    private void OnDestroy()
    {
        if (Current == this) Current = null;
        if (_ambiencePlaying && AudioManager.Exists) AudioManager.Instance.StopWeatherAmbience(Weather.ambienceFade);
    }

    private bool _ambiencePlaying;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForNewSession() => Current = null;

    /// <summary>The board's cells have registered: lay the weather out and show its badge.</summary>
    public void BeginBattle()
    {
        if (_started || _effect == null) return;
        _started = true;

        BuildHud();
        SpawnVisual();
        PlayAmbience();
        _effect.OnBattleStart();
        RefreshHud();
        Debug.Log($"[Weather] Battle weather: {Weather.DisplayName}");
    }

    private void LateUpdate()
    {
        if (_started && _effect != null && !IsBattleOver) _effect.Tick();
    }

    /// <summary>The weather's ambient world visual (its <see cref="WeatherSO.visualPrefab"/>), or null.</summary>
    public GameObject Visual { get; private set; }

    /// <summary>The board's centre in world space: cells sit at (x, -y), one unit apart.</summary>
    public Vector3 BoardCentre => new Vector3((Width - 1) * 0.5f, -(Height - 1) * 0.5f, 0f);

    private void PlayAmbience()
    {
        if (Weather.ambience == null || !Weather.ambience.HasAudibleClip) return;
        var audio = AudioManager.Instance;
        if (audio == null) return;
        audio.PlayWeatherAmbience(Weather.ambience, Weather.ambienceFade);
        _ambiencePlaying = true;
    }

    private void SpawnVisual()
    {
        if (Weather.visualPrefab == null) return;

        Visual = Instantiate(Weather.visualPrefab, BoardCentre, Quaternion.identity, transform);
        Visual.name = Weather.visualPrefab.name;
        _spawned.Add(Visual);
    }

    // ---------------------------------------------------------------- Static facade

    public static int ModifyDamage(int damage)
    {
        var effect = Effect;
        return effect != null && damage > 0 ? Mathf.Max(0, effect.ModifyDamage(damage)) : damage;
    }

    public static int AdjustCost(CardSO card, int cost)
    {
        var effect = Effect;
        return effect != null && card != null ? Mathf.Max(0, effect.AdjustCost(card, cost)) : cost;
    }

    public static bool FriendlyCollisionDamage => Effect != null && Effect.FriendlyCollisionDamage;

    public static void NotifyMinionSummoned(MinionController minion)
    {
        if (Effect == null || minion == null) return;
        Effect.OnMinionSummoned(minion);
        Current.RefreshHud();
    }

    /// <summary>Called by MinionController once a move has landed it on its new cell.</summary>
    public static void NotifyMinionEnteredCell(MinionController minion)
    {
        if (Effect == null || minion == null || !IsAlive(minion)) return;
        if (Current.IsBattleOver) return;
        Effect.OnMinionEnteredCell(minion, CellOf(minion));
    }

    // ---------------------------------------------------------------- Turn hooks (GameManager)

    public IEnumerator TurnStarted(bool playerTurn) => Wrap(_effect?.OnTurnStarted(playerTurn));

    public IEnumerator TurnEnded(bool playerTurn) => Wrap(_effect?.OnTurnEnded(playerTurn));

    /// <summary>Called from inside the summon broadcast (already on the scheduler); null when nothing happens.</summary>
    public IEnumerator AfterMinionSummoned(MinionController minion)
    {
        if (_effect == null || !IsAlive(minion)) return null;
        return Wrap(_effect.AfterMinionSummoned(minion, CellOf(minion)));
    }

    /// <summary>Wraps a hook so the HUD catches up when it is done; null when the hook does nothing.</summary>
    private IEnumerator Wrap(IEnumerator hook)
    {
        if (hook == null || !_started) return null;
        return WrapRoutine(hook);
    }

    private IEnumerator WrapRoutine(IEnumerator hook)
    {
        if (!IsBattleOver) yield return hook;
        RefreshHud();
    }

    private void HandleMinionDied(MinionController minion)
    {
        if (_effect == null || !_started || minion == null || minion == Hero(minion)) return;
        if (IsBattleOver) return;

        var routine = _effect.OnMinionDied(minion, CellOf(minion));
        if (routine != null && GameManager.Instance != null)
            GameManager.Instance.EnqueueWeatherAction(Wrap(routine));
    }

    private void HandleGridChanged()
    {
        if (_effect == null || !_started) return;
        _effect.OnBoardChanged();
    }

    // ---------------------------------------------------------------- Board helpers

    public bool IsBattleOver => GameManager.Instance == null || GameManager.Instance.currentState == GameState.EndGame;

    public int Width => GridManager.Instance != null ? GridManager.Instance.GridWidth : 0;
    public int Height => GridManager.Instance != null ? GridManager.Instance.GridHeight : 0;

    public static Vector3 CellWorldPos(Vector2Int cell) => new Vector3(cell.x, -cell.y, 0f);

    public static Vector2Int CellOf(MinionController minion) =>
        GridManager.Instance.PosToGridIndex(minion.gridEntity != null ? minion.gridEntity.WorldPos : minion.transform.position);

    public bool InGrid(Vector2Int cell) => cell.x >= 0 && cell.x < Width && cell.y >= 0 && cell.y < Height;

    public IEnumerable<Vector2Int> AllCells()
    {
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                yield return new Vector2Int(x, y);
    }

    public static bool IsAlive(MinionController minion) =>
        minion != null && minion.modal != null && minion.modal.health > 0;

    private static MinionController Hero(MinionController minion) => minion.owner != null ? minion.owner.hero : null;

    /// <summary>The living minion (never a hero) standing on <paramref name="cell"/>, or null.</summary>
    public MinionController MinionAt(Vector2Int cell)
    {
        foreach (var minion in AllMinions())
            if (CellOf(minion) == cell) return minion;
        return null;
    }

    /// <summary>Every living minion on the board, both sides, heroes excluded. A snapshot: safe to damage while iterating.</summary>
    public List<MinionController> AllMinions()
    {
        var all = new List<MinionController>();
        var gm = GameManager.Instance;
        if (gm == null) return all;

        if (gm.player != null) foreach (var m in gm.player.minions) if (IsAlive(m) && m != gm.player.hero) all.Add(m);
        if (gm.opponent != null) foreach (var m in gm.opponent.minions) if (IsAlive(m) && m != gm.opponent.hero) all.Add(m);
        return all;
    }

    public T Pick<T>(IList<T> items) => items.Count > 0 ? items[Rng.Next(items.Count)] : default;

    /// <summary>Up to <paramref name="count"/> distinct cells matching <paramref name="filter"/>, at random.</summary>
    public List<Vector2Int> RandomCells(int count, Predicate<Vector2Int> filter = null)
    {
        var bag = new List<Vector2Int>();
        foreach (var cell in AllCells())
            if (filter == null || filter(cell)) bag.Add(cell);

        var picked = new List<Vector2Int>();
        while (picked.Count < count && bag.Count > 0)
        {
            int i = Rng.Next(bag.Count);
            picked.Add(bag[i]);
            bag.RemoveAt(i);
        }
        return picked;
    }

    /// <summary>Weather damage to a minion, with a label floated off it.</summary>
    public void Damage(MinionController minion, int amount, string label = null)
    {
        if (!IsAlive(minion) || amount <= 0) return;
        if (!string.IsNullOrEmpty(label)) minion.ShowFloatingText(label);
        minion.TakeDamage(amount);
    }

    // ---------------------------------------------------------------- Visuals

    /// <summary>A sprite laid flat on a board tile, under the minions. Destroyed with the battle.</summary>
    public SpriteRenderer SpawnTileMarker(Vector2Int cell, Sprite sprite, Color tint, string label = "WeatherTile")
    {
        // No art configured: tint a copy of the tile itself, so the marked tiles still read.
        if (sprite == null) sprite = CellSprite(cell);

        var go = new GameObject($"{label} {cell.x},{cell.y}");
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.position = CellWorldPos(cell);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        tint.a *= Database != null ? Database.tileMarkerAlpha : 1f;
        sr.color = tint;
        FitToCell(go.transform, sprite, 0.9f);
        PlaceUnderMinions(sr, cell);

        _spawned.Add(go);
        return sr;
    }

    /// <summary>An authored prefab centred on a board tile (sorting comes from the prefab itself). Destroyed with the battle.</summary>
    public GameObject SpawnTilePrefab(Vector2Int cell, GameObject prefab, string label = "WeatherTile")
    {
        if (prefab == null) return null;
        var go = Instantiate(prefab, CellWorldPos(cell), Quaternion.identity, transform);
        go.name = $"{label} {cell.x},{cell.y}";
        _spawned.Add(go);
        return go;
    }

    /// <summary>
    /// A one-off effect (a falling meteor, an impact burst) at a world position. Not tracked with the
    /// battle's markers: the caller destroys it when it has played out.
    /// </summary>
    public GameObject SpawnEffect(GameObject prefab, Vector3 position)
    {
        if (prefab == null) return null;
        var go = Instantiate(prefab, position, Quaternion.identity, transform);
        go.name = prefab.name;
        return go;
    }

    /// <summary>Board art is 32 px per cell: rounding a moving sprite onto that grid keeps it crisp.</summary>
    public const float PixelsPerUnit = 32f;

    public static Vector3 SnapToPixel(Vector3 p) => new Vector3(
        Mathf.Round(p.x * PixelsPerUnit) / PixelsPerUnit,
        Mathf.Round(p.y * PixelsPerUnit) / PixelsPerUnit,
        p.z);

    /// <summary>A short shake of the main camera that settles back where it started.</summary>
    public static void ShakeCamera(float strength, float duration)
    {
        Camera cam = Camera.main;
        if (cam == null || strength <= 0f || duration <= 0f) return;

        // Finish any shake still running first, so shakes never stack and drift the camera off its rest spot.
        cam.transform.DOComplete();
        cam.transform.DOShakePosition(duration, new Vector3(strength, strength, 0f), vibrato: 18, randomness: 90f, fadeOut: true)
            .SetLink(cam.gameObject);
    }

    /// <summary>A one-off flash on a tile: the sprite pops in, swells and fades. For strikes (meteor, lightning).</summary>
    public void PlayStrike(Vector2Int cell, Sprite sprite, Color tint, float duration = 0.6f)
    {
        if (sprite == null) sprite = CellSprite(cell);
        if (sprite == null) return;

        var go = new GameObject($"WeatherStrike {cell.x},{cell.y}");
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.position = CellWorldPos(cell);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = tint;
        // Over everything on the tile, minions included: a strike reads as landing ON the unit.
        sr.sortingLayerName = "Layer 3";
        sr.sortingOrder = 100;

        FitToCell(go.transform, sprite, 1f);
        Vector3 full = go.transform.localScale;
        go.transform.localScale = full * 0.2f;

        DOTween.Sequence()
            .Append(go.transform.DOScale(full * 1.15f, duration * 0.35f).SetEase(Ease.OutBack))
            .Join(sr.DOFade(tint.a, duration * 0.2f))
            .Append(sr.DOFade(0f, duration * 0.65f))
            .OnComplete(() => { if (go != null) Destroy(go); })
            .SetLink(go);
    }

    private static void FitToCell(Transform t, Sprite sprite, float fill)
    {
        if (sprite == null) return;
        Vector2 size = sprite.bounds.size;
        float biggest = Mathf.Max(size.x, size.y);
        if (biggest > 0.0001f) t.localScale = Vector3.one * (fill / biggest);
    }

    private static Sprite _plainTile;

    /// <summary>A plain filled square, tinted by the weather: the fallback for weathers with no tile art.</summary>
    private static Sprite CellSprite(Vector2Int cell)
    {
        if (_plainTile == null)
        {
            var tex = Texture2D.whiteTexture;
            _plainTile = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
            _plainTile.name = "WeatherPlainTile";
        }
        return _plainTile;
    }

    /// <summary>On the cell's own (Default) sorting layer, just above its tile art. Minions draw on higher layers.</summary>
    private void PlaceUnderMinions(SpriteRenderer sr, Vector2Int cell)
    {
        int order = 0;
        GameObject cellObj = GridManager.Instance != null ? GridManager.Instance.GetCell(cell).cellObj : null;
        if (cellObj != null)
            foreach (var r in cellObj.GetComponentsInChildren<SpriteRenderer>(true))
                if (r.sortingLayerID == 0) order = Mathf.Max(order, r.sortingOrder);

        sr.sortingLayerID = 0;
        sr.sortingOrder = order + (Database != null ? Database.tileMarkerOrderOffset : 1);
    }

    /// <summary>A tile marker breathing its alpha, for something about to happen there.</summary>
    public static void Pulse(SpriteRenderer sr, float minAlphaFactor = 0.35f, float period = 0.7f)
    {
        if (sr == null) return;
        sr.DOKill();
        float full = sr.color.a;
        sr.DOFade(full * minAlphaFactor, period).SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine).SetLink(sr.gameObject);
    }

    /// <summary>A canvas group breathing its alpha between <paramref name="minAlpha"/> and <paramref name="maxAlpha"/>, forever.</summary>
    public static void Breathe(CanvasGroup group, float minAlpha = 0.35f, float maxAlpha = 1f, float period = 0.7f,
        Ease ease = Ease.InOutSine)
    {
        if (group == null) return;
        group.DOKill();
        group.alpha = maxAlpha;
        group.DOFade(minAlpha, period).SetLoops(-1, LoopType.Yoyo).SetEase(ease).SetLink(group.gameObject);
    }

    public static void MoveMarker(SpriteRenderer sr, Vector2Int cell) => MoveMarker(sr != null ? sr.transform : null, cell);

    public static void MoveMarker(Transform marker, Vector2Int cell)
    {
        if (marker == null) return;
        marker.DOKill();
        marker.DOMove(CellWorldPos(cell), 0.35f).SetEase(Ease.InOutSine).SetLink(marker.gameObject);
    }

    // ---------------------------------------------------------------- HUD

    /// <summary>Re-reads the effect's live status line (a countdown) into the badge.</summary>
    public void RefreshHud()
    {
        if (_hudStatus == null || _effect == null) return;
        string status = _effect.Status;
        _hudStatus.gameObject.SetActive(!string.IsNullOrEmpty(status));
        _hudStatus.text = status ?? string.Empty;
    }

    /// <summary>
    /// The weather badge: icon, name, rules text and live status, built at runtime on the game canvas so
    /// no scene needs wiring. Placement and look come from <see cref="WeatherDatabaseSO"/>.
    /// </summary>
    private void BuildHud()
    {
        Canvas canvas = FindGameCanvas();
        if (canvas == null)
        {
            Debug.LogWarning("[Weather] No game canvas found for the weather badge.");
            return;
        }

        var db = Database;
        TMP_FontAsset font = FindFont(canvas);

        var root = new GameObject("WeatherHUD", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
        _hudRoot = (RectTransform)root.transform;
        _hudRoot.SetParent(canvas.transform, false);
        // First sibling: every panel and popup on the canvas draws over it.
        _hudRoot.SetAsFirstSibling();
        _hudRoot.anchorMin = _hudRoot.anchorMax = _hudRoot.pivot = db.hudAnchor;
        _hudRoot.anchoredPosition = db.hudOffset;
        _spawned.Add(root);

        var bg = root.GetComponent<Image>();
        bg.color = db.hudBackgroundColor;
        bg.raycastTarget = false;

        var row = root.GetComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(12, 16, 10, 10);
        row.spacing = 12;
        row.childAlignment = TextAnchor.MiddleLeft;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;

        var fit = root.GetComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        if (Weather.icon != null)
        {
            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            iconGo.transform.SetParent(_hudRoot, false);
            var icon = iconGo.GetComponent<Image>();
            icon.sprite = Weather.icon;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            var le = iconGo.GetComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = db.hudIconSize;
        }

        var column = new GameObject("Text", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        column.transform.SetParent(_hudRoot, false);
        var col = column.GetComponent<VerticalLayoutGroup>();
        col.spacing = 2;
        col.childControlWidth = col.childControlHeight = true;
        col.childForceExpandWidth = true;
        col.childForceExpandHeight = false;
        column.GetComponent<LayoutElement>().preferredWidth = db.hudWidth;

        Label(column.transform, "Name", Weather.DisplayName, db.hudNameFontSize, db.hudNameColor, font, FontStyles.Bold);
        if (!string.IsNullOrEmpty(Weather.description))
            Label(column.transform, "Description", Weather.description, db.hudTextFontSize, db.hudTextColor, font, FontStyles.Normal);
        _hudStatus = Label(column.transform, "Status", string.Empty, db.hudTextFontSize, db.hudNameColor, font, FontStyles.Italic);

        _hudRoot.localScale = Vector3.zero;
        _hudRoot.DOScale(1f, 0.35f).SetEase(Ease.OutBack).SetDelay(0.2f).SetLink(root);
    }

    private static TextMeshProUGUI Label(Transform parent, string name, string text, float size, Color color,
        TMP_FontAsset font, FontStyles style)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.fontStyle = style;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.raycastTarget = false;
        return tmp;
    }

    /// <summary>The root canvas the played cards fly over, i.e. the game's own HUD canvas.</summary>
    private static Canvas FindGameCanvas()
    {
        if (PlayArea.Instance != null && PlayArea.Instance.PlayedCardParent != null)
        {
            var c = PlayArea.Instance.PlayedCardParent.GetComponentInParent<Canvas>();
            if (c != null) return c.rootCanvas;
        }

        foreach (var c in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            if (c.isRootCanvas && c.gameObject.activeInHierarchy) return c;
        return null;
    }

    /// <summary>The font the game's own UI is set in, so the badge doesn't stand out in LiberationSans.</summary>
    private static TMP_FontAsset FindFont(Canvas canvas)
    {
        foreach (var t in canvas.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (t.font != null) return t.font;
        return TMP_Settings.defaultFontAsset;
    }
}
