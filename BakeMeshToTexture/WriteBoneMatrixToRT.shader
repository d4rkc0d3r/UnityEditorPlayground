Shader "d4rkpl4y3r/BakeMeshToTexture/WriteBoneMatrixToRT"
{
	Properties
	{
		_BoneCount("Bone Count", Float) = 0
	}
	SubShader
	{
		Tags
		{
			"RenderType" = "Transparent"
			"Queue" = "Transparent+1000"
		}

		Pass
		{
			ZTest Always
			ZWrite On
			Cull Off

			CGPROGRAM
			#pragma vertex vert
			#pragma geometry geom
			#pragma fragment frag
			#pragma target 5.0

			#include "UnityCG.cginc"

			uniform float _BoneCount;

			float4 PixelToClipPos(float2 pixelPos)
			{
				float4 pos = float4((pixelPos + .5) / _ScreenParams.xy, 1, 1);
				pos.xy = pos.xy * 2 - 1;
				#if UNITY_UV_STARTS_AT_TOP
				pos.y = -pos.y;
				#endif
				return pos;
			}

			// Each bone outputs 4 points (one per matrix row)
			struct g2f
			{
				float4 pos : SV_POSITION;
				float4 matrixRow : COLOR0;   // one row of the bone matrix
			};

			// Vertex shader: pass through; skinning is handled by SkinnedMeshRenderer
			appdata_full vert(appdata_full v)
			{
				return v;
			}

			[maxvertexcount(3)]
			void geom(point appdata_full IN[1], uint boneID : SV_PrimitiveID, inout PointStream<g2f> stream)
			{
				if (_ScreenParams.x != _BoneCount || _ScreenParams.y != 3)
					return;

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

				g2f o;
				o.matrixRow = float4(0, 0, 0, 0);

				for (int row = 0; row < 3; row++)
				{
					o.matrixRow = boneToWorld[row];
					o.pos = PixelToClipPos(float2(boneID, row));
					stream.Append(o);
				}
			}

			float4 frag(g2f i) : SV_Target
			{
				return i.matrixRow;
			}

			ENDCG
		}
	}
}
