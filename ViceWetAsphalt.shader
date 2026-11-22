// ============================================================================
// Project: Vice Coast Open-World (Vice City Style Vertical Slice)
// Module:  Expansion — Custom URP Wet Asphalt & Neon Puddle Shader
// File:    _Project/Art/Shaders/Weather/ViceWetAsphalt.shader
// Author:  Lead Game Architect
// Purpose: URP Lit-compatible HLSL shader driven live by DynamicWeatherSystem:
//          - Reads _GlobalWetness, _GlobalPuddleLevel, and _RainRippleSpeed
//          - Procedurally masks asphalt puddles using world-space noise
//          - Animates concentric raindrop ripples in puddle normal maps
//          - Darkens wet asphalt albedo & boosts mirror smoothness for crisp
//            Vice City neon street reflections at night.
// ============================================================================

Shader "ViceCoast/URP/WetAsphaltNeon"
{
    Properties
    {
        [Header(Base Asphalt)]
        _BaseMap ("Asphalt Albedo", 2D) = "gray" {}
        _BaseColor ("Tint Color", Color) = (0.16, 0.17, 0.19, 1)
        [Normal] _BumpMap ("Asphalt Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Strength", Range(0, 2)) = 1.0
        _DrySmoothness ("Dry Smoothness", Range(0, 0.6)) = 0.22
        _WetSmoothness ("Wet Puddle Smoothness", Range(0.6, 0.99)) = 0.95
        _WetDarken ("Wet Albedo Darkening", Range(0.25, 1.0)) = 0.45

        [Header(Puddle & Ripple Settings)]
        _PuddleScale ("Puddle Noise Scale", Float) = 0.08
        _RippleScale ("Rain Ripple Density", Float) = 1.6
        _RippleStrength ("Rain Ripple Normal Strength", Range(0, 1)) = 0.35
        _LocalWetnessOverride ("Local Wetness Boost", Range(0, 1)) = 0.0

        [Header(Neon Reflection Boost)]
        _NeonSpecularTint ("Neon Reflection Tint", Color) = (1.0, 0.45, 0.85, 1)
        _ReflectionIntensity ("Puddle Reflection Boost", Range(0.5, 3.0)) = 1.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Global parameters broadcast by DynamicWeatherSystem.cs
            float _GlobalWetness;
            float _GlobalPuddleLevel;
            float _RainRippleSpeed;
            float _GlobalNightFactor;

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _BumpScale;
                half _DrySmoothness;
                half _WetSmoothness;
                half _WetDarken;
                float _PuddleScale;
                float _RippleScale;
                half _RippleStrength;
                half _LocalWetnessOverride;
                half4 _NeonSpecularTint;
                half _ReflectionIntensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float3 tangentWS   : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float fogFactor    : TEXCOORD5;
            };

            // Fast 2D hash & value noise for seamless world-space puddles
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise2D(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Procedural expanding concentric raindrop ripples in puddle cells
            float3 ComputeRainRippleNormal(float2 worldXZ, float timeVal)
            {
                float2 uv = worldXZ * _RippleScale;
                float2 cell = floor(uv);
                float2 local = frac(uv) - 0.5;

                float phase = frac(timeVal * 0.85 + Hash21(cell));
                float2 centerOffset = (float2(Hash21(cell + 17.3), Hash21(cell + 91.7)) - 0.5) * 0.45;
                float2 diff = local - centerOffset;
                float dist = length(diff);

                float ring = sin((dist - phase * 0.65) * 34.0);
                float envelope = smoothstep(0.45, 0.05, dist) * (1.0 - phase) * smoothstep(0.0, 0.12, phase);
                float2 grad = normalize(diff + 1e-4) * (ring * envelope * _RippleStrength);

                return normalize(float3(grad.x, grad.y, 1.0));
            }

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionHCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS = normInputs.normalWS;
                OUT.tangentWS = normInputs.tangentWS;
                OUT.bitangentWS = normInputs.bitangentWS;
                OUT.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float wetness = saturate(max(_GlobalWetness, _LocalWetnessOverride));
                float puddleLevel = saturate(max(_GlobalPuddleLevel, _LocalWetnessOverride));

                // World-space multi-octave puddle mask
                float n1 = ValueNoise2D(IN.positionWS.xz * _PuddleScale);
                float n2 = ValueNoise2D(IN.positionWS.xz * (_PuddleScale * 2.7));
                float puddleNoise = n1 * 0.7 + n2 * 0.3;
                float puddleMask = smoothstep(1.0 - puddleLevel, 1.0 - puddleLevel + 0.18, puddleNoise) * wetness;

                // Sample base albedo & darken wet asphalt realistically
                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                half3 albedo = lerp(baseSample.rgb, baseSample.rgb * _WetDarken, saturate(wetness * 0.85 + puddleMask * 0.4));

                // Tangent-space asphalt normal flattened inside puddles + animated rain ripples
                half3 dryNormalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                half3 flatNormalTS = half3(0, 0, 1);

                float rippleTime = _Time.y * max(0.0, _RainRippleSpeed);
                half3 rippleNormalTS = (_RainRippleSpeed > 0.05 && puddleMask > 0.05)
                    ? (half3)ComputeRainRippleNormal(IN.positionWS.xz, rippleTime)
                    : flatNormalTS;

                half3 finalNormalTS = normalize(lerp(dryNormalTS, rippleNormalTS, puddleMask));
                half3x3 TBN = half3x3(normalize(IN.tangentWS), normalize(IN.bitangentWS), normalize(IN.normalWS));
                half3 normalWS = normalize(mul(finalNormalTS, TBN));

                // Populate URP InputData & SurfaceData for full PBR lighting & environment reflections
                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogFactor;
                inputData.bakedGI = SampleSH(normalWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = lerp(0.0, 0.18, puddleMask);
                surfaceData.smoothness = lerp(_DrySmoothness, _WetSmoothness, saturate(wetness * 0.45 + puddleMask * 0.85));
                surfaceData.normalTS = finalNormalTS;
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1.0;

                // Subtle Fresnel neon sky/signage sheen across wet night puddles
                half ndotv = saturate(dot(normalWS, inputData.viewDirectionWS));
                half fresnel = pow(1.0 - ndotv, 4.0);
                surfaceData.emission = _NeonSpecularTint.rgb * (fresnel * puddleMask * saturate(_GlobalNightFactor + 0.25) * 0.28 * _ReflectionIntensity);

                half4 finalColor = UniversalFragmentPBR(inputData, surfaceData);
                finalColor.rgb = MixFog(finalColor.rgb, IN.fogFactor);
                return finalColor;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }
    FallBack "Universal Render Pipeline/Lit"
}
