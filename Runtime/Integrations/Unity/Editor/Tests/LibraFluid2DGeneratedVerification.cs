#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    public static class LibraFluid2DGeneratedVerification
    {
        public static async void Run() => EditorApplication.Exit(await Verify());
        internal static async Task<int> Verify()
        {
            const string folder = "Assets/LibraGeneratedVerification";
            var fixtures = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_FIXTURES");
            var output = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            GameObject owner = null, cameraObject = null;
            Material material = null;
            LiberaFxUnityBindingAsset bindings = null;
            RenderTexture target = null;
            Texture2D capture = null;
            var created = false;
            var exitCode = 0;
            try
            {
                Require(Path.IsPathRooted(fixtures) && Path.IsPathRooted(output) && !Directory.Exists(folder), "Fresh verification paths required.");
                Directory.CreateDirectory(folder); created = true;
                var json = File.ReadAllText(Path.Combine(fixtures, "generated-output.json"));
                LibraFluid2DManifestValidation.Validate(json);
                Reject(json.Replace("\"version\":3", "\"version\":2"), "Version 2 cannot accept generated resources.");
                Reject(json.Replace("\"automaticPlane\":false", "\"automaticPlane\":null"), "Plane selection must be a boolean.");
                Reject(json.Replace("\"outputId\":\"DyeOutput\"", "\"outputId\":\"Unknown\""), "Unknown outputs must fail preflight.");
                var producer = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(json).fluidPrograms[0].nodeId;
                Reject(json.Replace("\"producerNodeId\":\"" + producer + "\"", "\"producerNodeId\":\"" + Guid.NewGuid() + "\""),
                    "A generated reference cannot resolve outside its scope.");
                var manifest = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(json);
                var particle = manifest.programs[0];
                File.WriteAllText(folder + "/Particle.compute", particle.unityShaderSource);
                File.WriteAllText(folder + "/Particle.json", particle.unityMetadataJson);
                File.Copy(Path.Combine(fixtures, "generated-output.lfxp"), folder + "/Effect.lfxp");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(folder + "/Particle.compute");
                var metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/Particle.json");
                var package = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(folder + "/Effect.lfxp");
                Require(shader && metadata && package, "Compiler assets failed to import.");
                foreach (var message in ShaderUtil.GetComputeShaderMessages(shader))
                    Require(message.severity.ToString() != "Error", message.message);
                material = new Material(Shader.Find("Libra/GPU Particle Billboard")) { enableInstancing = true };
                bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                var serialized = new SerializedObject(bindings);
                var programs = serialized.FindProperty("gpuPrograms"); programs.arraySize = 1;
                var binding = programs.GetArrayElementAtIndex(0);
                binding.FindPropertyRelative("nodeId").stringValue = particle.nodeId;
                binding.FindPropertyRelative("computeShader").objectReferenceValue = shader;
                binding.FindPropertyRelative("metadata").objectReferenceValue = metadata;
                binding.FindPropertyRelative("billboardMaterial").objectReferenceValue = material;
                binding.FindPropertyRelative("spawnRate").floatValue = 64;
                binding.FindPropertyRelative("rendererMaterialIdentity").stringValue = particle.unreal.rendererBindings[0].material;
                binding.FindPropertyRelative("sizeScale").floatValue = (float)particle.unreal.rendererBindings[0].sizeScale;
                binding.FindPropertyRelative("textures").arraySize = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                owner = new GameObject("Generated texture verification") { layer = 8 };
                owner.SetActive(false);
                var player = owner.AddComponent<LiberaFxSpawnableComponent>();
                Require(player.ConfigureForSession(package, bindings, true) == NativeResult.Ok, "Generated package configuration failed.");
                owner.SetActive(true);
                Require(player.Play() == NativeResult.Ok, "Generated package Play failed: " + player.LastError);
                Require(player.RenderStatistics.activeSubmissions == 0, "Consumed fluid must suppress its automatic plane.");
                for (var i = 1; i <= 30; i++)
                {
                    Require(player.AdvancePreview(i / 60d) == NativeResult.Ok, "Generated frame failed: " + player.LastError);
                    await NextFrame();
                }
                cameraObject = new GameObject("Generated texture camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -200);
                camera.orthographic = true; camera.orthographicSize = 60;
                camera.nearClipPlane = .1f; camera.farClipPlane = 500;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 8;
                target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                target.Create(); camera.targetTexture = target;
                capture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                Require(player.AdvancePreview(.5) == NativeResult.Ok, "Paused consumer refresh failed.");
                var runtime = owner.GetComponentInChildren<LibraGpuParticleRuntime>();
                runtime.StepSpawnable(0, Vector3.zero);
                var colored = Capture(camera, target, capture);
                Require(colored > 10, "Generated dye was not visible through the particle consumer: " + colored);
                File.WriteAllBytes(Path.Combine(output, "generated-consumer.png"), capture.EncodeToPNG());
                Require(player.Stop() == NativeResult.Ok, "Stop failed.");
                await NextFrame();
                Require(Capture(camera, target, capture) == 0, "Stop left a generated consumer visible.");
                Require(player.Play() == NativeResult.Ok, "Restart failed.");
                await NextFrame();
                Require(Capture(camera, target, capture) == 0, "Restart retained previous generated output.");
                for (var i = 1; i <= 30; i++)
                {
                    Require(player.AdvancePreview(i / 60d) == NativeResult.Ok, "Restart frame failed.");
                    await NextFrame();
                }
                owner.GetComponentInChildren<LibraGpuParticleRuntime>().StepSpawnable(0, Vector3.zero);
                Require(Capture(camera, target, capture) == colored, "Restart did not produce deterministic generated output.");
                player.Stop();
                File.WriteAllText(Path.Combine(output, "generated-consumer-proof.txt"), "PASS: compiler v3 package, no static texture binding, suppressed source plane, generated GPU texture billboard, Stop, clear restart, deterministic replay; colored pixels=" + colored + "\n");
                Debug.Log("LIBERAFX GENERATED GPU TEXTURE PASS pixels=" + colored);
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            finally
            {
                RenderTexture.active = null;
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (material) UnityEngine.Object.DestroyImmediate(material);
                if (bindings) UnityEngine.Object.DestroyImmediate(bindings);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (capture) UnityEngine.Object.DestroyImmediate(capture);
                if (created) AssetDatabase.DeleteAsset(folder);
            }
            return exitCode;
        }

        private static Task NextFrame()
        {
            var completion = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () => completion.SetResult(true);
            EditorApplication.QueuePlayerLoopUpdate();
            return completion.Task;
        }

        private static int Capture(Camera camera, RenderTexture target, Texture2D capture)
        {
            camera.Render(); RenderTexture.active = target;
            capture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); capture.Apply();
            RenderTexture.active = null;
            var colored = 0;
            foreach (var pixel in capture.GetPixels())
                if (pixel.r > .01f && pixel.r > pixel.g * 1.4f && pixel.g > pixel.b) colored++;
            return colored;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Reject(string json, string message)
        {
            try { LibraFluid2DManifestValidation.Validate(json); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(message);
        }
    }
}
#endif
