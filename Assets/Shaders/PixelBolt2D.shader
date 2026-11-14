Shader "Custom/2D/PixelBolt2D"
{
    // Sprite shader for the lightning bolt. Samples the sprite once per world-space pixel (_PixelsPerUnit)
    // with a hard alpha cutoff, so smooth hi-res art renders as crisp pixel art at any scale, and adds a
    // pixel glow outline around it. Works with SpriteRenderer (vertex color = tint/fade, flipX supported).
    // The sprite should use Mesh Type: Full Rect so the glow is not clipped by a tight mesh.
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Color ("Core Color", Color) = (1,1,1,1)
        _GlowColor ("Glow Color", Color) = (0.55,0.75,1,0.9)
        _GlowColor2 ("Outer Glow Color", Color) = (0.35,0.45,1,0.45)
        _GlowWidth ("Glow Width (pixels, 0-2)", Range(0,2)) = 2
        _PixelsPerUnit ("Pixels Per Unit", Float) = 32
        _Cutoff ("Alpha Cutoff", Range(0.01,0.99)) = 0.4
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType"      = "Transparent"
            "PreviewType"     = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float4 color    : COLOR;
                float2 uv       : TEXCOORD0;
                float2 worldPos : TEXCOORD1;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _GlowColor;
            fixed4 _GlowColor2;
            float  _GlowWidth;
            float  _PixelsPerUnit;
            float  _Cutoff;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex   = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xy;
                o.uv       = v.texcoord;
                o.color    = v.color;
                return o;
            }

            float Solid(float2 uv)
            {
                return (any(uv < 0.0) || any(uv > 1.0)) ? 0.0 : step(_Cutoff, tex2D(_MainTex, uv).a);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // uv change per world unit along x and y (no rotation; flipX just makes it negative).
                float2 duvdw = float2(ddx(i.uv.x) / ddx(i.worldPos.x + 1e-7), ddy(i.uv.y) / ddy(i.worldPos.y + 1e-7));
                if (!all(isfinite(duvdw))) duvdw = 0;

                float ppu = max(_PixelsPerUnit, 1.0);
                float2 snapped = (floor(i.worldPos * ppu) + 0.5) / ppu;
                float2 uv = i.uv + (snapped - i.worldPos) * duvdw;
                float2 step1 = duvdw / ppu;   // one world pixel, in uv

                fixed4 col = 0;
                if (Solid(uv) > 0.5)
                {
                    col = _Color;
                }
                else if (_GlowWidth >= 1.0)
                {
                    // Inner glow: 4-neighbours one pixel away. Outer glow: within two pixels.
                    float n1 = Solid(uv + float2(step1.x, 0)) + Solid(uv - float2(step1.x, 0)) +
                               Solid(uv + float2(0, step1.y)) + Solid(uv - float2(0, step1.y));
                    if (n1 > 0.0) col = _GlowColor;
                    else if (_GlowWidth >= 2.0)
                    {
                        float n2 = Solid(uv + float2(2 * step1.x, 0)) + Solid(uv - float2(2 * step1.x, 0)) +
                                   Solid(uv + float2(0, 2 * step1.y)) + Solid(uv - float2(0, 2 * step1.y)) +
                                   Solid(uv + step1) + Solid(uv - step1) +
                                   Solid(uv + float2(step1.x, -step1.y)) + Solid(uv + float2(-step1.x, step1.y));
                        if (n2 > 0.0) col = _GlowColor2;
                    }
                }

                col *= i.color;
                col.rgb *= col.a;
                return col;
            }
            ENDCG
        }
    }
}
