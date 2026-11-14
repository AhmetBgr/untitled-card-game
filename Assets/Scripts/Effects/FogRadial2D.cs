using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the Custom/2D/FogRadial2D shader: a ring of fog or dust centred on this transform that circles
/// the centre and spirals in or out, made from the same baked noise as <see cref="Fog2DController"/> and
/// <see cref="FogColumn2D"/>. Built for the Sandstorm weather's visual (spawned at the board's centre), with
/// a thin "eye" over the board so the gameplay stays readable. Tunables go through a
/// MaterialPropertyBlock, so they can be tweaked live in the inspector without touching the shared material.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class FogRadial2D : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] private Material material;
    [Tooltip("Outer radii of the ring in world units (x = horizontal, y = vertical). A board cell is 1x1.")]
    [SerializeField] private Vector2 radii = new Vector2(5f, 5f);
    [Tooltip("Over the minions by default, so units read as standing inside the storm.")]
    [SerializeField] private string sortingLayer = "Layer 2";
    [SerializeField] private int sortingOrder = 50;

    [Header("Color")]
    [SerializeField] private Color fogColor = new Color(0.90f, 0.76f, 0.52f, 1f);
    [SerializeField] private Color shadowColor = new Color(0.62f, 0.45f, 0.26f, 1f);

    [Header("Amount")]
    [SerializeField, Range(0f, 1f)] private float density = 0.6f;
    [Tooltip("Dust everywhere in the ring, under the puffs.")]
    [SerializeField, Range(0f, 1f)] private float haze = 0.2f;
    [SerializeField, Range(0f, 1f)] private float coverage = 0.55f;
    [SerializeField, Range(0.01f, 0.5f)] private float softness = 0.3f;

    [Header("Swirl")]
    [Tooltip("Turns per second around the centre. Positive = counter-clockwise, negative = clockwise.")]
    [SerializeField] private float swirlSpeed = 0.03f;
    [Tooltip("World units per second toward the centre. Negative blows outward.")]
    [SerializeField] private float inflowSpeed = 0.15f;
    [Tooltip("How much the puffs curve into spiral arms (turns per world unit from the centre).")]
    [SerializeField] private float spiralTwist = 0.08f;
    [SerializeField, Range(0f, 3f)] private float layer2SpeedMult = 1.5f;
    [SerializeField, Range(0f, 1f)] private float gustStrength = 0.3f;
    [SerializeField, Min(0f)] private float gustsPerSecond = 0.15f;
    [SerializeField, Min(0f)] private float evolveSpeed = 0.03f;

    [Header("Shape")]
    [Tooltip("Puff size from the centre outward, in world units.")]
    [SerializeField, Min(0.01f)] private float puffSize = 0.8f;
    [Tooltip("Puffs around the ring = this x 8. Fewer = longer streaks around the circle.")]
    [SerializeField, Range(1, 12)] private int arcRepeats = 3;
    [SerializeField, Range(0.25f, 4f)] private float layer2Scale = 1.8f;
    [SerializeField, Range(0f, 1f)] private float layer2Weight = 0.5f;
    [SerializeField, Range(0f, 2f)] private float turbulence = 0.5f;

    [Header("Ring")]
    [Tooltip("How far in from the outer radius the fog fades out, as a fraction of the radius.")]
    [SerializeField, Range(0.01f, 1f)] private float outerFeather = 0.35f;
    [Tooltip("Radius of the thin centre (the eye), as a fraction of the outer radius. 0 = no eye.")]
    [SerializeField, Range(0f, 1f)] private float eyeRadius = 0.4f;
    [Tooltip("How wide the fade into the eye is, as a fraction of the outer radius.")]
    [SerializeField, Range(0.01f, 1f)] private float eyeFeather = 0.3f;
    [Tooltip("How much fog is left inside the eye (0 = clear, 1 = as thick as the ring).")]
    [SerializeField, Range(0f, 1f)] private float eyeOpacity = 0.2f;
    [Tooltip("How much the noise breaks up both edges into wisps.")]
    [SerializeField, Range(0f, 1f)] private float edgeRaggedness = 0.5f;

    [Header("Pixel Art")]
    [SerializeField] private bool pixelSnap;
    [SerializeField, Min(1f)] private float pixelsPerUnit = 32f;
    [SerializeField, Range(0, 16)] private int alphaSteps;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int ShadowColorId = Shader.PropertyToID("_ShadowColor");
    private static readonly int DensityId = Shader.PropertyToID("_Density");
    private static readonly int HazeId = Shader.PropertyToID("_Haze");
    private static readonly int CoverageId = Shader.PropertyToID("_Coverage");
    private static readonly int SoftnessId = Shader.PropertyToID("_Softness");
    private static readonly int SwirlSpeedId = Shader.PropertyToID("_SwirlSpeed");
    private static readonly int InflowSpeedId = Shader.PropertyToID("_InflowSpeed");
    private static readonly int TwistId = Shader.PropertyToID("_Twist");
    private static readonly int Layer2SpeedId = Shader.PropertyToID("_Layer2Speed");
    private static readonly int GustStrengthId = Shader.PropertyToID("_GustStrength");
    private static readonly int GustFrequencyId = Shader.PropertyToID("_GustFrequency");
    private static readonly int EvolveId = Shader.PropertyToID("_Evolve");
    private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
    private static readonly int ArcRepeatsId = Shader.PropertyToID("_ArcRepeats");
    private static readonly int Layer2ScaleId = Shader.PropertyToID("_Layer2Scale");
    private static readonly int Layer2WeightId = Shader.PropertyToID("_Layer2Weight");
    private static readonly int WarpId = Shader.PropertyToID("_Warp");
    private static readonly int RadiiId = Shader.PropertyToID("_Radii");
    private static readonly int OuterFeatherId = Shader.PropertyToID("_OuterFeather");
    private static readonly int InnerRadiusId = Shader.PropertyToID("_InnerRadius");
    private static readonly int InnerFeatherId = Shader.PropertyToID("_InnerFeather");
    private static readonly int InnerOpacityId = Shader.PropertyToID("_InnerOpacity");
    private static readonly int EdgeNoiseId = Shader.PropertyToID("_EdgeNoise");
    private static readonly int PixelSizeId = Shader.PropertyToID("_PixelSize");
    private static readonly int StepsId = Shader.PropertyToID("_Steps");

    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;
    private Mesh mesh;
    private MaterialPropertyBlock block;
    private Vector2 meshRadii;

    public Vector2 Radii
    {
        get => radii;
        set { radii = new Vector2(Mathf.Max(0.01f, value.x), Mathf.Max(0.01f, value.y)); BuildMesh(); Apply(); }
    }

    public float Density
    {
        get => density;
        set { density = Mathf.Clamp01(value); Apply(); }
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
            mesh = new Mesh { name = "FogRadial2D Mesh", hideFlags = HideFlags.DontSave };
            meshRadii = Vector2.zero;
        }
        meshFilter.sharedMesh = mesh;

        BuildMesh();
        Apply();
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
        BuildMesh();
        Apply();
    }

    /// <summary>A quad just big enough for the ring; nothing past the outer radius is drawn.</summary>
    private void BuildMesh()
    {
        if (mesh == null || radii == meshRadii) return;
        meshRadii = radii;

        // The ragged outer edge can push a little past the radius.
        float hx = radii.x * 1.05f, hy = radii.y * 1.05f;
        mesh.Clear();
        mesh.SetVertices(new[]
        {
            new Vector3(-hx, -hy), new Vector3(hx, -hy), new Vector3(-hx, hy), new Vector3(hx, hy),
        });
        mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) });
        mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
        mesh.RecalculateBounds();
    }

    private void Apply()
    {
        if (meshRenderer == null) return;
        if (material != null && meshRenderer.sharedMaterial != material) meshRenderer.sharedMaterial = material;
        meshRenderer.sortingLayerName = sortingLayer;
        meshRenderer.sortingOrder = sortingOrder;

        block ??= new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);

        block.SetColor(ColorId, fogColor);
        block.SetColor(ShadowColorId, shadowColor);
        block.SetFloat(DensityId, density);
        block.SetFloat(HazeId, haze);
        block.SetFloat(CoverageId, coverage);
        block.SetFloat(SoftnessId, softness);
        block.SetFloat(SwirlSpeedId, swirlSpeed);
        block.SetFloat(InflowSpeedId, inflowSpeed);
        block.SetFloat(TwistId, spiralTwist);
        block.SetFloat(Layer2SpeedId, layer2SpeedMult);
        block.SetFloat(GustStrengthId, gustStrength);
        block.SetFloat(GustFrequencyId, gustsPerSecond);
        block.SetFloat(EvolveId, evolveSpeed);
        block.SetFloat(NoiseScaleId, puffSize);
        block.SetFloat(ArcRepeatsId, arcRepeats);
        block.SetFloat(Layer2ScaleId, layer2Scale);
        block.SetFloat(Layer2WeightId, layer2Weight);
        block.SetFloat(WarpId, turbulence);
        block.SetVector(RadiiId, radii);
        block.SetFloat(OuterFeatherId, outerFeather);
        block.SetFloat(InnerRadiusId, eyeRadius);
        block.SetFloat(InnerFeatherId, eyeFeather);
        block.SetFloat(InnerOpacityId, eyeOpacity);
        block.SetFloat(EdgeNoiseId, edgeRaggedness);
        block.SetFloat(PixelSizeId, pixelSnap ? 1f / pixelsPerUnit : 0f);
        block.SetFloat(StepsId, alphaSteps);

        meshRenderer.SetPropertyBlock(block);
    }
}
