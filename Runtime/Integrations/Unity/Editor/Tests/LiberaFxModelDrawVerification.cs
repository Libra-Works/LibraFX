using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxModelDrawVerification
    {
        public static void RunBatch() => Run(false);
        public static void RunMaterialAnimationBatch() => Run(true);

        private static void Run(bool materialAnimation)
        {
            RequireIsolatedProject();
            var directory = Environment.GetEnvironmentVariable("LIBERAFX_MODEL_DRAW_FIXTURE_DIR");
            Require(Path.IsPathRooted(directory ?? ""), "Supply the absolute compiler-produced model fixture directory.");
            var folder = materialAnimation ? "Assets/LiberaFxMaterialSample" : "Assets/LiberaFxModelDrawSample";
            var name = materialAnimation ? "MaterialPulse" : "Cube";
            Require(!AssetDatabase.IsValidFolder(folder), "Refusing to overwrite an existing ModelDraw sample.");
            var bytes = File.ReadAllBytes(Path.Combine(directory, name + ".lfxp"));
            Require(bytes.Length <= 1024 * 1024, "Unexpected model fixture size.");
            var shader = Shader.Find("Unlit/Color");
            Require(shader, "The built-in Unlit/Color shader is unavailable.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var material = new Material(shader);
            var modelDefault = materialAnimation ? new Material(shader) { color = Color.red } : material;
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.SetActive(false);
            Object.DestroyImmediate(primitive.GetComponent<Collider>());
            primitive.GetComponent<MeshRenderer>().sharedMaterial = modelDefault;
            var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            var serialized = new SerializedObject(bindings);
            var assets = serialized.FindProperty("renderAssets");
            assets.arraySize = 1;
            var model = assets.GetArrayElementAtIndex(0);
            model.FindPropertyRelative("assetIdentity").stringValue = materialAnimation
                ? "/Engine/BasicShapes/Cube.Cube" : "/Game/LiberaFxSamples/Cube.Cube";
            model.FindPropertyRelative("mesh").objectReferenceValue = mesh;
            var defaults = model.FindPropertyRelative("defaultMaterials");
            defaults.arraySize = 1;
            defaults.GetArrayElementAtIndex(0).objectReferenceValue = modelDefault;
            var materials = serialized.FindProperty("materials");
            materials.arraySize = 1;
            var binding = materials.GetArrayElementAtIndex(0);
            binding.FindPropertyRelative("assetIdentity").stringValue = materialAnimation
                ? "/Game/LiberaFxMaterialSample/PulseMaterial.PulseMaterial" : "builtin:unlit-color";
            binding.FindPropertyRelative("material").objectReferenceValue = material;
            binding.FindPropertyRelative("tintPropertyName").stringValue = "_Color";
            var mappings = binding.FindPropertyRelative("parameterMappings");
            mappings.arraySize = 1;
            mappings.GetArrayElementAtIndex(0).FindPropertyRelative("authoredName").stringValue = "Color";
            mappings.GetArrayElementAtIndex(0).FindPropertyRelative("unityPropertyName").stringValue = "_Color";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            package.SetPackageBytes(bytes);
            var owner = new GameObject("Compiled ModelDraw " + name);
            owner.SetActive(false);
            var component = owner.AddComponent<LiberaFxSpawnableComponent>();
            serialized = new SerializedObject(component);
            serialized.FindProperty("effect").objectReferenceValue = package;
            serialized.FindProperty("engineBindings").objectReferenceValue = bindings;
            serialized.FindProperty("playOnEnable").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            owner.SetActive(true);
            Require(component.Play() == NativeResult.Ok, "Compiled model failed to play: " + component.LastError);
            var draw = component.InspectDrawNodes().Single();
            var submission = draw.Submissions.Single();
            Require(submission.enabled && submission.mesh == mesh,
                "The public component did not submit the exact model mesh.");
            Require(submission.material == material, "The selected material did not override the model default.");
            Require((draw.LocalScale - new Vector3(1, 1.5f, 0.75f)).sqrMagnitude < 0.00001f &&
                Quaternion.Angle(draw.LocalRotation, Quaternion.Euler(15, 30, 0)) < 0.01f,
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
                void Capture(int time)
                {
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                    pixels.Apply();
                    var center = pixels.GetPixel(128, 128);
                    Require(center.b > 0.5f && (materialAnimation
                        ? time == 2 ? center.r < 0.08f && center.g < 0.08f
                            : time == 1 ? center.r > 0.25f && center.r < center.b * 0.95f
                            : center.r > 0.6f && center.g > 0.6f
                        : center.g > center.r * 1.2f && center.b > center.r * 1.3f),
                        "The camera did not render the graph-authored color at " + time + ": " + center);
                    File.WriteAllBytes(Path.Combine(directory, name + ".Unity" +
                        (materialAnimation ? "." + time : "") + ".png"), pixels.EncodeToPNG());
                }
                if (materialAnimation)
                {
                    SampleAnimation(component, draw, Capture);
                }
                else
                {
                    Capture(0);
                }
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(target);
                component.Stop();
            }
            Require(component.RenderStatistics.activeSubmissions == 0 && !component.InspectDrawNodes().Any(),
                "Stopping the effect retained its model renderer.");
            AssetDatabase.CreateFolder("Assets", materialAnimation ? "LiberaFxMaterialSample" : "LiberaFxModelDrawSample");
            AssetDatabase.CreateAsset(material, folder + "/" + name + "Material.mat");
            if (materialAnimation) AssetDatabase.CreateAsset(modelDefault, folder + "/ModelDefault.mat");
            AssetDatabase.CreateAsset(bindings, folder + "/" + name + "Bindings.asset");
            AssetDatabase.CreateAsset(package, folder + "/" + name + "Package.asset");
            primitive.SetActive(true);
            PrefabUtility.SaveAsPrefabAsset(primitive, folder + "/" + name + ".prefab");
            Object.DestroyImmediate(primitive);
            AssetDatabase.SaveAssets();
            Require(EditorSceneManager.SaveScene(scene, folder + "/" + name + ".unity"), "Could not save the demo scene.");
            VerifySavedScene(materialAnimation);
            Debug.Log("LIBERA FX authored ModelDraw rendered; open " + folder + "/" + name + ".unity and press Play.");
        }

        public static void VerifySavedScene() => VerifySavedScene(false);
        public static void VerifySavedMaterialAnimation() => VerifySavedScene(true);

        private static void VerifySavedScene(bool materialAnimation)
        {
            RequireIsolatedProject();
            var scene = EditorSceneManager.OpenScene(materialAnimation
                ? "Assets/LiberaFxMaterialSample/MaterialPulse.unity" : "Assets/LiberaFxModelDrawSample/Cube.unity");
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
                var draw = component.InspectDrawNodes().Single();
                Require(draw.Submissions.Single().enabled, "The saved scene did not recreate its model draw.");
                if (materialAnimation)
                {
                    SampleAnimation(component, draw);
                }
                else
                {
                    var color = draw.Submissions.Single().properties.GetColor("_Color");
                    Require(Mathf.Abs(color.r - 0.15f) < 0.001f && Mathf.Abs(color.g - 0.65f) < 0.001f &&
                        Mathf.Abs(color.b - 1) < 0.001f, "The saved bindings lost the authored Color mapping.");
                }
            }
            finally { component.Stop(); }
            Selection.activeGameObject = component.gameObject;
            SceneView.lastActiveSceneView?.LookAt(Vector3.zero, Quaternion.Euler(25, -35, 0), 4);
            Debug.Log("LIBERA FX saved ModelDraw scene reload verified.");
        }

        private static void SampleAnimation(LiberaFxSpawnableComponent component, LiberaFxDirectRenderNodeInstance draw,
            Action<int> capture = null)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var elapsed = typeof(LiberaFxSpawnableComponent).GetField("elapsed", flags);
            var update = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate", flags);
            foreach (var time in new[] { 0, 1, 2, 4 })
            {
                elapsed.SetValue(component, time - (double)Time.deltaTime);
                update.Invoke(component, null);
                Require(string.IsNullOrEmpty(component.LastError) && Math.Abs(component.ElapsedTime - time) < 1e-8 &&
                    component.InspectDrawNodes().SingleOrDefault() == draw && draw.Submissions.Single().enabled,
                    "Forward animation must retain its model renderer: " + component.LastError);
                var actual = draw.Submissions.Single().properties.GetColor("_Color");
                var expected = (float)(0.5 + 0.5 * Math.Cos(Math.PI / 2 * time));
                Require(Mathf.Abs(actual.r - expected) < 0.001f && Mathf.Abs(actual.g - expected) < 0.001f &&
                    Mathf.Abs(actual.b - 1) < 0.001f && Mathf.Abs(actual.a - 1) < 0.001f,
                    "Compiled graph did not drive the material at " + time + ": " + actual);
                capture?.Invoke(time);
            }
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
