#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    public static class LibraFluid2DDrawConsumersVerification
    {
        public static async void Run() => EditorApplication.Exit(await Verify());
        internal static async Task<int> Verify()
        {
            var fixtures = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_FIXTURES");
            var output = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            var folder = "Assets/LibraFluidDrawVerification";
            GameObject owner = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D image = null;
            Mesh fixtureMesh = null; Material fixtureMaterial = null; LiberaFxUnityBindingAsset bindings = null;
            var created = false;
            var exit = 0;
            try
            {
                Require(Path.IsPathRooted(fixtures) && Path.IsPathRooted(output) && !Directory.Exists(folder), "Fresh absolute verification paths required.");
                Directory.CreateDirectory(folder); created = true;
                Directory.CreateDirectory(output);
                cameraObject = new GameObject("Ordinary generated draw camera") { tag = "MainCamera" };
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -200);
                camera.orthographic = true; camera.orthographicSize = 60;
                camera.nearClipPlane = .1f; camera.farClipPlane = 500;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 8;
                target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                target.Create(); camera.targetTexture = target;
                image = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                foreach (var mode in new[] { "generated-sprite", "generated-flexible", "generated-material", "generated-material-dye", "generated-material-fields" })
                {
                    File.Copy(Path.Combine(fixtures, mode + ".lfxp"), folder + "/" + mode + ".lfxp");
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    var package = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(folder + "/" + mode + ".lfxp");
                    Require(package, "Compiled package import failed.");
                    if (mode.StartsWith("generated-material", StringComparison.Ordinal))
                    {
                        fixtureMaterial = new Material(Resources.Load<Material>("LibraGeneratedTextureMaterial"));
                        fixtureMaterial.SetFloat("_LibraGeneratedPremultiplied", mode.EndsWith("-dye", StringComparison.Ordinal) ? 1 : 0);
                        fixtureMaterial.SetFloat("_LibraGeneratedFields", mode.EndsWith("-fields", StringComparison.Ordinal) ? 1 : 0);
                        fixtureMesh = new Mesh();
                        fixtureMesh.vertices = new[] { new Vector3(-50,-50,0), new Vector3(50,-50,0), new Vector3(50,50,0), new Vector3(-50,50,0) };
                        fixtureMesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                        fixtureMesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                        fixtureMesh.triangles = new[] { 0,2,1,0,3,2 }; fixtureMesh.RecalculateBounds();
                        bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                        var serialized = new SerializedObject(bindings);
                        var assets = serialized.FindProperty("renderAssets"); assets.arraySize = 1;
                        var asset = assets.GetArrayElementAtIndex(0);
                        asset.FindPropertyRelative("assetIdentity").stringValue = "/Game/LibraTests/GeneratedMesh.GeneratedMesh";
                        asset.FindPropertyRelative("mesh").objectReferenceValue = fixtureMesh;
                        asset.FindPropertyRelative("defaultMaterials").arraySize = 1;
                        asset.FindPropertyRelative("defaultMaterials").GetArrayElementAtIndex(0).objectReferenceValue = fixtureMaterial;
                        asset.FindPropertyRelative("defaultTintPropertyNames").arraySize = 1;
                        asset.FindPropertyRelative("defaultTintPropertyNames").GetArrayElementAtIndex(0).stringValue = "_Color";
                        var materials = serialized.FindProperty("materials"); materials.arraySize = 1;
                        var binding = materials.GetArrayElementAtIndex(0);
                        binding.FindPropertyRelative("assetIdentity").stringValue = "/Game/LibraTests/GeneratedMaterial.GeneratedMaterial";
                        binding.FindPropertyRelative("material").objectReferenceValue = fixtureMaterial;
                        binding.FindPropertyRelative("tintPropertyName").stringValue = "_Color";
                        var mappings = binding.FindPropertyRelative("parameterMappings"); mappings.arraySize = 1;
                        mappings.GetArrayElementAtIndex(0).FindPropertyRelative("authoredName").stringValue = "FluidTexture";
                        mappings.GetArrayElementAtIndex(0).FindPropertyRelative("unityPropertyName").stringValue = "_MainTex";
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                    owner = new GameObject("Ordinary generated draw owner") { layer = 8 };
                    owner.SetActive(false);
                    var player = owner.AddComponent<LiberaFxSpawnableComponent>();
                    Require(player.ConfigureForSession(package, bindings, true) == NativeResult.Ok, "Draw configuration failed.");
                    owner.SetActive(true);
                    Require(player.Play() == NativeResult.Ok, "Play failed: " + player.LastError);
                    for (var frame = 1; frame <= 30; frame++)
                    {
                        Require(player.AdvancePreview(frame / 60d) == NativeResult.Ok, "Draw advance failed: " + player.LastError);
                        await NextFrame();
                    }
                    var colored = Capture(camera, target, image, mode.EndsWith("-fields", StringComparison.Ordinal));
                    Require(colored > 10, mode + " failed to draw generated color: " + colored);
                    File.WriteAllBytes(Path.Combine(output, mode + ".png"), image.EncodeToPNG());
                    Require(player.Stop() == NativeResult.Ok, "Draw Stop failed.");
                    await NextFrame();
                    Require(Capture(camera, target, image, mode.EndsWith("-fields", StringComparison.Ordinal)) == 0, "Producer retirement left ordinary draw pixels.");
                    Require(player.Play() == NativeResult.Ok, "Draw replay failed.");
                    await NextFrame();
                    Require(Capture(camera, target, image, mode.EndsWith("-fields", StringComparison.Ordinal)) == 0, "Draw replay retained stale producer contents.");
                    for (var frame = 1; frame <= 30; frame++)
                    {
                        Require(player.AdvancePreview(frame / 60d) == NativeResult.Ok, "Draw replay frame failed.");
                        await NextFrame();
                    }
                    Require(Capture(camera, target, image, mode.EndsWith("-fields", StringComparison.Ordinal)) == colored, "Ordinary draw replay is not deterministic.");
                    Require(player.Stop() == NativeResult.Ok, "Final Stop failed.");
                    UnityEngine.Object.DestroyImmediate(owner); owner = null;
                    if (bindings) UnityEngine.Object.DestroyImmediate(bindings); bindings = null;
                    if (fixtureMesh) UnityEngine.Object.DestroyImmediate(fixtureMesh); fixtureMesh = null;
                    if (fixtureMaterial) UnityEngine.Object.DestroyImmediate(fixtureMaterial); fixtureMaterial = null;
                    File.WriteAllText(Path.Combine(output, mode + "-proof.txt"), "PASS compiler package, ordinary generated main texture, visible pixels=" + colored + ", Stop, clear restart, deterministic replay.");
                    Debug.Log("PASS ordinary generated texture: " + mode + ", pixels=" + colored);
                }
            }
            catch (Exception error) { Debug.LogException(error); exit = 1; }
            finally
            {
                RenderTexture.active = null;
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (image) UnityEngine.Object.DestroyImmediate(image);
                if (bindings) UnityEngine.Object.DestroyImmediate(bindings);
                if (fixtureMesh) UnityEngine.Object.DestroyImmediate(fixtureMesh);
                if (fixtureMaterial) UnityEngine.Object.DestroyImmediate(fixtureMaterial);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (created) AssetDatabase.DeleteAsset(folder);
            }
            return exit;
        }
        private static int Capture(Camera camera, RenderTexture target, Texture2D image, bool anyColor)
        {
            camera.Render(); RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); image.Apply();
            RenderTexture.active = null;
            var colored = 0;
            foreach (var pixel in image.GetPixels())
                if (anyColor ? Math.Max(pixel.r, Math.Max(pixel.g, pixel.b)) > .01f : pixel.r > .01f && pixel.r > pixel.g * 1.4f && pixel.g > pixel.b) colored++;
            return colored;
        }
        private static Task NextFrame()
        {
            var result = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () => result.SetResult(true);
            EditorApplication.QueuePlayerLoopUpdate();
            return result.Task;
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
