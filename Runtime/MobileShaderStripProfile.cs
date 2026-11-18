using System;
using System.Collections.Generic;
using UnityEngine;

namespace YusufOkuducu.MobileShaderGuard
{
    [Serializable]
    public struct ForbiddenKeywordPair
    {
        public string keywordA;
        public string keywordB;

        public ForbiddenKeywordPair(string a, string b)
        {
            keywordA = a;
            keywordB = b;
        }
    }

    /// <summary>
    /// Declarative configuration asset controlling which shader keyword permutations
    /// and pass types are automatically stripped during Android and iOS builds.
    /// </summary>
    [CreateAssetMenu(
        fileName = "MobileShaderStripProfile",
        menuName = "Mobile Shader Guard/Shader Strip Profile")]
    public sealed class MobileShaderStripProfile : ScriptableObject
    {
        [Header("Build Stripping Activation")]
        public bool enableStrippingOnMobileBuilds = true;
        public bool stripOnlyOnMobileTargets = true;

        [Header("Common Mobile Keyword Stripping Presets")]
        public bool stripDebugDisplayKeywords = true;
        public bool stripMainLightCascadeShadows = false;
        public bool stripAdditionalLightShadows = true;
        public bool stripSoftShadowPermutations = true;
        public bool stripScreenSpaceOcclusion = true;
        public bool stripDirectionalCombinedLightmaps = true;
        public bool stripBicubicLightmapSampling = true;
        public bool stripReflectionProbeBoxProjection = false;
        public bool stripLightLayersAndCookies = true;
        public bool stripDBufferAndDecalKeywords = true;
        public bool stripDeferredGBufferKeywords = true;
        public bool stripXrStereoKeywordsWhenNonVr = false;
        public bool stripFogExpAndLinear = false;

        [Header("Custom Keywords, Co-Occurrence Pairs & Pass Filters")]
        [Tooltip("Any variant containing ANY of these keywords will be stripped.")]
        public List<string> customForbiddenKeywords = new List<string>
        {
            "DEBUG_DISPLAY",
            "_DBUFFER_MRT1",
            "_DBUFFER_MRT2",
            "_DBUFFER_MRT3",
            "_WRITE_RENDERING_LAYERS"
        };

        [Tooltip("Variants where BOTH keywordA and keywordB are active simultaneously will be stripped.")]
        public List<ForbiddenKeywordPair> forbiddenKeywordPairs = new List<ForbiddenKeywordPair>
        {
            new ForbiddenKeywordPair("_MAIN_LIGHT_SHADOWS_CASCADE", "_ADDITIONAL_LIGHT_SHADOWS"),
            new ForbiddenKeywordPair("_ADDITIONAL_LIGHT_SHADOWS", "_SHADOWS_SOFT_HIGH")
        };

        [Tooltip("Shader passes whose name matches any entry (case-insensitive) will be stripped on mobile.")]
        public List<string> forbiddenPassNames = new List<string>
        {
            "GBuffer",
            "Deferred"
        };

        [Tooltip("Shaders whose names contain any of these substrings will be completely bypassed from stripping.")]
        public List<string> whitelistedShaderSubstrings = new List<string>
        {
            "Hidden/InternalErrorShader",
            "Sprites/Default",
            "UI/Default"
        };

        public HashSet<string> BuildEffectiveForbiddenKeywordSet()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);

