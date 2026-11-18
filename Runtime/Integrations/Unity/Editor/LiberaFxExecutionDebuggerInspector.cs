using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    [CustomEditor(typeof(LiberaFxRenderer))]
    public sealed class LiberaFxExecutionDebuggerInspector : UnityEditor.Editor
    {
        private string invitation = "";
        private string status = "";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (!Application.isPlaying) return;
            var renderer = (LiberaFxRenderer)target;
            EditorGUILayout.LabelField("LIBERA FX Runtime Debugger", EditorStyles.boldLabel);
            invitation = EditorGUILayout.TextField("Local invitation", invitation);
            if (GUILayout.Button("Attach local debugger"))
            {
                status = renderer.AttachExecutionDebugger(invitation.Trim()).ToString();
                invitation = "";
            }
            if (GUILayout.Button("Detach debugger"))
            {
                renderer.DetachExecutionDebugger();
                status = "";
            }
            if (status.Length > 0) EditorGUILayout.LabelField("Attach request", status);
        }

        [CustomEditor(typeof(LiberaFxSpawnableComponent))]
        public sealed class LiberaFxSpawnableDebuggerInspector : UnityEditor.Editor
        {
            private string invitation = "", container = "", instance = "", status = "";
            private ulong generation;

            public override void OnInspectorGUI()
            {
                DrawDefaultInspector();
                if (!Application.isPlaying) return;
                var component = (LiberaFxSpawnableComponent)target;
                EditorGUILayout.LabelField("LIBERA FX Package Debugger", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Leave the child fields empty for the root. For a child, use the container UUID and instance ID from this playback's Spawn event.", MessageType.Info);
                if (GUILayout.Button("Select current playback"))
                    status = component.GetDebuggerGeneration(out generation).ToString();
                EditorGUILayout.LabelField("Playback generation", generation.ToString());
                invitation = EditorGUILayout.TextField("Local invitation", invitation);
                container = EditorGUILayout.TextField("Child container UUID", container);
                instance = EditorGUILayout.TextField("Child instance ID", instance);
                using (new EditorGUI.DisabledScope(generation == 0))
                {
                    if (GUILayout.Button("Attach package debugger"))
                    {
                        if (string.IsNullOrWhiteSpace(container) && string.IsNullOrWhiteSpace(instance))
                            status = component.AttachExecutionDebugger(generation, null, 0, invitation.Trim()).ToString();
                        else if (System.Guid.TryParse(container, out var id) && id != System.Guid.Empty &&
                                 ulong.TryParse(instance, out var number) && number != 0)
                    {
                            var hex = id.ToString("N");
                            var bytes = new byte[16];
                            for (var i = 0; i < bytes.Length; i++) bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
                            status = component.AttachExecutionDebugger(generation, bytes, number, invitation.Trim()).ToString();
                    }
                        else status = "Invalid child identity";
                        invitation = "";
                    }
                }
                if (GUILayout.Button("Detach package debugger"))
                    status = component.DetachExecutionDebugger().ToString();
                if (status.Length != 0) EditorGUILayout.LabelField("Debugger request", status);
            }
        }
    }
}
