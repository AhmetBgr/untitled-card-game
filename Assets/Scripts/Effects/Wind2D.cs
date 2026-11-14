using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the Custom/2D/Wind2D shader for the Strong Wind weather. The wind idles as a light breeze; on
/// <see cref="PlayGust"/> it picks up into a strong gust in its current direction, holds while the minions
/// are shoved, dies down through a standstill, turns around and settles back into a breeze the other way.
///
/// The distance the dust has travelled is accumulated here every frame and passed to the shader, rather than
/// the shader computing speed * time, so speed and direction can change freely without the pattern jumping.
/// Everything is pushed through a MaterialPropertyBlock, so presets can be tweaked live in the inspector.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Wind2D : MonoBehaviour
{
    public enum WindQuality { High, Low, LowOnMobile }

    [System.Serializable]
    public struct Preset
    {
        [Tooltip("Travel speed in world units per second.")]
        [Min(0f)] public float speed;
        [Range(0f, 1f)] public float density;
        [Tooltip("How much of the screen the streaks cover.")]
        [Range(0f, 1f)] public float coverage;
        [Range(0.01f, 0.5f)] public float softness;

        public static Preset Lerp(Preset a, Preset b, float t) => new Preset
        {
            speed = Mathf.Lerp(a.speed, b.speed, t),
            density = Mathf.Lerp(a.density, b.density, t),
            coverage = Mathf.Lerp(a.coverage, b.coverage, t),
            softness = Mathf.Lerp(a.softness, b.softness, t),
        };
    }

    [Header("Setup")]
    [Tooltip("Camera to cover. Falls back to Camera.main.")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private bool followCamera = true;
    [Tooltip("Mesh size relative to the camera view; >1 avoids edge gaps.")]
    [SerializeField, Min(1f)] private float overscan = 1.1f;
    [SerializeField] private string sortingLayer = "Layer 2";
    [SerializeField] private int sortingOrder = 50;

    [Header("Quality")]
    [Tooltip("Low skips the turbulence and the second layer: 2 texture reads per pixel instead of 4.")]
    [SerializeField] private WindQuality quality = WindQuality.LowOnMobile;
    [SerializeField] private Material highQualityMaterial;
    [SerializeField] private Material lowQualityMaterial;

    [Header("Color")]
    [SerializeField] private Color dustColor = new Color(0.86f, 0.89f, 0.92f, 1f);
    [SerializeField] private Color shadowColor = new Color(0.60f, 0.65f, 0.70f, 1f);

    [Header("Breeze (idle)")]
    [SerializeField] private Preset breeze = new Preset { speed = 0.8f, density = 0.25f, coverage = 0.35f, softness = 0.3f };
    [Tooltip("How much the breeze speeds up and slows down on its own (0 = constant).")]
    [SerializeField, Range(0f, 1f)] private float breezeFlutter = 0.35f;
    [SerializeField, Min(0f)] private float flutterPerSecond = 0.2f;

    [Header("Gust")]
    [SerializeField] private Preset gust = new Preset { speed = 7f, density = 0.5f, coverage = 0.45f, softness = 0.22f };
    [Tooltip("Seconds from breeze to full gust. The weather waits this long before shoving the minions.")]
    [SerializeField, Min(0.01f)] private float gustRampUp = 0.3f;
    [Tooltip("Seconds the gust blows at full strength.")]
    [SerializeField, Min(0f)] private float gustHold = 0.5f;

    [Header("Turn Around")]
    [Tooltip("Seconds for the wind to fade out (still blowing the old way) before it turns around.")]
    [SerializeField, Min(0.01f)] private float fadeOut = 0.6f;
    [Tooltip("Seconds the wind stays gone before it comes back the other way.")]
    [SerializeField, Min(0f)] private float turnPause = 0.5f;
    [Tooltip("Seconds for the breeze to fade back in, blowing the new way.")]
    [SerializeField, Min(0.01f)] private float fadeIn = 0.8f;

    [Header("Shape")]
    [Tooltip("Axis the streaks run along, in degrees (0 = horizontal). The wind blows either way along it.")]
    [SerializeField, Range(0f, 180f)] private float axisAngle = 0f;
    [Tooltip("Elongates the dust along the axis so it reads as streaks.")]
    [SerializeField, Range(1f, 8f)] private float stretch = 4f;
    [SerializeField, Min(0.01f)] private float puffSize = 1.2f;
    [SerializeField, Range(0f, 3f)] private float layer2SpeedMult = 1.6f;
    [SerializeField, Range(0.25f, 4f)] private float layer2Scale = 1.8f;
    [SerializeField, Range(0f, 1f)] private float layer2Weight = 0.5f;
    [SerializeField, Range(0f, 2f)] private float turbulence = 0.3f;
    [SerializeField, Min(0f)] private float evolveSpeed = 0.05f;
    [SerializeField, Min(0.01f)] private float bankSize = 6f;
    [SerializeField, Range(0f, 1f)] private float bankGaps = 0.6f;

    [Header("Clear Zone")]
    [Tooltip("The wind thins out around this transform. Empty = around where this object was spawned " +
             "(the weather spawns its visual at the board's centre).")]
    [SerializeField] private Transform clearTarget;
    [SerializeField] private Vector2 clearRadii = new Vector2(2.2f, 2.2f);
    [SerializeField, Range(0.01f, 1f)] private float clearFeather = 0.6f;
    [Tooltip("0 = no clear zone, 1 = fully clear inside.")]
    [SerializeField, Range(0f, 1f)] private float clearStrength = 0.6f;

    [Header("Pixel Art")]
    [SerializeField] private bool pixelSnap;
    [SerializeField, Min(1f)] private float pixelsPerUnit = 32f;
    [SerializeField, Range(0, 16)] private int alphaSteps;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int ShadowColorId = Shader.PropertyToID("_ShadowColor");
    private static readonly int DensityId = Shader.PropertyToID("_Density");
    private static readonly int CoverageId = Shader.PropertyToID("_Coverage");
    private static readonly int SoftnessId = Shader.PropertyToID("_Softness");
    private static readonly int WindAxisId = Shader.PropertyToID("_WindAxis");
    private static readonly int WindOffsetId = Shader.PropertyToID("_WindOffset");
    private static readonly int Layer2SpeedId = Shader.PropertyToID("_Layer2Speed");
    private static readonly int StretchId = Shader.PropertyToID("_Stretch");
    private static readonly int EvolveId = Shader.PropertyToID("_Evolve");
    private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
    private static readonly int Layer2ScaleId = Shader.PropertyToID("_Layer2Scale");
    private static readonly int Layer2WeightId = Shader.PropertyToID("_Layer2Weight");
    private static readonly int WarpId = Shader.PropertyToID("_Warp");
    private static readonly int BankSizeId = Shader.PropertyToID("_BankSize");
    private static readonly int BankAmountId = Shader.PropertyToID("_BankAmount");
    private static readonly int ClearCenterId = Shader.PropertyToID("_ClearCenter");
    private static readonly int ClearSizeId = Shader.PropertyToID("_ClearSize");
    private static readonly int ClearFeatherId = Shader.PropertyToID("_ClearFeather");
    private static readonly int ClearStrengthId = Shader.PropertyToID("_ClearStrength");
    private static readonly int PixelSizeId = Shader.PropertyToID("_PixelSize");
    private static readonly int StepsId = Shader.PropertyToID("_Steps");

    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;
    private Mesh mesh;
    private MaterialPropertyBlock block;
    private Vector2 meshSize;
    private Vector2 spawnPoint;

    // Live wind state.
    private enum Phase { Idle, RampUp, Hold, FadeOut, Pause, FadeIn }
    private int sign = 1;              // +1 = blows toward +axis (right when horizontal), -1 = the other way
    private float intensity;           // 0 = breeze, 1 = gust
    private float visibility = 1f;     // fades to 0 and back while the wind turns around
    private Vector2 offset;            // distance travelled, world units
    private Phase phase = Phase.Idle;
    private float phaseTime;
    private float rampFrom;
    private float visibilityFrom;      // visibility when a gust interrupted a fade
    private bool holdGust;             // tuning only: stay at full gust until StopGust
    private float clock;               // seconds, drives the breeze flutter

    /// <summary>Which way the wind blows along its axis: +1 or -1 (right or left when horizontal).</summary>
    public int Direction => sign;

    /// <summary>Seconds from <see cref="PlayGust"/> until the gust is at full strength.</summary>
    public float GustLeadTime => gustRampUp;

    public bool IsGusting => phase != Phase.Idle;

    /// <summary>A "Gust Only" test gust is blowing (or building up) and will hold until <see cref="StopGust"/>.</summary>
    public bool IsHoldingGust => holdGust && (phase == Phase.RampUp || phase == Phase.Hold);

    /// <summary>Points the wind the given way along its axis straight away (e.g. at the start of a battle).</summary>
    public void SetDirection(int direction)
    {
        sign = direction >= 0 ? 1 : -1;
    }

    /// <summary>
    /// Gust toward <paramref name="direction"/> (+1 / -1 along the axis), then turn around and settle into a
    /// breeze the other way. Runs on its own; the caller can wait <see cref="GustLeadTime"/> for the gust to
    /// arrive before acting on it. Taking the direction (rather than trusting our own) keeps the visual in
    /// step with the gameplay even if a gust was cut short. Also works outside play mode, for tuning.
    /// </summary>
    public void PlayGust(int direction) => PlayGust(direction, false);

    private void PlayGust(int direction, bool hold)
    {
        if (!isActiveAndEnabled) return;
        SetDirection(direction);
        holdGust = hold;
        rampFrom = intensity;
        visibilityFrom = visibility;   // a gust mid turn-around fades back in as it builds
        EnterPhase(Phase.RampUp);
    }

    /// <summary>Turns the wind around without a gust: it fades out, pauses, flips and fades back in.</summary>
    public void TurnAround()
    {
        if (!isActiveAndEnabled) return;
        holdGust = false;
        visibilityFrom = visibility;
        EnterPhase(Phase.FadeOut);
    }

    /// <summary>Ends whatever the wind is doing and drops straight back to a breeze, no transition.</summary>
    public void StopGust()
    {
        holdGust = false;
        intensity = 0f;
        visibility = 1f;
        EnterPhase(Phase.Idle);
    }

    private void EnterPhase(Phase next)
    {
        phase = next;
        phaseTime = 0f;
    }

    /// <summary>Advances the gust sequence and the distance travelled by <paramref name="dt"/> seconds.</summary>
    private void Tick(float dt)
    {
        clock += dt;
        phaseTime += dt;
        switch (phase)
        {
            case Phase.Idle:
                // Outside play mode the preview slider holds the wind anywhere between breeze and gust.
                if (!Application.isPlaying) intensity = previewIntensity;
                break;

            case Phase.RampUp:
            {
                float k = EaseOut(Mathf.Clamp01(phaseTime / gustRampUp));
                intensity = Mathf.Lerp(rampFrom, 1f, k);
                visibility = Mathf.Lerp(visibilityFrom, 1f, k);
                if (phaseTime >= gustRampUp) { intensity = 1f; visibility = 1f; EnterPhase(Phase.Hold); }
                break;
            }

            case Phase.Hold:
                if (!holdGust && phaseTime >= gustHold) { visibilityFrom = visibility; EnterPhase(Phase.FadeOut); }
                break;

            case Phase.FadeOut:
                // The wind keeps blowing the old way while it fades away.
                visibility = visibilityFrom * (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(phaseTime / fadeOut)));
                if (phaseTime >= fadeOut) { visibility = 0f; EnterPhase(Phase.Pause); }
                break;

            case Phase.Pause:
                // Gone for a moment; it comes back as a breeze blowing the other way.
                if (phaseTime >= turnPause) { intensity = 0f; sign = -sign; EnterPhase(Phase.FadeIn); }
                break;

            case Phase.FadeIn:
                visibility = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(phaseTime / fadeIn));
                if (phaseTime >= fadeIn) { visibility = 1f; EnterPhase(Phase.Idle); }
                break;
        }

        // Breeze flutters on its own; a full gust blows steadily.
        float flutter = 1f + breezeFlutter * (1f - intensity) * Mathf.Sin(clock * flutterPerSecond * Mathf.PI * 2f);
        float speed = Preset.Lerp(breeze, gust, intensity).speed * Mathf.Max(0f, flutter);
        offset += Axis * (sign * speed * dt);
    }

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

    private void Awake()
    {
        spawnPoint = transform.position;
    }

    private void OnEnable()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        if (mesh == null)
        {
            mesh = new Mesh { name = "Wind2D Mesh", hideFlags = HideFlags.DontSave };
            meshSize = Vector2.zero;
        }
        meshFilter.sharedMesh = mesh;

        ApplyQuality();
        FitToCamera();
        Apply();

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            lastEditorTime = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update -= EditorTick;
            UnityEditor.EditorApplication.update += EditorTick;
        }
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorTick;
#endif
        // Drop back to a plain breeze. The next gust brings its direction.
        phase = Phase.Idle;
        intensity = 0f;
        visibility = 1f;
    }

    private void OnDestroy()
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh);
        else DestroyImmediate(mesh);
    }

    private void OnValidate()
    {
        if (!isActiveAndEnabled || meshRenderer == null) return;
        ApplyQuality();
        FitToCamera();
        Apply();
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying) return;   // outside play mode the editor ticks it, see EditorTick
        Tick(Time.deltaTime);
        FitToCamera();
        Apply();
    }

    // Always serialized (an editor-only serialized field breaks the player's serialization layout), but only
    // read outside play mode.
    [Header("Editor Preview")]
    [Tooltip("Animate outside play mode, so the wind can be tuned in the scene and game views.")]
    [SerializeField] private bool animateInEditor = true;
    [Tooltip("Outside play mode, hold the wind between breeze (0) and gust (1) while tuning. " +
             "Ignored in play mode and while a test gust is playing.")]
    [SerializeField, Range(0f, 1f)] private float previewIntensity;

