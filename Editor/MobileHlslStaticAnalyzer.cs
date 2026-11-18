using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
        public bool hasSvDepthOutput;
        public int textureSampleCount;
        public int textureSampleInLoopCount;
        public int dynamicLoopCount;
        public int derivativeInstructionCount;
        public int transcendentalAluCount;
        public int multiCompileDirectiveCount;
        public int shaderFeatureDirectiveCount;
        public bool hasSrpBatcherCBuffer;
        public bool hasMismatchedSrpBatcherCBuffer;
        public int cbufferPassCount;
        public int estimatedKeywordBurstRiskScore;
        public int mobileReadinessScore;
        public MobileShaderHazardSeverity severity;
        public List<string> diagnostics = new List<string>();
    }

    /// <summary>
    /// Static analyzer for ShaderLab (.shader) and HLSL (.hlsl/.cginc) source files targeting mobile TBDR GPUs.
    /// Evaluates FP16 (half/real) vs FP32 (float) register pressure, Early-Z / HSR kill instructions (clip/discard/SV_Depth),
    /// texture fetches inside loops, derivative/transcendental ALU pressure, SRP Batcher CBUFFER_START(UnityPerMaterial)
    /// cross-pass layout consistency, and multi_compile variant explosion risk.
    /// </summary>
    public static class MobileHlslStaticAnalyzer
    {
        private static readonly Regex ShaderNameRegex = new Regex(@"Shader\s+""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex HlslBlockRegex = new Regex(
            @"\b(?:HLSLPROGRAM|HLSLINCLUDE|CGPROGRAM|CGINCLUDE)\b([\s\S]*?)\b(?:ENDHLSL|ENDCG)\b",
            RegexOptions.Compiled);

        private static readonly Regex Fp32Regex = new Regex(@"\bfloat(?:[1234](?:x[1234])?)?\b", RegexOptions.Compiled);
        private static readonly Regex Fp16Regex = new Regex(@"\b(?:half|real|fixed|min16float)(?:[1234](?:x[1234])?)?\b", RegexOptions.Compiled);
        private static readonly Regex DiscardClipRegex = new Regex(@"\b(?:clip\s*\(|discard\b)", RegexOptions.Compiled);
        private static readonly Regex SvDepthOutputRegex = new Regex(@":\s*SV_Depth(?:GreaterEqual|LessEqual)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TexSampleRegex = new Regex(
            @"\b(?:SAMPLE_TEXTURE2D|SAMPLE_TEXTURE2D_LOD|SAMPLE_TEXTURE2D_BIAS|SAMPLE_TEXTURE2D_ARRAY|SAMPLE_TEXTURECUBE|tex2D|tex2Dlod|tex2Dbias|texCUBE)\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex NonLodTexSampleRegex = new Regex(
            @"\b(?:SAMPLE_TEXTURE2D|SAMPLE_TEXTURE2D_BIAS|SAMPLE_TEXTURECUBE|tex2D|texCUBE)\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex DerivativeRegex = new Regex(
            @"\b(?:ddx|ddy|fwidth|ddx_coarse|ddy_coarse|ddx_fine|ddy_fine)\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex TranscendentalAluRegex = new Regex(
            @"\b(?:sin|cos|tan|asin|acos|atan|atan2|pow|exp|exp2|log|log2|sqrt|rsqrt)\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex MultiCompileFullRegex = new Regex(
            @"^\s*#pragma\s+(multi_compile(?:_local|_fragment|_vertex|_local_fragment|_local_vertex|_fog|_instancing|_fwdbase|_fwdadd|_fwdadd_fullshadows|_shadowcaster)?)\b([^\r\n]*)",
            RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex ShaderFeatureRegex = new Regex(
            @"^\s*#pragma\s+shader_feature(?:_local|_fragment|_vertex|_local_fragment|_local_vertex)?\b",
            RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex CBufferPerMaterialBlockRegex = new Regex(
            @"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)([\s\S]*?)CBUFFER_END",
            RegexOptions.Compiled);
        private static readonly Regex ForOrWhileHeaderRegex = new Regex(
            @"(?:\[(?:unroll|loop)\s*(?:\(\s*\d+\s*\))?\s*\]\s*)?\b(for|while)\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex DoLoopHeaderRegex = new Regex(
            @"(?:\[(?:unroll|loop)\s*(?:\(\s*\d+\s*\))?\s*\]\s*)?\bdo\b",
            RegexOptions.Compiled);
        private static readonly Regex WhitespaceNormalizeRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex PunctuationSpaceNormalizeRegex = new Regex(@"\s*([;,\[\]])\s*", RegexOptions.Compiled);

        public static ShaderSourceAuditResult AnalyzeShaderFile(string filePath)
        {
            string source = (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                ? File.ReadAllText(filePath)
                : string.Empty;
            return AnalyzeShaderSource(source, filePath ?? "<Unknown>");
        }

        public static ShaderSourceAuditResult AnalyzeShaderSource(string rawSource, string assetPath = "<Memory>")
        {
            string safePath = string.IsNullOrEmpty(assetPath) ? "<Memory>" : assetPath;
            var result = new ShaderSourceAuditResult
            {
                assetPath = safePath,
                shaderName = GetSafeShaderDefaultName(safePath),
                fp16Ratio = 1f,
                mobileReadinessScore = 100
            };

            if (string.IsNullOrWhiteSpace(rawSource))
            {
                result.diagnostics.Add("Empty or unreadable shader source.");
                return result;
            }

            // 1. Single-pass lexical scan to strip block/line comments while respecting string literals (e.g. "http://...")
            SanitizeCommentsAndStrings(rawSource, out string uncommentedWithStrings, out string cleanFullSource);

            Match nameMatch = ShaderNameRegex.Match(uncommentedWithStrings);
            if (nameMatch.Success)
                result.shaderName = nameMatch.Groups[1].Value.Trim();

            // 2. Strip disabled preprocessor blocks (#if 0 ... #endif) before HLSL extraction
            string activeSource = StripDisabledPreprocessorBlocks(cleanFullSource);

            // 3. Extract HLSL/CG program blocks if present (.shader files contain ShaderLab Properties outside HLSLPROGRAM)
            string hlslCode = ExtractHlslCodeRegions(activeSource);

            result.fp32DeclarationsCount = Fp32Regex.Matches(hlslCode).Count;
            result.fp16DeclarationsCount = Fp16Regex.Matches(hlslCode).Count;

            int totalTypes = result.fp32DeclarationsCount + result.fp16DeclarationsCount;
            result.fp16Ratio = totalTypes > 0 ? (float)result.fp16DeclarationsCount / totalTypes : 1f;

            result.alphaTestDiscardCount = DiscardClipRegex.Matches(hlslCode).Count;
            result.hasSvDepthOutput = SvDepthOutputRegex.IsMatch(hlslCode);
            result.textureSampleCount = TexSampleRegex.Matches(hlslCode).Count;
            result.derivativeInstructionCount = DerivativeRegex.Matches(hlslCode).Count;
            result.transcendentalAluCount = TranscendentalAluRegex.Matches(hlslCode).Count;

            AnalyzeLoopsAndInLoopTexSamples(hlslCode, out result.dynamicLoopCount, out result.textureSampleInLoopCount);
            AnalyzeSrpBatcherCBuffers(hlslCode, out result.hasSrpBatcherCBuffer, out result.cbufferPassCount, out result.hasMismatchedSrpBatcherCBuffer);

            MatchCollection multiCompiles = MultiCompileFullRegex.Matches(hlslCode);
            result.multiCompileDirectiveCount = multiCompiles.Count;
            result.shaderFeatureDirectiveCount = ShaderFeatureRegex.Matches(hlslCode).Count;

            result.estimatedKeywordBurstRiskScore = CalculateKeywordBurstMultiplier(multiCompiles);

            EvaluateDiagnostics(result);
            return result;
        }

        private static string GetSafeShaderDefaultName(string assetPath)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(assetPath);
                return string.IsNullOrEmpty(name) ? "<Memory>" : name;
            }
            catch
            {
                return "<Memory>";
            }
        }

        /// <summary>
        /// Single-pass lexical scanner that strips block (/* ... */) and line (// ...) comments
        /// while respecting string literals ("...") so strings containing "//" or "/*" (e.g. URLs)
        /// never corrupt comment removal or swallow subsequent HLSL code.
        /// </summary>
        public static void SanitizeCommentsAndStrings(
            string rawSource,
            out string uncommentedWithStrings,
            out string uncommentedNoStrings)
        {
            if (string.IsNullOrEmpty(rawSource))
            {
                uncommentedWithStrings = string.Empty;
                uncommentedNoStrings = string.Empty;
                return;
            }

            int len = rawSource.Length;
            var sbWithStrings = new StringBuilder(len);
            var sbNoStrings = new StringBuilder(len);
            int i = 0;

            while (i < len)
            {
                char c = rawSource[i];

                // Block comment: /* ... */
                if (c == '/' && i + 1 < len && rawSource[i + 1] == '*')
                {
                    sbWithStrings.Append(' ');
                    sbNoStrings.Append(' ');
                    i += 2;
                    while (i < len)
                    {
                        if (rawSource[i] == '*' && i + 1 < len && rawSource[i + 1] == '/')
                        {
                            i += 2;
                            break;
                        }
                        if (rawSource[i] == '\n')
                        {
                            sbWithStrings.Append('\n');
                            sbNoStrings.Append('\n');
                        }
                        i++;
                    }
                    continue;
                }

                // Single-line comment: // ...
                if (c == '/' && i + 1 < len && rawSource[i + 1] == '/')
                {
                    i += 2;
                    while (i < len && rawSource[i] != '\n' && rawSource[i] != '\r')
                    {
                        i++;
                    }
                    continue;
                }

                // String literal: "..."
                if (c == '"')
                {
                    sbWithStrings.Append('"');
                    sbNoStrings.Append("\"\"");
                    i++;
                    while (i < len)
                    {
                        char sc = rawSource[i++];
                        sbWithStrings.Append(sc);
                        if (sc == '\\' && i < len)
                        {
                            sbWithStrings.Append(rawSource[i++]);
                        }
                        else if (sc == '"' || sc == '\n' || sc == '\r')
                        {
                            break;
                        }
                    }
                    continue;
                }

                sbWithStrings.Append(c);
                sbNoStrings.Append(c);
                i++;
            }

            uncommentedWithStrings = sbWithStrings.ToString();
            uncommentedNoStrings = sbNoStrings.ToString();
        }

        /// <summary>
        /// Strips preprocessor blocks disabled via #if 0 / #if false while preserving #else / active #elif branches.
        /// </summary>
        public static string StripDisabledPreprocessorBlocks(string source)
        {
            if (string.IsNullOrEmpty(source))
                return string.Empty;

            var sb = new StringBuilder(source.Length);
            // Stack tracks: (isParentActive, isCurrentBranchActive, hasSeenActiveBranch)
            var stack = new Stack<(bool parentActive, bool branchActive, bool seenActive)>();
            bool currentlyActive = true;

            using (var reader = new StringReader(source))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        string directiveLine = trimmed.Substring(1).TrimStart();
                        if (directiveLine.StartsWith("ifdef", StringComparison.Ordinal) ||
                            directiveLine.StartsWith("ifndef", StringComparison.Ordinal))
                        {
                            stack.Push((currentlyActive, currentlyActive, currentlyActive));
                            sb.AppendLine();
                            continue;
                        }
                        if (directiveLine.StartsWith("if", StringComparison.Ordinal) &&
                            (directiveLine.Length == 2 || char.IsWhiteSpace(directiveLine[2]) || directiveLine[2] == '('))
                        {
                            string expr = directiveLine.Substring(2).Trim();
                            bool cond = !IsAlwaysFalsePreprocessorCondition(expr);
                            bool branchActive = currentlyActive && cond;
                            stack.Push((currentlyActive, branchActive, branchActive));
                            currentlyActive = branchActive;
                            sb.AppendLine();
                            continue;
                        }
                        if (directiveLine.StartsWith("elif", StringComparison.Ordinal) &&
                            (directiveLine.Length == 4 || char.IsWhiteSpace(directiveLine[4]) || directiveLine[4] == '('))
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                string expr = directiveLine.Substring(4).Trim();
                                bool cond = !IsAlwaysFalsePreprocessorCondition(expr);
                                bool branchActive = top.parentActive && !top.seenActive && cond;
                                stack.Push((top.parentActive, branchActive, top.seenActive || branchActive));
                                currentlyActive = branchActive;
                            }
                            sb.AppendLine();
                            continue;
                        }
                        if (directiveLine.StartsWith("else", StringComparison.Ordinal) &&
                            (directiveLine.Length == 4 || char.IsWhiteSpace(directiveLine[4])))
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                bool branchActive = top.parentActive && !top.seenActive;
                                stack.Push((top.parentActive, branchActive, true));
                                currentlyActive = branchActive;
                            }
                            sb.AppendLine();
                            continue;
                        }
                        if (directiveLine.StartsWith("endif", StringComparison.Ordinal) &&
                            (directiveLine.Length == 5 || char.IsWhiteSpace(directiveLine[5])))
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                currentlyActive = top.parentActive;
                            }
                            sb.AppendLine();
                            continue;
                        }
                    }

                    if (currentlyActive)
                        sb.AppendLine(line);
                    else
                        sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private static bool IsAlwaysFalsePreprocessorCondition(string expr)
        {
            if (string.IsNullOrEmpty(expr))
                return false;

            while (expr.Length >= 2 && expr[0] == '(' && expr[expr.Length - 1] == ')')
            {
                expr = expr.Substring(1, expr.Length - 2).Trim();
            }

            return string.Equals(expr, "0", StringComparison.Ordinal) ||
                   string.Equals(expr, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractHlslCodeRegions(string cleanFullSource)
        {
            MatchCollection blocks = HlslBlockRegex.Matches(cleanFullSource);
            if (blocks.Count == 0)
                return cleanFullSource;

            var sb = new StringBuilder(cleanFullSource.Length);
            for (int i = 0; i < blocks.Count; i++)
            {
                sb.AppendLine(blocks[i].Groups[1].Value);
            }
            return sb.ToString();
        }

        private static int CalculateKeywordBurstMultiplier(MatchCollection multiCompiles)
        {
            if (multiCompiles == null || multiCompiles.Count == 0)
                return 0;

            long variantRisk = 1;
            foreach (Match m in multiCompiles)
            {
                string directive = m.Groups[1].Value.Trim();
                string tail = m.Groups[2].Value.Trim();

                int multiplier;
                if (string.Equals(directive, "multi_compile_fog", StringComparison.Ordinal))
                {
                    multiplier = 4;
                }
                else if (string.Equals(directive, "multi_compile_instancing", StringComparison.Ordinal))
                {
                    multiplier = 2;
                }
                else if (string.Equals(directive, "multi_compile_fwdbase", StringComparison.Ordinal))
                {
                    multiplier = 16;
                }
                else if (string.Equals(directive, "multi_compile_fwdadd_fullshadows", StringComparison.Ordinal))
                {
                    multiplier = 8;
                }
                else if (string.Equals(directive, "multi_compile_fwdadd", StringComparison.Ordinal) ||
                         string.Equals(directive, "multi_compile_shadowcaster", StringComparison.Ordinal))
                {
                    multiplier = 4;
                }
                else if (string.IsNullOrEmpty(tail))
                {
                    multiplier = 2;
                }
                else
                {
                    string[] rawTokens = tail.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var uniqueTokens = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < rawTokens.Length; i++)
                    {
                        string tok = rawTokens[i];
                        // Normalize multiple consecutive underscores ("__" -> "_") as single-no-keyword slot
                        if (tok.Trim('_').Length == 0)
                            uniqueTokens.Add("_");
                        else
                            uniqueTokens.Add(tok);
                    }
                    multiplier = Math.Max(2, uniqueTokens.Count);
                }

                variantRisk = Math.Min(1048576L, variantRisk * multiplier);
            }

            return (int)variantRisk;
        }

        private static void AnalyzeLoopsAndInLoopTexSamples(string hlslCode, out int dynamicLoopCount, out int texInLoopCount)
        {
            dynamicLoopCount = 0;
            texInLoopCount = 0;

            var bodySpans = new List<(int start, int end)>();
            var doWhileTailPositions = new HashSet<int>();

            // 1. Detect do { ... } while (...); loops
            MatchCollection doMatches = DoLoopHeaderRegex.Matches(hlslCode);
            for (int i = 0; i < doMatches.Count; i++)
            {
                Match dm = doMatches[i];
                int afterDo = dm.Index + dm.Length;
                // Ensure 'do' is followed by '{' or whitespace then '{'
                int probe = afterDo;
                while (probe < hlslCode.Length && char.IsWhiteSpace(hlslCode[probe]))
                    probe++;

                if (probe >= hlslCode.Length || hlslCode[probe] != '{')
                    continue;

                bool hasUnroll = dm.Value.IndexOf("unroll", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasUnroll)
                    dynamicLoopCount++;

                if (TryExtractStatementOrBlockSpan(hlslCode, probe, out int bStart, out int bEnd, out int afterBlock))
                {
                    bodySpans.Add((bStart, bEnd));
                    while (afterBlock < hlslCode.Length && char.IsWhiteSpace(hlslCode[afterBlock]))
                        afterBlock++;
                    if (afterBlock + 5 <= hlslCode.Length &&
                        string.Compare(hlslCode, afterBlock, "while", 0, 5, StringComparison.Ordinal) == 0)
                    {
                        doWhileTailPositions.Add(afterBlock);
                    }
                }
            }

            // 2. Detect for (...) and while (...) loops
            MatchCollection loops = ForOrWhileHeaderRegex.Matches(hlslCode);
            for (int i = 0; i < loops.Count; i++)
            {
                Match m = loops[i];
                int keywordIdx = m.Groups[1].Index;
                if (doWhileTailPositions.Contains(keywordIdx))
                    continue;

                bool hasUnrollAttribute = m.Value.IndexOf("unroll", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasUnrollAttribute)
                {
                    dynamicLoopCount++;
                }

                if (TryExtractLoopBodySpanAfterHeaderParen(hlslCode, m.Index + m.Length, out int bodyStart, out int bodyEnd))
                {
                    bodySpans.Add((bodyStart, bodyEnd));
                }
            }

            if (bodySpans.Count == 0)
                return;

            // Merge overlapping/nested loop spans so nested loops never double-count texture samples
            bodySpans.Sort((a, b) => a.start != b.start ? a.start.CompareTo(b.start) : a.end.CompareTo(b.end));
            var merged = new List<(int start, int end)>(bodySpans.Count);
            var cur = bodySpans[0];
            for (int i = 1; i < bodySpans.Count; i++)
            {
                var next = bodySpans[i];
                if (next.start <= cur.end)
                {
                    cur = (cur.start, Math.Max(cur.end, next.end));
                }
                else
                {
                    merged.Add(cur);
                    cur = next;
                }
            }
            merged.Add(cur);

            for (int i = 0; i < merged.Count; i++)
            {
                int len = merged[i].end - merged[i].start;
                if (len > 0)
                {
                    string spanCode = hlslCode.Substring(merged[i].start, len);
                    texInLoopCount += NonLodTexSampleRegex.Matches(spanCode).Count;
                }
            }
        }

        private static bool TryExtractLoopBodySpanAfterHeaderParen(string code, int startAfterOpenParen, out int bodyStart, out int bodyEnd)
        {
            bodyStart = 0;
            bodyEnd = 0;
            int idx = startAfterOpenParen;
            int parenDepth = 1;
            while (idx < code.Length && parenDepth > 0)
            {
                char c = code[idx++];
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
            }

            while (idx < code.Length && char.IsWhiteSpace(code[idx]))
                idx++;

            return TryExtractStatementOrBlockSpan(code, idx, out bodyStart, out bodyEnd, out _);
        }

        private static bool TryExtractStatementOrBlockSpan(string code, int idx, out int bodyStart, out int bodyEnd, out int afterBlockIdx)
        {
            bodyStart = 0;
            bodyEnd = 0;
            afterBlockIdx = idx;

            if (idx >= code.Length)
                return false;

            if (code[idx] == '{')
            {
                bodyStart = idx + 1;
                int braceDepth = 1;
                idx++;
                while (idx < code.Length && braceDepth > 0)
                {
                    char c = code[idx++];
                    if (c == '{') braceDepth++;
                    else if (c == '}') braceDepth--;
                }
                bodyEnd = Math.Max(bodyStart, idx - 1);
                afterBlockIdx = idx;
                return bodyEnd > bodyStart;
            }
            else
            {
                bodyStart = idx;
                int semiIdx = code.IndexOf(';', bodyStart);
                if (semiIdx < 0)
                {
                    bodyEnd = code.Length;
                    afterBlockIdx = code.Length;
                }
                else
                {
                    bodyEnd = semiIdx;
                    afterBlockIdx = semiIdx + 1;
                }
                return bodyEnd > bodyStart;
            }
        }

        private static void AnalyzeSrpBatcherCBuffers(
            string hlslCode,
            out bool hasCBuffer,
            out int passCBufferCount,
            out bool hasMismatch)
        {
            MatchCollection matches = CBufferPerMaterialBlockRegex.Matches(hlslCode);
            passCBufferCount = matches.Count;
            hasCBuffer = passCBufferCount > 0;
            hasMismatch = false;

            if (passCBufferCount <= 1)
                return;

            string firstNormalized = NormalizeCBufferContent(matches[0].Groups[1].Value);
            for (int i = 1; i < matches.Count; i++)
            {
                string currentNormalized = NormalizeCBufferContent(matches[i].Groups[1].Value);
                if (!string.Equals(firstNormalized, currentNormalized, StringComparison.Ordinal))
                {
                    hasMismatch = true;
                    break;
                }
            }
        }

        private static string NormalizeCBufferContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;
            string collapsed = WhitespaceNormalizeRegex.Replace(content.Trim(), " ");
            return PunctuationSpaceNormalizeRegex.Replace(collapsed, "$1").Trim();
        }

        private static void EvaluateDiagnostics(ShaderSourceAuditResult r)
        {
            r.severity = MobileShaderHazardSeverity.Good;
            int scorePenalty = 0;

            if (r.fp32DeclarationsCount > 8 && r.fp16Ratio < 0.35f)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Warning);
                scorePenalty += 22;
                r.diagnostics.Add(
                    $"LOW FP16 USAGE ({r.fp16Ratio * 100f:F0}% half/real vs {r.fp32DeclarationsCount} float tokens): " +
                    "Excessive 32-bit float registers halve ALU occupancy and warp count on ARM Mali and Adreno GPUs. Prefer half/real for colors, normals, and lighting.");
            }

            if (r.alphaTestDiscardCount > 0)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Warning);
                scorePenalty += 20;
                r.diagnostics.Add(
                    $"EARLY-Z / HSR HAZARD ({r.alphaTestDiscardCount} clip/discard call(s)): " +
                    "clip()/discard disables Hidden Surface Removal (HSR) and Early-Z on PowerVR, Apple, and Adreno TBDR GPUs, forcing late depth updates. Prefer opaque or alpha-blended LODs where possible.");
            }

            if (r.hasSvDepthOutput)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Critical);
                scorePenalty += 30;
                r.diagnostics.Add(
                    "SV_DEPTH OUTPUT HAZARD: Writing custom fragment depth (SV_Depth) completely disables hardware Early-Z and TBDR Hidden Surface Removal across the entire draw call.");
            }

            if (r.hasMismatchedSrpBatcherCBuffer)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Critical);
                scorePenalty += 30;
                r.diagnostics.Add(
                    $"SRP BATCHER CBUFFER MISMATCH ({r.cbufferPassCount} UnityPerMaterial blocks differ across passes): " +
                    "All passes in a shader must declare identical variables in the exact same order inside CBUFFER_START(UnityPerMaterial). Move UnityPerMaterial into an HLSLINCLUDE block.");
            }
            else if (!r.hasSrpBatcherCBuffer && r.assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Advisory);
                scorePenalty += 10;
                r.diagnostics.Add(
                    "MISSING CBUFFER_START(UnityPerMaterial): Shader may break URP SRP Batcher compatibility unless all material properties are wrapped inside UnityPerMaterial.");
            }

            if (r.estimatedKeywordBurstRiskScore >= 64)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Critical);
                scorePenalty += 35;
                r.diagnostics.Add(
                    $"VARIANT EXPLOSION RISK ({r.multiCompileDirectiveCount} #pragma multi_compile directives, ~{r.estimatedKeywordBurstRiskScore}x permutation multiplier): " +
                    "Convert artist-toggled keywords to #pragma shader_feature_local or strip unused keywords via MobileShaderStripProfile.");
            }
            else if (r.estimatedKeywordBurstRiskScore >= 16)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Advisory);
                scorePenalty += 10;
                r.diagnostics.Add(
                    $"MODERATE VARIANT MULTIPLIER (~{r.estimatedKeywordBurstRiskScore}x from {r.multiCompileDirectiveCount} #pragma multi_compile directives): " +
                    "Prefer _local or _fragment suffixes where applicable.");
            }

            if (r.textureSampleInLoopCount > 0)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Critical);
                scorePenalty += 25;
                r.diagnostics.Add(
                    $"IN-LOOP TEXTURE FETCH WITHOUT EXPLICIT LOD ({r.textureSampleInLoopCount} sample(s)): " +
                    "Sampling textures inside loops using implicit gradients (SAMPLE_TEXTURE2D / tex2D) causes divergent gradient stalls or compiler unroll failures on mobile GPUs. Use SAMPLE_TEXTURE2D_LOD.");
            }

            if (r.textureSampleCount > 8)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Warning);
                scorePenalty += 18;
                r.diagnostics.Add(
                    $"HIGH TEXTURE BANDWIDTH ({r.textureSampleCount} texture samples): Pack roughness/metallic/AO channels into a single RGBA mask texture to reduce texture unit stalls.");
            }

            if (r.derivativeInstructionCount > 2)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Advisory);
                scorePenalty += 8;
                r.diagnostics.Add(
                    $"HIGH DERIVATIVE USAGE ({r.derivativeInstructionCount} ddx/ddy/fwidth calls): Fine screen-space derivatives can stall 2x2 pixel quad helper invocations on mobile TBDR cores.");
            }

            if (r.transcendentalAluCount > 8)
            {
                r.severity = MaxSeverity(r.severity, MobileShaderHazardSeverity.Warning);
                scorePenalty += 12;
                r.diagnostics.Add(
                    $"HEAVY TRANSCENDENTAL ALU ({r.transcendentalAluCount} trig/pow/exp/sqrt calls): Move invariant math to the vertex shader or use polynomial approximations / LUTs on mobile.");
            }

            r.mobileReadinessScore = Math.Max(0, 100 - scorePenalty);

            if (r.diagnostics.Count == 0)
            {
                r.diagnostics.Add("Clean mobile shader profile: Good FP16 usage, no Early-Z hazards, and controlled keyword permutations.");
            }
        }

        public static string ExportAuditResultsToCsv(IReadOnlyList<ShaderSourceAuditResult> results)
        {
            var sb = new StringBuilder((results != null ? results.Count : 0) * 128 + 128);
            sb.AppendLine("shader_name,asset_path,severity,score,fp16_ratio,fp16_count,fp32_count,clip_discard,sv_depth,tex_samples,tex_in_loop,multi_compile,variant_multiplier,srp_cbuffer,cbuffer_mismatch");
            if (results == null) return sb.ToString();

            for (int i = 0; i < results.Count; i++)
            {
                ShaderSourceAuditResult r = results[i];
                if (r == null) continue;
                sb.Append('"').Append(EscapeCsv(r.shaderName)).Append("\",")
                  .Append('"').Append(EscapeCsv(r.assetPath)).Append("\",")
                  .Append(r.severity).Append(',')
                  .Append(r.mobileReadinessScore).Append(',')
                  .Append(r.fp16Ratio.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.fp16DeclarationsCount).Append(',')
                  .Append(r.fp32DeclarationsCount).Append(',')
                  .Append(r.alphaTestDiscardCount).Append(',')
                  .Append(r.hasSvDepthOutput).Append(',')
                  .Append(r.textureSampleCount).Append(',')
                  .Append(r.textureSampleInLoopCount).Append(',')
                  .Append(r.multiCompileDirectiveCount).Append(',')
                  .Append(r.estimatedKeywordBurstRiskScore).Append(',')
                  .Append(r.hasSrpBatcherCBuffer).Append(',')
                  .AppendLine(r.hasMismatchedSrpBatcherCBuffer.ToString());
            }
            return sb.ToString();
        }

        public static string ExportAuditResultsToMarkdown(IReadOnlyList<ShaderSourceAuditResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Mobile Shader Guard — Static HLSL Audit Report");
            sb.AppendLine();
            if (results == null || results.Count == 0)
            {
                sb.AppendLine("_No shaders audited._");
                return sb.ToString();
            }

            int critical = 0, warning = 0, advisory = 0, good = 0;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i] == null) continue;
                switch (results[i].severity)
                {
                    case MobileShaderHazardSeverity.Critical: critical++; break;
                    case MobileShaderHazardSeverity.Warning: warning++; break;
                    case MobileShaderHazardSeverity.Advisory: advisory++; break;
                    default: good++; break;
                }
            }

            sb.AppendLine($"- **Total Files Audited**: {results.Count}");
            sb.AppendLine($"- **Critical**: {critical} | **Warning**: {warning} | **Advisory**: {advisory} | **Good**: {good}");
            sb.AppendLine();
            sb.AppendLine("| Shader | Severity | Score | FP16 % | Clip/Discard | Variant Risk | SRP CBuffer |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");

            for (int i = 0; i < results.Count; i++)
            {
                ShaderSourceAuditResult r = results[i];
                if (r == null) continue;
                string cbufferState = r.hasMismatchedSrpBatcherCBuffer
                    ? "MISMATCH"
                    : (r.hasSrpBatcherCBuffer ? "Yes" : "No");
                sb.Append("| `").Append(r.shaderName).Append("` | **").Append(r.severity).Append("** | ")
                  .Append(r.mobileReadinessScore).Append("/100 | ")
                  .Append((r.fp16Ratio * 100f).ToString("F0", CultureInfo.InvariantCulture)).Append("% | ")
                  .Append(r.alphaTestDiscardCount).Append(" | ")
                  .Append(r.estimatedKeywordBurstRiskScore).Append("x | ")
                  .Append(cbufferState).AppendLine(" |");
            }

            return sb.ToString();
        }

        private static string EscapeCsv(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace("\"", "\"\"");
        }

        private static MobileShaderHazardSeverity MaxSeverity(MobileShaderHazardSeverity a, MobileShaderHazardSeverity b)
        {
            return a > b ? a : b;
        }
    }
}
