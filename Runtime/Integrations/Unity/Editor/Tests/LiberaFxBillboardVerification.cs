using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxBillboardVerification
    {
        [Serializable] private sealed class Expected { public Frame[] frames; }
        [Serializable] private sealed class Frame { public double timeSeconds; public Pose[] nodes; }
        [Serializable] private sealed class Pose
        {
            public string nodeId;
            public double[] position, scale, rotation, forward, up;
            public bool tree;
        }

        public static void Run() => Verify(true);

        public static void RunPoses() => Verify(false);

        private static void Verify(bool capturePixels)
        {
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_BILLBOARD_FIXTURE");
            if (string.IsNullOrEmpty(fixture) || !File.Exists(fixture))
                throw new InvalidOperationException("Set LIBERAFX_BILLBOARD_FIXTURE to an isolated moving-camera.lfxp.");
            var previewScene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var expected = JsonUtility.FromJson<Expected>(File.ReadAllText(Path.ChangeExtension(fixture, "expected.json")));
            var asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
            var material = new Material(Shader.Find("Unlit/Color"));
            material.color = Color.white;
            var owner = new GameObject("LIBERA FX isolated Billboard verification");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, previewScene);
            owner.SetActive(false);
            var cameraObject = new GameObject("Billboard verification camera");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.scene = previewScene;
            camera.overrideSceneCullingMask =
                UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(previewScene);
            camera.transform.position = new Vector3(25, -35, 25);
            camera.transform.LookAt(new Vector3(5, 0, 2), Vector3.up);
            camera.orthographic = true;
            camera.orthographicSize = 11;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var target = new RenderTexture(640, 480, 24);
            var pixels = new Texture2D(640, 480, TextureFormat.RGB24, false);
            var component = owner.AddComponent<LiberaFxSpawnableComponent>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var asyncCompilation = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                ShaderUtil.CompilePass(material, 0, true);
                Require(material.shader.isSupported && !ShaderUtil.ShaderHasError(material.shader),
                    "The verification material shader must compile on the active graphics device.");
                asset.SetPackageBytes(File.ReadAllBytes(fixture));
                var settings = new SerializedObject(bindings);
                var entries = settings.FindProperty("renderAssets");
                entries.arraySize = 1;
                var entry = entries.GetArrayElementAtIndex(0);
                entry.FindPropertyRelative("assetIdentity").stringValue = "/Engine/BasicShapes/Cube.Cube";
                entry.FindPropertyRelative("mesh").objectReferenceValue = mesh;
                var defaults = entry.FindPropertyRelative("defaultMaterials");
                defaults.arraySize = 1;
                defaults.GetArrayElementAtIndex(0).objectReferenceValue = material;
                var tintNames = entry.FindPropertyRelative("defaultTintPropertyNames");
                tintNames.arraySize = 1;
                tintNames.GetArrayElementAtIndex(0).stringValue = "_Color";
                settings.ApplyModifiedPropertiesWithoutUndo();
                typeof(LiberaFxSpawnableComponent).GetField("effect", flags).SetValue(component, asset);
                typeof(LiberaFxSpawnableComponent).GetField("engineBindings", flags).SetValue(component, bindings);
                typeof(LiberaFxSpawnableComponent).GetField("playOnEnable", flags).SetValue(component, false);
                owner.SetActive(true);
                Require(component.Play() == NativeResult.Ok, "Package playback failed.");
                var update = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate", flags);
                foreach (var frame in expected.frames)
                {
                    if (frame.timeSeconds > 0)
                    {
                        typeof(LiberaFxSpawnableComponent).GetField("elapsed", flags)
                            .SetValue(component, frame.timeSeconds - Time.deltaTime);
                        update.Invoke(component, null);
                    }
                    Require(component.ActiveRenderScopeCount == 1, "The root scope must remain alive without LifeTime.");
                    foreach (var pose in frame.nodes)
                    {
                        var renderer = owner.GetComponentsInChildren<MeshRenderer>()
                            .Single(value => value.gameObject.name == "Libera FX Model " + pose.nodeId);
                        var transform = renderer.transform;
                        Require(Vector3.Distance(transform.localPosition, Vector(pose.position)) < 1e-4f,
                            "Rendered position differs from the compiler fixture.");
                        Require(Vector3.Distance(transform.localScale, Vector(pose.scale)) < 1e-4f,
                            "Rendered scale differs from the compiler fixture.");
                        Require(Vector3.Dot(transform.localRotation * Vector3.right, Vector(pose.forward)) > 0.99999f,
                            "World LookAt forward was changed by Unity Euler ordering or a second billboard rotation.");
                        Require(Vector3.Dot(transform.localRotation * Vector3.forward, Vector(pose.up)) > 0.99999f,
                            "Rendered Up/roll differs from the native world-matrix convention.");
                        if (pose.tree)
                            Require(Vector3.Dot(transform.localRotation * Vector3.forward, Vector3.forward) > 0.99999f,
                                "Tree Billboard did not preserve World Up.");
                    }
                    if (!capturePixels) continue;
                    camera.targetTexture = target;
                    camera.Render();
                    var previous = RenderTexture.active;
                    try
                    {
                        RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                        pixels.Apply();
                        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(fixture),
                            "unity-billboard-" + frame.timeSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".png"),
                            pixels.EncodeToPNG());
                        Require(pixels.GetPixels32().Count(pixel => pixel.r + pixel.g + pixel.b > 30) > 100,
                            "The actual camera render contains no visible Billboard meshes.");
                    }
                    finally { RenderTexture.active = previous; }
                }
                Require(!component.IsRootFinished, "Billboards must not add an implicit lifetime.");
                File.WriteAllText(fixture + ".unity-result.txt",
                    capturePixels
                        ? "PASS: production package, actual meshes/pixels, world orientation, Tree Up, no double rotation, no implicit lifetime.\n"
                        : "PASS: production package and actual component poses at every fixture time. Pixel rendering was not tested.\n");
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncCompilation;
                component.Stop();
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(pixels);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(bindings);
                UnityEngine.Object.DestroyImmediate(asset);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        private static Vector3 Vector(double[] value) => new Vector3((float)value[0], (float)value[1], (float)value[2]);
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