#if UNITY_EDITOR
    private double lastEditorTime;

    /// <summary>Outside play mode: the same tick as in play, on editor time, plus a repaint so it shows.</summary>
    private void EditorTick()
    {
        if (this == null || Application.isPlaying) return;

        double now = UnityEditor.EditorApplication.timeSinceStartup;
        float dt = Mathf.Min((float)(now - lastEditorTime), 0.1f);
        lastEditorTime = now;

        if (!animateInEditor)
        {
            if (phase == Phase.Idle) intensity = previewIntensity;
            Apply();
            return;
        }

        Tick(dt);
        FitToCamera();
        Apply();
        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        UnityEditor.SceneView.RepaintAll();
    }

    /// <summary>Plays the whole gust sequence in the current direction (inspector button, works in edit mode).</summary>
    /// <summary>What a gust does in a match: gust this way, then turn around into a breeze the other way.</summary>
    [ContextMenu("Gust + Flip (as in game)")]
    public void TestGust() => PlayGust(sign, false);

    /// <summary>Just the gust: builds up and keeps blowing at full strength until <see cref="StopGust"/>.</summary>
    [ContextMenu("Gust Only")]
    public void TestGustOnly() => PlayGust(sign, true);

    /// <summary>Just the turn-around: the breeze dies down, flips and picks back up.</summary>
    [ContextMenu("Flip Only")]
    public void FlipDirection() => TurnAround();
