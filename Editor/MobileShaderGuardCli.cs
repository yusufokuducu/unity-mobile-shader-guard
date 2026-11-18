using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace YusufOkuducu.MobileShaderGuard.Editor
{
    public static class MobileShaderGuardCli
    {
        public static List<ShaderSourceAuditResult> ScanDirectoryShaders(string rootDirectory = "Assets")
        {
            var results = new List<ShaderSourceAuditResult>();
            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                return results;

            string[] files = Directory.GetFiles(rootDirectory, "*.*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string ext = Path.GetExtension(files[i]);
                if (!string.Equals(ext, ".shader", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".hlsl", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".cginc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string normalized = files[i].Replace('\\', '/');
                results.Add(MobileHlslStaticAnalyzer.AnalyzeShaderFile(normalized));
            }

            results.Sort((a, b) =>
            {
                int sevCmp = b.severity.CompareTo(a.severity);
                if (sevCmp != 0) return sevCmp;
                return a.mobileReadinessScore.CompareTo(b.mobileReadinessScore);
            });

            return results;
        }

        /// <summary>
        /// Batchmode / CI entrypoint:
        /// unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileShaderGuard.Editor.MobileShaderGuardCli.RunCiAudit
        /// </summary>
        public static void RunCiAudit()
        {
            List<ShaderSourceAuditResult> results = ScanDirectoryShaders("Assets");
            string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            string csvPath = Path.Combine(logDir, "MobileShaderGuardAudit.csv");
            string mdPath = Path.Combine(logDir, "MobileShaderGuardAudit.md");
            string jsonPath = Path.Combine(logDir, "MobileShaderGuardMcpReport.json");

            File.WriteAllText(csvPath, MobileHlslStaticAnalyzer.ExportAuditResultsToCsv(results), Encoding.UTF8);
            File.WriteAllText(mdPath, MobileHlslStaticAnalyzer.ExportAuditResultsToMarkdown(results), Encoding.UTF8);
            File.WriteAllText(jsonPath, MobileShaderGuardMcpBridge.ExportAuditReportToJson(results), Encoding.UTF8);

            int criticalCount = 0;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].severity == MobileShaderHazardSeverity.Critical)
                    criticalCount++;
            }

            Debug.Log($"[MobileShaderGuard CI] Audited {results.Count} shader files ({criticalCount} Critical). Reports saved to {logDir}");
        }

        /// <summary>
        /// MCP / AI Agent batchmode entrypoint outputting structured JSON diagnostics & heatmap summary:
        /// unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.MobileShaderGuard.Editor.MobileShaderGuardCli.RunMcpAuditJson
        /// </summary>
        public static string RunMcpAuditJson()
        {
            return RunMcpAuditJson("Assets");
        }

        public static string RunMcpAuditJson(string rootDirectory)
        {
            List<ShaderSourceAuditResult> results = ScanDirectoryShaders(string.IsNullOrEmpty(rootDirectory) ? "Assets" : rootDirectory);
            string json = MobileShaderGuardMcpBridge.ExportAuditReportToJson(results);

            string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            string jsonPath = Path.Combine(logDir, "MobileShaderGuardMcpReport.json");
            File.WriteAllText(jsonPath, json, Encoding.UTF8);
            Debug.Log($"[MobileShaderGuard MCP] Exported structured JSON audit ({results.Count} shaders) to {jsonPath}");
            return json;
        }
    }
}
