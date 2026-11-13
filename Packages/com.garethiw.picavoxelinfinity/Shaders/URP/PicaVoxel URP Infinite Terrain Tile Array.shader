// PicaVoxel infinite terrain, sampling a Texture2DArray of tiles (Tools > PicaVoxel > Tile Array Builder) instead of
// a padded tile sheet. Each tile has its own mips, so distant terrain is filtered instead of shimmering, while
// close-up texels stay pixel-sharp. Opaque with alpha cut-out (tile alpha is all or nothing), and it fades into the
// sky at the draw distance instead of going transparent: the sky's colour in the pixel's direction comes from a
// cubemap of the skybox (Tools > PicaVoxel > Skybox Cubemap Builder), the scene's reflection probe, or a colour.
//
// Mesh UVs as the PicaVoxel Culled mesher writes them: xy 0..1 across a face, z the tile column, w the tile row
// (from the bottom of the sheet). w < 0 is a custom block mesh, whose xy are tile sheet UVs: those sample the sheet.
Shader "PicaVoxel/PicaVoxel URP Infinite Terrain Tile Array"
{
    Properties
    {
        [NoScaleOffset] _TileArray("Tile Array", 2DArray) = "" {}
        [NoScaleOffset] _TileSheet("Tile Sheet (custom block meshes)", 2D) = "white" {}
        _NumTiles("Num Tiles (columns, rows)", Vector) = (14,6,0,0)
        _Tint("Tint", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5

        [Header(Filtering)]
        [Toggle(_SMOOTH_MIPS)] _SmoothMips("Smooth Between Mips (sharp texels up close either way)", Float) = 1
        _MipBias("Mip Bias (+ softer sooner, - sharper further)", Range(-2,2)) = 0

        [Header(Distance Fade Into The Sky)]
        _DrawDistance("Draw Distance", Float) = 180
        _DistanceFalloff("Distance Falloff", Float) = 20
        [KeywordEnum(Cubemap, Reflection, Color)] _SkyFade("Sky Colour From", Float) = 1
        [NoScaleOffset] _SkyCube("Sky Cubemap", Cube) = "" {}
        _SkyTint("Sky Cubemap Tint", Color) = (1,1,1,1)
        _FadeColor("Fade Colour", Color) = (0.6,0.75,0.9,1)
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D_ARRAY(_TileArray);
    TEXTURE2D(_TileSheet);
    // Inline sampler states: nearest texel and nearest mip; and trilinear for past the first mip
    SAMPLER(sampler_pv_point_clamp);
    SAMPLER(sampler_pv_trilinear_clamp);

    CBUFFER_START(UnityPerMaterial)
        float4 _TileArray_TexelSize;
        float4 _TileSheet_TexelSize;
        float4 _NumTiles;
        float4 _Tint;
        float4 _SkyTint;
        float4 _FadeColor;
        float _Cutoff;
        float _MipBias;
        float _DrawDistance;
        float _DistanceFalloff;
    CBUFFER_END

    // The tile's colour for this pixel. Up close (a texel covering a pixel or more) it is the nearest texel of the
    // full-size tile, as the tile sheet shader shows it; further off it comes from the tile's mips.
    half4 SampleTile(float4 uv)
    {
        if (uv.w < 0.0)
            return SAMPLE_TEXTURE2D(_TileSheet, sampler_pv_point_clamp, uv.xy);

        float slice = floor(uv.w * _NumTiles.x + uv.z + 0.5);
        // Texels per pixel, from this face's own 0..1 UVs (continuous across the face, so no seams)
        float2 t = uv.xy * _TileArray_TexelSize.zw;
        float2 dx = ddx(t), dy = ddy(t);
        float lod = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8)) + _MipBias;

    #if defined(_SMOOTH_MIPS)
        UNITY_BRANCH
        if (lod <= 0.0)
            return SAMPLE_TEXTURE2D_ARRAY_LOD(_TileArray, sampler_pv_point_clamp, uv.xy, slice, 0);
        return SAMPLE_TEXTURE2D_ARRAY_LOD(_TileArray, sampler_pv_trilinear_clamp, uv.xy, slice, lod);
    #else
        return SAMPLE_TEXTURE2D_ARRAY_LOD(_TileArray, sampler_pv_point_clamp, uv.xy, slice, max(lod, 0.0));
    #endif
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _SMOOTH_MIPS
            #pragma shader_feature_local_fragment _SKYFADE_CUBEMAP _SKYFADE_REFLECTION _SKYFADE_COLOR

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURECUBE(_SkyCube);
            SAMPLER(sampler_SkyCube);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                float4 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float fogFactor : TEXCOORD4;
                half3 vertexSH : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.uv = input.texcoord;
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                output.vertexSH = SampleSH(output.normalWS);
                return output;
            }

            // What the sky shows in this direction from the camera
            half3 SkyColour(float3 dirWS)
            {
            #if defined(_SKYFADE_CUBEMAP)
                return SAMPLE_TEXTURECUBE_LOD(_SkyCube, sampler_SkyCube, dirWS, 0).rgb * _SkyTint.rgb;
            #elif defined(_SKYFADE_REFLECTION)
                half4 encoded = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, dirWS, 0);
                return DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);
            #else
                return _FadeColor.rgb;
            #endif
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 texColor = SampleTile(input.uv);
                clip(texColor.a - _Cutoff);

                half3 albedo = texColor.rgb * input.color.rgb * _Tint.rgb;

                // Lighting, as the tile sheet terrain shader does it
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.bakedGI = input.vertexSH;

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = 0;
                surfaceData.smoothness = 0;
                surfaceData.alpha = 1;
                surfaceData.occlusion = 1;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, input.fogFactor);

                // Into the sky towards the draw distance (the same curve the tile sheet shader fades its alpha by),
                // so the terrain's edge meets the skybox behind it instead of a hard line
                float3 toPixel = input.positionWS - GetCameraPositionWS();
                float dist = length(toPixel);
                float fade = saturate(pow(dist / _DrawDistance, _DistanceFalloff));
                color.rgb = lerp(color.rgb, SkyColour(toPixel / max(dist, 1e-4)), fade);
                color.a = 1;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _SMOOTH_MIPS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.uv = input.texcoord;
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                clip(SampleTile(input.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _SMOOTH_MIPS

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float4 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.texcoord;
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // (the same cut-out as the colour pass, mips included, so depth and colour agree)
                clip(SampleTile(input.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
