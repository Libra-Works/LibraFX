using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    [CustomEditor(typeof(LibraGpuParticleRuntime))]
    public sealed class LibraGpuParticleRuntimeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var runtime = (LibraGpuParticleRuntime)target;
            EditorGUILayout.HelpBox("Assign an exported .compute, metadata JSON, billboard material and texture bindings by resource ID. Runtime execution is separate from source export.", MessageType.Info);
            if (runtime.IsConfigured)
                EditorGUILayout.LabelField("GPU execution", runtime.RuntimeExecuted ? "Executed" : "Configured");
        }

        [MenuItem("Assets/Libra GPU/Reimport generated shaders and metadata")]
        static void ReimportSelected()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
