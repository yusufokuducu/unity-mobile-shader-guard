using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace YusufOkuducu.MobileShaderGuard.Editor
{
    [Serializable]
    public sealed class StrippedShaderEntry
    {
        public string shaderName;
        public int variantsBefore;
        public int variantsAfter;
        public int strippedCount;
    }

    [Serializable]
    public sealed class MobileShaderStripBuildReport
    {
        public string buildTimeUtc;
        public string targetPlatform;
        public int totalVariantsBefore;
        public int totalVariantsAfter;
        public int totalVariantsStripped;
        public List<StrippedShaderEntry> entries = new List<StrippedShaderEntry>();
    }

    /// <summary>
    /// Hooks into Unity's shader build pipeline (IPreprocessShaders) to strip forbidden mobile shader
    /// keyword permutations and pass types, outputting an audit report to Logs/MobileShaderGuardStripReport.json.
    /// </summary>
    public sealed class MobileShaderVariantStripper : IPreprocessShaders, IPostprocessBuildWithReport
    {
        public int callbackOrder => 100;

        private static readonly Dictionary<string, StrippedShaderEntry> SessionEntries =
            new Dictionary<string, StrippedShaderEntry>(StringComparer.Ordinal);

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (shader == null || data == null || data.Count == 0)
                return;

            MobileShaderStripProfile profile = FindActiveProfile();
            if (profile == null || !profile.enableStrippingOnMobileBuilds)
                return;

            if (profile.stripOnlyOnMobileTargets && !IsMobileBuildTarget(EditorUserBuildSettings.activeBuildTarget))
                return;

            string shaderName = shader.name;
            if (profile.IsShaderWhitelisted(shaderName))
                return;

            string passName = snippet.passName;
            int beforeCount = data.Count;

            if (profile.IsPassForbidden(passName))
            {
                data.Clear();
                RecordStats(shaderName, beforeCount, 0);
                return;
            }

            HashSet<string> forbidden = profile.BuildEffectiveForbiddenKeywordSet();
            var activeKeywordBuffer = new List<string>(16);

            for (int i = data.Count - 1; i >= 0; i--)
            {
                ExtractActiveKeywordNames(shader, data[i].shaderKeywordSet, activeKeywordBuffer);
                if (profile.ShouldStripVariant(shaderName, passName, activeKeywordBuffer, forbidden, out _))
                {
                    data.RemoveAt(i);
                }
            }

            int afterCount = data.Count;
            RecordStats(shaderName, beforeCount, afterCount);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (SessionEntries.Count == 0)
                return;

            var summary = new MobileShaderStripBuildReport
            {
                buildTimeUtc = DateTime.UtcNow.ToString("O"),
                targetPlatform = report.summary.platform.ToString()
            };

            foreach (var kvp in SessionEntries)
            {
                summary.totalVariantsBefore += kvp.Value.variantsBefore;
                summary.totalVariantsAfter += kvp.Value.variantsAfter;
                summary.totalVariantsStripped += kvp.Value.strippedCount;
                summary.entries.Add(kvp.Value);
            }

            summary.entries.Sort((a, b) => b.strippedCount.CompareTo(a.strippedCount));

            string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            string outPath = Path.Combine(logDir, "MobileShaderGuardStripReport.json");
            File.WriteAllText(outPath, JsonUtility.ToJson(summary, true));

            Debug.Log(
                $"[MobileShaderGuard] Stripped {summary.totalVariantsStripped} / {summary.totalVariantsBefore} " +
                $"shader variants ({(summary.totalVariantsBefore > 0 ? (summary.totalVariantsStripped * 100f / summary.totalVariantsBefore) : 0f):F1}%). " +
                $"Report saved to {outPath}");

            SessionEntries.Clear();
        }

        private static void ExtractActiveKeywordNames(
            Shader shader,
            ShaderKeywordSet keywordSet,
            List<string> outNames)
        {
            outNames.Clear();
            ShaderKeyword[] activeKeywords = keywordSet.GetShaderKeywords();
            for (int i = 0; i < activeKeywords.Length; i++)
            {
                string kwName = ShaderKeyword.GetKeywordName(shader, activeKeywords[i]);
                if (string.IsNullOrEmpty(kwName))
                    kwName = activeKeywords[i].name;

                if (!string.IsNullOrEmpty(kwName))
                    outNames.Add(kwName);
            }
        }

        public static bool IsMobileBuildTarget(BuildTarget target)
        {
            return target == BuildTarget.Android || target == BuildTarget.iOS;
        }

        private static MobileShaderStripProfile FindActiveProfile()
        {
            string[] guids = AssetDatabase.FindAssets("t:MobileShaderStripProfile");
            if (guids == null || guids.Length == 0)
                return null;
            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<MobileShaderStripProfile>(path);
        }

        private static void RecordStats(string shaderName, int before, int after)
        {
            if (!SessionEntries.TryGetValue(shaderName, out StrippedShaderEntry entry))
            {
                entry = new StrippedShaderEntry { shaderName = shaderName };
                SessionEntries[shaderName] = entry;
            }
            entry.variantsBefore += before;
            entry.variantsAfter += after;
            entry.strippedCount += (before - after);
        }
    }
}
