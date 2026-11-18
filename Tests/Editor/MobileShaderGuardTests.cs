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

        [Test]
        public void McpBridge_AutoFixShaderSource_UnifiesCBuffersFixesInLoopTexAndMultiCompile()
        {
            const string brokenShader = @"
Shader ""Custom/BrokenMobileShader""
{
    SubShader
    {
        Pass
        {
            Name ""ForwardLit""
            HLSLPROGRAM
            #pragma multi_compile _ _CUSTOM_RIM _CUSTOM_GLOW _CUSTOM_SPARKLE
            #pragma multi_compile _ _EFFECT_A _EFFECT_B _EFFECT_C _EFFECT_D
            #pragma multi_compile _ _MODE_1 _MODE_2 _MODE_3 _MODE_4 _MODE_5 _MODE_6
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Smoothness;
            CBUFFER_END

            half4 frag(half2 uv : TEXCOORD0) : SV_Target
            {
                half4 acc = 0;
                for (int i = 0; i < 4; i++)
                {
                    acc += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                }
                return acc * _BaseColor;
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
            half4 frag() : SV_Target { return _BaseColor; }
            ENDHLSL
        }
    }
}";

            ShaderAutoFixReport fixReport = MobileShaderGuardMcpBridge.AutoFixShaderSource(
                brokenShader,
                "Assets/BrokenMobileShader.shader");

            Assert.IsTrue(fixReport.modified);
            Assert.AreEqual(3, fixReport.appliedFixes.Count);
            Assert.IsTrue(fixReport.beforeAudit.hasMismatchedSrpBatcherCBuffer);
            Assert.IsFalse(fixReport.afterAudit.hasMismatchedSrpBatcherCBuffer);
            Assert.AreEqual(1, fixReport.beforeAudit.textureSampleInLoopCount);
            Assert.AreEqual(0, fixReport.afterAudit.textureSampleInLoopCount);
            Assert.AreEqual(0, fixReport.afterAudit.multiCompileDirectiveCount);
            Assert.AreEqual(3, fixReport.afterAudit.shaderFeatureDirectiveCount);
            Assert.Greater(fixReport.scoreAfter, fixReport.scoreBefore);
            Assert.AreEqual(MobileShaderHazardSeverity.Good, fixReport.severityAfter);
            StringAssert.Contains("SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0)", fixReport.patchedSource);
        }

        [Test]
        public void McpBridge_HandlesJsonRpcInitializeToolsListToolsCallAndErrors()
        {
            // 1. initialize
            string initRes = MobileShaderGuardMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize""}");
            StringAssert.Contains(@"""protocolVersion"":""2024-11-05""", initRes);
            StringAssert.Contains(@"""name"":""unity-mobile-shader-guard""", initRes);
            StringAssert.Contains(@"""id"":1", initRes);

            // 2. tools/list
            string listRes = MobileShaderGuardMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":""req-2"",""method"":""tools/list""}");
            StringAssert.Contains("shader_guard.audit_shader_source", listRes);
            StringAssert.Contains("shader_guard.scan_directory", listRes);
            StringAssert.Contains("shader_guard.autofix_shader_source", listRes);

            // 3. tools/call -> shader_guard.audit_shader_source
            string callAudit = MobileShaderGuardMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":3,""method"":""tools/call"",""params"":{""name"":""shader_guard.audit_shader_source"",""arguments"":{""source"":""Shader \""Custom/McpTest\"" { SubShader { Pass { HLSLPROGRAM CBUFFER_START(UnityPerMaterial) half4 _Col; CBUFFER_END half4 frag() : SV_Target { clip(-1); return _Col; } ENDHLSL } } }""}}}");
            StringAssert.Contains(@"""shaderName"":""Custom/McpTest""", callAudit);
            StringAssert.Contains(@"""alphaTestDiscardCount"":1", callAudit);
            StringAssert.Contains(@"""isError"":false", callAudit);

            // 4. tools/call -> shader_guard.autofix_shader_source
            string callFix = MobileShaderGuardMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""shader_guard.autofix_shader_source"",""arguments"":{""source"":""Shader \""Custom/FixMe\"" { SubShader { Pass { HLSLPROGRAM CBUFFER_START(UnityPerMaterial) half4 _Col; CBUFFER_END half4 frag(half2 uv : TEXCOORD0) : SV_Target { half4 c = 0; for(int i=0;i<2;i++) { c += SAMPLE_TEXTURE2D(_T, sampler_T, uv); } return c; } ENDHLSL } } }""}}}");
            StringAssert.Contains(@"""modified"":true", callFix);
            StringAssert.Contains("SAMPLE_TEXTURE2D_LOD", callFix);

            // 5. Error handling: empty, unknown method, unknown tool
            string errEmpty = MobileShaderGuardMcpBridge.HandleJsonRpcRequest("");
            StringAssert.Contains("-32700", errEmpty);

            string errUnknownMethod = MobileShaderGuardMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":99,""method"":""unknown/method""}");
            StringAssert.Contains("-32601", errUnknownMethod);

            string errUnknownTool = MobileShaderGuardMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":100,""method"":""tools/call"",""params"":{""name"":""nonexistent.tool""}}");
            StringAssert.Contains("-32602", errUnknownTool);
        }

        [Test]
        public void UiToolkitWindow_BuildsHeatmapAndFiltersResultsBySearchAndSeverity()
        {
            var rGood = MobileHlslStaticAnalyzer.AnalyzeShaderSource(
                @"Shader ""Custom/GoodMobile"" { SubShader { Pass { HLSLPROGRAM CBUFFER_START(UnityPerMaterial) half4 _C; CBUFFER_END half4 frag() : SV_Target { return _C; } ENDHLSL } } }",
                "Assets/GoodMobile.shader");

            var rCritical = MobileHlslStaticAnalyzer.AnalyzeShaderSource(
                @"Shader ""Custom/CriticalDepth"" { SubShader { Pass { HLSLPROGRAM half4 frag(out float d : SV_Depth) : SV_Target { d = 0.5; return 0; } ENDHLSL } } }",
                "Assets/CriticalDepth.shader");

            var win = new MobileShaderGuardWindow();
            win.CreateGUI();
            Assert.Greater(win.rootVisualElement.childCount, 0);

            win.SetAuditResults(new[] { rGood, rCritical });
            Assert.AreEqual(2, win.GetFilteredResults().Count);

            ShaderGuardHeatmapSummary heatmap = MobileShaderGuardMcpBridge.ComputeHeatmapSummary(win.Results);
            Assert.AreEqual(2, heatmap.totalShaders);
            Assert.AreEqual(1, heatmap.criticalCount);
            Assert.AreEqual(1, heatmap.goodCount);
            Assert.AreEqual(0.5f, heatmap.criticalRatio, 0.001f);
            Assert.AreEqual(0.5f, heatmap.goodRatio, 0.001f);

            // Filter by minimum severity
            win.SetMinimumSeverityFilter(MobileShaderHazardSeverity.Critical);
            Assert.AreEqual(1, win.GetFilteredResults().Count);
            Assert.AreEqual("Custom/CriticalDepth", win.GetFilteredResults()[0].shaderName);

            // Reset severity and filter by search query
            win.SetMinimumSeverityFilter(MobileShaderHazardSeverity.Good);
            win.SetSearchFilter("GoodMobile");
            Assert.AreEqual(1, win.GetFilteredResults().Count);
            Assert.AreEqual("Custom/GoodMobile", win.GetFilteredResults()[0].shaderName);
        }

        [Test]
        public void McpBridge_AutoFix_HandlesNestedUvParensCrlfMultiDeclarationsAndJsonKeyOrdering()
        {
            // Build shader with explicit CRLF (\r\n) line endings, multi-variable CBUFFER declaration,
            // commented-out CBUFFER fields, nested UV parentheses in SAMPLE_TEXTURE2D, and do-while tex2D
            string complexShader =
                "Shader \"Custom/ComplexAutoFix\"\r\n" +
                "{\r\n" +
                "    SubShader\r\n" +
                "    {\r\n" +
                "        Pass\r\n" +
                "        {\r\n" +
                "            HLSLPROGRAM\r\n" +
                "            #pragma multi_compile _ _CUSTOM_A _CUSTOM_B _CUSTOM_C\r\n" +
                "            #pragma multi_compile_local_fragment _ _FEAT_X _FEAT_Y _FEAT_Z\r\n" +
                "            CBUFFER_START(UnityPerMaterial)\r\n" +
                "                /*\r\n" +
                "                half4 _CommentedOutDeadProp;\r\n" +
                "                */\r\n" +
                "                half4 _BaseColor, _SpecColor;\r\n" +
                "            CBUFFER_END\r\n" +
                "            half4 frag(half2 uv : TEXCOORD0) : SV_Target\r\n" +
                "            {\r\n" +
                "                half4 acc = 0;\r\n" +
                "                for (int i = 0; i < 3; i++)\r\n" +
                "                    acc += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + half2(i * 0.01, 0));\r\n" +
                "                int k = 0;\r\n" +
                "                do { acc += tex2D(_SecondTex, uv + half2(0, k)); k++; } while (k < 2);\r\n" +
                "                return acc * _BaseColor * _SpecColor;\r\n" +
                "            }\r\n" +
                "            ENDHLSL\r\n" +
                "        }\r\n" +
                "        Pass\r\n" +
                "        {\r\n" +
                "            HLSLPROGRAM\r\n" +
                "            CBUFFER_START(UnityPerMaterial)\r\n" +
                "                half4 _BaseColor;\r\n" +
                "            CBUFFER_END\r\n" +
                "            half4 frag() : SV_Target { return _BaseColor; }\r\n" +
                "            ENDHLSL\r\n" +
                "        }\r\n" +
                "    }\r\n" +
                "}\r\n";

            ShaderAutoFixReport report = MobileShaderGuardMcpBridge.AutoFixShaderSource(complexShader, "Assets/ComplexAutoFix.shader");

            Assert.IsTrue(report.modified);
            // Both _BaseColor and _SpecColor must be preserved, and _CommentedOutDeadProp must NOT be resurrected
            StringAssert.Contains("half4 _BaseColor;", report.patchedSource);
            StringAssert.Contains("half4 _SpecColor;", report.patchedSource);
            StringAssert.DoesNotContain("_CommentedOutDeadProp", report.AfterCBufferSectionOrSource());
            Assert.IsFalse(report.afterAudit.hasMismatchedSrpBatcherCBuffer);

            // Nested parentheses in UV must remain intact with ', 0)' appended to SAMPLE_TEXTURE2D_LOD
            StringAssert.Contains("SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + half2(i * 0.01, 0), 0)", report.patchedSource);
            StringAssert.Contains("tex2Dlod(_SecondTex, float4(uv + half2(0, k), 0, 0))", report.patchedSource);
            Assert.AreEqual(0, report.afterAudit.textureSampleInLoopCount);

            // CRLF #pragma multi_compile and #pragma multi_compile_local_fragment must be converted
            Assert.AreEqual(0, report.afterAudit.multiCompileDirectiveCount);
            Assert.AreEqual(2, report.afterAudit.shaderFeatureDirectiveCount);
            StringAssert.Contains("#pragma shader_feature_local _ _CUSTOM_A _CUSTOM_B _CUSTOM_C", report.patchedSource);
            StringAssert.Contains("#pragma shader_feature_local_fragment _ _FEAT_X _FEAT_Y _FEAT_Z", report.patchedSource);

            // JSON-RPC 2.0 with alphabetical key ordering ("arguments" before "name", "id" at end) and decoy keys inside source
            string decoyJsonRpc =
                "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"arguments\":{\"source\":\"Shader \\\"Custom/Decoy\\\" { /* \\\"id\\\": 999, \\\"name\\\": \\\"evil.tool\\\", \\\"method\\\": \\\"hack\\\" */ SubShader { Pass { HLSLPROGRAM CBUFFER_START(UnityPerMaterial) half4 _C; CBUFFER_END half4 frag() : SV_Target { return _C; } ENDHLSL } } }\",\"assetPath\":\"Assets/Decoy.shader\"},\"name\":\"shader_guard.audit_shader_source\"},\"id\":42}";
            string rpcResponse = MobileShaderGuardMcpBridge.HandleJsonRpcRequest(decoyJsonRpc);
            StringAssert.Contains("\"id\":42", rpcResponse);
            StringAssert.Contains("\"shaderName\":\"Custom/Decoy\"", rpcResponse);
            StringAssert.Contains("\"isError\":false", rpcResponse);

            // Verify parameterless CLI overload exists for Unity -executeMethod
            Assert.IsNotNull(typeof(MobileShaderGuardCli).GetMethod("RunMcpAuditJson", System.Type.EmptyTypes));
        }
    }

    internal static class TestReportExtensions
    {
        public static string AfterCBufferSectionOrSource(this ShaderAutoFixReport report)
        {
            int idx = report.patchedSource.LastIndexOf("CBUFFER_START", System.StringComparison.Ordinal);
            return idx >= 0 ? report.patchedSource.Substring(idx) : report.patchedSource;
        }
    }
}
