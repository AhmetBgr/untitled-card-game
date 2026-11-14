using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Meteor Shower's pixel-art VFX prefabs: MeteorFall (the ball, its fire trail, smoke and
/// embers) and MeteorImpact (flash, shockwave ring, debris, sparks, smoke, lingering embers). Re-run it
/// from Tools > Weather > Build Meteor VFX after changing a number here; hand tweaks made on the prefabs
/// are overwritten by a rebuild, so either tune here or stop rebuilding.
///
/// Art is 32 px per unit (one board cell), so particle sizes are whole multiples of a pixel (P) and
/// every texture is point filtered.
/// </summary>
public static class MeteorVfxBuilder
{
    private const string TexDir = "Assets/Sprites/VFX/Meteor/";
    private const string MatDir = "Assets/Materials/VFX/";
    private const string PrefabDir = "Assets/Prefabs/Weather/";
    private const string BallSprite = "Assets/Sprites/Highlight/meteor_ball.png";
    private const string Layer = "Layer 3"; // over the minions: the meteor lands ON the unit
    private const float P = 1f / 32f;

    [MenuItem("Tools/Weather/Build Meteor VFX")]
    public static void Build()
    {
        Material pixel = Mat("vfx_pixel");
        Material smoke = Mat("vfx_smoke_puff");
        Material ring = Mat("vfx_impact_ring");
        Material flash = Mat("vfx_impact_flash");
        Material glow = Mat("vfx_glow");

        BuildFall(pixel, glow);
        BuildImpact(pixel, smoke, ring, flash);
        AssetDatabase.SaveAssets();
        Debug.Log("[MeteorVfx] Built MeteorFall and MeteorImpact.");
    }

    // ------------------------------------------------------------------ fall

    private static void BuildFall(Material pixel, Material halo)
    {
        var root = new GameObject("MeteorFall");

        var ball = root.AddComponent<SpriteRenderer>();
        ball.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(BallSprite);
        ball.sortingLayerName = Layer;
        ball.sortingOrder = 110;

        // A hot halo around the rock so it reads as burning, not just falling.
        var glowGo = new GameObject("Glow");
        glowGo.transform.SetParent(root.transform, false);
        var glow = glowGo.AddComponent<SpriteRenderer>();
        glow.sprite = Sprite(halo);
        glow.color = new Color(1f, 0.5f, 0.12f, 0.8f);
        glow.sortingLayerName = Layer;
        glow.sortingOrder = 108;

        // Fire: dense hot pixels left behind as it moves, cooling white > yellow > orange > red > soot.
        var fire = PS(root.transform, "Trail", pixel, 105, loop: true);
        {
            var m = fire.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.4f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.3f);
            m.startSize = new ParticleSystem.MinMaxCurve(2 * P, 4 * P);
            m.maxParticles = 400;
            var e = fire.emission; e.rateOverDistance = 55f;
            Sphere(fire, 0.12f);
            ColorOverLife(fire, Fire(), Fade(0.7f));
            SizeOverLife(fire, 1f, 0.35f);
        }

