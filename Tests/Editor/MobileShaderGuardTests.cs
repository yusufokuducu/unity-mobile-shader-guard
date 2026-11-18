using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
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
            Assert.AreEqual(4, audit.multiCompileDirectiveCount);
            Assert.AreEqual(3 * 3 * 4 * 4, audit.estimatedKeywordBurstRiskScore); // 144x including multi_compile_fog
            Assert.IsFalse(audit.hasSrpBatcherCBuffer);
            Assert.AreEqual(MobileShaderHazardSeverity.Critical, audit.severity);
            Assert.Less(audit.mobileReadinessScore, 50);
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
            Assert.IsFalse(audit.hasMismatchedSrpBatcherCBuffer);
            Assert.AreEqual(0, audit.alphaTestDiscardCount);
            Assert.Greater(audit.fp16Ratio, 0.9f);
            Assert.AreEqual(MobileShaderHazardSeverity.Good, audit.severity);
            Assert.AreEqual(100, audit.mobileReadinessScore);
        }

        [Test]
        public void StaticAnalyzer_DetectsSrpBatcherCBufferMismatchAcrossPassesAndSvDepth()
        {
            const string mismatchedShader = @"
Shader ""Custom/MismatchedPasses""
{
    SubShader
    {
        Pass
        {
            Name ""ForwardLit""
            HLSLPROGRAM
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Smoothness;
            CBUFFER_END
            half4 frag(out float outDepth : SV_Depth) : SV_Target
            {
                outDepth = 0.5;
                return _BaseColor;
            }
            ENDHLSL
        }
        Pass
        {
            Name ""ShadowCaster""
            HLSLPROGRAM
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}";

            ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(mismatchedShader, "Assets/MismatchedPasses.shader");

            Assert.IsTrue(audit.hasSrpBatcherCBuffer);
            Assert.AreEqual(2, audit.cbufferPassCount);
            Assert.IsTrue(audit.hasMismatchedSrpBatcherCBuffer);
            Assert.IsTrue(audit.hasSvDepthOutput);
            Assert.AreEqual(MobileShaderHazardSeverity.Critical, audit.severity);
        }

        [Test]
        public void StaticAnalyzer_DetectsInLoopTextureFetchWithoutExplicitLod()
        {
            const string loopShader = @"
Shader ""Custom/BlurLoop""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END
            half4 frag(half2 uv : TEXCOORD0) : SV_Target
            {
                half4 acc = 0;
                for (int i = 0; i < 4; i++)
                {
                    acc += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                    acc += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0);
                }
                return acc * _Tint;
            }
            ENDHLSL
        }
    }
}";

            ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(loopShader, "Assets/BlurLoop.shader");

            Assert.AreEqual(1, audit.dynamicLoopCount);
            Assert.AreEqual(2, audit.textureSampleCount);
            Assert.AreEqual(1, audit.textureSampleInLoopCount); // Only non-LOD fetch flagged
            Assert.AreEqual(MobileShaderHazardSeverity.Critical, audit.severity);
        }

        [Test]
        public void StaticAnalyzer_IgnoresCommentsStringLiteralsAndHandlesEmptyInput()
        {
            ShaderSourceAuditResult emptyAudit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(null, null);
            Assert.AreEqual(MobileShaderHazardSeverity.Good, emptyAudit.severity);
            Assert.AreEqual("<Memory>", emptyAudit.shaderName);

            const string commentedOutHazards = @"
Shader ""Custom/CommentSafe""
{
    Properties
    {
        _Prop (""float clip(x) discard ddx(x)"", Float) = 1.0
    }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            // float4 a; clip(x); discard; #pragma multi_compile A B C D E
            /* float4 b; float4 c; float4 d; float4 e; float4 f; float4 g; float4 h; float4 i; float4 j; */
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END
            half4 frag() : SV_Target { return _Color; }
            ENDHLSL
        }
    }
}";

            ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(commentedOutHazards, "Assets/CommentSafe.shader");
            Assert.AreEqual(0, audit.fp32DeclarationsCount);
            Assert.AreEqual(0, audit.alphaTestDiscardCount);
            Assert.AreEqual(0, audit.multiCompileDirectiveCount);
            Assert.AreEqual(MobileShaderHazardSeverity.Good, audit.severity);
        }

        [Test]
        public void StripProfile_EvaluatesSingleKeywordsPairsPassesAndWhitelistCorrectly()
        {
            var profile = ScriptableObject.CreateInstance<MobileShaderStripProfile>();
            try
            {
                HashSet<string> forbidden = profile.BuildEffectiveForbiddenKeywordSet();

                // Verify leading space bug on EDITOR_VISUALIZATION is fixed
                Assert.IsTrue(forbidden.Contains("EDITOR_VISUALIZATION"));
                Assert.IsFalse(forbidden.Contains(" EDITOR_VISUALIZATION"));

                // 1. Whitelisted shader is never stripped even with forbidden keywords
                bool stripWhitelisted = profile.ShouldStripVariant(
                    "Sprites/Default",
                    "ForwardLit",
                    new[] { "DEBUG_DISPLAY", "_SHADOWS_SOFT" },
                    forbidden,
                    out _);
                Assert.IsFalse(stripWhitelisted);

                // 2. Forbidden pass (GBuffer) is stripped
                bool stripPass = profile.ShouldStripVariant(
                    "Universal Render Pipeline/Lit",
                    "GBuffer",
                    new string[0],
                    forbidden,
                    out string passReason);
                Assert.IsTrue(stripPass);
                Assert.AreEqual("ForbiddenPass:GBuffer", passReason);

                // 3. Co-occurring pair (_MAIN_LIGHT_SHADOWS_CASCADE + _ADDITIONAL_LIGHT_SHADOWS)
                profile.stripAdditionalLightShadows = false;
                profile.stripMainLightCascadeShadows = false;
                forbidden = profile.BuildEffectiveForbiddenKeywordSet();

                bool stripCascadeAlone = profile.ShouldStripVariant(
                    "Universal Render Pipeline/Lit",
                    "ForwardLit",
                    new[] { "_MAIN_LIGHT_SHADOWS_CASCADE" },
                    forbidden,
                    out _);
                Assert.IsFalse(stripCascadeAlone);

                bool stripBothTogether = profile.ShouldStripVariant(
                    "Universal Render Pipeline/Lit",
                    "ForwardLit",
                    new[] { "_MAIN_LIGHT_SHADOWS_CASCADE", "_ADDITIONAL_LIGHT_SHADOWS" },
                    forbidden,
                    out string pairReason);
                Assert.IsTrue(stripBothTogether);
                Assert.AreEqual("ForbiddenPair:_MAIN_LIGHT_SHADOWS_CASCADE+_ADDITIONAL_LIGHT_SHADOWS", pairReason);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ReportExporters_GenerateValidMarkdownAndCsv()
        {
            var r = MobileHlslStaticAnalyzer.AnalyzeShaderSource(
                @"Shader ""Custom/Test"" { SubShader { Pass { HLSLPROGRAM half4 frag() : SV_Target { return 0; } ENDHLSL } } }",
                "Assets/Test.shader");

            string csv = MobileHlslStaticAnalyzer.ExportAuditResultsToCsv(new[] { r });
            string md = MobileHlslStaticAnalyzer.ExportAuditResultsToMarkdown(new[] { r });

            StringAssert.Contains("Custom/Test", csv);
            StringAssert.Contains("Custom/Test", md);
            StringAssert.Contains("Mobile Shader Guard", md);
        }

        [Test]
        public void StaticAnalyzer_HandlesUrlSlashesInsideStringsAndPreprocessorIfZeroBlocks()
        {
            const string shaderWithUrlAndIfZero = @"
Shader ""Custom/UrlAndIfZeroShader""
{
    Properties
    {
        _DocUrl (""https://docs.unity3d.com/Manual/* Precision */"", Float) = 1.0
    }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Smoothness;
            CBUFFER_END

            #if 0
                float4 _Dead1, _Dead2, _Dead3, _Dead4, _Dead5, _Dead6, _Dead7, _Dead8, _Dead9, _Dead10;
                void DeadFunc(out float d : SV_Depth) { clip(-1); d = 0.5; }
            #else
                half4 frag(half2 uv : TEXCOORD0) : SV_Target
                {
                    return _BaseColor * _Smoothness;
                }
            #endif
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            CBUFFER_START(UnityPerMaterial)
                half4   _BaseColor ;
                half    _Smoothness ;
            CBUFFER_END
            half4 frag() : SV_Target { return _BaseColor; }
            ENDHLSL
        }
    }
    Fallback ""Hidden/Universal Render Pipeline/FallbackError""
}";

            ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(shaderWithUrlAndIfZero, "Assets/UrlAndIfZeroShader.shader");

            Assert.AreEqual("Custom/UrlAndIfZeroShader", audit.shaderName);
            Assert.AreEqual(0, audit.fp32DeclarationsCount);
            Assert.GreaterOrEqual(audit.fp16DeclarationsCount, 4);
            Assert.AreEqual(0, audit.alphaTestDiscardCount);
            Assert.IsFalse(audit.hasSvDepthOutput);
            Assert.IsTrue(audit.hasSrpBatcherCBuffer);
            Assert.IsFalse(audit.hasMismatchedSrpBatcherCBuffer);
            Assert.AreEqual(MobileShaderHazardSeverity.Good, audit.severity);
        }

        [Test]
        public void StaticAnalyzer_DetectsDoWhileLoopsAndDoesNotDoubleCountNestedLoops()
        {
            const string nestedAndDoWhileShader = @"
Shader ""Custom/NestedAndDoWhile""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END
            half4 frag(half2 uv : TEXCOORD0) : SV_Target
            {
                half4 acc = 0;
                for (int x = 0; x < 2; x++)
                {
                    for (int y = 0; y < 2; y++)
                    {
                        acc += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                    }
                }

                int k = 0;
                do
                {
                    acc += tex2D(_SecondTex, uv);
                    k++;
                } while (k < 2);

                return acc * _Tint;
            }
            ENDHLSL
        }
    }
}";

            ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(nestedAndDoWhileShader, "Assets/NestedAndDoWhile.shader");

            // 2 nested for loops + 1 do-while loop = 3 dynamic loops
            Assert.AreEqual(3, audit.dynamicLoopCount);
            // Total 2 texture sample calls in source (1 in nested for, 1 in do-while)
            Assert.AreEqual(2, audit.textureSampleCount);
            Assert.AreEqual(2, audit.textureSampleInLoopCount);
        }
    }
}
