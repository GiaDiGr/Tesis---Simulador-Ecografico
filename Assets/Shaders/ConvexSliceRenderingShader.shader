// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "VolumeRendering/ConvexSectorSamplingShader"
{
    Properties
    {
        _DataTex("Data Texture (Generated)", 3D) = "" {}
        _TFTex("Transfer Function Texture", 2D) = "white" {}

        _PowerOn ("Power On", Float) = 1.0

        _Gain ("Gain", Float) = 1.0
        _DynamicRange ("Dynamic Range", Float) = 60.0
        _ReferenceDynamicRange ("Reference Dynamic Range", Float) = 60.0
        _ContrastFromDynamicRange ("Contrast From Dynamic Range", Float) = 1.0

        _DepthVisible ("Depth Visible", Float) = 1.0
        _InvertDepthAxis ("Invert Depth Axis", Float) = 0.0

        _Zoom ("Zoom", Float) = 1.0
        _ZoomCenter ("Zoom Center", Vector) = (0.5, 0.5, 0.0, 0.0)
        _ShowZoomMinimap ("Show Zoom Minimap", Float) = 1.0
        _ZoomMinimapRect ("Zoom Minimap Rect", Vector) = (0.72, 0.72, 0.25, 0.25)
        _ZoomBoxThickness ("Zoom Box Thickness", Float) = 0.01

        _SectorAngleDegrees ("Sector Angle Degrees", Float) = 70.0
        _SectorApexDistanceAboveTop ("Sector Apex Distance Above Top", Float) = 0.15
        _SectorInnerRadius ("Sector Inner Radius", Float) = 0.25

        _SectorApexLocalZ ("Sector Apex Local Z", Float) = 0.45
        _SectorLocalXScale ("Sector Local X Scale", Float) = 1.0
        _SectorLocalZScale ("Sector Local Z Scale", Float) = 1.0

        _FlipSectorX ("Flip Sector X", Float) = 1.0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" }
        LOD 100

        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler3D _DataTex;
            sampler2D _TFTex;

            float _PowerOn;

            float _Gain;
            float _DynamicRange;
            float _ReferenceDynamicRange;
            float _ContrastFromDynamicRange;

            float _DepthVisible;
            float _InvertDepthAxis;

            float _Zoom;
            float4 _ZoomCenter;
            float _ShowZoomMinimap;
            float4 _ZoomMinimapRect;
            float _ZoomBoxThickness;

            float _SectorAngleDegrees;
            float _SectorApexDistanceAboveTop;
            float _SectorInnerRadius;

            float _SectorApexLocalZ;
            float _SectorLocalXScale;
            float _SectorLocalZScale;

            float _FlipSectorX;

            uniform float4x4 _parentInverseMat;
            uniform float4x4 _planeMat;

            v2f vert(appdata v)
            {
                v2f o;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

                return o;
            }

            float2 ApplyZoomToUV(float2 uv)
            {
                float safeZoom = max(_Zoom, 1.0f);
                float2 center = saturate(_ZoomCenter.xy);

                return center + (uv - center) / safeZoom;
            }

            bool IsInsideRect(float2 uv, float4 rect)
            {
                return
                    uv.x >= rect.x &&
                    uv.x <= rect.x + rect.z &&
                    uv.y >= rect.y &&
                    uv.y <= rect.y + rect.w;
            }

            float2 RemapToRectUV(float2 uv, float4 rect)
            {
                return float2(
                    (uv.x - rect.x) / rect.z,
                    (uv.y - rect.y) / rect.w
                );
            }

            bool IsMinimapOuterBorder(float2 minimapUV)
            {
                float t = max(_ZoomBoxThickness, 0.001f);

                bool nearLeft = minimapUV.x < t;
                bool nearRight = minimapUV.x > 1.0f - t;
                bool nearBottom = minimapUV.y < t;
                bool nearTop = minimapUV.y > 1.0f - t;

                return nearLeft || nearRight || nearBottom || nearTop;
            }

            bool IsZoomBoxBorder(float2 minimapUV)
            {
                float safeZoom = max(_Zoom, 1.0f);
                float2 center = saturate(_ZoomCenter.xy);

                float2 halfSize = float2(0.5f, 0.5f) / safeZoom;

                float2 boxMin = center - halfSize;
                float2 boxMax = center + halfSize;

                boxMin = saturate(boxMin);
                boxMax = saturate(boxMax);

                bool insideBox =
                    minimapUV.x >= boxMin.x &&
                    minimapUV.x <= boxMax.x &&
                    minimapUV.y >= boxMin.y &&
                    minimapUV.y <= boxMax.y;

                if (!insideBox)
                    return false;

                float t = max(_ZoomBoxThickness, 0.001f);

                bool nearLeft = abs(minimapUV.x - boxMin.x) < t;
                bool nearRight = abs(minimapUV.x - boxMax.x) < t;
                bool nearBottom = abs(minimapUV.y - boxMin.y) < t;
                bool nearTop = abs(minimapUV.y - boxMax.y) < t;

                return nearLeft || nearRight || nearBottom || nearTop;
            }

            fixed4 SampleUltrasoundAtUV(float2 uv)
            {
                if (
                    uv.x < 0.0f || uv.x > 1.0f ||
                    uv.y < 0.0f || uv.y > 1.0f
                )
                {
                    return float4(0.0f, 0.0f, 0.0f, 1.0f);
                }

                float apexDist = _SectorApexDistanceAboveTop;
                float innerRadius = _SectorInnerRadius;
                float outerRadius = 1.0f + apexDist;

                float halfAngle =
                    _SectorAngleDegrees *
                    0.5f *
                    0.01745329252f;

                float yFromApex =
                    uv.y +
                    apexDist;

                float halfWidth =
                    outerRadius *
                    sin(halfAngle);

                float xFromApex =
                    (uv.x - 0.5f) *
                    2.0f *
                    halfWidth;

                float radius = length(
                    float2(xFromApex, yFromApex)
                );

                float angle = atan2(
                    xFromApex,
                    yFromApex
                );

                if (
                    abs(angle) > halfAngle ||
                    radius < innerRadius ||
                    radius > outerRadius
                )
                {
                    return float4(0.0f, 0.0f, 0.0f, 1.0f);
                }

                float radial01 =
                    (radius - innerRadius) /
                    (outerRadius - innerRadius);

                float depth01 = radial01;

                if (_InvertDepthAxis > 0.5f)
                    depth01 = 1.0f - radial01;

                if (depth01 > _DepthVisible)
                    return float4(0.0f, 0.0f, 0.0f, 1.0f);

                float flipX = 1.0f;

                if (_FlipSectorX > 0.5f)
                    flipX = -1.0f;

                float localX =
                    xFromApex *
                    flipX *
                    _SectorLocalXScale;

                float localZ =
                    _SectorApexLocalZ -
                    yFromApex *
                    _SectorLocalZScale;

                float3 worldPoint = mul(
                    _planeMat,
                    float4(localX, 0.0f, localZ, 1.0f)
                ).xyz;

                float3 relVert = mul(
                    _parentInverseMat,
                    float4(worldPoint, 1.0f)
                ).xyz;

                float3 dataCoord =
                    relVert +
                    float3(0.5f, 0.5f, 0.5f);

                if (
                    dataCoord.x < 0.0f || dataCoord.x > 1.0f ||
                    dataCoord.y < 0.0f || dataCoord.y > 1.0f ||
                    dataCoord.z < 0.0f || dataCoord.z > 1.0f
                )
                {
                    return float4(0.0f, 0.0f, 0.0f, 1.0f);
                }

                float dataVal = tex3D(
                    _DataTex,
                    dataCoord
                ).r;

                float4 col = tex2D(
                    _TFTex,
                    float2(dataVal, 0.0f)
                );

                float signalValue = max(
                    max(col.r, col.g),
                    col.b
                );

                if (signalValue <= 0.001f)
                {
                    col.rgb = float3(0.0f, 0.0f, 0.0f);
                }
                else
                {
                    col.rgb = saturate(col.rgb * _Gain);

                    col.rgb = saturate(
                        0.5f +
                        _ContrastFromDynamicRange *
                        (col.rgb - 0.5f)
                    );
                }

                col.a = 1.0f;

                return col;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_PowerOn < 0.5f)
                {
                    return float4(0.0f, 0.0f, 0.0f, 1.0f);
                }

                float2 originalUV = i.uv;

                float2 zoomedUV = ApplyZoomToUV(originalUV);

                fixed4 mainColor = SampleUltrasoundAtUV(zoomedUV);

                if (
                    _ShowZoomMinimap > 0.5f &&
                    _Zoom > 1.0001f &&
                    IsInsideRect(originalUV, _ZoomMinimapRect)
                )
                {
                    float2 minimapUV = RemapToRectUV(
                        originalUV,
                        _ZoomMinimapRect
                    );

                    fixed4 minimapColor = SampleUltrasoundAtUV(minimapUV);

                    minimapColor.rgb *= 0.65f;

                    if (IsZoomBoxBorder(minimapUV))
                    {
                        minimapColor.rgb = float3(1.0f, 1.0f, 1.0f);
                        minimapColor.a = 1.0f;
                    }

                    if (IsMinimapOuterBorder(minimapUV))
                    {
                        minimapColor.rgb = float3(1.0f, 1.0f, 1.0f);
                        minimapColor.a = 1.0f;
                    }

                    return minimapColor;
                }

                return mainColor;
            }

            ENDCG
        }
    }
}