using NUnit.Framework;
using YusufOkuducu.MobileShaderGuard.Editor;

namespace YusufOkuducu.MobileShaderGuard.Tests
{
    public sealed class MobileShaderGuardTests
    {
        [Test]
        public void StaticAnalyzer_DetectsFp32RegisterPressureAndEarlyZDiscard()
        {
            const string badMobileShader = @"
Shader ""Custom/HeavyFoliage""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            float4 _BaseColor;
            float _Cutoff;

            float4 frag(float2 uv : TEXCOORD0) : SV_Target
            {
                float4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                float3 normal = float3(0, 1, 0);
                float3 viewDir = float3(0, 0, 1);
                float ndotl = dot(normal, viewDir);
                float spec = pow(ndotl, 32.0);
                float3 finalRgb = col.rgb * _BaseColor.rgb + float3(spec, spec, spec);
                clip(col.a - _Cutoff);
                return float4(finalRgb, col.a);
            }
            ENDHLSL
        }
    }
}";

            ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(badMobileShader, "Assets/HeavyFoliage.shader");

            Assert.AreEqual("Custom/HeavyFoliage", audit.shaderName);
            Assert.GreaterOrEqual(audit.fp32DeclarationsCount, 10);
            Assert.AreEqual(0, audit.fp16DeclarationsCount);
            Assert.AreEqual(1, audit.alphaTestDiscardCount);
            Assert.IsFalse(audit.hasSrpBatcherCBuffer);
            Assert.AreEqual(MobileShaderHazardSeverity.Critical, audit.severity);
        }

        [Test]
        public void StaticAnalyzer_ApprovesCleanHalfPrecisionSrpBatcherShader()
        {
            const string cleanMobileShader = @"
Shader ""Custom/CleanMobileLit""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma shader_feature_local _NORMALMAP
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            half4 frag(half2 uv : TEXCOORD0) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                half3 lit = col.rgb * _BaseColor.rgb;
                return half4(lit, col.a);
            }
            ENDHLSL
        }
    }
}";

            ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(cleanMobileShader, "Assets/CleanMobileLit.shader");

            Assert.IsTrue(audit.hasSrpBatcherCBuffer);
            Assert.AreEqual(0, audit.alphaTestDiscardCount);
            Assert.Greater(audit.fp16Ratio, 0.9f);
            Assert.AreEqual(MobileShaderHazardSeverity.Good, audit.severity);
        }
    }
}