            if (stripDebugDisplayKeywords)
            {
                set.Add("DEBUG_DISPLAY");
                set.Add("EDITOR_VISUALIZATION");
            }
            if (stripMainLightCascadeShadows)
            {
                set.Add("_MAIN_LIGHT_SHADOWS_CASCADE");
            }
            if (stripAdditionalLightShadows)
            {
                set.Add("_ADDITIONAL_LIGHT_SHADOWS");
            }
            if (stripSoftShadowPermutations)
            {
                set.Add("_SHADOWS_SOFT");
                set.Add("_SHADOWS_SOFT_LOW");
                set.Add("_SHADOWS_SOFT_MEDIUM");
                set.Add("_SHADOWS_SOFT_HIGH");
            }
            if (stripScreenSpaceOcclusion)
            {
                set.Add("_SCREEN_SPACE_OCCLUSION");
            }
            if (stripDirectionalCombinedLightmaps)
            {
                set.Add("DIRLIGHTMAP_COMBINED");
                set.Add("DYNAMICLIGHTMAP_ON");
            }
            if (stripBicubicLightmapSampling)
            {
                set.Add("LIGHTMAP_BICUBIC_SAMPLING");
            }
            if (stripReflectionProbeBoxProjection)
            {
                set.Add("_REFLECTION_PROBE_BOX_PROJECTION");
                set.Add("_REFLECTION_PROBE_BLENDING");
            }
            if (stripLightLayersAndCookies)
            {
                set.Add("_LIGHT_LAYERS");
                set.Add("_LIGHT_COOKIES");
                set.Add("_WRITE_RENDERING_LAYERS");
            }
            if (stripDBufferAndDecalKeywords)
            {
                set.Add("_DBUFFER_MRT1");
                set.Add("_DBUFFER_MRT2");
                set.Add("_DBUFFER_MRT3");
                set.Add("_DECAL_NORMAL_BLEND_LOW");
                set.Add("_DECAL_NORMAL_BLEND_MEDIUM");
                set.Add("_DECAL_NORMAL_BLEND_HIGH");
            }
            if (stripDeferredGBufferKeywords)
            {
                set.Add("_GBUFFER_NORMALS_OCT");
                set.Add("_RENDER_PASS_ENABLED");
            }
            if (stripXrStereoKeywordsWhenNonVr)
            {
                set.Add("STEREO_INSTANCING_ON");
                set.Add("STEREO_MULTIVIEW_ON");
                set.Add("UNITY_SINGLE_PASS_STEREO");
            }
            if (stripFogExpAndLinear)
            {
                set.Add("FOG_LINEAR");
                set.Add("FOG_EXP");
                set.Add("FOG_EXP2");
            }

            if (customForbiddenKeywords != null)
            {
                for (int i = 0; i < customForbiddenKeywords.Count; i++)
                {
                    string kw = customForbiddenKeywords[i];
                    if (!string.IsNullOrWhiteSpace(kw))
                        set.Add(kw.Trim());
                }
            }

            return set;
        }

        public bool IsShaderWhitelisted(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName) || whitelistedShaderSubstrings == null)
                return false;

            for (int i = 0; i < whitelistedShaderSubstrings.Count; i++)
            {
                string sub = whitelistedShaderSubstrings[i];
                if (!string.IsNullOrWhiteSpace(sub) &&
                    shaderName.IndexOf(sub.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsPassForbidden(string passName)
        {
            if (string.IsNullOrEmpty(passName) || forbiddenPassNames == null)
                return false;

            for (int i = 0; i < forbiddenPassNames.Count; i++)
            {
                string forbiddenPass = forbiddenPassNames[i];
                if (!string.IsNullOrWhiteSpace(forbiddenPass) &&
                    string.Equals(passName.Trim(), forbiddenPass.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Evaluates whether a specific shader variant should be stripped based on the profile's
        /// whitelist, pass filter, forbidden single keywords, and forbidden co-occurring keyword pairs.
        /// </summary>
        public bool ShouldStripVariant(
            string shaderName,
            string passName,
            IReadOnlyList<string> activeKeywords,
            HashSet<string> precomputedForbiddenSet,
            out string stripReason)
        {
            stripReason = null;

            if (!enableStrippingOnMobileBuilds)
                return false;

            if (IsShaderWhitelisted(shaderName))
                return false;

            if (IsPassForbidden(passName))
            {
                stripReason = $"ForbiddenPass:{passName}";
                return true;
            }

            if (activeKeywords == null || activeKeywords.Count == 0)
                return false;

            HashSet<string> forbidden = precomputedForbiddenSet ?? BuildEffectiveForbiddenKeywordSet();

            for (int i = 0; i < activeKeywords.Count; i++)
            {
                string kw = activeKeywords[i];
                if (!string.IsNullOrEmpty(kw) && forbidden.Contains(kw))
                {
                    stripReason = $"ForbiddenKeyword:{kw}";
                    return true;
                }
            }

            if (forbiddenKeywordPairs != null && forbiddenKeywordPairs.Count > 0 && activeKeywords.Count >= 2)
            {
                for (int p = 0; p < forbiddenKeywordPairs.Count; p++)
                {
                    ForbiddenKeywordPair pair = forbiddenKeywordPairs[p];
                    if (string.IsNullOrWhiteSpace(pair.keywordA) || string.IsNullOrWhiteSpace(pair.keywordB))
                        continue;

                    string targetA = pair.keywordA.Trim();
                    string targetB = pair.keywordB.Trim();
                    bool hasA = false;
                    bool hasB = false;

                    for (int k = 0; k < activeKeywords.Count; k++)
                    {
                        string active = activeKeywords[k];
                        if (string.Equals(active, targetA, StringComparison.Ordinal)) hasA = true;
                        if (string.Equals(active, targetB, StringComparison.Ordinal)) hasB = true;
                        if (hasA && hasB)
                        {
                            stripReason = $"ForbiddenPair:{targetA}+{targetB}";
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
