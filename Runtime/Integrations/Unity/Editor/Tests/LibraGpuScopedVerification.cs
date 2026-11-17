using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    public static class LibraGpuScopedVerification
    {
        private static string Fixture(string file)
        {
            var path = "Assets/LibraFXGPUValidation/Fixtures/" + file;
            return File.Exists(path) ? path : "Assets/Fixtures/" + file;
        }

        private static string OutputPath()
        {
            var directory = Environment.GetEnvironmentVariable("LIBRA_GPU_VERIFICATION_DIR");
            return string.IsNullOrEmpty(directory)
                ? Path.GetFullPath("spawnable-gpu-verification.txt")
                : Path.Combine(directory, "spawnable-gpu-verification.txt");
        }

        [MenuItem("LiberaFx/Verify scoped GPU runtime")]
        public static void Run()
        {
            GameObject firstObject = null;
            GameObject secondObject = null;
            Material material = null;
            try
            {
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11,
                    "D3D11 graphics are required for scoped GPU verification.");
                var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(Fixture("Emitter.compute"));
                var metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(Fixture("Emitter.json"));
                Require(compute != null && metadata != null, "Missing generated emitter assets.");
                var shader = Shader.Find("Libra/GPU Particle Billboard");
                Require(shader != null && shader.isSupported, "Missing supported billboard shader.");
                material = new Material(shader) { enableInstancing = true };
                var bindings = new[]
                {
                    new LibraGpuTextureBinding { resourceId = "billboardTexture", texture = Texture2D.whiteTexture }
                };

                firstObject = new GameObject("LibraFX Spawnable GPU scope A");
                secondObject = new GameObject("LibraFX Spawnable GPU scope B");
                var first = firstObject.AddComponent<LibraGpuParticleRuntime>();
                var second = secondObject.AddComponent<LibraGpuParticleRuntime>();
                var rejected = false;
                try
                {
                    first.ConfigureForSpawnable(compute, metadata, material,
                        Array.Empty<LibraGpuTextureBinding>(), 1, 10);
                }
                catch (ArgumentException) { rejected = true; }
                Require(rejected && !first.IsConfigured,
                    "Missing GPU texture binding was not rejected before allocating a scope.");
                rejected = false;
                try
                {
                    first.ConfigureForSpawnable(compute, metadata, material, bindings, 1, float.NaN);
                }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected && !first.IsConfigured,
                    "Invalid per-scope spawn rate was not rejected before allocating a scope.");
                first.ConfigureForSpawnable(compute, metadata, material, bindings, 0x12345678, 10);
                second.ConfigureForSpawnable(compute, metadata, material, bindings, 0x87654321, 10);
                first.StartSimulation();
                second.StartSimulation();

                first.StepSpawnable(.05f, Vector3.zero);
                var firstParticles = new LibraGpuParticle[first.Capacity];
                first.ReadbackForDiagnostics(firstParticles);
                Require(CountAlive(firstParticles) == 0,
                    "The first half of a fractional spawn interval created a particle.");
                first.StepSpawnable(.05f, Vector3.zero);
                first.ReadbackForDiagnostics(firstParticles);
                Require(CountAlive(firstParticles) == 1,
                    "Per-scope fractional spawn progress did not carry across steps.");

                second.StepSpawnable(.2f, Vector3.zero);
                var secondParticles = new LibraGpuParticle[second.Capacity];
                second.ReadbackForDiagnostics(secondParticles);
                Require(CountAlive(secondParticles) == 2,
                    "The second scope did not spawn independently.");
                first.StepSpawnable(.1f, Vector3.zero);
                first.ReadbackForDiagnostics(firstParticles);
                Require(secondParticles[0].positionAge.w > firstParticles[0].positionAge.w,
                    "GPU age was not isolated to each owning scope's local time.");
                Require(firstParticles[0].seed != secondParticles[0].seed,
                    "Separate Spawn events did not seed their GPU scopes independently.");

                File.WriteAllText(OutputPath(),
                    "PASS\nUnity: " + Application.unityVersion +
                    "\nDevice: " + SystemInfo.graphicsDeviceName +
                    "\nAPI: " + SystemInfo.graphicsDeviceType +
                    "\nFractional spawn cursor, independent buffers, local GPU age and per-scope seed: passed\n");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(OutputPath(), "FAIL\n" + error);
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
            finally
            {
                if (firstObject != null) UnityEngine.Object.DestroyImmediate(firstObject);
                if (secondObject != null) UnityEngine.Object.DestroyImmediate(secondObject);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static int CountAlive(LibraGpuParticle[] particles)
        {
            var count = 0;
            foreach (var particle in particles)
                if (particle.alive != 0) ++count;
            return count;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
