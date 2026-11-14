using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Rendering;

/// <summary>
/// Lightning for the Thunderstorm weather. <see cref="Strike(Vector3)"/> drops a pixel-art bolt onto a world
/// point, lights the screen up from that point (Custom/2D/ScreenFlash2D, screen blend), turns the whole image
/// two-tone on the big frames (<see cref="LightningTwoToneFeature"/>) and plays the thunder a moment later,
/// since light arrives before sound. The strike is a list of hard frames, like a pixel-art animation.
/// Everything runs on its own. Use the inspector's "Strike Lightning" button to tune it, in or out of play
/// mode (the sound only plays in play mode).
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class LightningFlash2D : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("Camera to cover. Falls back to Camera.main.")]
    [SerializeField] private Camera targetCamera;
    [Tooltip("Mesh size relative to the camera view; >1 avoids edge gaps.")]
    [SerializeField, Min(1f)] private float overscan = 1.1f;
    [SerializeField] private Material material;
    [Tooltip("Above the board and minions, so the whole play area lights up.")]
    [SerializeField] private string sortingLayer = "Layer 3";
    [SerializeField] private int sortingOrder = 500;

    [System.Serializable]
    public struct Frame
    {
        [Tooltip("Seconds this frame is held.")]
        [Min(0.005f)] public float duration;
        [Tooltip("Radial light brightness (0..1).")]
        [Range(0f, 1f)] public float light;
        [Tooltip("Two-tone strength (0 = normal image, 1 = fully two colours).")]
        [Range(0f, 1f)] public float twoTone;
        [Tooltip("Swap the two tones on this frame (a negative frame).")]
        public bool invert;
        [Tooltip("Bolt visibility (0..1).")]
        [Range(0f, 1f)] public float bolt;
        [Tooltip("The bolt hits on this frame: the weather deals its damage when it starts.")]
        public bool impact;

        public Frame(float duration, float light, float twoTone, float bolt, bool impact = false, bool invert = false)
        {
            this.duration = duration; this.light = light; this.twoTone = twoTone;
            this.bolt = bolt; this.impact = impact; this.invert = invert;
        }
    }

    [Header("Frames")]
    [Tooltip("The strike, frame by frame. Hard cuts between frames, no smoothing.")]
    [SerializeField] private Frame[] frames =
    {
        new Frame(0.05f, 0.45f, 0f, 1f),               // leader: thin flash
        new Frame(0.05f, 0f, 0f, 0f),                  // dark beat
        new Frame(0.07f, 0f, 1f, 1f, impact: true),    // the hit: two-tone
        new Frame(0.05f, 0f, 1f, 1f, invert: true),    // negative frame
        new Frame(0.06f, 0.6f, 0f, 1f),
        new Frame(0.07f, 0.3f, 0f, 0.6f),
        new Frame(0.08f, 0.12f, 0f, 0.3f),
    };
    [Tooltip("Stretches or squeezes the whole strike in time.")]
    [SerializeField, Min(0.05f)] private float timeScale = 1f;

    [Header("Light")]
    [Tooltip("Off = no glow at all.")]
    [SerializeField] private bool lightEnabled = true;
    [Tooltip("On = the glow lights the whole screen evenly. Off = it only glows out from the strike over Flash Size.")]
    [SerializeField] private bool lightWholeScreen = false;
    [Tooltip("Flash size: how far (world units) the light reaches from the strike point.")]
    [SerializeField, Min(0.1f)] private float flashSize = 5f;
    [SerializeField] private Color flashColor = new Color(0.85f, 0.9f, 1f, 1f);
    [Tooltip("Brightness of a frame whose light is 1.")]
    [SerializeField, Range(0f, 1f)] private float peakBrightness = 0.85f;
    [Tooltip("Higher = light concentrated tighter around the strike point.")]
    [SerializeField, Range(0.25f, 4f)] private float falloff = 1.5f;
    [Tooltip("Hard brightness bands across the falloff, for a pixel-art look (0 = smooth).")]
    [SerializeField, Range(0, 6)] private int bands = 0;
    [SerializeField, Min(1f)] private float pixelsPerUnit = 32f;

    [Header("Two-Tone")]
    [Tooltip("Off = no two-tone frames (they show the normal image).")]
    [SerializeField] private bool twoToneEnabled = true;
    [Tooltip("On = the two-tone covers the whole screen. Off = only inside Flash Size around the strike.")]
    [SerializeField] private bool twoToneWholeScreen = false;
    [Tooltip("When not whole screen: how much of Flash Size the two-tone fades over toward its edge. " +
             "0 = hard edge, 1 = fades all the way from the strike point.")]
    [SerializeField, Range(0f, 1f)] private float twoToneFalloff = 0.25f;
    [Tooltip("Two-tone strength at the strike point (and everywhere when whole screen).")]
    [SerializeField, Range(0f, 1f)] private float twoToneMaxAlpha = 1f;
    [Tooltip("Two-tone strength at the edge of Flash Size and beyond. Above 0 tints the rest of the screen too.")]
    [SerializeField, Range(0f, 1f)] private float twoToneMinAlpha = 0f;
    [Tooltip("Needs LightningTwoToneFeature on the 2D renderer (Assets/Settings/Renderer2D).")]
    [SerializeField] private Color lightTone = new Color(0.88f, 0.93f, 1f, 1f);
    [SerializeField] private Color darkTone = new Color(0.08f, 0.09f, 0.22f, 1f);
    [Tooltip("At the strike point: brightness above which a pixel becomes the light tone (lower = more light).")]
    [SerializeField, Range(0f, 1f)] private float thresholdNear = 0.12f;
    [Tooltip("At Flash Size away and beyond. Raise toward 1 to keep the far screen dark.")]
    [SerializeField, Range(0f, 1f)] private float thresholdFar = 0.55f;

    [Header("Bolt")]
    [Tooltip("Off = no bolt sprite.")]
    [SerializeField] private bool boltEnabled = true;
    [Tooltip("The bolt sprite renderer. Its sprite's pivot must sit on the bolt's tip.")]
    [SerializeField] private SpriteRenderer bolt;
    [Tooltip("The bolt is scaled so its top reaches this far (world units) past the top of the screen.")]
    [SerializeField] private float boltTopMargin = 0.5f;
    [Tooltip("Never scale the bolt below this (world units, tip to top), e.g. when the target is near the top.")]
    [SerializeField, Min(0.1f)] private float boltMinHeight = 3f;
    [Tooltip("Randomly mirror the bolt each strike for variety.")]
    [SerializeField] private bool randomFlip = true;

    [Header("Thunder")]
    [Tooltip("Thunder sound. Empty = silent flash.")]
    [SerializeField] private SoundEffect thunderSound;
    [Tooltip("Seconds from the flash to the thunder (random between min and max). Light arrives before sound.")]
    [SerializeField] private Vector2 thunderDelay = new Vector2(0.35f, 0.7f);

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
    private static readonly int CenterId = Shader.PropertyToID("_Center");
    private static readonly int RadiusId = Shader.PropertyToID("_Radius");
    private static readonly int FalloffId = Shader.PropertyToID("_Falloff");
    private static readonly int AmbientId = Shader.PropertyToID("_Ambient");
    private static readonly int PixelsPerUnitId = Shader.PropertyToID("_PixelsPerUnit");
    private static readonly int BandsId = Shader.PropertyToID("_Bands");

    private MeshRenderer meshRenderer;
    private Mesh mesh;
    private Vector2 meshSize;
    private MaterialPropertyBlock block;
    private MaterialPropertyBlock boltBlock;
    private float elapsed = -1f;     // seconds into the current strike, <0 = idle
    private Vector2 strikePoint;

    /// <summary>A strike is playing.</summary>
    public bool IsFlashing => elapsed >= 0f;

    /// <summary>The thunder asset, or null. For the inspector's "no clip" warning.</summary>
    public SoundEffect ThunderSound => thunderSound;

    /// <summary>Seconds from <see cref="Strike(Vector3)"/> until the bolt hits, for gameplay to sync its damage.</summary>
    public float ImpactTime
    {
        get
        {
            float t = 0f;
            if (frames != null)
                foreach (var frame in frames)
                {
                    if (frame.impact) return t * timeScale;
                    t += frame.duration;
                }
            return 0f;
        }
    }

    /// <summary>Strikes the middle of the camera view.</summary>
    public void Strike()
    {
        Camera cam = Cam;
        Strike(cam != null ? cam.transform.position : transform.position);
    }

    /// <summary>Drops the bolt on <paramref name="target"/>, lights the screen from there and plays the thunder after <see cref="thunderDelay"/>.</summary>
    public void Strike(Vector3 target) => Strike(new[] { target });

    /// <summary>
    /// One strike hitting every target at once: a bolt on each, the light and two-tone centred between
    /// them, one thunder.
    /// </summary>
    public void Strike(IReadOnlyList<Vector3> targets)
    {
        if (!isActiveAndEnabled || targets == null || targets.Count == 0) return;
        elapsed = 0f;

        Vector3 centre = Vector3.zero;
        foreach (var t in targets) centre += t;
        strikePoint = centre / targets.Count;

        EnsureBolts(targets.Count);
        activeBolts = targets.Count;
        for (int i = 0; i < bolts.Count; i++)
        {
            if (i < activeBolts) PlaceBolt(bolts[i], targets[i]);
            else bolts[i].enabled = false;
        }

        if (thunderSound != null)
        {
            float delay = Mathf.Max(0f, Random.Range(Mathf.Min(thunderDelay.x, thunderDelay.y), Mathf.Max(thunderDelay.x, thunderDelay.y)));
            if (Application.isPlaying) AudioManager.Instance.Play(thunderSound, delay);
#if UNITY_EDITOR
            else PreviewSoundInEditor(thunderSound, delay);
#endif
        }

        Evaluate();

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            lastEditorTime = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.EditorApplication.update -= EditorTick;
            UnityEditor.EditorApplication.update += EditorTick;
        }
