using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    // Graphics-enabled isolated executeMethod; the caller provides the compiler's settings fixture.
    public static class LibraFluid2DVerification
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static Vector4[] Read(LibraFluid2DInstance fluid, int field, int count)
        {
            var data = new Vector4[count];
            fluid.FieldForDiagnostics(field).GetData(data);
            return data;
        }

        private static double Density(Vector4[] values)
        {
            double result = 0;
            foreach (var value in values) result += value.w;
            return result;
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (Exception) { return; }
            throw new InvalidOperationException("Malformed fluid manifest was accepted.");
        }

        public static void Run()
        {
            var outputDirectory = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            var settingsPath = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_SETTINGS");
            GameObject owner = null, cameraObject = null;
            LibraFluid2DInstance first = null, second = null;
            RenderTexture target = null;
            Texture2D capture = null;
            var exitCode = 0;
            try
            {
                Require(!string.IsNullOrEmpty(outputDirectory) && Path.IsPathRooted(outputDirectory) &&
                    !string.IsNullOrEmpty(settingsPath), "Absolute verification paths are required.");
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11, "D3D11 is required.");
                var compute = LibraFluid2DInstance.ValidateAssets();
                foreach (var message in ShaderUtil.GetComputeShaderMessages(compute))
                    Require(message.severity.ToString() != "Error", "Compute shader error: " + message.message);
                var shader = Resources.Load<Shader>("LibraFluid2DDraw");
                Require(shader && !ShaderUtil.ShaderHasError(shader), "Fluid draw shader failed compilation.");
                var json = File.ReadAllText(settingsPath);
                var id = "11d00000-0000-4000-8000-000000000001";
                var id2 = "11d00000-0000-4000-8000-000000000002";
                var manifest = "{\"version\":2,\"programs\":[],\"fluidPrograms\":[{\"nodeId\":\"" + id +
                    "\",\"settings\":" + json + ",\"renderer\":{\"material\":\"builtin:fluid-alpha\",\"sizeScale\":100}}]}";
                var containers = "[{\"id\":\"" + id + "\"}]";
                LiberaFxPreparedScope Prepare(string text, string snapshots = null) =>
                    new LiberaFxUnityBindingLookup(null).Prepare("root", Array.Empty<byte>(),
                        Encoding.UTF8.GetBytes(snapshots ?? containers), Encoding.UTF8.GetBytes(text), true);
                var prepared = Prepare(manifest);
                Require(prepared.fluidPrograms.Length == 1 && prepared.gpuPrograms.Length == 0, "Fluid scope preparation failed.");
                Reject(() => Prepare(manifest.Replace("\"width\": 24", "\"width\": 7")));
                Reject(() => Prepare(manifest.Replace("\"width\": 24", "\"width\": 24,\"width\":24")));
                Reject(() => Prepare(manifest.Replace("\"sourceEnabled\": true,", "")));
                Reject(() => Prepare(manifest.Replace("\"sourceEnabled\": true", "\"sourceEnabled\": 1")));
                Reject(() => Prepare(manifest.Replace("builtin:fluid-alpha", "unknown")));
                Reject(() => Prepare(manifest.Replace("\"programs\":[]", "\"programs\":[{\"nodeId\":\"" + id + "\"}]")));
                Reject(() => Prepare(manifest.Replace(id, Guid.Empty.ToString()), "[{\"id\":\"" + Guid.Empty + "\"}]"));
                Reject(() => Prepare(manifest, "[{\"id\":\"" + id2 + "\"}]"));
                Reject(() => Prepare(manifest.Replace("\"fluidPrograms\"", "\"wrongFluidPrograms\"")));
                var tooMany = new StringBuilder("{\"version\":2,\"programs\":[],\"fluidPrograms\":[");
                for (var i = 0; i < 17; i++)
                {
                    if (i != 0) tooMany.Append(',');
                    tooMany.Append("{\"nodeId\":\"").Append(Guid.NewGuid().ToString("D"))
                        .Append("\",\"settings\":").Append(json)
                        .Append(",\"renderer\":{\"material\":\"builtin:fluid-alpha\",\"sizeScale\":100}}");
                }
                tooMany.Append("]}");
                Reject(() => Prepare(tooMany.ToString()));
                owner = new GameObject("Fluid verification owner") { layer = 8 };
                var settings = prepared.fluidPrograms[0].settings;
                var count = settings.width * settings.height;
                prepared.fluidPrograms[0].sizeScale = 1;
                var statistics = new LiberaFxRenderStatisticsCounter();
                first = new LibraFluid2DInstance(owner.transform, prepared.fluidPrograms[0], statistics);
                var secondSettings = JsonUtility.FromJson<LibraFluid2DSettings>(json);
                secondSettings.pressureIterations = 40;
                second = new LibraFluid2DInstance(owner.transform, new LiberaFxPreparedFluidProgram
                {
                    nodeId = id2, settings = secondSettings, sizeScale = 1
                }, null);
                first.Step(0);
                Require(Density(Read(first, 2, count)) == 0 && Density(Read(second, 2, count)) == 0, "Zero delta was not blank.");
                first.Step(.2);
                Require(first.LastSubsteps == 4 && first.DiscardedDelta > .13, "Frame cap statistics failed.");
                var injected = Read(first, 2, count);
                var injectionDensity = Density(injected);
                Require(injectionDensity > .001 && Density(Read(second, 2, count)) == 0, "Source or per-instance isolation failed.");
                second.Step(.016);
                Require(Density(Read(second, 2, count)) > 0, "Even pressure iteration path failed.");
                var scopeSettings = JsonUtility.FromJson<LibraFluid2DSettings>(json);
                var scopePrepared = new LiberaFxPreparedScope
                {
                    templateId = "verification", renderNodes = Array.Empty<LiberaFxPreparedRenderNode>(),
                    cpuParticles = Array.Empty<LiberaFxPreparedCpuParticleRenderer>(),
                    gpuPrograms = Array.Empty<LiberaFxPreparedGpuProgram>(),
                    fluidPrograms = new[] { new LiberaFxPreparedFluidProgram
                        { nodeId = id, settings = scopeSettings, sizeScale = 1 } }
                };
                var scope = LiberaFxRenderScope.CreatePending(owner.transform, scopePrepared,
                    true, null, 0, 0, 1);
                scope.Initialize(null, 0);
                Require(scope.TryGetFluidOutput(id, out var scopedOutput) && scopedOutput,
                    "Effect-owned root scope did not expose a fluid texture.");
                scope.Update(null, .016);
                Require(scope.TryGetFluidStats(id, out var scopeStats) && scopeStats.Substeps == 1,
                    "Effect-owned scope did not advance fluid.");
                Require(owner.GetComponentsInChildren<LibraGpuParticleRuntime>(true).Length == 0 &&
                    owner.GetComponentsInChildren<Renderer>(true).Length == 0, "Fluid allocated engine renderer components.");
                scope.Dispose();
                Require(!scope.TryGetFluidOutput(id, out _), "Effect-owned scope did not retire fluid.");
                var velocity = Read(first, 0, count);
                for (var y = 0; y < settings.height; y++)
                for (var x = 0; x < settings.width; x++)
                {
                    var value = velocity[y * settings.width + x];
                    Require(!float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                        !float.IsNaN(value.y) && !float.IsInfinity(value.y), "Nonfinite velocity.");
                    if (x == 0 || x == settings.width - 1) Require(Math.Abs(value.x) < 1e-6f, "Horizontal boundary leaked.");
                    if (y == 0 || y == settings.height - 1) Require(Math.Abs(value.y) < 1e-6f, "Vertical boundary leaked.");
                }
                for (var field = 0; field < 7; field++)
                    foreach (var v in Read(first, field, count))
                        Require(!float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
                            !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
                            !float.IsNaN(v.z) && !float.IsInfinity(v.z) &&
                            !float.IsNaN(v.w) && !float.IsInfinity(v.w), "Nonfinite field.");
                settings.sourceEnabled = false;
                for (var i = 0; i < 10; i++) first.Step(.016);
                var faded = Read(first, 2, count);
                Require(Density(faded) > 0 && Density(faded) < injectionDensity, "Disabled source did not advect/fade.");
                var changed = false;
                for (var i = 0; i < count; i++)
                    if (Math.Abs(faded[i].w - injected[i].w) > 1e-5f) { changed = true; break; }
                Require(changed, "Dye did not move or dissipate after source disable.");

                cameraObject = new GameObject("Fluid verification camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black; camera.orthographic = true; camera.orthographicSize = .6f;
                camera.transform.position = new Vector3(0, 0, -2); camera.cullingMask = 1 << 8;
                target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                capture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                first.UpdateTransform(Matrix4x4.identity);
                var textureRead = new Texture2D(settings.width, settings.height, TextureFormat.RGBA32, false);
                RenderTexture.active = first.Output;
                textureRead.ReadPixels(new Rect(0, 0, settings.width, settings.height), 0, 0); textureRead.Apply();
                var textureMax = 0f;
                foreach (var pixel in textureRead.GetPixels()) textureMax = Math.Max(textureMax, pixel.r);
                UnityEngine.Object.DestroyImmediate(textureRead);
                RenderTexture.active = null;
                camera.Render();
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); capture.Apply();
                var pixels = capture.GetPixels();
                var max = 0f;
                foreach (var pixel in pixels) max = Math.Max(max, pixel.r);
                Require(max > .005f && max < .99f, "Fluid quad must render nonblack translucent dye, max=" + max +
                    ", textureMax=" + textureMax + ", draws=" + statistics.value.drawCalls);
                File.WriteAllBytes(Path.Combine(outputDirectory, "fluid-render.png"), capture.EncodeToPNG());
                RenderTexture.active = null;
                first.Reset();
                Require(Density(Read(first, 2, count)) == 0 && Density(Read(second, 2, count)) > 0, "Reset or independent state failed.");
                var cleared = new Texture2D(settings.width, settings.height, TextureFormat.RGBA32, false);
                RenderTexture.active = first.Output;
                cleared.ReadPixels(new Rect(0, 0, settings.width, settings.height), 0, 0); cleared.Apply();
                foreach (var pixel in cleared.GetPixels())
                    Require(pixel.a == 0 && pixel.r == 0 && pixel.g == 0 && pixel.b == 0, "Reset output was not transparent.");
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(cleared);
                first.Dispose(); first = null;
                camera.Render();
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); capture.Apply();
                foreach (var pixel in capture.GetPixels())
                    Require(pixel.r < .005f && pixel.g < .005f && pixel.b < .005f, "Disposed fluid still drew.");
                File.WriteAllText(Path.Combine(outputDirectory, "fluid-proof.txt"),
                    "PASS: v2 validation, 24x40 grid, zero delta, independent effects, frame cap, 39/40 pressure iterations, finite fields, closed boundaries, source disable/advection/fade, framebuffer, reset and dispose.\n");
                Debug.Log("LIBERAFX FLUID2D VERIFICATION PASS");
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                exitCode = 1;
            }
            finally
            {
                RenderTexture.active = null;
                first?.Dispose(); second?.Dispose();
                if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (capture) UnityEngine.Object.DestroyImmediate(capture);
            }
            EditorApplication.Exit(exitCode);
        }

        // Runs against the real exporter output; no synthetic binary is accepted.
        // The fixture is a root fluid-only package with AutoDestroyWhenIdle=false.
        public static void RunPackage()
        {
            const string assetPath = "Assets/LibraFluid2DVerification.lfxp";
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_PACKAGE");
            var nodeId = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_NODE_ID");
            var outputDirectory = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            GameObject owner = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D capture = null;
            var imported = false;
            var exitCode = 0;
            try
            {
                Require(!string.IsNullOrEmpty(fixture) && Path.IsPathRooted(fixture) && File.Exists(fixture) &&
                    Guid.TryParse(nodeId, out _) && !string.IsNullOrEmpty(outputDirectory) && Path.IsPathRooted(outputDirectory),
                    "Absolute compiler package/output paths and a fluid node GUID are required.");
                Require(!File.Exists(assetPath), "Refusing to overwrite an existing verification asset.");
                File.Copy(fixture, assetPath);
                imported = true;
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(assetPath);
                Require(asset && asset.PackageBytes != null &&
                    Convert.ToBase64String(asset.PackageBytes) == Convert.ToBase64String(File.ReadAllBytes(fixture)),
                    "Unity importer did not preserve the compiler package.");
                owner = new GameObject("Fluid package verification") { layer = 8 };
                owner.SetActive(false);
                var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                Require(component.ConfigureForSession(asset, null, true) == NativeResult.Ok, "Package setup failed.");
                owner.SetActive(true);
                Require(component.Play() == NativeResult.Ok, "Play failed: " + component.LastError);
                Require(component.TryGetFluidOutput(nodeId, 0, out var output) && output,
                    "Root fluid output was not exposed.");
                Require(component.AdvancePreview(.06) == NativeResult.Ok, "Package advance failed: " + component.LastError);
                Require(component.TryGetFluidStats(nodeId, 0, out var stats) && stats.Substeps > 0,
                    "Fluid scope did not advance.");
                var readback = new Texture2D(output.width, output.height, TextureFormat.RGBA32, false);
                RenderTexture.active = output;
                readback.ReadPixels(new Rect(0, 0, output.width, output.height), 0, 0); readback.Apply();
                var beforePause = readback.GetPixels32();
                var alpha = 0;
                foreach (var pixel in beforePause) alpha = Math.Max(alpha, pixel.a);
                Require(alpha > 0 && alpha < 255, "Package output lacks translucent dye.");
                RenderTexture.active = null;
                Require(component.AdvancePreview(.06) == NativeResult.Ok, "Paused preview failed.");
                RenderTexture.active = output;
                readback.ReadPixels(new Rect(0, 0, output.width, output.height), 0, 0); readback.Apply();
                var afterPause = readback.GetPixels32();
                for (var i = 0; i < beforePause.Length; i++)
                    Require(beforePause[i].Equals(afterPause[i]), "Zero-delta pause changed fluid output.");
                UnityEngine.Object.DestroyImmediate(readback);
                RenderTexture.active = null;
                cameraObject = new GameObject("Fluid package camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -200);
                camera.orthographic = true; camera.orthographicSize = 60;
                camera.nearClipPlane = .1f; camera.farClipPlane = 500;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 8;
                target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                target.Create(); camera.targetTexture = target;
                capture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                camera.Render();
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); capture.Apply();
                var maximum = 0f;
                foreach (var pixel in capture.GetPixels()) maximum = Math.Max(maximum, pixel.r);
                Require(maximum > .005f && maximum < .99f, "Package quad did not draw translucent dye: " + maximum);
                File.WriteAllBytes(Path.Combine(outputDirectory, "fluid-package.png"), capture.EncodeToPNG());
                RenderTexture.active = null;
                var matrixMode = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_MATRIX_MODE");
                if (matrixMode == "draw-matrix" || matrixMode == "owner-matrix")
                {
                    var initial = ScreenCentroid(camera, target, capture);
                    owner.transform.position = new Vector3(20, 0, 0);
                    if (matrixMode == "owner-matrix")
                    {
                        Require(component.SetOwnerMatrix(new double[]
                            { 1, 0, 0, 20, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }) == NativeResult.Ok,
                            "Host could not update the compiled OwnerMatrix input.");
                    }
                    Require(component.AdvancePreview(.061) == NativeResult.Ok,
                        "Could not refresh the compiled Matrix.");
                    var moved = ScreenCentroid(camera, target, capture);
                    if (matrixMode == "draw-matrix")
                    {
                        Require(Math.Abs(moved.x - initial.x) < .005f,
                            "Authored world Draw Matrix was transformed by the owner twice.");
                    }
                    else
                    {
                        Require(Math.Abs(moved.x - initial.x - 20f / 120f) < .01f,
                            "OwnerMatrix did not apply owner translation exactly once.");
                    }
                    Debug.Log("LIBERAFX FLUID2D MATRIX PASS " + matrixMode + ": " + initial + " -> " + moved);
                }
                Require(component.Stop() == NativeResult.Ok && !component.TryGetFluidOutput(nodeId, 0, out _),
                    "Stop did not release fluid scope.");
                camera.Render();
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); capture.Apply();
                foreach (var pixel in capture.GetPixels())
                    Require(pixel.r < .005f && pixel.g < .005f && pixel.b < .005f, "Fluid survived package Stop.");
                RenderTexture.active = null;
                Require(component.Play() == NativeResult.Ok && component.TryGetFluidOutput(nodeId, 0, out output),
                    "Package restart did not recreate fluid.");
                var restart = new Texture2D(output.width, output.height, TextureFormat.RGBA32, false);
                RenderTexture.active = output;
                restart.ReadPixels(new Rect(0, 0, output.width, output.height), 0, 0); restart.Apply();
                foreach (var pixel in restart.GetPixels())
                    Require(pixel.a == 0, "Restart retained previous fluid density.");
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(restart);
                Require(component.Stop() == NativeResult.Ok, "Stop after restart failed.");
                File.WriteAllText(Path.Combine(outputDirectory, "fluid-package-proof.txt"),
                    "Fixture: " + fixture + "\nPASS: LFXP importer, root fluid Play, advance, zero-delta pause, native texture, real quad, Stop clearing and blank restart.\n");
                Debug.Log("LIBERAFX FLUID2D PACKAGE PASS");
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            finally
            {
                RenderTexture.active = null;
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (capture) UnityEngine.Object.DestroyImmediate(capture);
                if (imported) AssetDatabase.DeleteAsset(assetPath);
            }
            EditorApplication.Exit(exitCode);
        }

        // Exercises the actual compiler JSON for fluid-only, mixed, and nested
        // scope shapes, including imported particle assets for the mixed scope.
        public static void RunCompiledScopes()
        {
            const string folder = "Assets/LibraFluidCompiledScopeCheck";
            var fixtureDir = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_FIXTURES");
            var outputDirectory = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            LiberaFxUnityBindingAsset bindings = null;
            Material material = null;
            GameObject owner = null;
            var exitCode = 0;
            var created = false;
            try
            {
                Require(!string.IsNullOrEmpty(fixtureDir) && Path.IsPathRooted(fixtureDir) &&
                    !string.IsNullOrEmpty(outputDirectory) && Path.IsPathRooted(outputDirectory) &&
                    !Directory.Exists(folder), "Absolute fixture/output paths and a fresh asset folder are required.");
                Directory.CreateDirectory(folder);
                created = true;
                var mixedJson = File.ReadAllText(Path.Combine(fixtureDir, "mixed.json"));
                LibraFluid2DManifestValidation.Validate(mixedJson);
                var mixed = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(mixedJson);
                Require(mixed.programs.Length == 1 && mixed.fluidPrograms.Length == 1, "Expected mixed compiler output.");
                var particle = mixed.programs[0];
                File.WriteAllText(folder + "/Particle.compute", particle.unityShaderSource);
                File.WriteAllText(folder + "/Particle.json", particle.unityMetadataJson);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(folder + "/Particle.compute");
                var metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/Particle.json");
                Require(shader && metadata, "Could not import the compiler particle program.");
                foreach (var message in ShaderUtil.GetComputeShaderMessages(shader))
                    Require(message.severity.ToString() != "Error", "Particle shader error: " + message.message);
                var drawShader = Shader.Find("Libra/GPU Particle Billboard");
                Require(drawShader && drawShader.isSupported, "Particle billboard shader unavailable.");
                material = new Material(drawShader) { enableInstancing = true };
                var parsed = LibraGpuMetadata.Parse(particle.unityMetadataJson);
                var renderer = particle.unreal.rendererBindings[0];
                bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                var serialized = new SerializedObject(bindings);
                var array = serialized.FindProperty("gpuPrograms");
                array.arraySize = 1;
                var binding = array.GetArrayElementAtIndex(0);
                binding.FindPropertyRelative("nodeId").stringValue = particle.nodeId;
                binding.FindPropertyRelative("computeShader").objectReferenceValue = shader;
                binding.FindPropertyRelative("metadata").objectReferenceValue = metadata;
                binding.FindPropertyRelative("billboardMaterial").objectReferenceValue = material;
                binding.FindPropertyRelative("spawnRate").floatValue = 64;
                binding.FindPropertyRelative("rendererMaterialIdentity").stringValue = renderer.material;
                binding.FindPropertyRelative("sizeScale").floatValue = (float)renderer.sizeScale;
                var textures = binding.FindPropertyRelative("textures");
                textures.arraySize = parsed.resources.Length;
                for (var i = 0; i < parsed.resources.Length; i++)
                {
                    var texture = textures.GetArrayElementAtIndex(i);
                    texture.FindPropertyRelative("resourceId").stringValue = parsed.resources[i].id;
                    texture.FindPropertyRelative("texture").objectReferenceValue = Texture2D.whiteTexture;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                owner = new GameObject("Compiled GPU scope verification");
                foreach (var name in new[] { "fluid-only", "mixed", "nested" })
                {
                    var text = File.ReadAllText(Path.Combine(fixtureDir, name + ".json"));
                    LibraFluid2DManifestValidation.Validate(text);
                    var manifest = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(text);
                    var ids = new StringBuilder("[");
                    foreach (var item in manifest.programs)
                    {
                        if (ids.Length > 1) ids.Append(',');
                        ids.Append("{\"id\":\"").Append(item.nodeId).Append("\"}");
                    }
                    foreach (var item in manifest.fluidPrograms)
                    {
                        if (ids.Length > 1) ids.Append(',');
                        ids.Append("{\"id\":\"").Append(item.nodeId).Append("\"}");
                    }
                    ids.Append(']');
                    var prepared = new LiberaFxUnityBindingLookup(bindings).Prepare(name,
                        Array.Empty<byte>(), Encoding.UTF8.GetBytes(ids.ToString()),
                        Encoding.UTF8.GetBytes(text), name != "nested");
                    var scope = LiberaFxRenderScope.CreatePending(owner.transform, prepared,
                        name != "nested", null, 1, 0, 1);
                    try
                    {
                        scope.Initialize(null, 0);
                        scope.Update(null, .1);
                        Require(scope.TryGetFluidOutput(manifest.fluidPrograms[0].nodeId, out var output) && output &&
                            scope.TryGetFluidStats(manifest.fluidPrograms[0].nodeId, out var stats) &&
                            stats.Substeps > 0, "Compiled " + name + " fluid scope did not advance.");
                        if (name == "mixed")
                        {
                            var runtimes = owner.GetComponentsInChildren<LibraGpuParticleRuntime>();
                            Require(runtimes.Length == 1 && runtimes[0].RuntimeExecuted,
                                "Mixed scope particle program did not execute.");
                            var particles = new LibraGpuParticle[runtimes[0].Capacity];
                            runtimes[0].ReadbackForDiagnostics(particles);
                            var alive = 0;
                            foreach (var value in particles) if (value.alive != 0) alive++;
                            Require(alive > 0, "Mixed scope particle program produced no particles.");
                        }
                    }
                    finally { scope.Dispose(); }
                }
                File.Copy(Path.Combine(fixtureDir, "mixed.lfxb"), folder + "/Mixed.lfxp");
                AssetDatabase.ImportAsset(folder + "/Mixed.lfxp", ImportAssetOptions.ForceSynchronousImport);
                var package = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(folder + "/Mixed.lfxp");
                Require(package, "Could not import the mixed compiler package.");
                owner.SetActive(false);
                var player = owner.AddComponent<LiberaFxSpawnableComponent>();
                Require(player.ConfigureForSession(package, bindings, true) == NativeResult.Ok,
                    "Could not configure mixed package.");
                owner.SetActive(true);
                Require(player.Play() == NativeResult.Ok && player.AdvancePreview(.1) == NativeResult.Ok,
                    "Mixed LFXP playback failed: " + player.LastError);
                Require(player.TryGetFluidOutput(mixed.fluidPrograms[0].nodeId, 0, out var mixedOutput) &&
                    mixedOutput && player.TryGetFluidStats(mixed.fluidPrograms[0].nodeId, 0, out var mixedStats) &&
                    mixedStats.Substeps > 0, "Mixed LFXP fluid did not advance.");
                var activeParticles = owner.GetComponentsInChildren<LibraGpuParticleRuntime>();
                Require(activeParticles.Length == 1 && activeParticles[0].RuntimeExecuted,
                    "Mixed LFXP particle scope did not execute.");
                var mixedParticles = new LibraGpuParticle[activeParticles[0].Capacity];
                activeParticles[0].ReadbackForDiagnostics(mixedParticles);
                var mixedAlive = 0;
                foreach (var value in mixedParticles) if (value.alive != 0) mixedAlive++;
                Require(mixedAlive > 0, "Mixed LFXP particles were not alive.");
                Require(player.Stop() == NativeResult.Ok &&
                    !player.TryGetFluidOutput(mixed.fluidPrograms[0].nodeId, 0, out _) &&
                    !activeParticles[0].IsConfigured, "Mixed LFXP Stop did not release both GPU backends.");
                File.WriteAllText(Path.Combine(outputDirectory, "compiled-scopes-proof.txt"),
                    "Fixture directory: " + fixtureDir + "\nPASS: compiler-produced v2 fluid-only, mixed particle/fluid and nested-scope manifests prepared and executed; mixed LFXP import/Play/advance/Stop; mixed particles read back alive.\n");
                Debug.Log("LIBERAFX COMPILED GPU SCOPES PASS");
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            finally
            {
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (bindings) UnityEngine.Object.DestroyImmediate(bindings);
                if (material) UnityEngine.Object.DestroyImmediate(material);
                if (created) AssetDatabase.DeleteAsset(folder);
            }
            EditorApplication.Exit(exitCode);
        }

        public static void RunNestedPackage()
        {
            const string assetPath = "Assets/LibraFluidNestedVerification.lfxp";
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_PACKAGE");
            var nodeId = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_NODE_ID");
            var outputDirectory = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            GameObject owner = null;
            var created = false;
            var exitCode = 0;
            try
            {
                Require(!string.IsNullOrEmpty(fixture) && Path.IsPathRooted(fixture) && File.Exists(fixture) &&
                    Guid.TryParse(nodeId, out _) && !string.IsNullOrEmpty(outputDirectory) && Path.IsPathRooted(outputDirectory) &&
                    !File.Exists(assetPath), "A fresh imported package and absolute verification paths are required.");
                File.Copy(fixture, assetPath); created = true;
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(assetPath);
                Require(asset, "Nested compiler package import failed.");
                owner = new GameObject("Nested fluid package verification");
                owner.SetActive(false);
                var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                Require(component.ConfigureForSession(asset, null, true) == NativeResult.Ok, "Nested setup failed.");
                ulong childId = 0;
                component.HostEventReceived += item => { if (item.Kind == 1) childId = item.InstanceId; };
                owner.SetActive(true);
                Require(component.Play() == NativeResult.Ok, "Nested Play failed: " + component.LastError);
                for (var i = 1; i <= 20 && childId == 0; i++)
                    Require(component.AdvancePreview(i * .05) == NativeResult.Ok, "Nested advance failed: " + component.LastError);
                Require(childId != 0, "The compiler fixture emitted no native Spawn event.");
                Require(component.TryGetFluidOutput(nodeId, childId, out var output) && output,
                    "Native Spawn event did not create its fluid scope.");
                Require(owner.GetComponentsInChildren<LibraGpuParticleRuntime>(true).Length == 0 &&
                    owner.GetComponentsInChildren<Renderer>(true).Length == 0,
                    "Nested fluid created a particle or renderer component.");
                Require(component.AdvancePreview(.1) == NativeResult.Ok &&
                    component.TryGetFluidStats(nodeId, childId, out var stats) && stats.Substeps > 0,
                    "Spawned fluid did not advance.");
                var naturalEnd = false;
                component.HostEventReceived += item => { if (item.InstanceId == childId && item.Kind == 3) naturalEnd = true; };
                for (var i = 1; i <= 22; i++)
                    Require(component.AdvancePreview(.1 + i * .1) == NativeResult.Ok,
                        "Nested lifetime advance failed: " + component.LastError);
                Require(naturalEnd && !component.TryGetFluidOutput(nodeId, childId, out _),
                    "Authored child lifetime did not naturally retire the fluid.");
                childId = 0;
                Require(component.Play() == NativeResult.Ok && childId != 0 &&
                    component.TryGetFluidOutput(nodeId, childId, out _), "Nested restart did not spawn a new fluid.");
                Require(component.Stop() == NativeResult.Ok && !component.TryGetFluidOutput(nodeId, childId, out _),
                    "Nested Stop did not release its fluid scope.");
                File.WriteAllText(Path.Combine(outputDirectory, "fluid-nested-package-proof.txt"),
                    "Fixture: " + fixture + "\nPASS: compiler LFXP importer, native Spawn event, child fluid advance, authored natural lifetime retirement, restart and Stop retirement.\n");
                Debug.Log("LIBERAFX NESTED FLUID PACKAGE PASS");
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            finally
            {
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (created) AssetDatabase.DeleteAsset(assetPath);
            }
            EditorApplication.Exit(exitCode);
        }

        private static Vector2 ScreenCentroid(Camera camera, RenderTexture target, Texture2D capture)
        {
            camera.Render();
            RenderTexture.active = target;
            capture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            capture.Apply();
            RenderTexture.active = null;
            var pixels = capture.GetPixels();
            double weight = 0, xSum = 0, ySum = 0;
            for (var y = 0; y < target.height; y++)
            for (var x = 0; x < target.width; x++)
            {
                var value = pixels[y * target.width + x].r;
                weight += value;
                xSum += value * (x + .5);
                ySum += value * (y + .5);
            }
            Require(weight > 1, "The rendered fluid is blank.");
            return new Vector2((float)(xSum / weight / target.width), (float)(ySum / weight / target.height));
        }

        // Verifies logical Y-up against camera framebuffer pixels, not just
        // structured-buffer row order or a nonblack-output assertion.
        public static void RunOrientation()
        {
            var fixtureDir = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_FIXTURES");
            var outputDirectory = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_UNITY_OUTPUT");
            GameObject owner = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D capture = null;
            LibraFluid2DInstance fluid = null;
            var exitCode = 0;
            try
            {
                Require(!string.IsNullOrEmpty(fixtureDir) && Path.IsPathRooted(fixtureDir) &&
                    !string.IsNullOrEmpty(outputDirectory) && Path.IsPathRooted(outputDirectory),
                    "Absolute compiler fixture/output paths required.");
                var json = File.ReadAllText(Path.Combine(fixtureDir, "fluid-only.json"));
                LibraFluid2DManifestValidation.Validate(json);
                var manifest = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(json);
                owner = new GameObject("Fluid orientation owner") { layer = 8 };
                cameraObject = new GameObject("Fluid orientation camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true; camera.orthographicSize = .6f;
                camera.transform.position = new Vector3(0, 0, -2);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 8;
                target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                capture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                var proof = new StringBuilder("Unity " + Application.unityVersion + ", " + SystemInfo.graphicsDeviceType +
                    ", UV starts at top: " + SystemInfo.graphicsUVStartsAtTop + "\n");
                foreach (var mode in new[] { "velocity", "buoyancy" })
                {
                    // Preserve compiler output and vary only the isolated test instance.
                    var settings = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(json).fluidPrograms[0].settings;
                    if (mode == "velocity") settings.buoyancy = 0;
                    else { settings.sourceVelocityX = 0; settings.sourceVelocityY = 0; }
                    fluid = new LibraFluid2DInstance(owner.transform, new LiberaFxPreparedFluidProgram
                    {
                        nodeId = manifest.fluidPrograms[0].nodeId, settings = settings, sizeScale = 1
                    }, null);
                    fluid.UpdateTransform(Matrix4x4.identity);
                    fluid.Step(.05);
                    var initial = ScreenCentroid(camera, target, capture);
                    var expectedY = .5f + (settings.sourceY - .5f) / 1.2f;
                    Require(Math.Abs(initial.y - expectedY) < .04f,
                        mode + " source is vertically inverted: rendered=" + initial.y + ", expected=" + expectedY);
                    File.WriteAllBytes(Path.Combine(outputDirectory, "fluid-" + mode + "-initial.png"), capture.EncodeToPNG());
                    for (var frame = 0; frame < 120; frame++) fluid.Step(.016);
                    var risen = ScreenCentroid(camera, target, capture);
                    Require(risen.y > initial.y + .025f,
                        mode + " plume did not rise in the camera image: " + initial.y + " -> " + risen.y);
                    Require(Math.Abs(risen.x - initial.x) < .04f, mode + " plume unexpectedly shifted sideways.");
                    File.WriteAllBytes(Path.Combine(outputDirectory, "fluid-" + mode + "-risen.png"), capture.EncodeToPNG());
                    proof.AppendLine(mode + ": source framebuffer centroid " + initial + " -> " + risen + " (Y-up)");
                    fluid.Dispose(); fluid = null;
                }
                File.WriteAllText(Path.Combine(outputDirectory, "fluid-orientation-proof.txt"), proof + "PASS\n");
                Debug.Log("LIBERAFX FLUID ORIENTATION PASS\n" + proof);
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            finally
            {
                RenderTexture.active = null;
                fluid?.Dispose();
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (capture) UnityEngine.Object.DestroyImmediate(capture);
            }
            EditorApplication.Exit(exitCode);
        }
    }
}
