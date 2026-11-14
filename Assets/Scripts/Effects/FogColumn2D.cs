using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the Custom/2D/FogColumn2D shader: a band of fog the size of <see cref="Size"/>, centred on this
/// transform, made from the same baked noise and wind as <see cref="Fog2DController"/> so the two read as
/// one weather. Built for the Fog weather's fogged board column (FogWeather moves it onto the column), but
/// any vertical or horizontal strip works. Tunables go through a MaterialPropertyBlock, so they can be
/// tweaked live in the inspector without touching the shared material.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class FogColumn2D : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] private Material material;
    [Tooltip("Band size in world units. A board cell is 1x1; the defaults overhang the cell so the soft " +
             "sides and ends still cover it.")]
    [SerializeField] private Vector2 size = new Vector2(1.6f, 3.8f);
    [Tooltip("Over the minions by default, so units standing in the fog read as being inside it.")]
    [SerializeField] private string sortingLayer = "Layer 2";
    [SerializeField] private int sortingOrder = 50;

    [Header("Color")]
    [SerializeField] private Color fogColor = new Color(0.72f, 0.77f, 0.80f, 1f);
    [SerializeField] private Color shadowColor = new Color(0.36f, 0.41f, 0.44f, 1f);

    [Header("Amount")]
    [SerializeField, Range(0f, 1f)] private float density = 0.6f;
    [Tooltip("Fog everywhere in the band, under the puffs, so the whole column reads as fogged.")]
    [SerializeField, Range(0f, 1f)] private float haze = 0.3f;
    [SerializeField, Range(0f, 1f)] private float coverage = 0.55f;
    [SerializeField, Range(0.01f, 0.5f)] private float softness = 0.3f;

    [Header("Wind")]
    [Tooltip("Direction the fog drifts, in degrees. 90 = up the column, 270 = down.")]
    [SerializeField, Range(0f, 360f)] private float windAngle = 90f;
    [SerializeField, Min(0f)] private float windSpeed = 0.35f;
    [SerializeField, Range(0f, 3f)] private float layer2SpeedMult = 1.5f;
    [SerializeField, Range(1f, 4f)] private float stretch = 1.6f;
    [SerializeField, Range(0f, 1f)] private float gustStrength = 0.3f;
    [SerializeField, Min(0f)] private float gustsPerSecond = 0.15f;
    [SerializeField, Min(0f)] private float evolveSpeed = 0.03f;

    [Header("Shape")]
    [SerializeField, Min(0.01f)] private float puffSize = 0.8f;
    [SerializeField, Range(0.25f, 4f)] private float layer2Scale = 1.8f;
    [SerializeField, Range(0f, 1f)] private float layer2Weight = 0.5f;
    [SerializeField, Range(0f, 2f)] private float turbulence = 0.5f;

    [Header("Band Edges")]
    [Tooltip("How far in from each side the fog fades, as a fraction of the width.")]
    [SerializeField, Range(0.01f, 0.5f)] private float sideFeather = 0.3f;
    [Tooltip("How far in from the top and bottom the fog fades, as a fraction of the height.")]
    [SerializeField, Range(0.01f, 0.5f)] private float endFeather = 0.12f;
    [Tooltip("How much the noise breaks up the edges into wisps.")]
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
    private static readonly int WindDirId = Shader.PropertyToID("_WindDir");
    private static readonly int WindSpeedId = Shader.PropertyToID("_WindSpeed");
    private static readonly int Layer2SpeedId = Shader.PropertyToID("_Layer2Speed");
    private static readonly int StretchId = Shader.PropertyToID("_Stretch");
    private static readonly int GustStrengthId = Shader.PropertyToID("_GustStrength");
    private static readonly int GustFrequencyId = Shader.PropertyToID("_GustFrequency");
    private static readonly int EvolveId = Shader.PropertyToID("_Evolve");
    private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
    private static readonly int Layer2ScaleId = Shader.PropertyToID("_Layer2Scale");
    private static readonly int Layer2WeightId = Shader.PropertyToID("_Layer2Weight");
    private static readonly int WarpId = Shader.PropertyToID("_Warp");
    private static readonly int SideFeatherId = Shader.PropertyToID("_SideFeather");
    private static readonly int EndFeatherId = Shader.PropertyToID("_EndFeather");
    private static readonly int EdgeNoiseId = Shader.PropertyToID("_EdgeNoise");
    private static readonly int PixelSizeId = Shader.PropertyToID("_PixelSize");
    private static readonly int StepsId = Shader.PropertyToID("_Steps");

    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;
    private Mesh mesh;
    private MaterialPropertyBlock block;
    private Vector2 meshSize;

    public Vector2 Size
    {
        get => size;
        set { size = new Vector2(Mathf.Max(0.01f, value.x), Mathf.Max(0.01f, value.y)); BuildMesh(); }
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
            mesh = new Mesh { name = "FogColumn2D Mesh", hideFlags = HideFlags.DontSave };
            meshSize = Vector2.zero;
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

    private void BuildMesh()
    {
        if (mesh == null || size == meshSize) return;
        meshSize = size;

        float hx = size.x * 0.5f, hy = size.y * 0.5f;
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

        float rad = windAngle * Mathf.Deg2Rad;
        block.SetColor(ColorId, fogColor);
        block.SetColor(ShadowColorId, shadowColor);
        block.SetFloat(DensityId, density);
        block.SetFloat(HazeId, haze);
        block.SetFloat(CoverageId, coverage);
        block.SetFloat(SoftnessId, softness);
        block.SetVector(WindDirId, new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)));
        block.SetFloat(WindSpeedId, windSpeed);
        block.SetFloat(Layer2SpeedId, layer2SpeedMult);
        block.SetFloat(StretchId, stretch);
        block.SetFloat(GustStrengthId, gustStrength);
        block.SetFloat(GustFrequencyId, gustsPerSecond);
        block.SetFloat(EvolveId, evolveSpeed);
        block.SetFloat(NoiseScaleId, puffSize);
        block.SetFloat(Layer2ScaleId, layer2Scale);
        block.SetFloat(Layer2WeightId, layer2Weight);
        block.SetFloat(WarpId, turbulence);
        block.SetFloat(SideFeatherId, sideFeather);
        block.SetFloat(EndFeatherId, endFeather);
        block.SetFloat(EdgeNoiseId, edgeRaggedness);
        block.SetFloat(PixelSizeId, pixelSnap ? 1f / pixelsPerUnit : 0f);
        block.SetFloat(StepsId, alphaSteps);

        meshRenderer.SetPropertyBlock(block);
    }
}