#endif
    }

    private Camera Cam => targetCamera != null ? targetCamera : Camera.main;

    private void OnEnable()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        if (material != null) meshRenderer.sharedMaterial = material;

        if (mesh == null)
        {
            mesh = new Mesh { name = "LightningFlash2D Mesh", hideFlags = HideFlags.DontSave };
            meshSize = Vector2.zero;
        }
        GetComponent<MeshFilter>().sharedMesh = mesh;

        elapsed = -1f;
        EnsureBolts(1);
        activeBolts = 0;
        ApplyFlash(0f);
        ApplyBolt(0f);
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorTick;
#endif
        if (bolt != null) bolt.enabled = false;
        foreach (var b in bolts) if (b != null) b.enabled = false;
        if (elapsed >= 0f) LightningTwoToneFeature.Clear();
        elapsed = -1f;
    }

    private void OnDestroy()
    {
        OnDestroyBolts();
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh);
        else DestroyImmediate(mesh);
    }

    private void OnValidate()
    {
        if (!isActiveAndEnabled || meshRenderer == null) return;
        if (material != null) meshRenderer.sharedMaterial = material;
    }

    private void LateUpdate()
    {
        if (Application.isPlaying) Tick(Time.deltaTime);
    }

    private void Tick(float dt)
    {
        if (elapsed < 0f) return;
        elapsed += dt;
        Evaluate();
    }

    private void Evaluate()
    {
        float t = elapsed / timeScale;
        int index = -1;
        if (elapsed >= 0f && frames != null)
        {
            float end = 0f;
            for (int i = 0; i < frames.Length; i++)
            {
                end += frames[i].duration;
                if (t < end) { index = i; break; }
            }
        }

        if (index < 0)
        {
            elapsed = -1f;
            ApplyFlash(0f);
            ApplyBolt(0f);
            LightningTwoToneFeature.Clear();
            return;
        }

        Frame f = frames[index];
        ApplyFlash(lightEnabled ? f.light * peakBrightness : 0f);
        ApplyBolt(boltEnabled ? f.bolt : 0f);
        if (twoToneEnabled && f.twoTone > 0f) SetTwoTone(f);
        else LightningTwoToneFeature.Clear();
    }

    private void SetTwoTone(Frame f)
    {
        Camera cam = Cam;
        Vector2 center = new Vector2(0.5f, 0.5f);
        float radius = 0.5f, aspect = 1f;
        if (cam != null)
        {
            center = cam.WorldToViewportPoint(strikePoint);
            aspect = cam.aspect;
            if (cam.orthographic) radius = flashSize / (2f * cam.orthographicSize);
        }
        LightningTwoToneFeature.Set(f.twoTone, thresholdNear, thresholdFar, lightTone, darkTone, f.invert,
                                    center, radius, aspect, local: !twoToneWholeScreen, edgeFalloff: twoToneFalloff,
                                    minAlpha: twoToneMinAlpha, maxAlpha: twoToneMaxAlpha);
    }

