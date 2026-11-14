using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP renderer feature for the lightning's two-tone frames: while <see cref="Strength"/> is above 0 the
/// camera image is remapped to two colours (bright parts -> light tone, dark parts -> dark tone), the classic
/// retro lightning look. Driven by <see cref="LightningFlash2D"/> through <see cref="Set"/>. When idle the pass
/// is not even enqueued, so it costs nothing between strikes.
/// Add it to the 2D renderer (Assets/Settings/Renderer2D) with the Hidden/LightningTwoTone material.
/// </summary>
public class LightningTwoToneFeature : ScriptableRendererFeature
{
    [SerializeField] private Material material;
    [SerializeField] private RenderPassEvent passEvent = RenderPassEvent.AfterRenderingPostProcessing;

    private static readonly int StrengthId = Shader.PropertyToID("_LTT_Strength");
    private static readonly int ThresholdId = Shader.PropertyToID("_LTT_Thresholds");
    private static readonly int LightId = Shader.PropertyToID("_LTT_Light");
    private static readonly int DarkId = Shader.PropertyToID("_LTT_Dark");
    private static readonly int InvertId = Shader.PropertyToID("_LTT_Invert");
    private static readonly int CenterId = Shader.PropertyToID("_LTT_Center");
    private static readonly int AlphaId = Shader.PropertyToID("_LTT_Alpha");

    /// <summary>0 = off, 1 = fully two-tone.</summary>
    public static float Strength { get; private set; }

    /// <summary>
    /// Turns the two-tone on. A pixel goes light above a brightness threshold that runs from
    /// <paramref name="thresholdNear"/> at the strike point to <paramref name="thresholdFar"/> at
    /// <paramref name="radiusViewport"/> (viewport heights) and beyond, so more lights up near the bolt.
    /// </summary>
    public static void Set(float strength, float thresholdNear, float thresholdFar, Color light, Color dark, bool invert,
                           Vector2 centerViewport, float radiusViewport, float aspect, bool local, float edgeFalloff,
                           float minAlpha = 0f, float maxAlpha = 1f)
    {
        Strength = Mathf.Clamp01(strength);
        Shader.SetGlobalFloat(StrengthId, Strength);
        Shader.SetGlobalVector(ThresholdId, new Vector4(thresholdNear, thresholdFar, Mathf.Max(1e-3f, radiusViewport), aspect));
        Shader.SetGlobalVector(AlphaId, new Vector4(Mathf.Clamp01(minAlpha), Mathf.Clamp01(maxAlpha), 0f, 0f));
        Shader.SetGlobalVector(CenterId, new Vector4(centerViewport.x, centerViewport.y, local ? 1f : 0f, Mathf.Clamp01(edgeFalloff)));
        // Globals are not colour-space converted like material colours, so convert here.
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        Shader.SetGlobalColor(LightId, linear ? light.linear : light);
        Shader.SetGlobalColor(DarkId, linear ? dark.linear : dark);
        Shader.SetGlobalFloat(InvertId, invert ? 1f : 0f);
    }

    public static void Clear()
    {
        Strength = 0f;
        Shader.SetGlobalFloat(StrengthId, 0f);
    }

    private Pass pass;

    public override void Create()
    {
        pass = new Pass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (material == null || Strength <= 0f) return;
        if (renderingData.cameraData.cameraType != CameraType.Game) return;

        pass.Setup(material);
        pass.renderPassEvent = passEvent;
        renderer.EnqueuePass(pass);
    }

    private class Pass : ScriptableRenderPass
    {
        private Material material;

        public void Setup(Material mat)
        {
            material = mat;
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer) return;

            TextureHandle source = resources.activeColorTexture;
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = "_LightningTwoTone";
            desc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(source, destination, material, 0),
                                    "Lightning Two-Tone");
            resources.cameraColor = destination;
        }
    }
}
