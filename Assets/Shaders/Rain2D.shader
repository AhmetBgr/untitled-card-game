Shader "Custom/2D/Rain2D"
{
    // Procedural pixel-art rain falling onto the ground plane (the play area), for the Thunderstorm weather.
    // The view is split into cells; each cell drops one raindrop per cycle. The drop falls a short distance
    // along the slant, lands on a random pixel inside its cell and plays a tiny 3-frame splash there.
    // Everything is computed on a world-space pixel grid (_PixelsPerUnit), so drops and splashes are crisp.
    // Values are driven per-instance by Rain2D.cs (MaterialPropertyBlock).
    Properties
    {
        [HideInInspector] _MainTex ("Unused", 2D) = "white" {}

        [Header(Color)]
        _Color ("Drop Color", Color) = (0.75,0.85,1,0.8)
        _SplashColor ("Splash Color", Color) = (0.85,0.92,1,0.7)
        _BrightnessVariation ("Brightness Variation", Range(0,1)) = 0.4

        [Header(Grid)]
        _PixelsPerUnit ("Pixels Per Unit", Float) = 32
        _CellSize ("Cell Size (pixels, one drop each)", Float) = 14

        [Header(Drops)]
        _Density ("Density", Range(0,1)) = 0.5
        _Length ("Drop Length (pixels)", Float) = 6
        _TailFade ("Tail Fade", Range(0,1)) = 0.6
        _FallDistance ("Fall Distance (world units)", Float) = 1.5
        _FallTime ("Fall Time (s)", Float) = 0.18
        _SplashTime ("Splash Time (s)", Float) = 0.15
        _RestTime ("Max Rest Between Drops (s)", Float) = 0.6
        _Slant ("Slant (pixels across per pixel down)", Range(-2,2)) = 0.35
        _TimeOffset ("Time (set by Rain2D)", Float) = 0

        [Header(Fade)]
        _Opacity ("Overall Opacity", Range(0,1)) = 1
        _Steps ("Alpha Steps (0 = smooth)", Float) = 3
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType"      = "Transparent"
            "PreviewType"     = "Plane"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha   // premultiplied alpha, same as Sprites-Default

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.5   // integer hashing
            #include "UnityCG.cginc"

            // Upper bounds on the cells a pixel has to look at; keeps the loops bounded on every platform.
            #define MAX_CELLS_Y 10
            #define MAX_CELLS_X 8

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float4 color    : COLOR;
                float2 worldPos : TEXCOORD0;
            };

            fixed4 _Color;
            fixed4 _SplashColor;
            float  _BrightnessVariation;
            float  _PixelsPerUnit;
            float  _CellSize;
            float  _Density;
            float  _Length;
            float  _TailFade;
            float  _FallDistance;
            float  _FallTime;
            float  _SplashTime;
            float  _RestTime;
            float  _Slant;
            float  _TimeOffset;
            float  _Opacity;
            float  _Steps;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex   = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xy;
                o.color    = v.color;
                return o;
            }

            // Integer hash (lowbias32). Unlike a frac(sin)-style float hash it stays random for any input
            // size, so drops do not degrade into a fixed pattern as the rain time grows.
            uint Hash(uint x)
            {
                x ^= x >> 16; x *= 0x7feb352du;
                x ^= x >> 15; x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }

            // Random in [0,1) for a cell, a cycle of that cell, and a salt (one per random quantity).
            float Rand(float2 cell, float cycle, uint salt)
            {
                uint h = Hash((uint)(int)cell.x * 0x9E3779B1u ^ salt);
                h = Hash(h ^ (uint)(int)cell.y * 0x85EBCA77u);
                h = Hash(h ^ (uint)(int)cycle * 0xC2B2AE3Du);
                return (h >> 8) * (1.0 / 16777216.0);
            }

            // 3-frame pixel splash: a dot, then a little crown, then two fading droplets flying apart.
            float Splash(float2 d, float frame)
            {
                float ax = abs(d.x);
                if (frame < 1.0) return (d.y == 0 && ax <= 1) ? 1.0 : 0.0;
                if (frame < 2.0) return ((d.y == 1 && ax == 1) || (d.y == 0 && ax == 2)) ? 0.8 : 0.0;
                return (d.y == 1 && ax == 3) ? 0.45 : 0.0;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = floor(i.worldPos * _PixelsPerUnit);

                float cell   = max(2.0, floor(_CellSize));
                float len    = max(1.0, floor(_Length));
                float fallPx = max(0.0, floor(_FallDistance * _PixelsPerUnit));
                float reach  = fallPx + len;
                float fallT  = max(0.01, _FallTime);
                float splT   = max(0.01, _SplashTime);

                // Landing points that could reach this pixel: below it by up to `reach`, and offset sideways
                // along the slant (plus the splash's 3-pixel spread).
                float side = _Slant * reach;
                float2 lo = float2(p.x - max(0.0, side) - 3.0, p.y - reach - 1.0);
                float2 hi = float2(p.x - min(0.0, side) + 3.0, p.y);
                float2 c0 = floor(lo / cell);
                float2 c1 = floor(hi / cell);

                float best = 0.0;
                float3 rgb = _Color.rgb;
                float  a   = _Color.a;

                [loop]
                for (int iy = 0; iy < MAX_CELLS_Y; iy++)
                {
                    float cy = c0.y + iy;
                    if (cy > c1.y) break;

                    [loop]
                    for (int ix = 0; ix < MAX_CELLS_X; ix++)
                    {
                        float cx = c0.x + ix;
                        if (cx > c1.x) break;
                        float2 c = float2(cx, cy);

                        // Each cell runs its own cycle: fall, splash, then a random rest.
                        float hc     = Rand(c, 0, 1u);
                        float period = fallT + splT + _RestTime * hc;
                        float cyc    = _TimeOffset / period + Rand(c, 0, 2u);
                        float k      = floor(cyc);
                        float tau    = (cyc - k) * period;
                        if (tau >= fallT + splT) continue;

                        if (Rand(c, k, 3u) >= _Density) continue;

                        float2 land = c * cell + floor(float2(Rand(c, k, 4u), Rand(c, k, 5u)) * cell);
                        float2 d = p - land;
                        float  bright = 1.0 - _BrightnessVariation * Rand(c, k, 6u);

                        float v;
                        if (tau < fallT)
                        {
                            // Falling: the head is h pixels above the landing point; the trail runs up from it
                            // along the same stair-stepped path, so the drop visibly lands on its spot.
                            float h = floor(fallPx * (1.0 - tau / fallT));
                            float dy = d.y;
                            if (dy < h || dy >= h + len) continue;
                            if (d.x != floor(dy * _Slant)) continue;
                            v = (1.0 - _TailFade * (dy - h) / len) * bright;
                            if (v > best) { best = v; rgb = _Color.rgb; a = _Color.a; }
                        }
                        else
                        {
                            float frame = floor((tau - fallT) / splT * 3.0);
                            v = Splash(d, frame) * bright;
                            if (v > best) { best = v; rgb = _SplashColor.rgb; a = _SplashColor.a; }
                        }
                    }
                }

                float alpha = best * a;
                // Quantise before the overall fade, so fading the rain in/out stays smooth.
                if (_Steps >= 1) alpha = ceil(alpha * _Steps) / _Steps;
                alpha *= _Opacity;

                fixed4 col = fixed4(rgb, saturate(alpha)) * i.color;
                col.rgb *= col.a;
                return col;
            }
            ENDCG
        }
    }
}