        // Smoke: bigger, slower, dark pixels that drift up behind the fire.
        var soot = PS(root.transform, "Smoke", pixel, 100, loop: true);
        {
            var m = soot.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.75f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.2f);
            m.startSize = new ParticleSystem.MinMaxCurve(3 * P, 5 * P);
            m.gravityModifier = -0.15f;
            m.maxParticles = 200;
            var e = soot.emission; e.rateOverDistance = 18f;
            Sphere(soot, 0.15f);
            ColorOverLife(soot, Grad(new Color(0.42f, 0.36f, 0.36f), new Color(0.2f, 0.18f, 0.2f)), Fade(0.4f, 0.75f));
        }

        // Embers: a few single pixels flung off the sides, falling away.
        var embers = PS(root.transform, "Embers", pixel, 106, loop: true);
        {
            var m = embers.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
            m.startSize = new ParticleSystem.MinMaxCurve(1 * P, 2 * P);
            m.gravityModifier = 1.5f;
            var e = embers.emission; e.rateOverDistance = 10f;
            Sphere(embers, 0.1f);
            ColorOverLife(embers, Fire(), Fade(0.6f));
        }

        Save(root, "MeteorFall");
    }

    // ------------------------------------------------------------------ impact

    private static void BuildImpact(Material pixel, Material smoke, Material ring, Material flash)
    {
        var root = new GameObject("MeteorImpact");

        // Flash: one frame-ish burst of white light on the tile.
        var fl = PS(root.transform, "Flash", flash, 130);
        {
            var m = fl.main;
            m.startLifetime = 0.12f;
            m.startSpeed = 0f;
            m.startSize = 1f;
            Burst(fl, 0f, 1);
            ColorOverLife(fl, Grad(new Color(1f, 0.85f, 0.45f), new Color(1f, 0.5f, 0.15f)), Fade(0.2f, 0.6f));
            SizeOverLife(fl, 0.6f, 1f);
        }

        // Shockwave: a pixel ring racing out past the tile edge.
        var rg = PS(root.transform, "Shockwave", ring, 120);
        {
            var m = rg.main;
            m.startLifetime = 0.5f;
            m.startSpeed = 0f;
            m.startSize = 1f;
            Burst(rg, 0.1f, 1); // a beat after the flash, so the two read as hit-then-wave
            ColorOverLife(rg, Grad(new Color(1f, 0.85f, 0.45f), new Color(1f, 0.45f, 0.12f)), Fade(0.1f, 0.55f));
            var s = rg.sizeOverLifetime;
            s.enabled = true;
            s.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f, 0f, 2.5f), new Keyframe(1f, 1.4f, 0f, 0f)));
        }

        // Debris: chunks of rock and molten bits kicked up and falling back.
        var debris = PS(root.transform, "Debris", pixel, 125);
        {
            var m = debris.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.8f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 2.5f);
            m.startSize = new ParticleSystem.MinMaxCurve(2 * P, 3 * P);
            m.startColor = new ParticleSystem.MinMaxGradient(new Color(0.38f, 0.32f, 0.3f), new Color(1f, 0.55f, 0.15f));
            m.gravityModifier = 2f;
            Burst(debris, 0f, 7);
            Hemisphere(debris, 0.15f);
            ColorOverLife(debris, Grad(Color.white, Color.white), Fade(0.8f));
        }

        // Sparks: tiny bright pixels in every direction, quick.
        var sparks = PS(root.transform, "Sparks", pixel, 128);
        {
            var m = sparks.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.4f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 3f);
            m.startSize = new ParticleSystem.MinMaxCurve(1 * P, 2 * P);
            m.gravityModifier = 1f;
            Burst(sparks, 0f, 8);
            Sphere(sparks, 0.1f);
            ColorOverLife(sparks, Fire(), Fade(0.6f));
        }

        // Smoke: pixel puffs that bloom and hang a moment after the hit.
        var puff = PS(root.transform, "Smoke", smoke, 115);
        {
            var m = puff.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
            m.startSize = new ParticleSystem.MinMaxCurve(8 * P, 12 * P);
            m.gravityModifier = -0.15f;
            Burst(puff, 0.05f, 4);
            Hemisphere(puff, 0.2f);
            ColorOverLife(puff, Grad(new Color(0.45f, 0.38f, 0.36f), new Color(0.22f, 0.2f, 0.22f)), Fade(0.35f, 0.55f));
            SizeOverLife(puff, 0.6f, 1.3f);
        }

        // Embers: a few slow sparks rising off the crater after everything else has settled.
        var embers = PS(root.transform, "Embers", pixel, 127);
        {
            var m = embers.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.3f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
            m.startSize = 1 * P;
            m.gravityModifier = -0.3f;
            Burst(embers, 0.1f, 4);
            Hemisphere(embers, 0.3f);
            ColorOverLife(embers, Grad(new Color(1f, 0.85f, 0.3f), new Color(1f, 0.4f, 0.1f), new Color(0.7f, 0.15f, 0.05f)), Fade(0.6f));
            var n = embers.noise;
            n.enabled = true;
            n.strength = 0.4f;
            n.frequency = 1.5f;
        }

        Save(root, "MeteorImpact");
    }

    // ------------------------------------------------------------------ helpers

    private static ParticleSystem PS(Transform parent, string name, Material mat, int order, bool loop = false)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var m = ps.main;
        m.duration = 1f;
        m.loop = loop;
        m.playOnAwake = true;
        m.simulationSpace = ParticleSystemSimulationSpace.World;
        m.scalingMode = ParticleSystemScalingMode.Hierarchy;
        m.startRotation = 0f; // axis-aligned squares keep the pixel look

        var e = ps.emission;
        e.rateOverTime = 0f;
        e.rateOverDistance = 0f;

        var sh = ps.shape;
        sh.enabled = false;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.alignment = ParticleSystemRenderSpace.View;
        r.sortingLayerName = Layer;
        r.sortingOrder = order;
        return ps;
    }

    private static void Burst(ParticleSystem ps, float time, int count)
    {
        var e = ps.emission;
        e.SetBursts(new[] { new ParticleSystem.Burst(time, (short)count) });
    }

    private static void Sphere(ParticleSystem ps, float radius)
    {
        var sh = ps.shape;
        sh.enabled = true;
        sh.shapeType = ParticleSystemShapeType.Sphere;
        sh.radius = radius;
    }

    /// <summary>A dome opening upward (+Y): things kicked off the ground.</summary>
    private static void Hemisphere(ParticleSystem ps, float radius)
    {
        var sh = ps.shape;
        sh.enabled = true;
        sh.shapeType = ParticleSystemShapeType.Hemisphere;
        sh.radius = radius;
        sh.rotation = new Vector3(-90f, 0f, 0f);
    }

    private static void ColorOverLife(ParticleSystem ps, Gradient colors, GradientAlphaKey[] alpha)
    {
        var g = new Gradient();
        g.SetKeys(colors.colorKeys, alpha);
        var c = ps.colorOverLifetime;
        c.enabled = true;
        c.color = new ParticleSystem.MinMaxGradient(g);
    }

    private static void SizeOverLife(ParticleSystem ps, float from, float to)
    {
        var s = ps.sizeOverLifetime;
        s.enabled = true;
        s.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, from, 1f, to));
    }

    private static Gradient Fire() => Grad(
        new Color(1f, 1f, 0.85f), new Color(1f, 0.85f, 0.3f), new Color(1f, 0.45f, 0.1f),
        new Color(0.75f, 0.18f, 0.08f), new Color(0.25f, 0.2f, 0.2f));

    private static Gradient Grad(params Color[] colors)
    {
        var keys = new GradientColorKey[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            keys[i] = new GradientColorKey(colors[i], colors.Length == 1 ? 0f : i / (float)(colors.Length - 1));
        var g = new Gradient();
        g.SetKeys(keys, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }

    /// <summary>Opaque until <paramref name="holdUntil"/> of its life, then gone by the end.</summary>
    private static GradientAlphaKey[] Fade(float holdUntil, float start = 1f) => new[]
    {
        new GradientAlphaKey(start, 0f), new GradientAlphaKey(start, holdUntil), new GradientAlphaKey(0f, 1f),
    };

    private static Material Mat(string texture)
    {
        string texPath = TexDir + texture + ".png";
        var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.spritePixelsPerUnit = 32;
        ti.filterMode = FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.alphaIsTransparency = true;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.SaveAndReimport();

        if (!AssetDatabase.IsValidFolder("Assets/Materials/VFX")) AssetDatabase.CreateFolder("Assets/Materials", "VFX");

        string path = MatDir + "Meteor_" + texture.Replace("vfx_", "") + ".mat";
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static Sprite Sprite(Material mat) =>
        AssetDatabase.LoadAssetAtPath<Sprite>(AssetDatabase.GetAssetPath(mat.mainTexture));

    private static void Save(GameObject root, string name)
    {
        PrefabUtility.SaveAsPrefabAsset(root, PrefabDir + name + ".prefab");
        Object.DestroyImmediate(root);
    }
}