#if UNITY_EDITOR
    private double lastEditorTime;

    /// <summary>
    /// Outside play mode there is no AudioManager, so the test strike plays the thunder on a throwaway
    /// hidden AudioSource instead (no buses or throttling, just the clip at the asset's volume and pitch).
    /// </summary>
    private static void PreviewSoundInEditor(SoundEffect sound, float delay)
    {
        AudioClip clip = sound.NextClip();
        if (clip == null) return;

        var go = new GameObject("ThunderPreview") { hideFlags = HideFlags.HideAndDontSave };
        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.volume = sound.ResolveVolume();
        source.pitch = sound.ResolvePitch();
        source.spatialBlend = 0f;
        source.PlayDelayed(delay);

        double end = UnityEditor.EditorApplication.timeSinceStartup + delay + clip.length / Mathf.Max(0.01f, Mathf.Abs(source.pitch)) + 0.2;
        void Cleanup()
        {
            if (go != null && UnityEditor.EditorApplication.timeSinceStartup < end && !Application.isPlaying) return;
            UnityEditor.EditorApplication.update -= Cleanup;
            if (go != null) DestroyImmediate(go);
        }
        UnityEditor.EditorApplication.update += Cleanup;
    }

    private void EditorTick()
    {
        if (this == null || Application.isPlaying) { UnityEditor.EditorApplication.update -= EditorTick; return; }

        double now = UnityEditor.EditorApplication.timeSinceStartup;
        float dt = Mathf.Min((float)(now - lastEditorTime), 0.1f);
        lastEditorTime = now;

        Tick(dt);
        if (elapsed < 0f) UnityEditor.EditorApplication.update -= EditorTick;
        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        UnityEditor.SceneView.RepaintAll();
    }
