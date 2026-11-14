using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the Custom/2D/Rain2D shader: crisp pixel-art rain over the whole camera view, for the Thunderstorm
/// weather. Drops fall a short way and splash on the ground (the play area). A camera-sized quad is built and kept on the camera, like <see cref="Wind2D"/>.
///
/// Rain time is accumulated here (scaled by <see cref="intensity"/>), so the rain can speed up or calm down
/// without the drops jumping. Everything goes through a MaterialPropertyBlock, so it can be tuned live in the
/// inspector, in play mode or in edit mode.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Rain2D : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("Camera to cover. Falls back to Camera.main.")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private bool followCamera = true;
    [Tooltip("Mesh size relative to the camera view; >1 avoids edge gaps.")]
    [SerializeField, Min(1f)] private float overscan = 1.1f;
    [SerializeField] private Material material;
    [SerializeField] private string sortingLayer = "Layer 2";
    [SerializeField] private int sortingOrder = 49;

    [Header("Intensity")]
    [Tooltip("0 = no rain, 1 = full storm. Scales opacity, density and fall speed together.")]
    [SerializeField, Range(0f, 1f)] private float intensity = 1f;
    [Tooltip("Seconds to fade the rain in when it appears.")]
    [SerializeField, Min(0f)] private float fadeInTime = 1.2f;
    [Tooltip("At intensity 0, drops fall this fraction of full speed (so a light drizzle is also slower).")]
    [SerializeField, Range(0f, 1f)] private float drizzleSpeed = 0.6f;

    [Header("Color")]
    [SerializeField] private Color dropColor = new Color(0.75f, 0.85f, 1f, 0.8f);
    [SerializeField] private Color splashColor = new Color(0.85f, 0.92f, 1f, 0.7f);
    [Tooltip("Random per-drop dimming, so not every drop is equally bright.")]
    [SerializeField, Range(0f, 1f)] private float brightnessVariation = 0.4f;
    [SerializeField, Range(0f, 1f)] private float opacity = 1f;

    [Header("Pixel Grid")]
    [Tooltip("Rain pixels per world unit. Match your sprites' PPU for a consistent pixel size.")]
    [SerializeField, Min(1f)] private float pixelsPerUnit = 32f;
    [Tooltip("The ground is split into square cells of this many pixels; each holds at most one drop at a time. " +
             "Smaller = more drops.")]
    [SerializeField, Range(4, 64)] private int cellSize = 14;

    [Header("Drops")]
    [Tooltip("Chance a cell drops a raindrop each cycle.")]
    [SerializeField, Range(0f, 1f)] private float density = 0.5f;
    [Tooltip("Streak length in pixels.")]
    [SerializeField, Range(1, 24)] private int dropLength = 6;
    [Tooltip("How much the tail fades toward its top (0 = solid streak).")]
    [SerializeField, Range(0f, 1f)] private float tailFade = 0.6f;
    [Tooltip("Quantise alpha into this many levels for a pixel-art look (0 = smooth).")]
    [SerializeField, Range(0, 8)] private int alphaSteps = 3;

    [Header("Fall & Splash")]
    [Tooltip("How far (world units) a drop is seen falling before it hits the ground. " +
             "Capped so the shader's search window still covers it (larger cells allow longer falls).")]
    [SerializeField, Min(0f)] private float fallDistance = 1.5f;
    [Tooltip("Seconds a drop takes to fall that distance.")]
    [SerializeField, Min(0.01f)] private float fallTime = 0.18f;
    [Tooltip("Seconds the 3-frame splash lasts.")]
    [SerializeField, Min(0.01f)] private float splashTime = 0.15f;
    [Tooltip("Max random pause before a cell drops again. Higher = calmer, sparser rain.")]
    [SerializeField, Min(0f)] private float restTime = 0.6f;
    [Tooltip("Rain angle in degrees from straight down. Positive = falling toward the right, negative = left. " +
             "Steep angles are limited by fall distance and cell size (longer falls / smaller cells allow less).")]
    [SerializeField, Range(-60f, 60f)] private float angle = 20f;

    [Header("Editor Preview")]
    [Tooltip("Animate outside play mode, so the rain can be tuned in the scene and game views.")]
    [SerializeField] private bool animateInEditor = true;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int SplashColorId = Shader.PropertyToID("_SplashColor");
    private static readonly int BrightnessVariationId = Shader.PropertyToID("_BrightnessVariation");
    private static readonly int PixelsPerUnitId = Shader.PropertyToID("_PixelsPerUnit");
    private static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
    private static readonly int DensityId = Shader.PropertyToID("_Density");
    private static readonly int LengthId = Shader.PropertyToID("_Length");
    private static readonly int TailFadeId = Shader.PropertyToID("_TailFade");
    private static readonly int FallDistanceId = Shader.PropertyToID("_FallDistance");
    private static readonly int FallTimeId = Shader.PropertyToID("_FallTime");
    private static readonly int SplashTimeId = Shader.PropertyToID("_SplashTime");
    private static readonly int RestTimeId = Shader.PropertyToID("_RestTime");
    private static readonly int SlantId = Shader.PropertyToID("_Slant");
    private static readonly int TimeOffsetId = Shader.PropertyToID("_TimeOffset");
    private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
    private static readonly int StepsId = Shader.PropertyToID("_Steps");

    // Must match MAX_CELLS_Y / MAX_CELLS_X in Rain2D.shader.
    private const int MaxCellsY = 10;
    private const int MaxCellsX = 8;

    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;
    private Mesh mesh;
    private MaterialPropertyBlock block;
    private Vector2 meshSize;

    private float rainTime;     // seconds of rain, scaled by the current speed
    private float fade = 1f;    // 0..1 fade-in on enable
    private float targetIntensity;
    private float intensityVelocity;
    private float intensityBlend = -1f;

    /// <summary>Current storm strength, 0..1.</summary>
    public float Intensity => intensity;

    /// <summary>Eases the rain to <paramref name="value"/> (0..1) over <paramref name="seconds"/>.</summary>
    public void SetIntensity(float value, float seconds = 1f)
    {
        targetIntensity = Mathf.Clamp01(value);
        intensityBlend = Mathf.Max(0.01f, seconds);
    }

    private void OnEnable()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        if (material != null) meshRenderer.sharedMaterial = material;

        if (mesh == null)
        {
            mesh = new Mesh { name = "Rain2D Mesh", hideFlags = HideFlags.DontSave };
            meshSize = Vector2.zero;
        }
        meshFilter.sharedMesh = mesh;

        fade = Application.isPlaying && fadeInTime > 0f ? 0f : 1f;
        intensityBlend = -1f;

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
        if (material != null) meshRenderer.sharedMaterial = material;
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

    private void Tick(float dt)
    {
        if (intensityBlend > 0f)
        {
            intensity = Mathf.SmoothDamp(intensity, targetIntensity, ref intensityVelocity, intensityBlend * 0.35f);
            if (Mathf.Abs(intensity - targetIntensity) < 0.001f) { intensity = targetIntensity; intensityBlend = -1f; }
        }

        if (fade < 1f) fade = fadeInTime > 0f ? Mathf.Min(1f, fade + dt / fadeInTime) : 1f;

        rainTime += dt * Mathf.Lerp(drizzleSpeed, 1f, intensity);
        // Keep the time small so float precision never makes the drops stutter in long sessions.
        if (rainTime > 3600f) rainTime -= 3600f;
    }

