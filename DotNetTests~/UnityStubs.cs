using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; } = string.Empty;
        public int GetInstanceID() => GetHashCode();
        public static void DestroyImmediate(Object obj) { }
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
    }

    public class Shader : Object
    {
        public bool isSupported { get; set; } = true;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CreateAssetMenuAttribute : Attribute
    {
        public string fileName { get; set; }
        public string menuName { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public static class JsonUtility
    {
        public static string ToJson(object obj, bool prettyPrint = false) => "{}";
    }

    public static class Debug
    {
        public static void Log(object message) { }
    }

    public sealed class GUIContent
    {
        public static readonly GUIContent none = new GUIContent();
    }

    public sealed class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
    }

    public sealed class GUISkin
    {
        public GUIStyle box { get; set; } = new GUIStyle();
        public GUIStyle label { get; set; } = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin { get; } = new GUISkin();
    }

    public sealed class GUILayoutOption { }

    public static class GUILayout
    {
        public static bool Button(string text, params GUILayoutOption[] options) => false;
        public static GUILayoutOption Height(float h) => new GUILayoutOption();
        public static GUILayoutOption Width(float w) => new GUILayoutOption();
    }
}

namespace UnityEngine.Rendering
{
    public struct ShaderKeyword
    {
        public string name { get; set; }
        public static string GetKeywordName(UnityEngine.Shader shader, ShaderKeyword keyword) => keyword.name;
    }

    public struct ShaderKeywordSet
    {
        public ShaderKeyword[] Keywords { get; set; }
        public ShaderKeyword[] GetShaderKeywords() => Keywords ?? Array.Empty<ShaderKeyword>();
    }
}

namespace UnityEditor
{
    public enum BuildTarget
    {
        StandaloneWindows64 = 19,
        iOS = 9,
        Android = 13
    }

    public enum MessageType
    {
        None,
        Info,
        Warning,
        Error
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
    }

    public class EditorWindow : UnityEngine.ScriptableObject
    {
        public UnityEngine.Vector2 minSize { get; set; }
        public static T GetWindow<T>(string title) where T : EditorWindow, new() => new T();
        public void Show() { }
        public void Repaint() { }
    }

    public static class EditorStyles
    {
        public static UnityEngine.GUIStyle boldLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle miniLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle wordWrappedMiniLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle helpBox { get; } = new UnityEngine.GUIStyle();
    }

    public static class EditorGUILayout
    {
        public static void Space(float width = 6f) { }
        public static void LabelField(string label, params object[] args) { }
        public static void HelpBox(string message, MessageType type) { }
        public static void BeginHorizontal() { }
        public static void EndHorizontal() { }
        public static void BeginVertical(params object[] args) { }
        public static void EndVertical() { }
        public static UnityEngine.Vector2 BeginScrollView(UnityEngine.Vector2 pos) => pos;
        public static void EndScrollView() { }
        public static Enum EnumPopup(string label, Enum selected) => selected;
    }

    public static class EditorGUIUtility
    {
        public static void PingObject(UnityEngine.Object obj) { }
    }

    public static class EditorUtility
    {
        public static string SaveFilePanel(string title, string directory, string defaultName, string extension) => string.Empty;
        public static void RevealInFinder(string path) { }
    }

    public static class Selection
    {
        public static UnityEngine.Object activeObject { get; set; }
    }

    public static class EditorUserBuildSettings
    {
        public static BuildTarget activeBuildTarget { get; set; } = BuildTarget.Android;
    }

    public static class AssetDatabase
    {
        public static string[] FindAssets(string filter) => Array.Empty<string>();
        public static string GUIDToAssetPath(string guid) => string.Empty;
        public static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object => null;
        public static UnityEngine.Object LoadMainAssetAtPath(string assetPath) => null;
        public static void CreateAsset(UnityEngine.Object asset, string path) { }
        public static void SaveAssets() { }
    }
}

namespace UnityEditor.Build
{
    public interface IOrderedCallback
    {
        int callbackOrder { get; }
    }

    public interface IPostprocessBuildWithReport : IOrderedCallback
    {
        void OnPostprocessBuild(Reporting.BuildReport report);
    }
}

namespace UnityEditor.Build.Reporting
{
    public struct BuildSummary
    {
        public BuildTarget platform;
    }

    public sealed class BuildReport
    {
        public BuildSummary summary;
    }
}

namespace UnityEditor.Rendering
{
    public struct ShaderSnippetData
    {
        public string passName { get; set; }
    }

    public struct ShaderCompilerData
    {
        public UnityEngine.Rendering.ShaderKeywordSet shaderKeywordSet;
    }

    public interface IPreprocessShaders : Build.IOrderedCallback
    {
        void OnProcessShader(UnityEngine.Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data);
    }
}
