using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace YusufOkuducu.MobileShaderGuard.Editor
{
    public sealed class MobileShaderGuardWindow : EditorWindow
    {
        private readonly List<ShaderSourceAuditResult> _results = new List<ShaderSourceAuditResult>();
        private Vector2 _scroll;
        private MobileShaderHazardSeverity _minFilterSeverity = MobileShaderHazardSeverity.Good;

        [MenuItem("Window/Analysis/Mobile Shader Guard & HLSL Analyzer")]
        public static void ShowWindow()
        {
            var win = GetWindow<MobileShaderGuardWindow>("Mobile Shader Guard");
            win.minSize = new Vector2(680f, 480f);
            win.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Mobile Shader Guard — Static HLSL Analyzer & Variant Stripper", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scans project .shader, .hlsl, and .cginc files for Mobile TBDR GPU anti-patterns: FP32 vs FP16 register pressure, clip/discard/SV_Depth Early-Z/HSR breaks, in-loop texture fetches, SRP Batcher cross-pass CBUFFER mismatches, and multi_compile variant explosion.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan All Project Shaders & HLSL", GUILayout.Height(28f)))
            {
                ScanProjectShaders();
            }

            if (GUILayout.Button("Create / Select Strip Profile Asset", GUILayout.Height(28f)))
            {
                EnsureOrSelectStripProfile();
            }

            if (GUILayout.Button("Export Markdown / CSV Report", GUILayout.Height(28f)))
            {
                ExportReports();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            _minFilterSeverity = (MobileShaderHazardSeverity)EditorGUILayout.EnumPopup("Minimum Severity Filter", _minFilterSeverity);

            EditorGUILayout.Space(6f);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _results.Count; i++)
            {
                ShaderSourceAuditResult r = _results[i];
                if (r.severity < _minFilterSeverity)
                    continue;

                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    $"[{r.severity}] {r.shaderName} — Readiness Score: {r.mobileReadinessScore}/100",
                    EditorStyles.boldLabel);
                if (GUILayout.Button("Ping", GUILayout.Width(56f)))
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(r.assetPath);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }
                EditorGUILayout.EndHorizontal();

                string cbufferStatus = r.hasMismatchedSrpBatcherCBuffer
                    ? "MISMATCH ACROSS PASSES"
                    : r.hasSrpBatcherCBuffer.ToString();

                EditorGUILayout.LabelField(
                    $"Path: {r.assetPath} | FP16: {(r.fp16Ratio * 100f):F0}% ({r.fp16DeclarationsCount} half vs {r.fp32DeclarationsCount} float) | " +
                    $"Tex: {r.textureSampleCount} (In-Loop: {r.textureSampleInLoopCount}) | clip/discard: {r.alphaTestDiscardCount} | " +
                    $"SV_Depth: {r.hasSvDepthOutput} | Variant Risk: ~{r.estimatedKeywordBurstRiskScore}x | SRP CBuffer: {cbufferStatus}",
                    EditorStyles.miniLabel);

                for (int d = 0; d < r.diagnostics.Count; d++)
                {
                    EditorGUILayout.LabelField("• " + r.diagnostics[d], EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void ScanProjectShaders()
        {
            _results.Clear();
            _results.AddRange(MobileShaderGuardCli.ScanDirectoryShaders("Assets"));
        }

        private void ExportReports()
        {
            if (_results.Count == 0)
                ScanProjectShaders();

            string path = EditorUtility.SaveFilePanel("Export Mobile Shader Guard Report", "", "MobileShaderGuardAudit.md", "md");
            if (string.IsNullOrEmpty(path))
                return;

            File.WriteAllText(path, MobileHlslStaticAnalyzer.ExportAuditResultsToMarkdown(_results), Encoding.UTF8);
            string csvPath = Path.ChangeExtension(path, ".csv");
            File.WriteAllText(csvPath, MobileHlslStaticAnalyzer.ExportAuditResultsToCsv(_results), Encoding.UTF8);
            EditorUtility.RevealInFinder(path);
        }

        private static void EnsureOrSelectStripProfile()
        {
            string[] guids = AssetDatabase.FindAssets("t:MobileShaderStripProfile");
            if (guids != null && guids.Length > 0)
            {
                string existingPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<MobileShaderStripProfile>(existingPath);
                EditorGUIUtility.PingObject(Selection.activeObject);
                return;
            }

            var profile = CreateInstance<MobileShaderStripProfile>();
            const string targetPath = "Assets/MobileShaderStripProfile.asset";
            AssetDatabase.CreateAsset(profile, targetPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }
    }
}
