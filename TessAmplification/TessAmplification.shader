Shader "d4rkpl4y3r/Debug/TessAmplification"
{
    Properties {
		[IntRange]_TessCenterA("Center Tess 0", Range(0,64)) = 3
		[IntRange]_TessCenterB("Center Tess 1", Range(0,64)) = 3
        _LerpToLinear("Lerp to Linear", Range(0,1)) = 0
        _AnimationTime("Animation Time", Float) = 10
        [Toggle]_UseQuadTess("Use Quad Tess", Float) = 1
		[KeywordEnum(Point, Triangle, Quad)] INPUT_TOPOLOGY("Input Topology", Float) = 0
	}
	SubShader {

		CGINCLUDE

		#include "UnityCG.cginc"

		float _TessCenterA;
		float _TessCenterB;
        float _LerpToLinear;
        float _AnimationTime;

        #pragma shader_feature_local _USEQUADTESS_ON
        #pragma shader_feature_local INPUT_TOPOLOGY_POINT INPUT_TOPOLOGY_TRIANGLE INPUT_TOPOLOGY_QUAD

		struct appdata
		{
			float4 vertex : POSITION;
			float3 normal : NORMAL;
			float2 uv : TEXCOORD0;
		};

		struct v2h
		{
			float4 position : POS;
			float3 normal : NORMAL;
			float2 uv : TEXCOORD;
		};

		struct tessFactors
		{
            #if defined(_USEQUADTESS_ON)
                float edgeTess[4] : SV_TessFactor;
                float insideTess[2] : SV_InsideTessFactor;
            #else
                float edgeTess[3] : SV_TessFactor;
                float insideTess : SV_InsideTessFactor;
            #endif
		};

		struct h2d
		{
			float3 position : POS;
			float3 normal : NORMAL;
			float2 uv : TEXCOORD;
		};

		struct d2g
		{
			float3 position : POS;
			float2 bary : TEXCOORD;
		};

		struct g2f
		{
			float4 position : SV_Position;
			float3 bary : TEXCOORD0;
			float4 color : COLOR0;
		};

		v2h vert(appdata I)
		{
			return (v2h)0;
		}

        #if defined(INPUT_TOPOLOGY_TRIANGLE)
            #define PATCH_SIZE 3
        #elif defined(INPUT_TOPOLOGY_QUAD)
            #define PATCH_SIZE 4
        #else
            #define PATCH_SIZE 1
        #endif

		tessFactors hullConstant(InputPatch<v2h, PATCH_SIZE> I , uint primID : SV_PrimitiveID)
		{
			tessFactors o = (tessFactors)0;

			if (primID > 1) return o;

            #if defined(_USEQUADTESS_ON)
                o.insideTess[0] = _TessCenterA;
                o.insideTess[1] = _TessCenterB;
                o.edgeTess[1] = o.edgeTess[3] = o.insideTess[0];
                o.edgeTess[0] = o.edgeTess[2] = o.insideTess[1];
            #else
                o.insideTess = _TessCenterA;
                o.edgeTess[0] = o.edgeTess[1] = o.edgeTess[2] = o.insideTess;
            #endif
           
			return o;
		}

        #if defined(_USEQUADTESS_ON)
            [domain("quad")]
            [partitioning("integer")]
            [outputtopology("triangle_cw")]
            [patchconstantfunc("hullConstant")]
            [outputcontrolpoints(4)]
            h2d hull(InputPatch<v2h, PATCH_SIZE> I, uint uCPID : SV_OutputControlPointID )
            {
                h2d O = (h2d)0;
                O.position = I[uCPID].position.xyz;
                O.normal = I[uCPID].normal;
                O.uv = I[uCPID].uv;
                return O;
            }
    
            [domain("quad")]
            d2g dom(tessFactors HSConstantData, const OutputPatch<h2d, 4> I, float2 bary : SV_DomainLocation )
            {
                d2g o = (d2g)0;
                o.position.z = 0;
                o.position.xy = bary * 2 - 1;
                o.bary = bary;
                return o;
            }
        #else
            [domain("tri")]
            [partitioning("integer")]
            [outputtopology("triangle_cw")]
            [patchconstantfunc("hullConstant")]
            [outputcontrolpoints(3)]
            h2d hull(InputPatch<v2h, PATCH_SIZE> I, uint uCPID : SV_OutputControlPointID )
            {
                h2d O = (h2d)0;
                O.position = I[uCPID].position.xyz;
                O.normal = I[uCPID].normal;
                O.uv = I[uCPID].uv;
                return O;
            }
    
            [domain("tri")]
            d2g dom(tessFactors HSConstantData, const OutputPatch<h2d, 3> I, float3 bary : SV_DomainLocation )
            {
                d2g o = (d2g)0;
                o.position.z = 0;
                o.position.xy = bary.xy * 2 - 1;
                o.bary = bary;
                return o;
            }
        #endif

		float max3(float3 f)
		{
			return max(max(f.x, f.y), f.z);
		}

        float min3(float3 f)
        {
            return min(min(f.x, f.y), f.z);
        }

		float flength(float f)
		{
			return length(float2(ddx(f), ddy(f)));
		}

		float3 flength(float3 f)
		{
			return float3(flength(f.x), flength(f.y), flength(f.z));
		}
 
		float4 frag(g2f f) : SV_Target
		{
			return lerp(float4(1, 1, 1, 1), f.color, clamp(min3(f.bary / flength(f.bary)), 0, 1));
		}
 
		ENDCG

		Cull Off
		
		Pass
		{
			CGPROGRAM
			#pragma target 5.0
 
			#pragma vertex vert
			#pragma hull hull
			#pragma domain dom
			#pragma geometry geom
			#pragma fragment frag
 
			[maxvertexcount(3)]
			void geom(triangle d2g IN[3], inout TriangleStream<g2f> tristream)
			{
				g2f o = (g2f)0;

				float2 uvCenter = (IN[0].bary + IN[1].bary + IN[2].bary) / 3.0;
                float3 center = (IN[0].position + IN[1].position + IN[2].position) / 3.0;
                float3 offset = 0;

                #if defined(_USEQUADTESS_ON)
                    float2 uvScaled = uvCenter * float2(_TessCenterA, _TessCenterB);

                    float totalCount = _TessCenterA * _TessCenterB * 2;
                    float id = floor(uvScaled.y) * 2 * _TessCenterA + floor(uvScaled.x) * 2 + (frac(uvScaled.x) > 0.5);
                #else
                    float w0 = 1 - uvCenter.x - uvCenter.y;
                    float w1 = uvCenter.x;
                    float w2 = uvCenter.y;
                    float subSection = (w0 <= w1 && w0 <= w2) ? 2 : (w1 <= w2 ? 1 : 0);
                    float n = floor(_TessCenterA / 2);
                    float odd = _TessCenterA - 2 * n;
                    float totalCount = 6 * n * n + odd * (6 * n + 1);
                    float id = subSection * floor(totalCount / 3);
                    float2 uvScaled = uvCenter;

                    // map subsections 1 & 2 to the canonical triangle (0,0),(1,0),(1/3,1/3)
                    if (subSection == 1)
                    {
                        // mirror across x=y: (0,0),(0,1),(1/3,1/3) -> (0,0),(1,0),(1/3,1/3)
                        uvScaled = float2(uvScaled.y, uvScaled.x);
                    }
                    else if (subSection == 2)
                    {
                        // rotate: (1,0),(0,1),(1/3,1/3) -> (0,0),(1,0),(1/3,1/3)
                        uvScaled = float2(uvScaled.y, 1 - uvScaled.x - uvScaled.y);
                    }

                    // map tri (0,0),(1,0),(1/3,1/3) -> (0,1),(1,1),(0.5,0)
                    uvScaled = (_TessCenterA / 2) * float2(uvScaled.x + uvScaled.y * 0.5, 1 - 3 * uvScaled.y);
                    // Odd factors leave an inner triangle and shift the subsection rows by half a step.
                    float row = floor(uvScaled.y - 0.5 * odd);
                    float triBeforeRow = 2 * row * (row + odd);
                    float stepSize = 0.25;
                    float idInRow = floor(uvScaled.x / stepSize) - 1 - 2 * (n - row - 1);
                    id += triBeforeRow + idInRow;
                    if (odd > 0 && uvScaled.y < 0.5)
                    {
                        id = totalCount - 1;
                    }
                #endif

                float animState = _LerpToLinear;

                if (_AnimationTime > 0)
                {
                    float timeline = frac(_Time.y / _AnimationTime);
                    animState = smoothstep(0.1, 0.4, timeline) * (1 - smoothstep(0.6, 0.9, timeline));
                }

                float tileWidth = sqrt(2) / sqrt(totalCount);
                offset += lerp(center, float3((id - (totalCount - 1) / 2) * tileWidth, 0, 0), animState);
                offset += center * (animState * (1 - animState)) * 4;

				o.color = float4(0,GammaToLinearSpaceExact(id / max(totalCount - 1, 1)),0,1);
                o.color = float4(
                    GammaToLinearSpaceExact(uvCenter.x),
                    GammaToLinearSpaceExact(uvCenter.y),
                    0,1);
				o.position = UnityObjectToClipPos(IN[0].position - center + offset);
				o.bary = float3(1, 0, 0);
				tristream.Append(o);
				o.position = UnityObjectToClipPos(IN[1].position - center + offset);
				o.bary = float3(0, 1, 0);
				tristream.Append(o);
				o.position = UnityObjectToClipPos(IN[2].position - center + offset);
				o.bary = float3(0, 0, 1);
				tristream.Append(o);
			}
 
			ENDCG
		}
	}
}
