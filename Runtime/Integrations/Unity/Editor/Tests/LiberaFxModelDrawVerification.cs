using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxModelDrawVerification
    {
        public static void RunBatch()
        {
            RequireIsolatedProject();
            var directory = Environment.GetEnvironmentVariable("LIBERAFX_MODEL_DRAW_FIXTURE_DIR");
            Require(Path.IsPathRooted(directory ?? ""), "Supply the absolute compiler-produced model fixture directory.");
            const string folder = "Assets/LiberaFxModelDrawSample";
            Require(!AssetDatabase.IsValidFolder(folder), "Refusing to overwrite an existing ModelDraw sample.");
            var bytes = File.ReadAllBytes(Path.Combine(directory, "Cube.lfxp"));
            Require(bytes.Length <= 1024 * 1024, "Unexpected model fixture size.");
            var shader = Shader.Find("Unlit/Color");
            Require(shader, "The built-in Unlit/Color shader is unavailable.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var material = new Material(shader);
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.SetActive(false);
            Object.DestroyImmediate(primitive.GetComponent<Collider>());
            primitive.GetComponent<MeshRenderer>().sharedMaterial = material;
            var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            var serialized = new SerializedObject(bindings);
            var assets = serialized.FindProperty("renderAssets");
            assets.arraySize = 1;
            var model = assets.GetArrayElementAtIndex(0);
            model.FindPropertyRelative("assetIdentity").stringValue = "/Game/LiberaFxSamples/Cube.Cube";
            model.FindPropertyRelative("mesh").objectReferenceValue = mesh;
            var defaults = model.FindPropertyRelative("defaultMaterials");
            defaults.arraySize = 1;
            defaults.GetArrayElementAtIndex(0).objectReferenceValue = material;
            var materials = serialized.FindProperty("materials");
            materials.arraySize = 1;
            var binding = materials.GetArrayElementAtIndex(0);
            binding.FindPropertyRelative("assetIdentity").stringValue = "builtin:unlit-color";
            binding.FindPropertyRelative("material").objectReferenceValue = material;
            binding.FindPropertyRelative("tintPropertyName").stringValue = "_Color";
            var mappings = binding.FindPropertyRelative("parameterMappings");
            mappings.arraySize = 1;
            mappings.GetArrayElementAtIndex(0).FindPropertyRelative("authoredName").stringValue = "Color";
            mappings.GetArrayElementAtIndex(0).FindPropertyRelative("unityPropertyName").stringValue = "_Color";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            package.SetPackageBytes(bytes);
            var owner = new GameObject("Compiled ModelDraw Cube");
            owner.SetActive(false);
            var component = owner.AddComponent<LiberaFxSpawnableComponent>();
            serialized = new SerializedObject(component);
            serialized.FindProperty("effect").objectReferenceValue = package;
            serialized.FindProperty("engineBindings").objectReferenceValue = bindings;
            serialized.FindProperty("playOnEnable").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            owner.SetActive(true);
            Require(component.Play() == NativeResult.Ok, "Compiled model failed to play: " + component.LastError);
            var renderers = owner.GetComponentsInChildren<MeshRenderer>();
            Require(renderers.Length == 1 && renderers[0].GetComponent<MeshFilter>().sharedMesh == mesh,
                "The public component did not create exactly one real model renderer.");
            Require((renderers[0].transform.localScale - new Vector3(1, 1.5f, 0.75f)).sqrMagnitude < 0.00001f &&
                Quaternion.Angle(renderers[0].transform.localRotation, Quaternion.Euler(15, 30, 0)) < 0.01f,
                "Authored scale/rotation did not reach the engine model.");
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.position = new Vector3(3, 2, -4);
            cameraObject.transform.LookAt(Vector3.zero);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.025f, 0.04f);
            var target = new RenderTexture(256, 256, 24);
            var pixels = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                pixels.Apply();
                var center = pixels.GetPixel(128, 128);
                Require(center.b > 0.5f && center.g > center.r * 1.2f && center.b > center.r * 1.3f,
                    "The camera did not render the graph-authored blue Cube.");
                File.WriteAllBytes(Path.Combine(directory, "Cube.Unity.png"), pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(target);
                component.Stop();
            }
            Require(owner.GetComponentsInChildren<MeshRenderer>().Length == 0,
                "Stopping the effect retained its model renderer.");
            AssetDatabase.CreateFolder("Assets", "LiberaFxModelDrawSample");
            AssetDatabase.CreateAsset(material, folder + "/CubeMaterial.mat");
            AssetDatabase.CreateAsset(bindings, folder + "/CubeBindings.asset");
            AssetDatabase.CreateAsset(package, folder + "/CubePackage.asset");
            primitive.SetActive(true);
            PrefabUtility.SaveAsPrefabAsset(primitive, folder + "/Cube.prefab");
            Object.DestroyImmediate(primitive);
            AssetDatabase.SaveAssets();
            Require(EditorSceneManager.SaveScene(scene, folder + "/Cube.unity"), "Could not save the demo scene.");
            VerifySavedScene();
            Debug.Log("LIBERA FX authored ModelDraw Cube rendered; open " + folder + "/Cube.unity and press Play.");
        }

        public static void VerifySavedScene()
        {
            RequireIsolatedProject();
            var scene = EditorSceneManager.OpenScene("Assets/LiberaFxModelDrawSample/Cube.unity");
            LiberaFxSpawnableComponent component = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var candidate = root.GetComponent<LiberaFxSpawnableComponent>();
                if (!candidate) continue;
                Require(!component, "The saved sample contains multiple effect components.");
                component = candidate;
            }
            Require(component, "The saved sample lost its public effect component.");
            try
            {
                Require(component.Play() == NativeResult.Ok, "The saved package/bindings cannot play: " + component.LastError);
                var renderers = component.GetComponentsInChildren<MeshRenderer>();
                Require(renderers.Length == 1, "The saved scene did not recreate its model renderer.");
                var block = new MaterialPropertyBlock();
                renderers[0].GetPropertyBlock(block, 0);
                var color = block.GetColor("_Color");
                Require(Mathf.Abs(color.r - 0.15f) < 0.001f && Mathf.Abs(color.g - 0.65f) < 0.001f &&
                    Mathf.Abs(color.b - 1) < 0.001f, "The saved bindings lost the authored Color mapping.");
            }
            finally { component.Stop(); }
            Selection.activeGameObject = component.gameObject;
            SceneView.lastActiveSceneView?.LookAt(Vector3.zero, Quaternion.Euler(25, -35, 0), 4);
            Debug.Log("LIBERA FX saved ModelDraw scene reload verified.");
        }

        private static void RequireIsolatedProject()
        {
            var project = Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT");
            Require(Path.IsPathRooted(project ?? "") && string.Equals(Path.GetFullPath(project).TrimEnd('\\', '/'),
                Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase), "Use only the explicitly named isolated verification project.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
