using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace YusufOkuducu.MobileShaderGuard.Editor
{
    public enum MobileShaderHazardSeverity
    {
        Good = 0,
        Advisory = 1,
        Warning = 2,
        Critical = 3
    }

    [Serializable]
    public sealed class ShaderSourceAuditResult
    {
        public string assetPath;
        public string shaderName;
        public int fp32DeclarationsCount;
        public int fp16DeclarationsCount;
        public float fp16Ratio;
        public int alphaTestDiscardCount;
        public int textureSampleCount;
        public int dynamicLoopCount;
        public int multiCompileDirectiveCount;
        public int shaderFeatureDirectiveCount;
        public bool hasSrpBatcherCBuffer;
        public int estimatedKeywordBurstRiskScore;
        public MobileShaderHazardSeverity severity;
        public List<string> diagnostics = new List<string>();
    }

    /// <summary>
    /// Static analyzer for ShaderLab (.shader) and HLSL (.hlsl/.cginc) source files targeting mobile TBDR GPUs.
    /// Evaluates FP16 (half/real) vs FP32 (float) register pressure, Early-Z / HSR kill instructions (clip/discard),
    /// SRP Batcher CBUFFER_START(UnityPerMaterial) compliance, and multi_compile variant explosion risk.
    /// </summary>
    public static class MobileHlslStaticAnalyzer
    {
        private static readonly Regex ShaderNameRegex = new Regex(@"Shader\s+""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex Fp32Regex = new Regex(@"\bfloat(?:[234](?:x[234])?)?\b", RegexOptions.Compiled);
        private static readonly Regex Fp16Regex = new Regex(@"\b(?:half|real|fixed)(?:[234](?:x[234])?)?\b", RegexOptions.Compiled);
        private static readonly Regex DiscardClipRegex = new Regex(@"\b(?:clip\s*\(|discard\b)", RegexOptions.Compiled);
        private static readonly Regex TexSampleRegex = new Regex(@"\b(?:SAMPLE_TEXTURE2D|SAMPLE_TEXTURE2D_LOD|tex2D|tex2Dlod)\s*\(", RegexOptions.Compiled);
        private static readonly Regex MultiCompileRegex = new Regex(@"#pragma\s+multi_compile(?:_local|_fragment|_vertex)?\b([^\r\n]*)", RegexOptions.Compiled);
        private static readonly Regex ShaderFeatureRegex = new Regex(@"#pragma\s+shader_feature(?:_local|_fragment|_vertex)?\b", RegexOptions.Compiled);
        private static readonly Regex CBufferPerMaterialRegex = new Regex(@"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)", RegexOptions.Compiled);
        private static readonly Regex UnrolledOrDynamicLoopRegex = new Regex(@"\b(?:for|while)\s*\(", RegexOptions.Compiled);
        private static readonly Regex SingleLineCommentRegex = new Regex(@"//.*?$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex BlockCommentRegex = new Regex(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);

        public static ShaderSourceAuditResult AnalyzeShaderFile(string filePath)
        {
            string source = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
            return AnalyzeShaderSource(source, filePath);
        }

        public static ShaderSourceAuditResult AnalyzeShaderSource(string rawSource, string assetPath = "<Memory>")
        {
            var result = new ShaderSourceAuditResult
            {
                assetPath = assetPath,
                shaderName = Path.GetFileNameWithoutExtension(assetPath)
            };

            if (string.IsNullOrEmpty(rawSource))
                return result;

            Match nameMatch = ShaderNameRegex.Match(rawSource);
            if (nameMatch.Success)
                result.shaderName = nameMatch.Groups[1].Value;

            // Strip comments before counting tokens
            string code = BlockCommentRegex.Replace(rawSource, string.Empty);
            code = SingleLineCommentRegex.Replace(code, string.Empty);

            result.fp32DeclarationsCount = Fp32Regex.Matches(code).Count;
            result.fp16DeclarationsCount = Fp16Regex.Matches(code).Count;

            int totalTypes = result.fp32DeclarationsCount + result.fp16DeclarationsCount;
            result.fp16Ratio = totalTypes > 0 ? (float)result.fp16DeclarationsCount / totalTypes : 1f;

            result.alphaTestDiscardCount = DiscardClipRegex.Matches(code).Count;
            result.textureSampleCount = TexSampleRegex.Matches(code).Count;
            result.dynamicLoopCount = UnrolledOrDynamicLoopRegex.Matches(code).Count;

            MatchCollection multiCompiles = MultiCompileRegex.Matches(code);
            result.multiCompileDirectiveCount = multiCompiles.Count;
            result.shaderFeatureDirectiveCount = ShaderFeatureRegex.Matches(code).Count;
            result.hasSrpBatcherCBuffer = CBufferPerMaterialRegex.IsMatch(code);

            // Estimate variant multiplier risk from multi_compile lines
            int variantRisk = 1;
            foreach (Match m in multiCompiles)
            {
                string tail = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(tail))
                {
                    variantRisk *= 2;
                }
                else
                {
                    string[] tokens = tail.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int kwCount = Math.Max(2, tokens.Length);
                    variantRisk = Math.Min(65536, variantRisk * kwCount);
                }
            }
            result.estimatedKeywordBurstRiskScore = multiCompiles.Count > 0 ? variantRisk : 0;

            EvaluateDiagnostics(result);
            return result;
        }

        private static void EvaluateDiagnostics(ShaderSourceAuditResult r)
        {
            r.severity = MobileShaderHazardSeverity.Good;

            if (r.fp32DeclarationsCount > 8 && r.fp16Ratio < 0.35f)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Warning);
                r.diagnostics.Add(
                    $"LOW FP16 USAGE ({r.fp16Ratio * 100f:F0}% half/real vs {r.fp32DeclarationsCount} float tokens): " +
                    "Excessive 32-bit float registers halve ALU occupancy and warp count on ARM Mali and Adreno GPUs. Prefer half/real for colors, normals, and lighting.");
            }

            if (r.alphaTestDiscardCount > 0)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Warning);
                r.diagnostics.Add(
                    $"EARLY-Z / HSR HAZARD ({r.alphaTestDiscardCount} clip/discard call(s)): " +
                    "clip()/discard disables Hidden Surface Removal (HSR) and Early-Z on PowerVR, Apple, and Adreno TBDR GPUs, forcing late depth updates. Prefer opaque or alpha-blended LODs where possible.");
            }

            if (r.estimatedKeywordBurstRiskScore >= 64)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Critical);
                r.diagnostics.Add(
                    $"VARIANT EXPLOSION RISK ({r.multiCompileDirectiveCount} #pragma multi_compile directives, ~{r.estimatedKeywordBurstRiskScore}x permutation multiplier): " +
                    "Convert artist-toggled keywords to #pragma shader_feature_local or strip unused keywords via MobileShaderStripProfile.");
            }

            if (!r.hasSrpBatcherCBuffer && r.assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Advisory);
                r.diagnostics.Add(
                    "MISSING CBUFFER_START(UnityPerMaterial): Shader may break URP SRP Batcher compatibility unless all material properties are wrapped inside UnityPerMaterial.");
            }

            if (r.textureSampleCount > 8)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Warning);
                r.diagnostics.Add(
                    $"HIGH TEXTURE BANDWIDTH ({r.textureSampleCount} texture samples): Pack roughness/metallic/AO channels into a single RGBA mask texture to reduce texture unit stalls.");
            }

            if (r.diagnostics.Count == 0)
            {
                r.diagnostics.Add("Clean mobile shader profile: Good FP16 usage, no Early-Z hazards, and controlled keyword permutations.");
            }
        }

        private static MobileShaderHazardSeverity MaxSeverity(MobileShaderHazardSeverity a, MobileShaderHazardSeverity b)
        {
            return a > b ? a : b;
        }
    }
}
