using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YusufOkuducu.MobileShaderGuard.Editor
{
    [Serializable]
    public struct ShaderGuardHeatmapSummary
    {
        public int totalShaders;
        public int criticalCount;
        public int warningCount;
        public int advisoryCount;
        public int goodCount;
        public float criticalRatio;
        public float warningRatio;
        public float advisoryRatio;
        public float goodRatio;
        public float averageReadinessScore;
        public float averageFp16Ratio;
        public int totalClipDiscardCount;
        public int totalInLoopTexSampleCount;
        public int mismatchedCBufferCount;
    }

    [Serializable]
    public sealed class ShaderAutoFixReport
    {
        public string assetPath;
        public bool modified;
        public int scoreBefore;
        public int scoreAfter;
        public MobileShaderHazardSeverity severityBefore;
        public MobileShaderHazardSeverity severityAfter;
        public string patchedSource;
        public List<string> appliedFixes = new List<string>();
        public ShaderSourceAuditResult beforeAudit;
        public ShaderSourceAuditResult afterAudit;
    }

    /// <summary>
    /// Model Context Protocol (MCP) & AI Agent JSON-RPC 2.0 Bridge for Mobile Shader Guard.
    /// Allows AI coding assistants (Cursor, Claude, Antigravity, Unity MCP servers) and CI pipelines
    /// to query structured JSON diagnostics, severity heatmaps, and invoke automated HLSL shader fixes.
    /// </summary>
    public static class MobileShaderGuardMcpBridge
    {
        public const string ServerName = "unity-mobile-shader-guard";
        public const string PackageVersion = "1.2.0";
        public const string McpProtocolVersion = "2024-11-05";

        private static readonly Regex CBufferBlockRegex = new Regex(
            @"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)([\s\S]*?)CBUFFER_END",
            RegexOptions.Compiled);

        private static readonly Regex CBufferFieldLineRegex = new Regex(
            @"^\s*([A-Za-z_]\w*)\s+([A-Za-z_]\w*(?:\s*\[\s*\d+\s*\])?)\s*;",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex CustomMultiCompileRegex = new Regex(
            @"^([ \t]*#pragma[ \t]+)multi_compile(_fragment|_vertex)?([ \t]+[^\r\n]+)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex SampleTex2DCallRegex = new Regex(
            @"\bSAMPLE_TEXTURE2D\s*\(\s*([^,]+?)\s*,\s*([^,]+?)\s*,\s*([^)]+?)\s*\)",
            RegexOptions.Compiled);

        private static readonly Regex ForWhileLoopHeaderRegex = new Regex(
            @"\b(?:for|while)\s*\(",
            RegexOptions.Compiled);

        public static ShaderGuardHeatmapSummary ComputeHeatmapSummary(IReadOnlyList<ShaderSourceAuditResult> results)
        {
            var summary = new ShaderGuardHeatmapSummary();
            if (results == null || results.Count == 0)
                return summary;

            float sumScore = 0f;
            float sumFp16 = 0f;
            int validCount = 0;

            for (int i = 0; i < results.Count; i++)
            {
                ShaderSourceAuditResult r = results[i];
                if (r == null) continue;
                validCount++;

                switch (r.severity)
                {
                    case MobileShaderHazardSeverity.Critical: summary.criticalCount++; break;
                    case MobileShaderHazardSeverity.Warning: summary.warningCount++; break;
                    case MobileShaderHazardSeverity.Advisory: summary.advisoryCount++; break;
                    default: summary.goodCount++; break;
                }

                sumScore += r.mobileReadinessScore;
                sumFp16 += r.fp16Ratio;
                summary.totalClipDiscardCount += r.alphaTestDiscardCount;
                summary.totalInLoopTexSampleCount += r.textureSampleInLoopCount;
                if (r.hasMismatchedSrpBatcherCBuffer)
                    summary.mismatchedCBufferCount++;
            }

            summary.totalShaders = validCount;
            if (validCount > 0)
            {
                summary.criticalRatio = (float)summary.criticalCount / validCount;
                summary.warningRatio = (float)summary.warningCount / validCount;
                summary.advisoryRatio = (float)summary.advisoryCount / validCount;
                summary.goodRatio = (float)summary.goodCount / validCount;
                summary.averageReadinessScore = sumScore / validCount;
                summary.averageFp16Ratio = sumFp16 / validCount;
            }

            return summary;
        }

        /// <summary>
        /// Automatically patches common mobile HLSL hazards in shader source code:
        /// 1. Unifies mismatched CBUFFER_START(UnityPerMaterial) blocks across passes for SRP Batcher compliance.
        /// 2. Upgrades implicit-gradient SAMPLE_TEXTURE2D calls inside loop bodies to SAMPLE_TEXTURE2D_LOD(..., 0).
        /// 3. Converts artist-toggled #pragma multi_compile directives to #pragma shader_feature_local.
        /// </summary>
        public static ShaderAutoFixReport AutoFixShaderSource(
            string rawShaderSource,
            string assetPath = "<Memory>",
            bool unifyMismatchedSrpBatcherCBuffers = true,
            bool fixInLoopTextureLods = true,
            bool convertArtistMultiCompileToShaderFeature = true)
        {
            string safePath = string.IsNullOrEmpty(assetPath) ? "<Memory>" : assetPath;
            ShaderSourceAuditResult before = MobileHlslStaticAnalyzer.AnalyzeShaderSource(rawShaderSource, safePath);

            var report = new ShaderAutoFixReport
            {
                assetPath = safePath,
                scoreBefore = before.mobileReadinessScore,
                severityBefore = before.severity,
                patchedSource = rawShaderSource ?? string.Empty,
                beforeAudit = before
            };

            if (string.IsNullOrWhiteSpace(rawShaderSource))
            {
                report.afterAudit = before;
                report.scoreAfter = before.mobileReadinessScore;
                report.severityAfter = before.severity;
                return report;
            }

            string working = rawShaderSource;

            // 1. Unify mismatched CBUFFER_START(UnityPerMaterial) blocks across passes
            if (unifyMismatchedSrpBatcherCBuffers && before.hasMismatchedSrpBatcherCBuffer)
            {
                MatchCollection cbufferMatches = CBufferBlockRegex.Matches(working);
                if (cbufferMatches.Count > 1)
                {
                    var canonicalFields = new List<(string type, string name)>();
                    var seenFieldNames = new HashSet<string>(StringComparer.Ordinal);

                    for (int m = 0; m < cbufferMatches.Count; m++)
                    {
                        string blockBody = cbufferMatches[m].Groups[1].Value;
                        MatchCollection fields = CBufferFieldLineRegex.Matches(blockBody);
                        for (int f = 0; f < fields.Count; f++)
                        {
                            string fType = fields[f].Groups[1].Value.Trim();
                            string fName = fields[f].Groups[2].Value.Trim();
                            if (seenFieldNames.Add(fName))
                            {
                                canonicalFields.Add((fType, fName));
                            }
                        }
                    }

                    if (canonicalFields.Count > 0)
                    {
                        var unifiedSb = new StringBuilder();
                        unifiedSb.AppendLine("CBUFFER_START(UnityPerMaterial)");
                        for (int i = 0; i < canonicalFields.Count; i++)
                        {
                            unifiedSb.Append("                ")
                                     .Append(canonicalFields[i].type)
                                     .Append(' ')
                                     .Append(canonicalFields[i].name)
                                     .AppendLine(";");
                        }
                        unifiedSb.Append("            CBUFFER_END");

                        string unifiedBlock = unifiedSb.ToString();
                        working = CBufferBlockRegex.Replace(working, unifiedBlock);
                        report.appliedFixes.Add(
                            $"Unified {cbufferMatches.Count} CBUFFER_START(UnityPerMaterial) blocks across passes with {canonicalFields.Count} canonical properties for SRP Batcher compatibility.");
                    }
                }
            }

            // 2. Upgrade in-loop SAMPLE_TEXTURE2D(tex, samp, uv) to SAMPLE_TEXTURE2D_LOD(tex, samp, uv, 0)
            if (fixInLoopTextureLods && before.textureSampleInLoopCount > 0)
            {
                int replacedCount = 0;
                working = UpgradeInLoopTextureSamplesToLod(working, out replacedCount);
                if (replacedCount > 0)
                {
                    report.appliedFixes.Add(
                        $"Replaced {replacedCount} in-loop SAMPLE_TEXTURE2D call(s) with explicit-gradient SAMPLE_TEXTURE2D_LOD(..., 0) to prevent mobile GPU gradient stalls.");
                }
            }

            // 3. Convert custom/artist #pragma multi_compile directives to #pragma shader_feature_local
            if (convertArtistMultiCompileToShaderFeature && before.multiCompileDirectiveCount > 0)
            {
                int convertedPragmas = 0;
                working = CustomMultiCompileRegex.Replace(working, match =>
                {
                    string prefix = match.Groups[1].Value;
                    string stageSuffix = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                    string tail = match.Groups[3].Value;

                    // Preserve core URP systemic multi_compile directives unless high burst risk
                    bool isCoreEngineShadowOrLight =
                        tail.IndexOf("_MAIN_LIGHT_SHADOWS", StringComparison.Ordinal) >= 0 ||
                        tail.IndexOf("_ADDITIONAL_LIGHTS", StringComparison.Ordinal) >= 0 ||
                        tail.IndexOf("DOTS_INSTANCING_ON", StringComparison.Ordinal) >= 0;

                    if (isCoreEngineShadowOrLight && before.estimatedKeywordBurstRiskScore < 64)
                        return match.Value;

                    convertedPragmas++;
                    string localDirective = string.IsNullOrEmpty(stageSuffix)
                        ? "shader_feature_local"
                        : "shader_feature_local" + stageSuffix;
                    return prefix + localDirective + tail;
                });

                if (convertedPragmas > 0)
                {
                    report.appliedFixes.Add(
                        $"Converted {convertedPragmas} #pragma multi_compile directive(s) to #pragma shader_feature_local to curb variant explosion.");
                }
            }

            ShaderSourceAuditResult after = MobileHlslStaticAnalyzer.AnalyzeShaderSource(working, safePath);
            report.patchedSource = working;
            report.modified = !string.Equals(rawShaderSource, working, StringComparison.Ordinal);
            report.afterAudit = after;
            report.scoreAfter = after.mobileReadinessScore;
            report.severityAfter = after.severity;

            return report;
        }

        private static string UpgradeInLoopTextureSamplesToLod(string source, out int replacedCount)
        {
            replacedCount = 0;
            MatchCollection loops = ForWhileLoopHeaderRegex.Matches(source);
            if (loops.Count == 0)
                return source;

            var sb = new StringBuilder(source);
            // Process from end to start so string offsets remain valid
            for (int i = loops.Count - 1; i >= 0; i--)
            {
                int idx = loops[i].Index + loops[i].Length;
                int parenDepth = 1;
                while (idx < sb.Length && parenDepth > 0)
                {
                    char c = sb[idx++];
                    if (c == '(') parenDepth++;
                    else if (c == ')') parenDepth--;
                }

                while (idx < sb.Length && char.IsWhiteSpace(sb[idx]))
                    idx++;

                if (idx >= sb.Length || sb[idx] != '{')
                    continue;

                int bodyStart = idx + 1;
                int braceDepth = 1;
                idx++;
                while (idx < sb.Length && braceDepth > 0)
                {
                    char c = sb[idx++];
                    if (c == '{') braceDepth++;
                    else if (c == '}') braceDepth--;
                }
                int bodyEnd = Math.Max(bodyStart, idx - 1);
                if (bodyEnd <= bodyStart)
                    continue;

                string body = sb.ToString(bodyStart, bodyEnd - bodyStart);
                int localCount = 0;
                string patchedBody = SampleTex2DCallRegex.Replace(body, m =>
                {
                    localCount++;
                    return $"SAMPLE_TEXTURE2D_LOD({m.Groups[1].Value.Trim()}, {m.Groups[2].Value.Trim()}, {m.Groups[3].Value.Trim()}, 0)";
                });

                if (localCount > 0)
                {
                    replacedCount += localCount;
                    sb.Remove(bodyStart, bodyEnd - bodyStart);
                    sb.Insert(bodyStart, patchedBody);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Exports a structured MCP diagnostic JSON payload containing heatmap statistics and per-shader findings.
        /// </summary>
        public static string ExportAuditReportToJson(IReadOnlyList<ShaderSourceAuditResult> results)
        {
            ShaderGuardHeatmapSummary heatmap = ComputeHeatmapSummary(results);
            var sb = new StringBuilder(2048);
            sb.Append('{');
            AppendJsonProp(sb, "tool", ServerName, true);
            AppendJsonProp(sb, "version", PackageVersion, true);
            sb.Append("\"heatmap\":");
            AppendHeatmapJson(sb, heatmap);
            sb.Append(",\"results\":[");
            if (results != null)
            {
                bool first = true;
                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i] == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendAuditResultJson(sb, results[i]);
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Processes a JSON-RPC 2.0 request according to the Model Context Protocol (MCP) specification.
        /// Supported methods: "initialize", "tools/list", "tools/call".
        /// </summary>
        public static string HandleJsonRpcRequest(string jsonRpcRequest)
        {
            if (string.IsNullOrWhiteSpace(jsonRpcRequest))
            {
                return BuildJsonRpcError("null", -32700, "Parse error: Empty JSON-RPC request.");
            }

            string trimmed = jsonRpcRequest.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return BuildJsonRpcError("null", -32700, "Parse error: Malformed JSON object.");
            }

            string idRaw = ExtractJsonRpcIdRaw(trimmed);
            string method = ExtractJsonStringField(trimmed, "method");

            if (string.IsNullOrEmpty(method))
            {
                return BuildJsonRpcError(idRaw, -32600, "Invalid Request: Missing 'method' field.");
            }

            if (string.Equals(method, "initialize", StringComparison.Ordinal))
            {
                var res = new StringBuilder();
                res.Append("{\"protocolVersion\":\"").Append(McpProtocolVersion).Append("\",");
                res.Append("\"capabilities\":{\"tools\":{\"listChanged\":false}},");
                res.Append("\"serverInfo\":{\"name\":\"").Append(ServerName).Append("\",\"version\":\"").Append(PackageVersion).Append("\"}}");
                return BuildJsonRpcResult(idRaw, res.ToString());
            }

            if (string.Equals(method, "tools/list", StringComparison.Ordinal))
            {
                return BuildJsonRpcResult(idRaw, BuildToolsListJson());
            }

            if (string.Equals(method, "tools/call", StringComparison.Ordinal))
            {
                string toolName = ExtractJsonStringField(trimmed, "name");
                if (string.IsNullOrEmpty(toolName))
                {
                    return BuildJsonRpcError(idRaw, -32602, "Invalid params: Missing tool 'name' in tools/call.");
                }

                if (string.Equals(toolName, "shader_guard.audit_shader_source", StringComparison.Ordinal))
                {
                    string source = ExtractJsonStringField(trimmed, "source");
                    string assetPath = ExtractJsonStringField(trimmed, "assetPath");
                    if (string.IsNullOrEmpty(assetPath)) assetPath = "<McpInlineShader.shader>";

                    ShaderSourceAuditResult audit = MobileHlslStaticAnalyzer.AnalyzeShaderSource(source ?? string.Empty, assetPath);
                    var sb = new StringBuilder();
                    AppendAuditResultJson(sb, audit);
                    return BuildJsonRpcToolCallResult(idRaw, sb.ToString());
                }

                if (string.Equals(toolName, "shader_guard.scan_directory", StringComparison.Ordinal))
                {
                    string dir = ExtractJsonStringField(trimmed, "rootDirectory");
                    if (string.IsNullOrEmpty(dir)) dir = "Assets";
                    List<ShaderSourceAuditResult> results = MobileShaderGuardCli.ScanDirectoryShaders(dir);
                    string payload = ExportAuditReportToJson(results);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                if (string.Equals(toolName, "shader_guard.autofix_shader_source", StringComparison.Ordinal))
                {
                    string source = ExtractJsonStringField(trimmed, "source");
                    string assetPath = ExtractJsonStringField(trimmed, "assetPath");
                    if (string.IsNullOrEmpty(assetPath)) assetPath = "<McpInlineShader.shader>";

                    ShaderAutoFixReport fixReport = AutoFixShaderSource(source ?? string.Empty, assetPath);
                    string payload = SerializeAutoFixReportJson(fixReport);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                return BuildJsonRpcError(idRaw, -32602, $"Unknown tool '{EscapeJson(toolName)}'.");
            }

            return BuildJsonRpcError(idRaw, -32601, $"Method not found: '{EscapeJson(method)}'.");
        }

        public static string SerializeAutoFixReportJson(ShaderAutoFixReport report)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonProp(sb, "assetPath", report.assetPath, true);
            AppendJsonProp(sb, "modified", report.modified, true);
            AppendJsonProp(sb, "scoreBefore", report.scoreBefore, true);
            AppendJsonProp(sb, "scoreAfter", report.scoreAfter, true);
            AppendJsonProp(sb, "severityBefore", report.severityBefore.ToString(), true);
            AppendJsonProp(sb, "severityAfter", report.severityAfter.ToString(), true);
            AppendJsonProp(sb, "patchedSource", report.patchedSource, true);
            sb.Append("\"appliedFixes\":[");
            if (report.appliedFixes != null)
            {
                for (int i = 0; i < report.appliedFixes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(report.appliedFixes[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string BuildToolsListJson()
        {
            return "{\"tools\":[" +
                "{\"name\":\"shader_guard.audit_shader_source\",\"description\":\"Audits HLSL/ShaderLab source code for mobile TBDR GPU anti-patterns (FP16 vs FP32 register pressure, clip/discard/SV_Depth Early-Z hazards, in-loop texture fetches, SRP Batcher CBUFFER mismatches, and variant explosion).\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"assetPath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}," +
                "{\"name\":\"shader_guard.scan_directory\",\"description\":\"Scans a project directory for .shader, .hlsl, and .cginc files and returns a complete JSON report with severity heatmap statistics.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"rootDirectory\":{\"type\":\"string\"}}}}," +
                "{\"name\":\"shader_guard.autofix_shader_source\",\"description\":\"Automatically fixes mobile HLSL hazards by unifying mismatched CBUFFER_START(UnityPerMaterial) blocks across passes, converting in-loop SAMPLE_TEXTURE2D to SAMPLE_TEXTURE2D_LOD, and converting artist #pragma multi_compile directives to shader_feature_local.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"assetPath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}" +
                "]}";
        }

        private static void AppendHeatmapJson(StringBuilder sb, ShaderGuardHeatmapSummary h)
        {
            sb.Append('{');
            AppendJsonProp(sb, "totalShaders", h.totalShaders, true);
            AppendJsonProp(sb, "criticalCount", h.criticalCount, true);
            AppendJsonProp(sb, "warningCount", h.warningCount, true);
            AppendJsonProp(sb, "advisoryCount", h.advisoryCount, true);
            AppendJsonProp(sb, "goodCount", h.goodCount, true);
            AppendJsonProp(sb, "criticalRatio", h.criticalRatio, true);
            AppendJsonProp(sb, "warningRatio", h.warningRatio, true);
            AppendJsonProp(sb, "advisoryRatio", h.advisoryRatio, true);
            AppendJsonProp(sb, "goodRatio", h.goodRatio, true);
            AppendJsonProp(sb, "averageReadinessScore", h.averageReadinessScore, true);
            AppendJsonProp(sb, "averageFp16Ratio", h.averageFp16Ratio, true);
            AppendJsonProp(sb, "totalClipDiscardCount", h.totalClipDiscardCount, true);
            AppendJsonProp(sb, "totalInLoopTexSampleCount", h.totalInLoopTexSampleCount, true);
            AppendJsonProp(sb, "mismatchedCBufferCount", h.mismatchedCBufferCount, false);
            sb.Append('}');
        }

        private static void AppendAuditResultJson(StringBuilder sb, ShaderSourceAuditResult r)
        {
            sb.Append('{');
            AppendJsonProp(sb, "shaderName", r.shaderName, true);
            AppendJsonProp(sb, "assetPath", r.assetPath, true);
            AppendJsonProp(sb, "severity", r.severity.ToString(), true);
            AppendJsonProp(sb, "mobileReadinessScore", r.mobileReadinessScore, true);
            AppendJsonProp(sb, "fp16Ratio", r.fp16Ratio, true);
            AppendJsonProp(sb, "fp16DeclarationsCount", r.fp16DeclarationsCount, true);
            AppendJsonProp(sb, "fp32DeclarationsCount", r.fp32DeclarationsCount, true);
            AppendJsonProp(sb, "alphaTestDiscardCount", r.alphaTestDiscardCount, true);
            AppendJsonProp(sb, "hasSvDepthOutput", r.hasSvDepthOutput, true);
            AppendJsonProp(sb, "textureSampleCount", r.textureSampleCount, true);
            AppendJsonProp(sb, "textureSampleInLoopCount", r.textureSampleInLoopCount, true);
            AppendJsonProp(sb, "dynamicLoopCount", r.dynamicLoopCount, true);
            AppendJsonProp(sb, "derivativeInstructionCount", r.derivativeInstructionCount, true);
            AppendJsonProp(sb, "transcendentalAluCount", r.transcendentalAluCount, true);
            AppendJsonProp(sb, "multiCompileDirectiveCount", r.multiCompileDirectiveCount, true);
            AppendJsonProp(sb, "shaderFeatureDirectiveCount", r.shaderFeatureDirectiveCount, true);
            AppendJsonProp(sb, "estimatedKeywordBurstRiskScore", r.estimatedKeywordBurstRiskScore, true);
            AppendJsonProp(sb, "hasSrpBatcherCBuffer", r.hasSrpBatcherCBuffer, true);
            AppendJsonProp(sb, "hasMismatchedSrpBatcherCBuffer", r.hasMismatchedSrpBatcherCBuffer, true);
            AppendJsonProp(sb, "cbufferPassCount", r.cbufferPassCount, true);
            sb.Append("\"diagnostics\":[");
            if (r.diagnostics != null)
            {
                for (int i = 0; i < r.diagnostics.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.diagnostics[i])).Append('"');
                }
            }
            sb.Append("]}");
        }

        private static string BuildJsonRpcToolCallResult(string idRaw, string structuredJsonPayload)
        {
            var sb = new StringBuilder(structuredJsonPayload.Length + 256);
            sb.Append("{\"content\":[{\"type\":\"text\",\"text\":\"")
              .Append(EscapeJson(structuredJsonPayload))
              .Append("\"}],\"structuredContent\":")
              .Append(structuredJsonPayload)
              .Append(",\"isError\":false}");
            return BuildJsonRpcResult(idRaw, sb.ToString());
        }

        private static string BuildJsonRpcResult(string idRaw, string resultJson)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}";
        }

        private static string BuildJsonRpcError(string idRaw, int code, string message)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":{code},\"message\":\"{EscapeJson(message)}\"}}}}";
        }

        private static void AppendJsonProp(StringBuilder sb, string key, string val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":\"").Append(EscapeJson(val ?? string.Empty)).Append('"');
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, int val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val);
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, float val, bool trailingComma)
        {
            float safe = (float.IsNaN(val) || float.IsInfinity(val)) ? 0f : val;
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(safe.ToString("F3", CultureInfo.InvariantCulture));
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, bool val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val ? "true" : "false");
            if (trailingComma) sb.Append(',');
        }

        public static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        internal static string ExtractJsonRpcIdRaw(string json)
        {
            var m = Regex.Match(json, @"""id""\s*:\s*(""[^""\\]*(?:\\.[^""\\]*)*""|-?\d+|null)");
            return m.Success ? m.Groups[1].Value : "null";
        }

        internal static string ExtractJsonStringField(string json, string fieldName)
        {
            string pattern = $@"""{Regex.Escape(fieldName)}""\s*:\s*""((?:[^""\\]|\\.)*)""";
            Match m = Regex.Match(json, pattern);
            if (!m.Success) return null;
            return UnescapeJsonString(m.Groups[1].Value);
        }

        private static string UnescapeJsonString(string escaped)
        {
            if (string.IsNullOrEmpty(escaped)) return string.Empty;
            var sb = new StringBuilder(escaped.Length);
            for (int i = 0; i < escaped.Length; i++)
            {
                char c = escaped[i];
                if (c == '\\' && i + 1 < escaped.Length)
                {
                    char next = escaped[++i];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < escaped.Length &&
                                int.TryParse(escaped.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(next); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
