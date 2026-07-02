Shader "d4rkpl4y3r/BakeMeshToTexture/Debug/Display Bones"
{
	Properties
	{
		_LineLength("Line Length", Float) = 4
	}
	SubShader
	{
		Tags
		{
			"RenderType"="Transparent"
			"Queue"="Transparent"
		}

		Pass
		{
			ZTest Always

			CGPROGRAM
			#pragma vertex vert
			#pragma geometry geom
			#pragma fragment frag

			#include "UnityCG.cginc"

			struct g2f
			{
				float4 pos : SV_POSITION;
				float4 col : COLOR;
			};
			
			appdata_full vert (appdata_full v)
			{
				return v;
			}

			float _LineLength;

			[maxvertexcount(6)]
			void geom(point appdata_full IN[1], inout LineStream<g2f> stream)
			{
				float3 position = IN[0].vertex.xyz;
				float3 normal = IN[0].normal;
				float4 tangent = float4(IN[0].tangent.xyz, IN[0].tangent.w);
				float3 biTangent = cross(normal, tangent.xyz) * tangent.w / length(normal);
				g2f o;
				o.col = float4(1,0,0,1);
				o.pos = UnityObjectToClipPos(position);
				stream.Append(o);
				o.pos = UnityObjectToClipPos(position + normal * (_LineLength * .01));
				stream.Append(o);
				stream.RestartStrip();
				o.col = float4(0,1,0,1);
				o.pos = UnityObjectToClipPos(position);
				stream.Append(o);
				o.pos = UnityObjectToClipPos(position + tangent.xyz * (_LineLength * .01));
				stream.Append(o);
				stream.RestartStrip();
				o.col = float4(0,0,1,1);
				o.pos = UnityObjectToClipPos(position);
				stream.Append(o);
				o.pos = UnityObjectToClipPos(position + biTangent * (_LineLength * .01));
				stream.Append(o);
			}

			half4 frag (g2f i) : SV_Target
			{
				return i.col;
			}

			ENDCG
		}
	}
}