#if UNITY_EDITOR
    private double lastEditorTime;

    private void EditorTick()
    {
        if (this == null || Application.isPlaying) return;

        double now = UnityEditor.EditorApplication.timeSinceStartup;
        float dt = Mathf.Min((float)(now - lastEditorTime), 0.1f);
        lastEditorTime = now;

        if (animateInEditor) Tick(dt);
        FitToCamera();
        Apply();
        if (!animateInEditor) return;
        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        UnityEditor.SceneView.RepaintAll();
    }
#endif

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

        // Light rain is sparser as well as fainter; at 0 it is gone.
        float strength = Mathf.SmoothStep(0f, 1f, intensity);

        // Keep the fall inside the shader's search window, or drops would be cut off mid-air.
        float maxReachPx = (MaxCellsY - 2) * cellSize;
        float fallPx = Mathf.Min(fallDistance * pixelsPerUnit, maxReachPx - dropLength);
        float maxSlant = Mathf.Max(0f, (MaxCellsX - 2) * cellSize - 6f) / Mathf.Max(1f, fallPx + dropLength);
        float slant = Mathf.Tan(angle * Mathf.Deg2Rad);
        float clampedSlant = Mathf.Clamp(slant, -maxSlant, maxSlant);

        block.SetColor(ColorId, dropColor);
        block.SetColor(SplashColorId, splashColor);
        block.SetFloat(BrightnessVariationId, brightnessVariation);
        block.SetFloat(PixelsPerUnitId, pixelsPerUnit);
        block.SetFloat(CellSizeId, cellSize);
        block.SetFloat(DensityId, density * Mathf.Lerp(0.25f, 1f, strength));
        block.SetFloat(LengthId, dropLength);
        block.SetFloat(TailFadeId, tailFade);
        block.SetFloat(FallDistanceId, Mathf.Max(0f, fallPx) / pixelsPerUnit);
        block.SetFloat(FallTimeId, fallTime);
        block.SetFloat(SplashTimeId, splashTime);
        block.SetFloat(RestTimeId, restTime);
        block.SetFloat(SlantId, clampedSlant);
        block.SetFloat(TimeOffsetId, rainTime);
        block.SetFloat(OpacityId, opacity * fade * Mathf.Clamp01(intensity * 4f));
        block.SetFloat(StepsId, alphaSteps);

        meshRenderer.SetPropertyBlock(block);
    }
}
