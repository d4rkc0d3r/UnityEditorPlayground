Shader "d4rkpl4y3r/RenderBakedMesh"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _DataTex("Data Texture", 2D) = "white" {}
        _Color("Color", Color) = (1,1,1,1)
        [gamma]_Smoothness("Smoothness", Range(0,1)) = 0.5
        _Metallic("Metallic", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

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
                return uint2(64, 40);
            }

            uint GetTessAmplificationFactor()
            {
                uint2 factors = GetTessFactors();
                return factors.x * factors.y * 2;
            }

            tessFactors hullConstant(InputPatch<v2h, 1> inputPatch, uint patchID : SV_PrimitiveID)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(inputPatch[0]);
                tessFactors o = (tessFactors)0;
                uint2 factors = GetTessFactors();
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
                uint width, height;
                _DataTex.GetDimensions(width, height);
                uint x = flatIndex % width;
                uint y = flatIndex / width;
                return _DataTex[int2(x, y)];
            }

            g2f LoadVertexData(uint vertexID)
            {
                g2f o;
                uint vertexOffset = 1 + (uint)_DataTex[int2(0, 0)] * 3 + vertexID * 8;
                o.vertex.x = LoadData(vertexOffset);
                o.vertex.y = LoadData(vertexOffset + 1);
                o.vertex.z = LoadData(vertexOffset + 2);
                o.vertex.w = 1;
                o.wPos = o.vertex.xyz;
                o.normal.x = LoadData(vertexOffset + 3);
                o.normal.y = LoadData(vertexOffset + 4);
                o.normal.z = LoadData(vertexOffset + 5);
                o.uv.x = LoadData(vertexOffset + 6);
                o.uv.y = LoadData(vertexOffset + 7);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                return o;
            }

            [maxvertexcount(3)]
            [instance(2)]
            void geom(triangle h2g inputVertices[3], inout TriangleStream<g2f> tristream,
                    uint instanceID : SV_GSInstanceID, uint patchID : SV_PrimitiveID)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(inputVertices[0]);
                uint tessID = GetIDFromTessellation(inputVertices);
                uint triID = tessID * 2 + instanceID;
                uint totalTriCount = (uint)_DataTex[int2(0, 0)];
                if (triID >= totalTriCount)
                    return;
                [unroll]
                for (int i = 0; i < 3; i++)
                {
                    int vertexIndex = LoadData(1 + triID * 3 + i);
                    g2f o = LoadVertexData(vertexIndex);
                    o.wPos = mul(unity_ObjectToWorld, o.vertex).xyz;
                    o.vertex = UnityObjectToClipPos(o.vertex);
                    o.normal = UnityObjectToWorldNormal(o.normal);
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
