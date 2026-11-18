using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YusufOkuducu.MobileShaderGuard.Editor
{
    public sealed class MobileShaderGuardWindow : EditorWindow
    {
        private readonly List<ShaderSourceAuditResult> _results = new List<ShaderSourceAuditResult>();
        private Vector2 _scroll;
        private MobileShaderHazardSeverity _minFilterSeverity = MobileShaderHazardSeverity.Good;
        private string _searchFilter = string.Empty;

        // UI Toolkit containers
        private VisualElement _heatmapContainer;
        private ScrollView _resultsListScroll;

        public IReadOnlyList<ShaderSourceAuditResult> Results => _results;
        public string SearchFilter => _searchFilter;
        public MobileShaderHazardSeverity MinimumSeverityFilter => _minFilterSeverity;

        [MenuItem("Window/Analysis/Mobile Shader Guard & HLSL Analyzer")]
        public static void ShowWindow()
        {
            var win = GetWindow<MobileShaderGuardWindow>("Mobile Shader Guard");
            win.minSize = new Vector2(720f, 520f);
            win.Show();
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            if (root == null)
                return;

            root.Clear();
            root.style.paddingTop = 8f;
            root.style.paddingBottom = 8f;
            root.style.paddingLeft = 10f;
            root.style.paddingRight = 10f;

            var titleLabel = new Label("Mobile Shader Guard — Static HLSL Analyzer, Heatmap & Variant Stripper")
            {
                name = "header-title"
            };
            titleLabel.style.fontSize = 15f;
            titleLabel.style.marginBottom = 4f;
            root.Add(titleLabel);

            var infoBox = new HelpBox(
                "Scans project .shader, .hlsl, and .cginc files for Mobile TBDR GPU anti-patterns: FP32 vs FP16 register pressure, clip/discard/SV_Depth Early-Z/HSR breaks, in-loop texture fetches, SRP Batcher cross-pass CBUFFER mismatches, and multi_compile variant explosion.",
                HelpBoxMessageType.Info);
            infoBox.style.marginBottom = 8f;
            root.Add(infoBox);

            // Action Toolbar Row
            var toolbarRow = new VisualElement { name = "action-toolbar" };
            toolbarRow.style.flexDirection = FlexDirection.Row;
            toolbarRow.style.marginBottom = 8f;

            var scanBtn = new Button(() =>
            {
                ScanProjectShaders();
                RefreshUiToolkitView();
            })
            {
                name = "btn-scan",
                text = "Scan All Project Shaders & HLSL"
            };
            scanBtn.style.height = 28f;
            scanBtn.style.flexGrow = 1f;
            scanBtn.style.marginRight = 4f;

            var profileBtn = new Button(EnsureOrSelectStripProfile)
            {
                name = "btn-strip-profile",
                text = "Create / Select Strip Profile Asset"
            };
            profileBtn.style.height = 28f;
            profileBtn.style.flexGrow = 1f;
            profileBtn.style.marginRight = 4f;

            var exportBtn = new Button(ExportReports)
            {
                name = "btn-export",
                text = "Export MD / CSV / MCP JSON"
            };
            exportBtn.style.height = 28f;
            exportBtn.style.flexGrow = 1f;

            toolbarRow.Add(scanBtn);
            toolbarRow.Add(profileBtn);
            toolbarRow.Add(exportBtn);
            root.Add(toolbarRow);

            // Interactive Search & Severity Filter Row
            var filterRow = new VisualElement { name = "filter-row" };
            filterRow.style.flexDirection = FlexDirection.Row;
            filterRow.style.alignItems = Align.Center;
            filterRow.style.marginBottom = 8f;

            var searchField = new TextField("Search Shader / Diagnostic")
            {
                name = "search-field",
                value = _searchFilter
            };
            searchField.style.flexGrow = 1f;
            searchField.style.marginRight = 8f;
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue ?? string.Empty;
                RefreshUiToolkitView();
            });
            filterRow.Add(searchField);

            AddSeverityFilterButton(filterRow, "All", MobileShaderHazardSeverity.Good);
            AddSeverityFilterButton(filterRow, "Advisory+", MobileShaderHazardSeverity.Advisory);
            AddSeverityFilterButton(filterRow, "Warning+", MobileShaderHazardSeverity.Warning);
            AddSeverityFilterButton(filterRow, "Critical", MobileShaderHazardSeverity.Critical);
            root.Add(filterRow);

            // Visual Heatmap & Readiness Summary Container
            _heatmapContainer = new VisualElement { name = "heatmap-container" };
            _heatmapContainer.style.marginBottom = 8f;
            root.Add(_heatmapContainer);

            // Scrollable Shader Cards List
            _resultsListScroll = new ScrollView { name = "results-scroll" };
            _resultsListScroll.style.flexGrow = 1f;
            root.Add(_resultsListScroll);

            RefreshUiToolkitView();
        }

        private void AddSeverityFilterButton(VisualElement parent, string label, MobileShaderHazardSeverity sev)
        {
            var btn = new Button(() =>
            {
                _minFilterSeverity = sev;
                RefreshUiToolkitView();
            })
            {
                name = $"btn-filter-{sev}",
                text = label
            };
            btn.style.height = 22f;
            btn.style.marginLeft = 2f;
            parent.Add(btn);
        }

        public void SetAuditResults(IEnumerable<ShaderSourceAuditResult> customResults)
        {
            _results.Clear();
            if (customResults != null)
                _results.AddRange(customResults);
            RefreshUiToolkitView();
        }

        public void SetSearchFilter(string query)
        {
            _searchFilter = query ?? string.Empty;
            RefreshUiToolkitView();
        }

        public void SetMinimumSeverityFilter(MobileShaderHazardSeverity severity)
        {
            _minFilterSeverity = severity;
            RefreshUiToolkitView();
        }

        public List<ShaderSourceAuditResult> GetFilteredResults()
        {
            var filtered = new List<ShaderSourceAuditResult>(_results.Count);
            string q = (_searchFilter ?? string.Empty).Trim();

            for (int i = 0; i < _results.Count; i++)
            {
                ShaderSourceAuditResult r = _results[i];
                if (r == null || r.severity < _minFilterSeverity)
                    continue;

                if (!string.IsNullOrEmpty(q))
                {
                    bool match =
                        (!string.IsNullOrEmpty(r.shaderName) && r.shaderName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(r.assetPath) && r.assetPath.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!match && r.diagnostics != null)
                    {
                        for (int d = 0; d < r.diagnostics.Count; d++)
                        {
                            if (r.diagnostics[d] != null && r.diagnostics[d].IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                match = true;
                                break;
                            }
                        }
                    }

                    if (!match)
                        continue;
                }

                filtered.Add(r);
            }

            return filtered;
        }

        public void RefreshUiToolkitView()
        {
            if (_heatmapContainer == null || _resultsListScroll == null)
                return;

            _heatmapContainer.Clear();
            _resultsListScroll.Clear();

            ShaderGuardHeatmapSummary heatmap = MobileShaderGuardMcpBridge.ComputeHeatmapSummary(_results);
            _heatmapContainer.Add(BuildHeatmapVisualElement(heatmap));

            List<ShaderSourceAuditResult> filtered = GetFilteredResults();
            for (int i = 0; i < filtered.Count; i++)
            {
                _resultsListScroll.Add(BuildShaderAuditCard(filtered[i]));
            }
        }

        public static VisualElement BuildHeatmapVisualElement(ShaderGuardHeatmapSummary h)
        {
            var box = new VisualElement { name = "heatmap-summary-card" };
            box.style.paddingTop = 8f;
            box.style.paddingBottom = 8f;
            box.style.paddingLeft = 10f;
            box.style.paddingRight = 10f;
            box.style.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f);

            var statsLabel = new Label(
                $"Severity Heatmap — Audited: {h.totalShaders} | Critical: {h.criticalCount} | Warning: {h.warningCount} | " +
                $"Advisory: {h.advisoryCount} | Good: {h.goodCount} | Avg Readiness: {h.averageReadinessScore:F1}/100 | Avg FP16: {(h.averageFp16Ratio * 100f):F0}%")
            {
                name = "heatmap-stats-label"
            };
            statsLabel.style.marginBottom = 6f;
            box.Add(statsLabel);

            // Stacked horizontal severity distribution heatmap bar
            var barTrack = new VisualElement { name = "heatmap-distribution-bar" };
            barTrack.style.flexDirection = FlexDirection.Row;
            barTrack.style.height = 14f;
            barTrack.style.marginBottom = 6f;
            barTrack.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 1f);

            if (h.totalShaders > 0)
            {
                AddHeatmapSegment(barTrack, "segment-critical", h.criticalRatio, GetSeverityColor(MobileShaderHazardSeverity.Critical));
                AddHeatmapSegment(barTrack, "segment-warning", h.warningRatio, GetSeverityColor(MobileShaderHazardSeverity.Warning));
                AddHeatmapSegment(barTrack, "segment-advisory", h.advisoryRatio, GetSeverityColor(MobileShaderHazardSeverity.Advisory));
                AddHeatmapSegment(barTrack, "segment-good", h.goodRatio, GetSeverityColor(MobileShaderHazardSeverity.Good));
            }
            box.Add(barTrack);

            var readinessBar = new ProgressBar
            {
                name = "overall-readiness-gauge",
                lowValue = 0f,
                highValue = 100f,
                value = h.averageReadinessScore,
                title = $"Project Mobile TBDR Readiness: {h.averageReadinessScore:F1} / 100 (Clip/Discard: {h.totalClipDiscardCount} | In-Loop Tex: {h.totalInLoopTexSampleCount} | CBUFFER Mismatches: {h.mismatchedCBufferCount})"
            };
            box.Add(readinessBar);

            return box;
        }

        private static void AddHeatmapSegment(VisualElement track, string name, float ratio, Color color)
        {
            if (ratio <= 0f) return;
            var seg = new VisualElement { name = name };
            seg.style.flexGrow = ratio;
            seg.style.height = 14f;
            seg.style.backgroundColor = color;
            track.Add(seg);
        }

        private static VisualElement BuildShaderAuditCard(ShaderSourceAuditResult r)
        {
            var card = new VisualElement { name = $"shader-card-{r.shaderName}" };
            card.style.marginBottom = 6f;
            card.style.paddingTop = 6f;
            card.style.paddingBottom = 6f;
            card.style.paddingLeft = 8f;
            card.style.paddingRight = 8f;
            card.style.borderLeftWidth = 4f;
            card.style.borderLeftColor = GetSeverityColor(r.severity);
            card.style.backgroundColor = new Color(0.2f, 0.2f, 0.22f, 1f);

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.justifyContent = Justify.SpaceBetween;
            headerRow.style.alignItems = Align.Center;

            var title = new Label($"[{r.severity}] {r.shaderName} — Readiness Score: {r.mobileReadinessScore}/100")
            {
                name = "card-title"
            };
            headerRow.Add(title);

            var pingBtn = new Button(() =>
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(r.assetPath);
                if (obj != null) EditorGUIUtility.PingObject(obj);
            })
            {
                text = "Ping"
            };
            pingBtn.style.width = 56f;
            headerRow.Add(pingBtn);
            card.Add(headerRow);

            string cbufferStatus = r.hasMismatchedSrpBatcherCBuffer
                ? "MISMATCH ACROSS PASSES"
                : r.hasSrpBatcherCBuffer.ToString();

            var metaLabel = new Label(
                $"Path: {r.assetPath} | FP16: {(r.fp16Ratio * 100f):F0}% ({r.fp16DeclarationsCount} half vs {r.fp32DeclarationsCount} float) | " +
                $"Tex: {r.textureSampleCount} (In-Loop: {r.textureSampleInLoopCount}) | clip/discard: {r.alphaTestDiscardCount} | " +
                $"SV_Depth: {r.hasSvDepthOutput} | Variant Risk: ~{r.estimatedKeywordBurstRiskScore}x | SRP CBuffer: {cbufferStatus}");
            metaLabel.style.fontSize = 11f;
            metaLabel.style.marginBottom = 4f;
            card.Add(metaLabel);

            var fp16Bar = new ProgressBar
            {
                name = "fp16-ratio-bar",
                lowValue = 0f,
                highValue = 100f,
                value = r.fp16Ratio * 100f,
                title = $"FP16 Register Ratio: {(r.fp16Ratio * 100f):F0}% | Mobile Readiness: {r.mobileReadinessScore}/100"
            };
            fp16Bar.style.marginBottom = 4f;
            card.Add(fp16Bar);

            if (r.diagnostics != null)
            {
                for (int d = 0; d < r.diagnostics.Count; d++)
                {
                    var diagLabel = new Label("• " + r.diagnostics[d]);
                    diagLabel.style.whiteSpace = WhiteSpace.Normal;
                    card.Add(diagLabel);
                }
            }

            return card;
        }

        public static Color GetSeverityColor(MobileShaderHazardSeverity severity)
        {
            switch (severity)
            {
                case MobileShaderHazardSeverity.Critical:
                    return new Color(0.88f, 0.24f, 0.24f, 1f);
                case MobileShaderHazardSeverity.Warning:
                    return new Color(0.92f, 0.62f, 0.16f, 1f);
                case MobileShaderHazardSeverity.Advisory:
                    return new Color(0.28f, 0.64f, 0.92f, 1f);
                default:
                    return new Color(0.24f, 0.76f, 0.42f, 1f);
            }
        }

        private void OnGUI()
        {
            // When UI Toolkit (CreateGUI) has populated rootVisualElement, skip legacy IMGUI rendering to prevent overlap
            if (rootVisualElement != null && rootVisualElement.childCount > 0)
                return;

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

            if (GUILayout.Button("Export Markdown / CSV / MCP JSON Report", GUILayout.Height(28f)))
            {
                ExportReports();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            _minFilterSeverity = (MobileShaderHazardSeverity)EditorGUILayout.EnumPopup("Minimum Severity Filter", _minFilterSeverity);

            EditorGUILayout.Space(6f);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            List<ShaderSourceAuditResult> filtered = GetFilteredResults();
            for (int i = 0; i < filtered.Count; i++)
            {
                ShaderSourceAuditResult r = filtered[i];

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
            string jsonPath = Path.ChangeExtension(path, ".json");
            File.WriteAllText(jsonPath, MobileShaderGuardMcpBridge.ExportAuditReportToJson(_results), Encoding.UTF8);
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
