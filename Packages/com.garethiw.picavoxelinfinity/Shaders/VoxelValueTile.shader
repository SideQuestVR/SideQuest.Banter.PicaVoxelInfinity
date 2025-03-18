// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "PicaVoxel/VoxelValueTile"
{
	Properties
	{
		_TileSheet("TileSheet", 2D) = "white" {}
		_NumTiles("Num Tiles", Vector) = (0,0,0,0)
		_TilePadding("Tile Padding", Float) = 0
		_Tint("Tint", Color) = (1,1,1,0)
		[HideInInspector] _tex4coord( "", 2D ) = "white" {}
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}

	SubShader
	{
		Tags{ "RenderType" = "Opaque"  "Queue" = "Geometry+0" }
		Cull Back
		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf Standard keepalpha addshadow fullforwardshadows 
		#undef TRANSFORM_TEX
		#define TRANSFORM_TEX(tex,name) float4(tex.xy * name##_ST.xy + name##_ST.zw, tex.z, tex.w)
		struct Input
		{
			float4 uv_tex4coord;
			float2 uv_texcoord;
			float4 vertexColor : COLOR;
		};

		uniform sampler2D _TileSheet;
		uniform float2 _NumTiles;
		uniform float4 _TileSheet_TexelSize;
		uniform float _TilePadding;
		uniform float4 _Tint;

		void surf( Input i , inout SurfaceOutputStandard o )
		{
			float temp_output_14_0 = ( 1.0 / _NumTiles.x );
			float temp_output_24_0 = ( 1.0 / _NumTiles.y );
			float4 appendResult25 = (float4(temp_output_14_0 , temp_output_24_0 , 0.0 , 0.0));
			float4 appendResult32 = (float4(( _TileSheet_TexelSize.x * _TilePadding ) , ( _TileSheet_TexelSize.y * _TilePadding ) , 0.0 , 0.0));
			float4 appendResult6 = (float4(( temp_output_14_0 * i.uv_tex4coord.z ) , ( temp_output_24_0 * i.uv_tex4coord.w ) , 0.0 , 0.0));
			float2 uv_TexCoord13 = i.uv_texcoord * ( appendResult25 - ( appendResult32 * 2.0 ) ).xy + ( appendResult6 + appendResult32 ).xy;
			o.Albedo = ( tex2D( _TileSheet, uv_TexCoord13 ) * i.vertexColor * _Tint ).rgb;
			float temp_output_12_0 = 0.0;
			o.Metallic = temp_output_12_0;
			o.Smoothness = temp_output_12_0;
			o.Alpha = 1;
		}

		ENDCG
	}
	Fallback "Diffuse"
	CustomEditor "ASEMaterialInspector"
}
/*ASEBEGIN
Version=16700
-2235;137;1782;1085;1117.714;815.726;1.3;True;True
Node;AmplifyShaderEditor.TexturePropertyNode;3;-498,-581;Float;True;Property;_TileSheet;TileSheet;0;0;Create;True;0;0;False;0;None;13cd193caf89acc4d860228647c3fdd9;False;white;Auto;Texture2D;0;1;SAMPLER2D;0
Node;AmplifyShaderEditor.TexelSizeNode;31;-714.7141,263.274;Float;False;-1;1;0;SAMPLER2D;;False;5;FLOAT4;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.Vector2Node;23;-659.0005,-233.1012;Float;False;Property;_NumTiles;Num Tiles;1;0;Create;True;0;0;False;0;0,0;16,6;0;3;FLOAT2;0;FLOAT;1;FLOAT;2
Node;AmplifyShaderEditor.RangedFloatNode;15;-555.0002,154.8989;Float;False;Constant;_Float1;Float 1;2;0;Create;True;0;0;False;0;1;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;30;-664.0138,449.174;Float;False;Property;_TilePadding;Tile Padding;2;0;Create;True;0;0;False;0;0;1;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;33;-492.414,260.674;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;36;-475.5141,388.074;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleDivideOpNode;14;-283.0002,18.89893;Float;False;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleDivideOpNode;24;-286.0002,120.8989;Float;False;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.TexCoordVertexDataNode;2;-689.7998,-50.6;Float;False;0;4;0;5;FLOAT4;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;11;-276.0002,-128.1011;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.DynamicAppendNode;32;-341.614,291.8738;Float;False;FLOAT4;4;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;20;-44.00024,18.89893;Float;False;2;2;0;FLOAT;0;False;1;FLOAT;0;False;1;FLOAT;0
Node;AmplifyShaderEditor.RangedFloatNode;40;-439.1141,486.8741;Float;False;Constant;_Float2;Float 2;3;0;Create;True;0;0;False;0;2;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.DynamicAppendNode;25;-76.20019,-174.9011;Float;False;FLOAT4;4;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;39;-157.0142,411.4739;Float;False;2;2;0;FLOAT4;0,0,0,0;False;1;FLOAT;0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.DynamicAppendNode;6;-223.8,-342.0999;Float;False;FLOAT4;4;0;FLOAT;0;False;1;FLOAT;0;False;2;FLOAT;0;False;3;FLOAT;0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.SimpleAddOpNode;37;-64.71408,-333.4261;Float;False;2;2;0;FLOAT4;0,0,0,0;False;1;FLOAT4;0,0,0,0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.SimpleSubtractOpNode;38;82.18585,-156.6261;Float;False;2;0;FLOAT4;0,0,0,0;False;1;FLOAT4;0,0,0,0;False;1;FLOAT4;0
Node;AmplifyShaderEditor.TextureCoordinatesNode;13;107.3997,-338.7009;Float;False;0;-1;2;3;2;SAMPLER2D;;False;0;FLOAT2;1,1;False;1;FLOAT2;0,0;False;5;FLOAT2;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.VertexColorNode;21;155.9998,149.8989;Float;False;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.SamplerNode;10;-150.0002,-590.1011;Float;True;Property;_TextureSample0;Texture Sample 0;2;0;Create;True;0;0;False;0;None;None;True;0;False;white;Auto;False;Object;-1;Auto;Texture2D;6;0;SAMPLER2D;;False;1;FLOAT2;0,0;False;2;FLOAT;0;False;3;FLOAT2;0,0;False;4;FLOAT2;0,0;False;5;FLOAT;1;False;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.ColorNode;41;196.5859,-736.4258;Float;False;Property;_Tint;Tint;3;0;Create;True;0;0;False;0;1,1,1,0;0,0,0,0;True;0;5;COLOR;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.NormalVertexDataNode;5;-147,209;Float;False;0;5;FLOAT3;0;FLOAT;1;FLOAT;2;FLOAT;3;FLOAT;4
Node;AmplifyShaderEditor.RangedFloatNode;12;-507.0002,-344.1011;Float;False;Constant;_Float0;Float 0;4;0;Create;True;0;0;False;0;0;0;0;0;0;1;FLOAT;0
Node;AmplifyShaderEditor.SimpleMultiplyOpNode;22;373.9998,-502.1011;Float;False;3;3;0;COLOR;0,0,0,0;False;1;COLOR;0,0,0,0;False;2;COLOR;0,0,0,0;False;1;COLOR;0
Node;AmplifyShaderEditor.StandardSurfaceOutputNode;0;436,-242;Float;False;True;2;Float;ASEMaterialInspector;0;0;Standard;PicaVoxel/VoxelValueTile;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;False;Back;0;False;-1;0;False;-1;False;0;False;-1;0;False;-1;False;0;Opaque;0.5;True;True;0;False;Opaque;;Geometry;All;True;True;True;True;True;True;True;True;True;True;True;True;True;True;True;True;True;0;False;-1;False;0;False;-1;255;False;-1;255;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;-1;False;2;15;10;25;False;0.5;True;0;0;False;-1;0;False;-1;0;0;False;-1;0;False;-1;0;False;-1;0;False;-1;0;False;0;0,0,0,0;VertexOffset;True;False;Cylindrical;False;Relative;0;;-1;-1;-1;-1;0;False;0;0;False;-1;-1;0;False;-1;0;0;0;False;0.1;False;-1;0;False;-1;16;0;FLOAT3;0,0,0;False;1;FLOAT3;0,0,0;False;2;FLOAT3;0,0,0;False;3;FLOAT;0;False;4;FLOAT;0;False;5;FLOAT;0;False;6;FLOAT3;0,0,0;False;7;FLOAT3;0,0,0;False;8;FLOAT;0;False;9;FLOAT;0;False;10;FLOAT;0;False;13;FLOAT3;0,0,0;False;11;FLOAT3;0,0,0;False;12;FLOAT3;0,0,0;False;14;FLOAT4;0,0,0,0;False;15;FLOAT3;0,0,0;False;0
WireConnection;31;0;3;0
WireConnection;33;0;31;1
WireConnection;33;1;30;0
WireConnection;36;0;31;2
WireConnection;36;1;30;0
WireConnection;14;0;15;0
WireConnection;14;1;23;1
WireConnection;24;0;15;0
WireConnection;24;1;23;2
WireConnection;11;0;14;0
WireConnection;11;1;2;3
WireConnection;32;0;33;0
WireConnection;32;1;36;0
WireConnection;20;0;24;0
WireConnection;20;1;2;4
WireConnection;25;0;14;0
WireConnection;25;1;24;0
WireConnection;39;0;32;0
WireConnection;39;1;40;0
WireConnection;6;0;11;0
WireConnection;6;1;20;0
WireConnection;37;0;6;0
WireConnection;37;1;32;0
WireConnection;38;0;25;0
WireConnection;38;1;39;0
WireConnection;13;0;38;0
WireConnection;13;1;37;0
WireConnection;10;0;3;0
WireConnection;10;1;13;0
WireConnection;22;0;10;0
WireConnection;22;1;21;0
WireConnection;22;2;41;0
WireConnection;0;0;22;0
WireConnection;0;3;12;0
WireConnection;0;4;12;0
ASEEND*/
//CHKSM=8C8BE3E154EC43D6F52F218C880E1FC79B370934