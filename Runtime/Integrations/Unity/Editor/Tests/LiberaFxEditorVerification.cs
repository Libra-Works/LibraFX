using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    // Run only in a dedicated validation project. This exercises actual Editor
    // objects synchronously, so -executeMethod can finish normally with -quit.
    public static class LiberaFxEditorVerification
    {
        private static readonly List<string> proof = new List<string>();
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int engineErrors;
        private static string Project => Directory.GetParent(Application.dataPath).FullName;
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static FieldInfo Field(string name) => typeof(LiberaFxRenderer).GetField(name, Private);
        private static Action Method(LiberaFxRenderer owner, string name) =>
            (Action)typeof(LiberaFxRenderer).GetMethod(name, Private).CreateDelegate(typeof(Action), owner);
        private static void TimeAt(LiberaFxRenderer owner, double time)
        {
            Field("elapsed").SetValue(owner, time - Time.deltaTime);
        }
        private static void Log(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                ++engineErrors;
                proof.Add("engine_error=" + message);
            }
        }
        public static void RunBatch()
        {
            proof.Clear();
            engineErrors = 0;
            Application.logMessageReceived += Log;
            try
            {
                proof.Add("unity_version=" + Application.unityVersion);
                proof.Add("mode=Editor synchronous lifecycle invocation; not Play Mode");
                proof.Add("graphics_device=" + SystemInfo.graphicsDeviceType);
                proof.Add("graphics_name=" + SystemInfo.graphicsDeviceName);
                NativeAbi.ValidateLayout();
                using (var program = new NativeProgram(LiberaFxSampleProgram.Create()))
                using (var instance = program.CreateInstance())
                {
                    var bindings = new[] { instance.BindValue(LiberaFxSampleProgram.ColorId) };
                    var values = new NativeValue[1];
                    Assert(instance.Evaluate(1, bindings, values) == NativeResult.Ok &&
                        values[0].ComponentCount == 4 && values[0].W == 0.4,
                        "Native P/Invoke evaluated POD result mismatch.");
                }
                proof.Add("native_pinvoke_and_marshal_contracts=PASS");
                VerifyLifetime();
                proof.Add("native_effect_lifetime_start_advance_drain_finish_stop=PASS");
                var shader = Shader.Find("LiberaFx/Atlas Unlit");
                Assert(shader, "LiberaFx shader was not imported.");
                AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(shader), ImportAssetOptions.ForceSynchronousImport);
                var material = new Material(shader);
                var atlas = new Texture2D(64, 64) { filterMode = FilterMode.Point };
                var pixels = new Color[64 * 64];
                for (var y = 0; y < 64; ++y)
                    for (var x = 0; x < 64; ++x)
                        pixels[y * 64 + x] = Color.HSVToRGB(((3 - y / 16) * 4 + x / 16) / 16f, 0.8f, 1);
                atlas.SetPixels(pixels);
                atlas.Apply();
                try
                {
                    if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    {
                        ShaderUtil.allowAsyncCompilation = false;
                        ShaderUtil.CompilePass(material, 0, true);
                        Assert(!ShaderUtil.ShaderHasError(shader) && shader.isSupported, "Shader compile/support failed.");
                        proof.Add("shader_compile=PASS");
                    }
                    else proof.Add("shader_compile=SKIPPED_NULL_GRAPHICS_DEVICE");
                    VerifyCube(material);
                    VerifySprite(material, atlas, false);
                    VerifySprite(material, atlas, true);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(atlas);
                    UnityEngine.Object.DestroyImmediate(material);
                }
                Assert(engineErrors == 0, "Unity emitted errors during verification.");
                proof.Add("result=PASS");
                File.WriteAllLines(Path.Combine(Project, "verification-result.txt"), proof);
                Debug.Log("LiberaFx Editor native/buffer verification PASS");
            }
            catch (Exception error)
            {
                proof.Add("result=FAIL");
                proof.Add(error.ToString());
                File.WriteAllLines(Path.Combine(Project, "verification-result.txt"), proof);
                Debug.LogException(error);
                EditorApplication.Exit(2);
            }
            finally { Application.logMessageReceived -= Log; }
        }
        public static void RunLifetime()
        {
            try
            {
                NativeAbi.ValidateLayout();
                VerifyLifetime();
                VerifyTimeContext();
                File.WriteAllText(Path.Combine(Project, "lifetime-verification.txt"), "PASS");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Project, "lifetime-verification.txt"), error.ToString());
                Debug.LogException(error);
                EditorApplication.Exit(2);
            }
        }
        private static void VerifyTimeContext()
        {
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_CLOCK_FIXTURE");
            if (string.IsNullOrEmpty(fixture)) return;
            var ids = File.ReadAllLines(fixture + ".ids");
            using (var program = new NativeProgram(File.ReadAllBytes(fixture)))
            using (var instance = program.CreateInstance())
            {
                var bindings = new[] { instance.BindValue(ids[0]), instance.BindValue(ids[1]), instance.BindValue(ids[2]) };
                var values = new NativeValue[3];
                Assert(instance.SetTimeContext(5, 0.125) == NativeResult.Ok &&
                    instance.Evaluate(2, bindings, values) == NativeResult.Ok &&
                    values[0].X == 5 && values[1].X == 0.125 && values[2].X == 2,
                    "Unity native time context values are incorrect.");
            }
            const string assetPath = "Assets/LiberaFxClockVerification.bytes";
            if (File.Exists(assetPath)) throw new InvalidOperationException("Clock fixture asset already exists.");
            File.Copy(fixture, assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var material = new Material(Shader.Find("LiberaFx/Atlas Unlit"));
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.SetActive(false);
            var owner = target.AddComponent<LiberaFxRenderer>();
            try
            {
                owner.SetupCubeSample(target.GetComponent<MeshFilter>().sharedMesh, material);
                Field("compiledProgram").SetValue(owner, AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath));
                Field("positionNode").SetValue(owner, ids[0]);
                Field("atlasFrameNode").SetValue(owner, ids[1]);
                Field("colorNode").SetValue(owner, ids[3]);
                Field("defaultNode").SetValue(owner, "");
                Initialize(target, owner);
                TimeAt(owner, 1);
                Method(owner, "Update")();
                var values = (NativeValue[])Field("values").GetValue(owner);
                Assert(Math.Abs(values[0].X - 1) < 1e-6 &&
                    Math.Abs(values[2].X - Time.deltaTime) < 1e-7,
                    "Unity renderer did not forward elapsed time and the game frame delta.");
            }
            finally
            {
                Cleanup(target, owner);
                UnityEngine.Object.DestroyImmediate(material);
                AssetDatabase.DeleteAsset(assetPath);
            }
        }
        private static void VerifyLifetime()
        {
            using (var program = new NativeProgram(LiberaFxSampleProgram.Create()))
            using (var instance = program.CreateInstance())
            {
                var bindings = new[] { instance.BindValue(LiberaFxSampleProgram.TimeId) };
                var values = new NativeValue[1];
                Assert(instance.StartLifetime(2, false, null, 0, out var initial) == NativeResult.Ok &&
                    initial.Age == 0 && initial.Remaining == 2 && initial.Expired == 0,
                    "Unity lifetime did not start at zero.");
                Assert(instance.AdvanceLifetime(3, out var expired) == NativeResult.Ok &&
                    expired.Age == 2 && expired.Expired == 1 &&
                    instance.ReadValues(bindings, values) == NativeResult.Ok && values[0].X == 2,
                    "Unity lifetime did not clamp at expiry.");
                Assert(instance.DrainLifetime(4) == NativeResult.Ok &&
                    instance.ReadValues(bindings, values) == NativeResult.Ok && values[0].X == 4,
                    "Unity lifetime did not continue while draining.");
                Assert(instance.FinishLifetime() == NativeResult.Ok, "Unity lifetime did not finish.");
            }
            using (var program = new NativeProgram(LiberaFxSampleProgram.Create()))
            using (var instance = program.CreateInstance())
            {
                Assert(instance.StartLifetime(0, false, null, 0, out var initial) == NativeResult.Ok &&
                    initial.Expired == 1 && instance.StopLifetime() == NativeResult.Ok,
                    "Unity explicit stop after immediate expiry failed.");
            }
            using (var program = new NativeProgram(LiberaFxSampleProgram.Create()))
            using (var instance = program.CreateInstance())
            {
                var life = new NativeSignal("00000000-0000-0000-0000-000000000003");
                Assert(instance.StartCapturedLifetime(life, null, 0, out var initial) == NativeResult.Ok &&
                    initial.Remaining == 0.2 &&
                    instance.AdvanceLifetime(1, out var expired) == NativeResult.Ok &&
                    expired.Age == 0.2 && expired.Expired == 1 &&
                    instance.StopLifetime() == NativeResult.Ok,
                    "Unity captured lifetime did not expire at the captured value.");
            }
        }
        private static void Initialize(GameObject target, LiberaFxRenderer owner)
        {
            target.SetActive(true);
            if (Field("instance").GetValue(owner) == null) Method(owner, "OnEnable")();
            Assert(owner.enabled && Field("instance").GetValue(owner) != null, "Renderer did not initialize.");
        }
        private static void Cleanup(GameObject target, LiberaFxRenderer owner)
        {
            // Runtime cleanup normally uses Destroy in Play Mode. The verification
            // is Editor-only, so dispose its owned mesh explicitly and immediately.
            var mesh = (Mesh)Field("ownedMesh").GetValue(owner);
            Field("ownedMesh").SetValue(owner, null);
            Method(owner, "OnDisable")();
            if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(target);
        }
        private static void VerifyCube(Material material)
        {
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.name = "Verified native cube";
            target.SetActive(false);
            UnityEngine.Object.DestroyImmediate(target.GetComponent<Collider>());
            var owner = target.AddComponent<LiberaFxRenderer>();
            owner.SetupCubeSample(target.GetComponent<MeshFilter>().sharedMesh, material);
            try
            {
                Initialize(target, owner);
                var update = Method(owner, "Update");
                TimeAt(owner, 0); update();
                var renderer = target.GetComponent<MeshRenderer>();
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                var first = block.GetColor("_Color");
                TimeAt(owner, 1); update();
                renderer.GetPropertyBlock(block);
                Assert(Mathf.Abs(target.transform.localPosition.x - 0.2f) < 0.0001f &&
                    Mathf.Abs(block.GetColor("_Color").r - 0.4f) < 0.0001f &&
                    block.GetColor("_Color") != first, "Native cube time/color did not update Unity objects.");
                Assert(renderer.sharedMaterial == material, "Renderer cloned its material.");
                Assert(owner.SetDefaultGain(0.3) == NativeResult.Ok, "Native default mutation failed.");
                TimeAt(owner, 1); update();
                renderer.GetPropertyBlock(block);
                Assert(Mathf.Abs(target.transform.localPosition.x - 0.3f) < 0.0001f &&
                    Mathf.Abs(block.GetColor("_Color").r - 0.5f) < 0.0001f, "Mutated default did not reach the cube.");
                proof.Add("cube_transform_material_property_block_defaults=PASS");
                Render(target, "cube");
            }
            finally { Cleanup(target, owner); }
        }
        private static void VerifySprite(Material material, Texture atlas, bool dynamic)
        {
            var target = new GameObject(dynamic ? "Verified dynamic quad" : "Verified atlas");
            target.SetActive(false);
            var owner = target.AddComponent<LiberaFxRenderer>();
            owner.SetupAtlasSample(atlas, material, 4, 4, dynamic);
            try
            {
                Initialize(target, owner);
                var update = Method(owner, "Update");
                var renderer = target.GetComponent<MeshRenderer>();
                var block = new MaterialPropertyBlock();
                TimeAt(owner, 0); update();
                renderer.GetPropertyBlock(block);
                var firstRect = block.GetVector("_AtlasRect");
                Assert((firstRect - new Vector4(0, 0.75f, 0.25f, 0.25f)).sqrMagnitude < 0.00001f,
                    "Native frame zero did not reach the Unity atlas uniform.");
                TimeAt(owner, 1); update();
                renderer.GetPropertyBlock(block);
                Assert(block.GetTexture("_MainTex") == atlas && renderer.sharedMaterial == material,
                    "Atlas texture/material bindings were not applied.");
                var rect = block.GetVector("_AtlasRect");
                Assert((rect - new Vector4(0, 0.25f, 0.25f, 0.25f)).sqrMagnitude < 0.00001f,
                    "Native frame 8 top-left UV was not converted to Unity bottom-left UV.");
                Assert(rect != firstRect, "Native time did not animate the atlas uniform.");
                proof.Add((dynamic ? "dynamic_quad" : "static_atlas") + "_atlas_animation=PASS");
                var mesh = target.GetComponent<MeshFilter>().sharedMesh;
                var vertices = mesh.vertices;
                Assert(mesh.vertexCount == 4 && mesh.GetVertexBufferStride(0) == 20, "Quad buffer format/capacity mismatch.");
                Assert(Mathf.Abs(vertices[2].y - (dynamic ? 0.7f : 0.5f)) < 0.0001f,
                    "Native deformation did not update the actual Unity vertex buffer.");
                var workArray = Field("vertices").GetValue(owner);
                var outputArray = Field("values").GetValue(owner);
                IntPtr bufferPointer = IntPtr.Zero;
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    using (var buffer = mesh.GetVertexBuffer(0)) bufferPointer = buffer.GetNativeBufferPtr();
                for (var i = 0; i < 300; ++i) update();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; ++i) update();
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert(target.GetComponent<MeshFilter>().sharedMesh == mesh && mesh.vertexCount == 4 &&
                    ReferenceEquals(workArray, Field("vertices").GetValue(owner)) &&
                    ReferenceEquals(outputArray, Field("values").GetValue(owner)), "Per-frame storage was replaced.");
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    using (var buffer = mesh.GetVertexBuffer(0))
                        Assert(bufferPointer == buffer.GetNativeBufferPtr(), "Unity vertex buffer was reallocated.");
                proof.Add((dynamic ? "dynamic_quad" : "static_atlas") + "_buffer_reuse=PASS");
                proof.Add((dynamic ? "dynamic_quad" : "static_atlas") + "_1000_update_gc_bytes=" + allocated);
                Assert(allocated == 0, "Unity adapter steady-state updates allocated managed memory.");
                Render(target, dynamic ? "dynamic-quad" : "atlas");
            }
            finally { Cleanup(target, owner); }
        }
        private static void Render(GameObject target, string name)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                proof.Add(name + "_render=SKIPPED_NULL_GRAPHICS_DEVICE");
                return;
            }
            var cameraObject = new GameObject("Verification camera");
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.position = new Vector3(target.transform.position.x, 0, -5);
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var texture = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32);
            var readback = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = texture;
                camera.Render();
                RenderTexture.active = texture;
                readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                readback.Apply();
                var center = readback.GetPixel(32, 32);
                Assert(Mathf.Max(center.r, Mathf.Max(center.g, center.b)) > 0.01f,
                    "Rendered framebuffer contains only background.");
                File.WriteAllBytes(Path.Combine(Project, name + ".png"), readback.EncodeToPNG());
                proof.Add(name + "_render=PASS center=" + center);
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