#endif

    // ---------------------------------------------------------------- Bolt

    // The authored bolt plus runtime copies of it, one per target of a multi-target strike.
    private readonly List<SpriteRenderer> bolts = new List<SpriteRenderer>();
    private int activeBolts;

    private void EnsureBolts(int count)
    {
        bolts.RemoveAll(b => b == null);
        if (bolt == null) return;
        if (bolts.Count == 0) bolts.Add(bolt);

        while (bolts.Count < count)
        {
            var copy = Instantiate(bolt.gameObject, bolt.transform.parent);
            copy.name = $"{bolt.name} ({bolts.Count + 1})";
            copy.hideFlags = HideFlags.DontSave;   // runtime only, never saved into the scene or prefab
            var sr = copy.GetComponent<SpriteRenderer>();
            sr.enabled = false;
            bolts.Add(sr);
        }
    }

    /// <summary>Tip on the target, scaled so the top runs off the top of the screen.</summary>
    private void PlaceBolt(SpriteRenderer bolt, Vector3 target)
    {
        if (bolt == null || bolt.sprite == null) return;

        Sprite sprite = bolt.sprite;
        float spriteTop = sprite.bounds.max.y;   // pivot (tip) to top, at scale 1
        if (spriteTop <= 0.0001f) return;

        float height = boltMinHeight;
        Camera cam = Cam;
        if (cam != null && cam.orthographic)
            height = Mathf.Max(height, cam.transform.position.y + cam.orthographicSize + boltTopMargin - target.y);

        Transform t = bolt.transform;
        t.position = new Vector3(target.x, target.y, t.position.z);
        t.rotation = Quaternion.identity;
        t.localScale = Vector3.one;
        float scale = height / spriteTop;
        Vector3 parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(scale / parentScale.x, scale / parentScale.y, 1f);

        if (randomFlip) bolt.flipX = Random.value < 0.5f;
    }

    private void ApplyBolt(float visibility)
    {
        for (int i = 0; i < bolts.Count; i++)
        {
            SpriteRenderer b = bolts[i];
            if (b == null) continue;
            b.enabled = i < activeBolts && visibility > 0f;
            if (!b.enabled) continue;

            Color c = b.color;
            c.a = visibility;
            b.color = c;

            // Keep the bolt's pixels the same size as everything else's.
            boltBlock ??= new MaterialPropertyBlock();
            b.GetPropertyBlock(boltBlock);
            boltBlock.SetFloat(PixelsPerUnitId, pixelsPerUnit);
            b.SetPropertyBlock(boltBlock);
        }
    }

    private void OnDestroyBolts()
    {
        foreach (var b in bolts)
            if (b != null && b != bolt)
            {
                if (Application.isPlaying) Destroy(b.gameObject);
                else DestroyImmediate(b.gameObject);
            }
        bolts.Clear();
    }

    // ---------------------------------------------------------------- Flash

    private void FitToCamera()
    {
        Camera cam = Cam;
        if (cam == null || !cam.orthographic) return;

        float height = cam.orthographicSize * 2f * overscan;
        Vector2 size = new Vector2(height * cam.aspect, height);

        Vector3 camPos = cam.transform.position;
        transform.position = new Vector3(camPos.x, camPos.y, transform.position.z);
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one;

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

    private void ApplyFlash(float intensity)
    {
        if (meshRenderer == null) return;

        // Hidden while idle, so the flash costs nothing between strikes.
        meshRenderer.enabled = intensity > 0f;
        if (!meshRenderer.enabled) return;

        FitToCamera();
        meshRenderer.sortingLayerName = sortingLayer;
        meshRenderer.sortingOrder = sortingOrder;

        block ??= new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);
        block.SetColor(ColorId, flashColor);
        block.SetFloat(IntensityId, intensity);
        block.SetVector(CenterId, strikePoint);
        block.SetFloat(RadiusId, flashSize);
        block.SetFloat(FalloffId, falloff);
        block.SetFloat(AmbientId, lightWholeScreen ? 1f : 0f);   // 1 = same brightness everywhere, 0 = only inside Flash Size
        block.SetFloat(PixelsPerUnitId, pixelsPerUnit);
        block.SetFloat(BandsId, bands);
        meshRenderer.SetPropertyBlock(block);
    }
}
