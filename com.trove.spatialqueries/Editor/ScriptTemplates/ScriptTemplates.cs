using UnityEditor;
using UnityEngine;

namespace Trove.EventSystems
{
    static class ScriptTemplates
    {
        public const string ScriptTemplatePath = "Packages/com.trove.spatialqueries/Editor/ScriptTemplates/";

        [MenuItem("Assets/Create/Trove/SpatialQueries/New BVH", priority = 1)]
        static void NewGlobalEvent()
        {
            ProjectWindowUtil.CreateScriptAssetFromTemplateFile($"{ScriptTemplatePath}BVHTemplate.txt", "NewBVH.cs");
        }
    }
}