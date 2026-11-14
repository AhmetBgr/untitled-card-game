Shader "Custom/2D/ScreenFlash2D"
{
    // Full-screen lightning light, brightest at the strike point (_Center) and falling off over _Radius.
    // Screen blend (not additive), so it lifts the scene without washing colours out to flat white.
    // Optional hard bands on a world-space pixel grid for a pixel-art falloff. Driven by LightningFlash2D.
    Properties
    {
        [HideInInspector] _MainTex ("Unused", 2D) = "white" {}
        _Color ("Flash Color", Color) = (0.85,0.9,1,1)
        _Intensity ("Intensity", Range(0,1)) = 0
        _Center ("Strike Point (world XY)", Vector) = (0,0,0,0)
        _Radius ("Size (world units)", Float) = 5
        _Falloff ("Falloff Power", Range(0.25,4)) = 1.5
        _Ambient ("Ambient (brightness far away)", Range(0,1)) = 0.2
        _PixelsPerUnit ("Pixels Per Unit", Float) = 32
        _Bands ("Bands (0 = smooth)", Float) = 0
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
        Blend OneMinusDstColor One   // screen: result = src + dst - src * dst

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float2 worldPos : TEXCOORD0;
            };

            fixed4 _Color;
            float  _Intensity;
            float4 _Center;
            float  _Radius;
            float  _Falloff;
            float  _Ambient;
            float  _PixelsPerUnit;
            float  _Bands;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex   = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float ppu = max(_PixelsPerUnit, 1.0);
                float2 wp = (floor(i.worldPos * ppu) + 0.5) / ppu;

                float d = length(wp - _Center.xy) / max(_Radius, 1e-3);
                float near = pow(saturate(1.0 - d), _Falloff);
                if (_Bands >= 1) near = ceil(near * _Bands) / _Bands;

                float k = saturate(_Intensity * lerp(_Ambient, 1.0, near));
                return fixed4(_Color.rgb * k, 0);
            }
            ENDCG
        }
    }
}
