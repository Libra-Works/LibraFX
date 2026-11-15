using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    public static class LibraGpuVerification
    {
        static string Fixture(string file)
        {
            string path = "Assets/LibraFXGPUValidation/Fixtures/" + file;
            return File.Exists(path) ? path : "Assets/Fixtures/" + file;
        }
        static string Output(string file)
        {
            string directory = Environment.GetEnvironmentVariable("LIBRA_GPU_VERIFICATION_DIR");
            return string.IsNullOrEmpty(directory) ? Path.GetFullPath(file) : Path.Combine(directory, file);
        }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static void Near(float actual, float expected, string message)
        {
            Require(Math.Abs(actual - expected) < .002f, message + ": " + actual + " expected " + expected);
        }
        public static void Run()
        {
            string output = Output("verificationresult.txt");
            GameObject emitter = null, cameraObject = null;
            Material material = null;
            RenderTexture rt = null;
            Texture2D image = null;
            try
            {
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11, "D3D11 graphics required: " + SystemInfo.graphicsDeviceType);
                var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(Fixture("Emitter.compute"));
                var metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(Fixture("Emitter.json"));
                var blue = AssetDatabase.LoadAssetAtPath<ComputeShader>(Fixture("BlueEmitter.compute"));
                var blueMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>(Fixture("BlueEmitter.json"));
                Require(compute != null && metadata != null && blue != null && blueMetadata != null, "Missing generated assets");
                foreach (var message in ShaderUtil.GetComputeShaderMessages(compute))
                    Require(message.severity.ToString() != "Error", "Compute shader import error: " + message.message);
                foreach (var message in ShaderUtil.GetComputeShaderMessages(blue))
                    Require(message.severity.ToString() != "Error", "Compute shader import error: " + message.message);
                var shader = Shader.Find("Libra/GPU Particle Billboard");
                Require(shader != null && !ShaderUtil.ShaderHasError(shader), "Billboard shader import error");
                material = new Material(shader) { enableInstancing = true };
                emitter = new GameObject("GPU verification emitter");
                var runtime = emitter.AddComponent<LibraGpuParticleRuntime>();
                var bindings = new[] { new LibraGpuTextureBinding { resourceId = "billboardTexture", texture = Texture2D.whiteTexture } };
                runtime.Configure(compute, metadata, material, bindings);
                Require(!runtime.RuntimeExecuted && runtime.Capacity == 128, "Source export is not runtime execution");
                var data = new LibraGpuParticle[128];
                runtime.Step(.1f, 0, 64, Vector3.zero);
                runtime.ReadbackForDiagnostics(data);
                for (int i = 0; i < 128; i++)
                {
                    if (i < 64)
                    {
                        Require(data[i].alive == 1, "Spawn alive " + i);
                        Near(data[i].positionAge.y, .2019f, "Position");
                        Near(data[i].positionAge.w, .1f, "Age");
                        Near(data[i].velocityLifetime.y, 2.019f, "Gravity");
                        Near(data[i].velocityLifetime.w, 2, "Lifetime");
                        Near(data[i].color.w, .95f, "Fade");
                    }
                    else Require(data[i].alive == 0, "Spawn range");
                }
                runtime.Step(2, 0, 0, Vector3.zero);
                runtime.ReadbackForDiagnostics(data);
                Require(data[0].alive == 0, "Lifetime kill");
                runtime.Step(0, 64, 64, Vector3.zero);
                runtime.ReadbackForDiagnostics(data);
                Require(data[64].alive == 1 && data[63].alive == 0, "Spawn start");
                Near(data[64].positionAge.w, 0, "Spawn age reset");
                runtime.ResetSimulation();
                runtime.StepCircular(0, 120, 16, Vector3.zero);
                runtime.ReadbackForDiagnostics(data);
                for (int i = 0; i < 128; i++)
                    Require(data[i].alive == (i < 8 || i >= 120 ? 1u : 0u), "Circular spawn " + i);
                uint firstSeed = data[0].seed;
                runtime.ResetSimulation();
                runtime.StepCircular(0, 120, 16, Vector3.zero);
                runtime.ReadbackForDiagnostics(data);
                Require(data[0].seed == firstSeed, "Reset determinism");
                data[0].positionAge.x = float.NaN;
                data[1].velocityLifetime.w = float.PositiveInfinity;
                var testBuffer = (ComputeBuffer)typeof(LibraGpuParticleRuntime)
                    .GetField("particles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(runtime);
                testBuffer.SetData(data);
                runtime.Step(0, 0, 0, Vector3.zero);
                runtime.ReadbackForDiagnostics(data);
                Require(data[0].alive == 0 && data[1].alive == 0, "Nonfinite particles must be killed");
                bool rejected = false;
                try { runtime.Step(0, 127, 2, Vector3.zero); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected, "Invalid spawn bounds accepted");
                runtime.Step(0, 128, 0, Vector3.zero);
                for (int i = 0; i < 20; i++) runtime.Step(0, 0, 0, Vector3.zero);
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) runtime.Step(0, 0, 0, Vector3.zero);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
                Require(allocated == 0, "Steady-state CPU allocation: " + allocated);
                Require(runtime.DispatchCount >= 2000 && runtime.RuntimeExecuted, "Flow dispatch status");
                cameraObject = new GameObject("GPU verification camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.orthographic = true; camera.orthographicSize = 1; camera.transform.position = new Vector3(0, 0, -5);
                emitter.layer = 8; camera.cullingMask = 1 << 8;
                rt = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                image = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                runtime.ResetSimulation();
                runtime.Step(0, 0, 64, Vector3.zero, camera);
                camera.Render();
                RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); image.Apply();
                var orange = image.GetPixel(64, 64);
                File.WriteAllBytes(Output("orange.png"), image.EncodeToPNG());
                Require(orange.r > .4f && orange.g > .2f && orange.b < orange.r, "Orange GPU framebuffer " + orange);
                string hash = runtime.SourceContentHash;
                runtime.Configure(blue, blueMetadata, material, bindings);
                Require(hash != runtime.SourceContentHash && !runtime.RuntimeExecuted, "Hot reload metadata");
                emitter.layer = 9; camera.cullingMask = 1 << 9;
                runtime.Step(0, 0, 64, Vector3.zero, camera);
                camera.Render();
                RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); image.Apply();
                var bluePixel = image.GetPixel(64, 64);
                File.WriteAllBytes(Output("blue.png"), image.EncodeToPNG());
                Require(bluePixel.b > .4f && bluePixel.r < .1f, "Blue GPU framebuffer " + bluePixel);
                rejected = false;
                try { LibraGpuMetadata.Parse("{\"version\":2}"); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Unsupported metadata version accepted");
                rejected = false;
                try { LibraGpuMetadata.Parse(metadata.text.Replace("\"kind\": \"RenderBillboard\"", "\"kind\": \"ComputePass\"")); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Unsupported flow accepted");
                rejected = false;
                try { LibraGpuMetadata.Parse(metadata.text.Replace("\"particleStride\": 80", "\"particleStride\": 64")); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Invalid particle stride accepted");
                rejected = false;
                try { runtime.Configure(compute, metadata, material, Array.Empty<LibraGpuTextureBinding>()); } catch (ArgumentException) { rejected = true; }
                Require(rejected && runtime.IsConfigured && runtime.SourceContentHash != hash, "Invalid rebind must preserve previous runtime");
                rejected = false;
                try { runtime.Configure(compute, blueMetadata, material, bindings); } catch (ArgumentException) { rejected = true; }
                Require(rejected && runtime.IsConfigured, "Mismatched shader source must be rejected");
                string frontendResult = "not supplied";
                var frontendCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(Fixture("Frontend.compute"));
                var frontendMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>(Fixture("Frontend.json"));
                if (frontendCompute != null && frontendMetadata != null)
                {
                    foreach (var message in ShaderUtil.GetComputeShaderMessages(frontendCompute))
                        Require(message.severity.ToString() != "Error", "Frontend shader import error: " + message.message);
                    var frontendProgram = LibraGpuMetadata.Parse(frontendMetadata.text);
                    var frontendBindings = new LibraGpuTextureBinding[frontendProgram.resources.Length];
                    for (int i = 0; i < frontendBindings.Length; i++)
                        frontendBindings[i] = new LibraGpuTextureBinding { resourceId = frontendProgram.resources[i].id, texture = Texture2D.whiteTexture };
                    runtime.Configure(frontendCompute, frontendMetadata, material, frontendBindings);
                    runtime.Step(.1f, 0, 64, Vector3.zero);
                    var frontendParticles = new LibraGpuParticle[runtime.Capacity];
                    runtime.ReadbackForDiagnostics(frontendParticles);
                    Require(frontendParticles[0].alive == 1, "Frontend IR did not spawn a GPU particle");
                    frontendResult = runtime.SourceContentHash;
                }
                runtime.Dispose(); runtime.Dispose();
                Require(!runtime.IsConfigured, "Dispose");
                Require(LibraGpuParticleRuntime.PendingRetirements > 0, "Submitted buffers must await retirement");
                File.WriteAllText(output, "PASS\nUnity: " + Application.unityVersion + "\nDevice: " + SystemInfo.graphicsDeviceName + "\nAPI: " + SystemInfo.graphicsDeviceType + "\nPipeline: " + (GraphicsSettings.currentRenderPipeline == null ? "Built-in" : GraphicsSettings.currentRenderPipeline.GetType().FullName) + "\n1000 steps managed bytes: " + allocated + "\nOrange: " + orange + "\nBlue: " + bluePixel + "\nFrontend IR hash: " + frontendResult + "\nAsync compute: " + SystemInfo.supportsAsyncCompute + "\n");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                File.WriteAllText(output, "FAIL\n" + e);
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
            finally
            {
                RenderTexture.active = null;
                if (emitter != null) UnityEngine.Object.DestroyImmediate(emitter);
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            }
        }
    }
}
