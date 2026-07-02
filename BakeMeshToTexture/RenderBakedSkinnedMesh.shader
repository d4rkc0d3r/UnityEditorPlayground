Shader "d4rkpl4y3r/BakeMeshToTexture/RenderBakedSkinnedMesh"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _DataTex("Data Texture", 2D) = "white" {}
        _DataTexWidth("Data Texture Width", Float) = 0
        _DataTexHeight("Data Texture Height", Float) = 0
        _TessX("Tessellation X", Float) = 1
        _TessY("Tessellation Y", Float) = 1
        _Color("Color", Color) = (1,1,1,1)
        [gamma]_Smoothness("Smoothness", Range(0,1)) = 0.5
        _Metallic("Metallic", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="Geometry-1"
        }

        Pass
		{
			ZTest Always
			ZWrite Off
			Cull Off

			CGPROGRAM
			#pragma vertex vert
			#pragma geometry geom
			#pragma fragment frag
			#pragma target 5.0

			#include "UnityCG.cginc"

			Texture2D<float> _DataTex;
			float _DataTexWidth;

			float LoadData(uint flatIndex)
			{
				uint x = flatIndex % (uint)_DataTexWidth;
				uint y = flatIndex / (uint)_DataTexWidth;
				return _DataTex[int2(x, y)];
			}

			uint GetBoneCount() { return (uint)LoadData(2); }
			uint GetVertexCount() { return (uint)LoadData(1); }
			uint GetTriCount() { return (uint)LoadData(0); }
			uint GetVertexDataOffset() { return 3 + 3 * GetTriCount(); }
			uint GetBindposeOffset() { return GetVertexDataOffset() + 20 * GetVertexCount(); }

			float4x4 LoadBindpose(uint boneIndex)
			{
				uint offset = GetBindposeOffset() + boneIndex * 16;
				return float4x4(
					LoadData(offset + 0), LoadData(offset + 1), LoadData(offset + 2), LoadData(offset + 3),
					LoadData(offset + 4), LoadData(offset + 5), LoadData(offset + 6), LoadData(offset + 7),
					LoadData(offset + 8), LoadData(offset + 9), LoadData(offset + 10), LoadData(offset + 11),
					LoadData(offset + 12), LoadData(offset + 13), LoadData(offset + 14), LoadData(offset + 15)
				);
			}

			float4 PixelToClipPos(float2 pixelPos)
			{
				float4 pos = float4((pixelPos + .5) / _ScreenParams.xy, 1, 1);
				pos.xy = pos.xy * 2 - 1;
				#if UNITY_UV_STARTS_AT_TOP
				pos.y = -pos.y;
				#endif
				return pos;
			}

			struct g2f
			{
				float4 pos : SV_POSITION;
				float4 data : COLOR0;
			};

			// Vertex shader: pass through; skinning is handled by SkinnedMeshRenderer
			appdata_full vert(appdata_full v)
			{
				return v;
			}

			[maxvertexcount(3)]
			void geom(point appdata_full IN[1], uint boneID : SV_PrimitiveID, inout PointStream<g2f> stream)
			{
				// The vertex comes from a skinned point mesh where each point
				// is at the origin with a single bone weight, so after skinning
				// the position is the bone's world-space translation.
				//
				// We also get the bone's world-space X and Y axes from the
				// skinned normal and tangent (set up in the bone point mesh).
				// The Z axis is the cross product.

				float3 boneWorldPos = IN[0].vertex.xyz;

				// World-space axes from skinned normal (X) and tangent (Y)
				float3 worldX = IN[0].normal;
				float3 worldY = IN[0].tangent.xyz;
				float3 worldZ = cross(worldX, worldY) * IN[0].tangent.w / length(worldX);

				float4x4 boneToWorld = float4x4(
					worldX.x, worldY.x, worldZ.x, boneWorldPos.x,
					worldX.y, worldY.y, worldZ.y, boneWorldPos.y,
					worldX.z, worldY.z, worldZ.z, boneWorldPos.z,
					0, 0, 0, 1
				);

				// Pre-combine boneToWorld with bindpose so the second pass
				// only needs one matrix-vector multiply per bone weight.
				// combined = boneToWorld * bindpose
				float4x4 bindpose = LoadBindpose(boneID);
				float4x4 combined = mul(boneToWorld, bindpose);

				g2f o;
				o.data = float4(0, 0, 0, 0);

				for (int row = 0; row < 3; row++)
				{
					o.data = combined[row];
					o.pos = PixelToClipPos(float2(boneID, row));
					stream.Append(o);
				}
			}

			float4 frag(g2f i) : SV_Target
			{
				return i.data;
			}
            ENDCG
        }

        GrabPass
        {
            "_BoneMatrixScreen"
        }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma hull hull
            #pragma domain dom
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 5.0

            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            #include "Lighting.cginc"
            #include "UnityPBSLighting.cginc"

            #include "Assets/d4rkpl4y3rPrivateShaders/Includes/PBR.cginc"

            Texture2D _MainTex;
            SamplerState sampler_MainTex;
            Texture2D<float> _DataTex;
            Texture2D<float4> _BoneMatrixScreen;
            float _DataTexWidth;
            float _TessX;
            float _TessY;
            float4 _Color;
            float _Smoothness;
            float _Metallic;

            struct v2h
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct tessFactors
            {
                float edgeTess[4] : SV_TessFactor;
                float insideTess[2] : SV_InsideTessFactor;
            };

            struct h2g
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct g2f
            {
                float4 vertex : SV_POSITION;
                float3 wPos : WORLD_POS;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2h vert(appdata_base v)
            {
                v2h o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2h, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = v.vertex;
                return o;
            }

            uint2 GetTessFactors()
            {
                return uint2((uint)_TessX, (uint)_TessY);
            }

            uint GetTessAmplificationFactor()
            {
                uint2 factors = GetTessFactors();
                return factors.x * factors.y * 2;
            }

            uint GetTriCount();

            tessFactors hullConstant(InputPatch<v2h, 1> inputPatch, uint patchID : SV_PrimitiveID)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(inputPatch[0]);
                tessFactors o = (tessFactors)0;

                uint2 factors = GetTessFactors();
                if ((float)patchID > (GetTriCount() + GetTessAmplificationFactor() - 1.0) / GetTessAmplificationFactor())
                {
                    // Not all generated triangles will be used (tessellation generates a fixed number based on the max tessellation factors, but we might have fewer actual triangles).
                    // For unused triangles, set tessellation factors to 0 to avoid running the geometry shader on them.
                    return o;
                }
                o.edgeTess[1] = o.edgeTess[3] = o.insideTess[0] = factors.x;
                o.edgeTess[0] = o.edgeTess[2] = o.insideTess[1] = factors.y;
                return o;
            }

            [domain("quad")]
            [partitioning("integer")]
            [outputtopology("triangle_cw")]
            [patchconstantfunc("hullConstant")]
            [outputcontrolpoints(1)]
            v2h hull(InputPatch<v2h, 1> inputPatch, uint controlPointID : SV_OutputControlPointID)
            {
                return inputPatch[controlPointID];
            }

            [domain("quad")]
            h2g dom(tessFactors tessellation, const OutputPatch<v2h, 1> inputPatch, float2 bary : SV_DomainLocation)
            {
                h2g o = (h2g)inputPatch[0];
                o.vertex = float4(bary, 0, 1);
                return o;
            }

            uint GetIDFromTessellation(triangle h2g inputVertices[3])
            {
                float2 uv = (inputVertices[0].vertex.xy + inputVertices[1].vertex.xy + inputVertices[2].vertex.xy) / 3.0;
                uv *= (float2)GetTessFactors();
                return (((uint)floor(uv.y)) * GetTessFactors().x + (uint)floor(uv.x)) * 2u + (uint)(frac(uv.x) > 0.5);
            }

            float LoadData(uint flatIndex)
            {
                uint x = flatIndex % (uint)_DataTexWidth;
                uint y = flatIndex / (uint)_DataTexWidth;
                return _DataTex[int2(x, y)];
            }

            // Data layout:
            //   index 0: triCount
            //   index 1: vertexCount
            //   index 2: boneCount
            //   index 3 .. 3+3*triCount-1: triangle indices
            //   then vertex data: 20 floats per vertex
            //     pos(3) + normal(3) + uv(2) + tangent(4) + boneWeights(4) + boneIndices(4)
            //   then bindposes: 16 floats per bone

            uint GetTriCount() { return (uint)LoadData(0); }
            uint GetVertexCount() { return (uint)LoadData(1); }
            uint GetBoneCount() { return (uint)LoadData(2); }

            // First vertex data starts after: 3 counts + 3*triCount indices
            uint GetVertexDataOffset() { return 3 + 3 * GetTriCount(); }

            // Load pre-combined bone matrix (boneToWorld * bindpose) from the render texture.
            // The RT is boneCount x 3, each pixel is one row of the 4x4 combined matrix (first 3 rows).
            float4x4 LoadBoneMatrix(uint boneIndex)
            {
                float4 row0 = _BoneMatrixScreen[int2(boneIndex, 0)];
                float4 row1 = _BoneMatrixScreen[int2(boneIndex, 1)];
                float4 row2 = _BoneMatrixScreen[int2(boneIndex, 2)];
                return float4x4(
                    row0.xyz, row0.w,
                    row1.xyz, row1.w,
                    row2.xyz, row2.w,
                    0, 0, 0, 1
                );
            }

            g2f LoadVertexData(uint vertexID)
            {
                g2f o;
                uint offset = GetVertexDataOffset() + vertexID * 20;

                // Position (3)
                o.vertex.x = LoadData(offset + 0);
                o.vertex.y = LoadData(offset + 1);
                o.vertex.z = LoadData(offset + 2);
                o.vertex.w = 1;

                // Normal (3)
                o.normal.x = LoadData(offset + 3);
                o.normal.y = LoadData(offset + 4);
                o.normal.z = LoadData(offset + 5);

                // UV (2)
                o.uv.x = LoadData(offset + 6);
                o.uv.y = LoadData(offset + 7);

                // Tangent (4) - stored but not used in final output structure
                // offset + 8..11

                // Skinning: compute final transform from bone weights and pre-combined bone matrices.
                // The combined matrix (boneToWorld * bindpose) transforms directly from
                // rest-local vertex space to root-local skinned space in one multiply.

                // Skinned position and normal in a single loop
                float3 skinnedPos = float3(0, 0, 0);
                float3 skinnedNormal = float3(0, 0, 0);
                [loop]
                for (int i = 0; i < 4; i++)
                {
                    float weight = LoadData(offset + 12 + i);
                    if (weight == 0.0)
                        break;

                    uint boneIndex = (uint)LoadData(offset + 16 + i);
                    float4x4 combinedMat = LoadBoneMatrix(boneIndex);

                    skinnedPos += weight * mul(combinedMat, float4(o.vertex.xyz, 1)).xyz;
                    skinnedNormal += weight * mul((float3x3)combinedMat, o.normal);
                }

                o.wPos = mul(unity_ObjectToWorld, float4(skinnedPos, 1)).xyz;
                o.normal = mul((float3x3)unity_ObjectToWorld, skinnedNormal);

                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                return o;
            }

            [maxvertexcount(3)]
            void geom(triangle h2g inputVertices[3], inout TriangleStream<g2f> tristream, uint patchID : SV_PrimitiveID)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(inputVertices[0]);
                uint triID = GetIDFromTessellation(inputVertices) + patchID * GetTessAmplificationFactor();
                uint totalTriCount = GetTriCount();
                if (triID >= totalTriCount)
                    return;
                [unroll]
                for (int i = 0; i < 3; i++)
                {
                    int vertexIndex = (int)LoadData(3 + triID * 3 + i);
                    g2f o = LoadVertexData(vertexIndex);
                    o.vertex = UnityWorldToClipPos(o.wPos);
                    tristream.Append(o);
                }
            }

            float4 frag(g2f v) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
                float4 albedo = _MainTex.Sample(sampler_MainTex, v.uv);
                d4rkpl4y3r::pbr::PBRData pbr = d4rkpl4y3r::pbr::PBRData::d4rkDefault();
                pbr.worldPos = v.wPos;
                pbr.worldNormal = normalize(v.normal);
                pbr.viewDir = normalize(v.wPos - _WorldSpaceCameraPos);
                pbr.albedo = albedo.rgb * _Color.rgb;
                pbr.metallic = _Metallic;
                pbr.smoothness = _Smoothness;
                return pbr.UnityBRDF();
            }
            ENDCG
        }
    }
}
