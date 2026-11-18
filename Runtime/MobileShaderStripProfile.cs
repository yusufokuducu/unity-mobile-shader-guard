using System;
using System.Collections.Generic;
using UnityEngine;

namespace YusufOkuducu.MobileShaderGuard
{
    /// <summary>
    /// Declarative configuration asset controlling which shader keyword permutations
    /// are automatically stripped during Android and iOS builds.
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
        public bool stripFogExpAndLinear = false;

        [Header("Custom Keywords & Shaders")]
        [Tooltip("Any variant containing ANY of these keywords will be stripped.")]
        public List<string> customForbiddenKeywords = new List<string>
        {
            "DEBUG_DISPLAY",
            "_DBUFFER_MRT1",
            "_DBUFFER_MRT2",
            "_DBUFFER_MRT3",
            "_WRITE_RENDERING_LAYERS"
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
                set.Add(" EDITOR_VISUALIZATION");
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
    }
}
