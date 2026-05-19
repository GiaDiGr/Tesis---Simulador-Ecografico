// Upgrade NOTE: replaced '_Object2World' with 'unity_ObjectToWorld'

Shader "VolumeRendering/LinearSliceRenderingShader"
{
    Properties
    {
        _DataTex("Data Texture (Generated)", 3D) = "" {}
        _TFTex("Transfer Function Texture", 2D) = "white" {}

        _PowerOn ("Power On", Float) = 1.0

        _Gain ("Gain", Float) = 1.0
        _FrequencyMHz ("Frequency MHz", Float) = 5.0

        _TGCEnabled ("TGC Enabled", Float) = 1.0
        _TGC0 ("TGC 0 Superficial", Float) = 1.0
        _TGC1 ("TGC 1", Float) = 1.0
        _TGC2 ("TGC 2", Float) = 1.0
        _TGC3 ("TGC 3", Float) = 1.0
        _TGC4 ("TGC 4", Float) = 1.0
        _TGC5 ("TGC 5 Deep", Float) = 1.0

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

            // Atenuación
            #define TISSUE_ATTENUATION_DB_CM_MHZ 0.5f
            #define IMAGING_DEPTH_CM 10.0f
            #define ATTENUATION_VISUAL_SCALE 0.30f

            // Resolución física
            #define SOUND_SPEED_CM_PER_US 0.154f
            #define IMAGE_WIDTH_CM 8.0f
            #define IMAGE_WIDTH_PX 512.0f
            #define IMAGE_HEIGHT_PX 512.0f
            #define APERTURE_DIAMETER_CM 2.5f
            #define PULSE_CYCLES_N 2.0f
            #define K_AXIAL 1.0f
            #define K_LATERAL 1.0f
            #define MAX_SIGMA_AXIAL_PX 8.0f
            #define MAX_SIGMA_LATERAL_PX 12.0f

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
            float _FrequencyMHz;

            float _TGCEnabled;
            float _TGC0;
            float _TGC1;
            float _TGC2;
            float _TGC3;
            float _TGC4;
            float _TGC5;

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

            float GetTGCGain(float depth01)
            {
                depth01 = saturate(depth01);

                if (_TGCEnabled < 0.5f)
                    return 1.0f;

                float x = depth01 * 5.0f;

                if (x < 1.0f)
                    return lerp(_TGC0, _TGC1, x);

                if (x < 2.0f)
                    return lerp(_TGC1, _TGC2, x - 1.0f);

                if (x < 3.0f)
                    return lerp(_TGC2, _TGC3, x - 2.0f);

                if (x < 4.0f)
                    return lerp(_TGC3, _TGC4, x - 3.0f);

                return lerp(_TGC4, _TGC5, x - 4.0f);
            }

            float GetFrequencyAttenuation(float depth01)
            {
                depth01 = saturate(depth01);

                float safeFrequencyMHz = max(_FrequencyMHz, 0.01f);
                float depthCm = depth01 * IMAGING_DEPTH_CM;

                float attenuationDb =
                    2.0f *
                    TISSUE_ATTENUATION_DB_CM_MHZ *
                    safeFrequencyMHz *
                    depthCm *
                    ATTENUATION_VISUAL_SCALE;

                float attenuationGain = pow(10.0f, -attenuationDb / 20.0f);

                return saturate(attenuationGain);
            }

            float Hash21(float2 p)
            {
                return frac(sin(dot(p, float2(127.1f, 311.7f))) * 43758.5453f);
            }

            float SignedNoise(float2 p)
            {
                return Hash21(p) * 2.0f - 1.0f;
            }

            float GetWavelengthCm()
            {
                float safeFrequencyMHz = max(_FrequencyMHz, 0.01f);
                return SOUND_SPEED_CM_PER_US / safeFrequencyMHz;
            }

            float GetSigmaAxialPx()
            {
                float wavelengthCm = GetWavelengthCm();
                float deltaZCm = IMAGING_DEPTH_CM / IMAGE_HEIGHT_PX;

                float sigmaAxialPx =
                    K_AXIAL *
                    PULSE_CYCLES_N *
                    wavelengthCm /
                    (2.0f * deltaZCm);

                return clamp(sigmaAxialPx, 0.0f, MAX_SIGMA_AXIAL_PX);
            }

            float GetSigmaLateralPx(float depth01)
            {
                depth01 = saturate(depth01);

                float wavelengthCm = GetWavelengthCm();

                float depthCm = max(
                    depth01 * IMAGING_DEPTH_CM,
                    IMAGING_DEPTH_CM / IMAGE_HEIGHT_PX
                );

                float deltaXCm = IMAGE_WIDTH_CM / IMAGE_WIDTH_PX;

                float sigmaLateralPx =
                    K_LATERAL *
                    1.4f *
                    wavelengthCm *
                    depthCm /
                    (APERTURE_DIAMETER_CM * deltaXCm);

                return clamp(sigmaLateralPx, 0.0f, MAX_SIGMA_LATERAL_PX);
            }

            float2 GetPhysicalResolutionJitterUV(float2 uv, float depth01)
            {
                float sigmaAxialPx = GetSigmaAxialPx();
                float sigmaLateralPx = GetSigmaLateralPx(depth01);

                float axialNoise = SignedNoise(
                    uv * float2(173.0f, 251.0f) +
                    float2(_FrequencyMHz * 7.13f, depth01 * 31.0f)
                );

                float lateralNoise = SignedNoise(
                    uv * float2(241.0f, 139.0f) +
                    float2(_FrequencyMHz * 11.71f, depth01 * 17.0f)
                );

                float axialUV = axialNoise * sigmaAxialPx / IMAGE_HEIGHT_PX;
                float lateralUV = lateralNoise * sigmaLateralPx / IMAGE_WIDTH_PX;

                return float2(lateralUV, axialUV);
            }

            fixed4 SampleLinearSliceAtUV(float2 uv)
            {
                if (
                    uv.x < 0.0f || uv.x > 1.0f ||
                    uv.y < 0.0f || uv.y > 1.0f
                )
                {
                    return float4(0.0f, 0.0f, 0.0f, 1.0f);
                }

                float depthCoord = lerp(
                    uv.y,
                    1.0f - uv.y,
                    step(0.5f, _InvertDepthAxis)
                );

                if (depthCoord > _DepthVisible)
                {
                    return float4(0.0f, 0.0f, 0.0f, 1.0f);
                }

                float tgcDepth01 = saturate(depthCoord / max(_DepthVisible, 0.0001f));

                float2 resolutionJitterUV = GetPhysicalResolutionJitterUV(
                    uv,
                    tgcDepth01
                );

                float2 sampleUV = saturate(uv + resolutionJitterUV);

                float3 localPlanePoint = float3(
                    0.5f - sampleUV.x,
                    0.0f,
                    0.5f - sampleUV.y
                );

                float3 worldPoint = mul(
                    _planeMat,
                    float4(localPlanePoint, 1.0f)
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

                float dataVal = tex3D(_DataTex, dataCoord).r;

                float4 col = tex2D(
                    _TFTex,
                    float2(dataVal, 0.0f)
                );

                float signalValue = max(max(col.r, col.g), col.b);

                if (signalValue <= 0.001f)
                {
                    col.rgb = float3(0.0f, 0.0f, 0.0f);
                }
                else
                {
                    float frequencyAttenuation = GetFrequencyAttenuation(tgcDepth01);
                    float tgcGain = GetTGCGain(tgcDepth01);

                    col.rgb = saturate(
                        col.rgb *
                        frequencyAttenuation *
                        tgcGain *
                        _Gain
                    );

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

                fixed4 mainColor = SampleLinearSliceAtUV(zoomedUV);

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

                    fixed4 minimapColor = SampleLinearSliceAtUV(minimapUV);
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