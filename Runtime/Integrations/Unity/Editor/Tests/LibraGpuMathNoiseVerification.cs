using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    public static class LibraGpuMathNoiseVerification
    {
        [Serializable] sealed class NoiseReference
        {
            public float floatTolerance, finiteDifferenceEpsilon, divergenceTolerance;
            public NoiseCase[] cases;
        }
        [Serializable] sealed class NoiseCase
        {
            public string label;
            public float[] inputPosition, expectedPosition, expectedVelocity, expectedColor, expectedSize;
            public uint seed;
        }
        [Serializable] sealed class MathReference
        {
            public float absoluteTolerance, relativeTolerance;
            public MathCase[] cases;
        }
        [Serializable] sealed class MathCase
        {
            public string kernelId, outputAttribute;
            public float[] inputPosition, inputVelocity, inputColor, expected;
        }

        static string Fixture(string name) => "Assets/LibraFXGPUValidation/Fixtures/" + name;
        static string Output(string name)
        {
            var directory = Environment.GetEnvironmentVariable("LIBRA_GPU_VERIFICATION_DIR");
            return string.IsNullOrEmpty(directory) ? Path.GetFullPath(name) : Path.Combine(directory, name);
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Check(float actual, float expected, float absolute, float relative, string label)
        {
            double tolerance = absolute + relative * Math.Abs((double)expected);
            Require(!float.IsNaN(actual) && !float.IsInfinity(actual) &&
                Math.Abs((double)actual - expected) <= tolerance,
                label + ": actual=" + actual + " expected=" + expected + " tolerance=" + tolerance);
        }
        static void CheckVector(Vector4 value, float[] expected, float absolute, float relative, string label)
        {
            for (int i = 0; i < expected.Length; i++) Check(value[i], expected[i], absolute, relative, label + "[" + i + "]");
        }
        static Vector4 Vector(float[] values, float w = 0)
            => new Vector4(values[0], values[1], values[2], values.Length > 3 ? values[3] : w);
        static LibraGpuParticle Particle(float[] position, uint seed)
            => new LibraGpuParticle
            {
                positionAge = Vector(position), velocityLifetime = new Vector4(0, 0, 0, 100),
                color = Vector4.one, sizeRotation = new Vector4(1, 1, 0, 0), seed = seed, alive = 1
            };
        static ComputeBuffer TestBuffer(LibraGpuParticleRuntime runtime)
            => (ComputeBuffer)typeof(LibraGpuParticleRuntime).GetField("particles", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
        static ComputeShader LoadShader(string name)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(Fixture(name + ".compute"));
            Require(shader != null, "Missing " + name + " compute shader");
            foreach (var message in ShaderUtil.GetComputeShaderMessages(shader))
                Require(message.severity.ToString() != "Error", name + " import: " + message.message);
            return shader;
        }
        static TextAsset LoadText(string name)
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(Fixture(name));
            Require(text != null, "Missing " + name);
            return text;
        }

        public static void Run()
        {
            GameObject emitter = null;
            Material material = null;
            var temporaryMetadata = new List<TextAsset>();
            try
            {
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11, "D3D11 required");
                var shader = Shader.Find("Libra/GPU Particle Billboard");
                Require(shader != null && shader.isSupported, "Missing billboard shader");
                material = new Material(shader) { enableInstancing = true };
                emitter = new GameObject("GPU math and noise verification");
                var runtime = emitter.AddComponent<LibraGpuParticleRuntime>();
                var noTextures = Array.Empty<LibraGpuTextureBinding>();
                var noise = JsonUtility.FromJson<NoiseReference>(LoadText("noise.reference.json").text);
                var math = JsonUtility.FromJson<MathReference>(LoadText("math.reference.json").text);
                Require(noise != null && noise.cases != null && math != null && math.cases != null, "Missing numerical references");

                runtime.Configure(LoadShader("noise"), LoadText("noise.metadata.json"), material, noTextures);
                var particles = new LibraGpuParticle[runtime.Capacity];
                for (int i = 0; i < noise.cases.Length; i++) particles[i] = Particle(noise.cases[i].inputPosition, noise.cases[i].seed);
                TestBuffer(runtime).SetData(particles);
                runtime.Step(0, 0, 0, Vector3.zero);
                runtime.ReadbackForDiagnostics(particles);
                var indexes = new Dictionary<string, int>();
                for (int i = 0; i < noise.cases.Length; i++)
                {
                    var c = noise.cases[i];
                    var p = particles[i];
                    indexes.Add(c.label, i);
                    Require(p.alive == 1, c.label + " unexpectedly killed");
                    CheckVector(p.positionAge, c.expectedPosition, noise.floatTolerance, 0, c.label + " curl");
                    CheckVector(p.velocityLifetime, c.expectedVelocity, noise.floatTolerance, 0, c.label + " gradient/fbm/turbulence");
                    CheckVector(p.color, c.expectedColor, noise.floatTolerance, 0, c.label + " seed/octaves");
                    CheckVector(p.sizeRotation, c.expectedSize, noise.floatTolerance, 0, c.label + " single octave");
                    Require(Math.Abs(p.velocityLifetime.x) <= 1 && Math.Abs(p.velocityLifetime.y) <= 1 &&
                        p.velocityLifetime.z >= 0 && p.velocityLifetime.z <= 1, c.label + " noise range");
                }
                float divergence = (particles[indexes["divergence_plus_x"]].positionAge.x - particles[indexes["divergence_minus_x"]].positionAge.x +
                    particles[indexes["divergence_plus_y"]].positionAge.y - particles[indexes["divergence_minus_y"]].positionAge.y +
                    particles[indexes["divergence_plus_z"]].positionAge.z - particles[indexes["divergence_minus_z"]].positionAge.z)
                    / (2 * noise.finiteDifferenceEpsilon);
                Require(Math.Abs(divergence) <= noise.divergenceTolerance, "Curl divergence " + divergence);
                Require(Math.Abs(particles[indexes["positive"]].velocityLifetime.x - particles[indexes["seed_one"]].velocityLifetime.x) > .001f,
                    "Gradient noise did not vary with seed");

                runtime.Configure(LoadShader("noiseguards"), LoadText("noiseguards.metadata.json"), material, noTextures);
                particles = new LibraGpuParticle[runtime.Capacity];
                particles[0] = Particle(new[] { .23f, .41f, .67f }, 7);
                TestBuffer(runtime).SetData(particles);
                runtime.Step(0, 0, 0, Vector3.zero);
                runtime.ReadbackForDiagnostics(particles);
                Require(particles[0].alive == 1, "Noise guard unexpectedly killed particle");
                CheckVector(particles[0].positionAge, new[] { 0f, 0f, 0f }, 0, 0, "noise guard position");
                CheckVector(particles[0].velocityLifetime, new[] { 0f, 0f, 0f }, 0, 0, "noise guard velocity");
                CheckVector(particles[0].color, new[] { 0f, 0f, 0f, 1f }, 0, 0, "noise guard color");

                var mathShader = LoadShader("math");
                var metadata = LibraGpuMetadata.Parse(LoadText("math.metadata.json").text);
                var groups = new Dictionary<string, List<MathCase>>();
                foreach (var c in math.cases)
                {
                    if (!groups.TryGetValue(c.kernelId, out var group)) groups.Add(c.kernelId, group = new List<MathCase>());
                    group.Add(c);
                }
                foreach (var pair in groups)
                {
                    metadata.flow = new[] { new LibraGpuFlowMetadata { id = "probe", kind = "Update", kernelId = pair.Key } };
                    var testMetadata = new TextAsset(JsonUtility.ToJson(metadata));
                    temporaryMetadata.Add(testMetadata);
                    runtime.Configure(mathShader, testMetadata, material, noTextures);
                    particles = new LibraGpuParticle[runtime.Capacity];
                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        var c = pair.Value[i];
                        particles[i] = Particle(c.inputPosition, 0);
                        particles[i].velocityLifetime = Vector(c.inputVelocity, 100);
                        particles[i].color = Vector(c.inputColor);
                    }
                    TestBuffer(runtime).SetData(particles);
                    runtime.Step(0, 0, 0, Vector3.zero);
                    runtime.ReadbackForDiagnostics(particles);
                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        var c = pair.Value[i]; var p = particles[i];
                        Require(p.alive == 1, c.kernelId + " unexpectedly killed particle " + i);
                        var value = c.outputAttribute == "Color" ? p.color :
                            c.outputAttribute == "Position" ? p.positionAge :
                            c.outputAttribute == "Rotation" ? new Vector4(p.sizeRotation.z, 0, 0, 0) :
                            throw new Exception("Unknown math output attribute " + c.outputAttribute);
                        CheckVector(value, c.expected, math.absoluteTolerance, math.relativeTolerance, c.kernelId + " case " + i);
                    }
                }
                runtime.Dispose();
                File.WriteAllText(Output("math-noise-verification.txt"), "PASS\nUnity: " + Application.unityVersion +
                    "\nDevice: " + SystemInfo.graphicsDeviceName + "\nAPI: " + SystemInfo.graphicsDeviceType +
                    "\nNoise cases: " + noise.cases.Length + "\nMath cases: " + math.cases.Length +
                    "\nMath kernels: " + groups.Count + "\nNoise guards: passed\nCurl divergence: " + divergence + "\n");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                File.WriteAllText(Output("math-noise-verification.txt"), "FAIL\n" + e);
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
            finally
            {
                if (emitter != null) UnityEngine.Object.DestroyImmediate(emitter);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                foreach (var text in temporaryMetadata) UnityEngine.Object.DestroyImmediate(text);
            }
        }
    }
}
