// 冷氣噴流 / 白霧粒子（Roadster 推進器）。HDRP/Unlit 不吃粒子的頂點顏色，Color over Lifetime 淡不掉，
// 所以自己寫一個最小的 HDRP 透明 Unlit：頂點顏色 × 貼圖 × _Color，加上軟粒子（貼近地面 / 車身時邊緣變淡）。
// 亮度跟 HDRP/Unlit 一樣不乘曝光（DUST 固定 EV 13.8，數值 ~1 在白天看起來就是白霧；米格-25 的凝結雲也是這樣校正的）。
Shader "DUST/VaporParticle"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Brightness ("Brightness", Float) = 1
        _SoftDistance ("Soft fade distance (m)", Float) = 0.25
    }
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float _Brightness;
                float _SoftDistance;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float eyeDepth : TEXCOORD1;
            };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                float3 posWS = TransformObjectToWorld(i.positionOS);   // HDRP：相機相對座標
                o.positionCS = TransformWorldToHClip(posWS);
                o.eyeDepth = -TransformWorldToView(posWS).z;
                o.color = i.color;
                o.uv = i.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                float4 c = tex * i.color * _Color;
                // 軟粒子：跟後面的場景深度差越小越透明（白霧貼著地面散開時不會有一條硬線）
                float sceneDepth = LinearEyeDepth(LoadCameraDepth(uint2(i.positionCS.xy)), _ZBufferParams);
                c.a *= saturate((sceneDepth - i.eyeDepth) / max(_SoftDistance, 1e-3));
                c.rgb *= _Brightness;
                return c;
            }
            ENDHLSL
        }
    }
}