#endif

    private Vector2 Axis
    {
        get
        {
            float rad = axisAngle * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        }
    }

    private void ApplyQuality()
    {
        if (meshRenderer == null) return;
        bool low = quality == WindQuality.Low || (quality == WindQuality.LowOnMobile && Application.isMobilePlatform);
        Material material = low && lowQualityMaterial != null ? lowQualityMaterial : highQualityMaterial;
        if (material != null && meshRenderer.sharedMaterial != material) meshRenderer.sharedMaterial = material;
    }

    private void FitToCamera()
    {
        Camera cam = targetCamera != null ? targetCamera : Camera.main;

        Vector2 size = meshSize == Vector2.zero ? new Vector2(12f, 12f) : meshSize;
        if (followCamera && cam != null && cam.orthographic)
        {
            float height = cam.orthographicSize * 2f * overscan;
            size = new Vector2(height * cam.aspect, height);

            Vector3 camPos = cam.transform.position;
            transform.position = new Vector3(camPos.x, camPos.y, transform.position.z);
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        if (mesh == null || size == meshSize) return;
        meshSize = size;

        float hx = size.x * 0.5f, hy = size.y * 0.5f;
        mesh.Clear();
        mesh.SetVertices(new[]
        {
            new Vector3(-hx, -hy), new Vector3(hx, -hy), new Vector3(-hx, hy), new Vector3(hx, hy),
        });
        mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
        mesh.RecalculateBounds();
    }

    private void Apply()
    {
        if (meshRenderer == null) return;
        meshRenderer.sortingLayerName = sortingLayer;
        meshRenderer.sortingOrder = sortingOrder;

        block ??= new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);

        Preset now = Preset.Lerp(breeze, gust, intensity);
        Vector2 center = clearTarget != null ? (Vector2)clearTarget.position : spawnPoint;

        block.SetColor(ColorId, dustColor);
        block.SetColor(ShadowColorId, shadowColor);
        block.SetFloat(DensityId, now.density * visibility);
        block.SetFloat(CoverageId, now.coverage);
        block.SetFloat(SoftnessId, now.softness);
        block.SetVector(WindAxisId, Axis);
        block.SetVector(WindOffsetId, offset);
        block.SetFloat(Layer2SpeedId, layer2SpeedMult);
        block.SetFloat(StretchId, stretch);
        block.SetFloat(EvolveId, evolveSpeed);
        block.SetFloat(NoiseScaleId, puffSize);
        block.SetFloat(Layer2ScaleId, layer2Scale);
        block.SetFloat(Layer2WeightId, layer2Weight);
        block.SetFloat(WarpId, turbulence);
        block.SetFloat(BankSizeId, bankSize);
        block.SetFloat(BankAmountId, bankGaps);
        block.SetVector(ClearCenterId, center);
        block.SetVector(ClearSizeId, clearRadii);
        block.SetFloat(ClearFeatherId, clearFeather);
        block.SetFloat(ClearStrengthId, clearStrength);
        block.SetFloat(PixelSizeId, pixelSnap ? 1f / pixelsPerUnit : 0f);
        block.SetFloat(StepsId, alphaSteps);

        meshRenderer.SetPropertyBlock(block);
    }
}
