using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxPackageDebuggerVerification
    {
        // Run in the explicitly designated isolated Editor project; no external editor service.
        public static void RunBatch()
        {
            var project = Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT");
            Require(!string.IsNullOrEmpty(project) &&
                string.Equals(Path.GetFullPath(project).TrimEnd('\\', '/'),
                    Directory.GetParent(Application.dataPath).FullName, StringComparison.OrdinalIgnoreCase),
                "Set LIBERAFX_VERIFICATION_PROJECT to the isolated Unity project.");
            var directory = Environment.GetEnvironmentVariable("LIBERAFX_LIFECYCLE_FIXTURE_DIR");
            Require(Path.IsPathRooted(directory ?? ""), "Supply an absolute authored lifecycle fixture directory.");
            var bytes = File.ReadAllBytes(Path.Combine(directory, "ForceCurrent.lfxp"));
            var ids = File.ReadAllBytes(Path.Combine(directory, "ForceCurrent.ids"));
            Require(ids.Length == 48, "Missing fixture Spawn/Force UUIDs.");
            var force = Guid.ParseExact(BitConverter.ToString(ids, 16, 16).Replace("-", ""), "N").ToString("D");
            var invitation = Guid.NewGuid().ToString("N");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var pipe = new NamedPipeServerStream("LiberaFx.ExecutionDebug." + invitation,
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                var owner = new GameObject("LIBERA FX isolated package debugger");
                owner.SetActive(false);
                var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                try
                {
                    package.SetPackageBytes(bytes);
                    var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                    Check(component.ConfigureForSession(package, bindings));
                    owner.SetActive(true);
                    SpawnableHostEvent child = default;
                    component.HostEventReceived += item => { if (item.EventKind == SpawnableHostEventKind.Spawn) child = item; };
                    Check(component.Play());
                    Require(child != null && child.InstanceId != 0, "Authored OnStart did not spawn a child.");
                    Check(component.GetDebuggerGeneration(out var generation));
                    var connect = pipe.WaitForConnectionAsync(deadline.Token);
                    Check(component.AttachExecutionDebugger(generation, child.ContainerId, child.InstanceId, invitation));
                    connect.GetAwaiter().GetResult();
                    var hash = 14695981039346656037UL;
                    foreach (var b in bytes) hash = unchecked((hash ^ b) * 1099511628211UL);
                    var attached = Exchange(pipe, hash, 1, deadline.Token);
                    Require(attached[5] == 0 && BitConverter.ToUInt64(attached, 952) == generation &&
                        BitConverter.ToUInt64(attached, 960) == child.InstanceId, "Unexpected attached scope.");
                    Require(Exchange(pipe, hash, 4, deadline.Token)[5] == 0, "Could not arm Pause.");
                    Check(component.NotifyChildEndRequest(child.ContainerId, child.InstanceId, force));
                    byte[] frame;
                    do
                    {
                        frame = Exchange(pipe, hash, 0, deadline.Token);
                        if (frame[4] != 2) Thread.Sleep(2);
                    } while (frame[4] != 2);
                    for (var i = 0; i < 16; ++i)
                        Require(frame[16 + i] == ids[16 + i], "Paused on the wrong authored child node.");

                    var scopes = component.ActiveRenderScopeCount;
                    var clock = Stopwatch.StartNew();
                    typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(component, null);
                    Require(component.ActiveRenderScopeCount == scopes &&
                        component.ForceTerminate() == NativeResult.Busy, "Paused worker did not preserve owner state.");
                    Require(Exchange(pipe, hash, 0, deadline.Token)[4] == 2, "Owner tick released the pause.");
                    Require(clock.Elapsed < TimeSpan.FromSeconds(1), "Owner tick or Busy check blocked.");

                    clock.Restart();
                    Object.DestroyImmediate(owner); // Keep the controller open until destruction returns.
                    Require(!owner && clock.Elapsed < TimeSpan.FromSeconds(2), "Destroy did not release/join the paused worker.");
                    try
                    {
                        var eof = pipe.ReadAsync(new byte[1], 0, 1, deadline.Token).GetAwaiter().GetResult();
                        Require(eof == 0, "Destroyed component left the debugger transport open.");
                    }
                    catch (IOException) { /* A closed Windows pipe may report broken pipe rather than EOF. */ }
                }
                finally
                {
                    if (owner) Object.DestroyImmediate(owner);
                    Object.DestroyImmediate(package);
                    Object.DestroyImmediate(bindings);
                }
            }
            UnityEngine.Debug.Log("PASS PackageDebugger: exact child pause, responsive main thread, Busy, destroy while paused.");
            VerifyRenderedRoot(directory);
            UnityEngine.Debug.Log("PASS PackageDebugger: rendered root stays visible with its mesh, material and transform intact while paused, including across LateUpdate.");
        }

        private static void VerifyRenderedRoot(string directory)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, "PhasePresetRender.lfxp"));
            var shader = Shader.Find("Unlit/Color");
            Require(shader != null, "Missing Unity built-in Unlit/Color shader.");
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.SetActive(false);
            var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            var material = new Material(shader);
            var owner = new GameObject("LIBERA FX isolated rendered root debugger");
            owner.SetActive(false);
            var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var cameraObject = new GameObject("LIBERA FX isolated debugger camera");
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                var serialized = new SerializedObject(bindings);
                var assets = serialized.FindProperty("renderAssets");
                assets.arraySize = 1;
                var asset = assets.GetArrayElementAtIndex(0);
                asset.FindPropertyRelative("assetIdentity").stringValue = "/Engine/BasicShapes/Cube.Cube";
                asset.FindPropertyRelative("mesh").objectReferenceValue = mesh;
                var defaults = asset.FindPropertyRelative("defaultMaterials");
                defaults.arraySize = 1;
                defaults.GetArrayElementAtIndex(0).objectReferenceValue = material;
                var names = asset.FindPropertyRelative("defaultTintPropertyNames");
                names.arraySize = 1;
                names.GetArrayElementAtIndex(0).stringValue = "_Color";
                var materials = serialized.FindProperty("materials");
                materials.arraySize = 1;
                var binding = materials.GetArrayElementAtIndex(0);
                binding.FindPropertyRelative("assetIdentity").stringValue = "builtin:unlit-color";
                binding.FindPropertyRelative("material").objectReferenceValue = material;
                binding.FindPropertyRelative("tintPropertyName").stringValue = "_Color";
                binding.FindPropertyRelative("parameterMappings").arraySize = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.transform.position = new Vector3(0, 0, -6);
                camera.orthographic = true;
                camera.orthographicSize = 3;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31;
                camera.targetTexture = target;
                Require(target.Create(), "Could not create the debugger camera render target.");

                package.SetPackageBytes(bytes);
                var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                Check(component.ConfigureForSession(package, bindings));
                owner.SetActive(true);
                Check(component.Play());
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var elapsed = typeof(LiberaFxSpawnableComponent).GetField("elapsed", flags);
                var update = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate", flags);
                elapsed.SetValue(component, -(double)Time.deltaTime);
                update.Invoke(component, null);

                var renderers = owner.GetComponentsInChildren<MeshRenderer>();
                Require(renderers.Length == 1 && component.ActiveRenderScopeCount == 1,
                    "The authored root did not create exactly one live ModelDraw renderer.");
                var renderer = renderers[0];
                renderer.gameObject.layer = 31;
                var filter = renderer.GetComponent<MeshFilter>();
                Require(renderer.enabled && filter && filter.sharedMesh == mesh && renderer.sharedMaterial == material,
                    "The root renderer did not bind the built-in cube and test material.");
                var matrix = renderer.transform.localToWorldMatrix;
                var properties = new MaterialPropertyBlock();
                var colorId = Shader.PropertyToID("_Color");
                renderer.GetPropertyBlock(properties, 0);
                var tint = properties.GetColor(colorId);
                Require(CountRedPixels() > 32, "The root cube was not visible before attaching the debugger.");

                var invitation = Guid.NewGuid().ToString("N");
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var pipe = new NamedPipeServerStream("LiberaFx.ExecutionDebug." + invitation,
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                {
                    Check(component.GetDebuggerGeneration(out var generation));
                    var connect = pipe.WaitForConnectionAsync(deadline.Token);
                    Check(component.AttachExecutionDebugger(generation, null, 0, invitation));
                    connect.GetAwaiter().GetResult();
                    var hash = 14695981039346656037UL;
                    foreach (var b in bytes) hash = unchecked((hash ^ b) * 1099511628211UL);
                    CheckRootScope(Exchange(pipe, hash, 1, deadline.Token));
                    Require(Exchange(pipe, hash, 4, deadline.Token)[5] == 0, "Could not arm root Pause.");

                    // Queue a frame in the second phase: it must not replace the last red frame.
                    elapsed.SetValue(component, 1.5 - Time.deltaTime);
                    var clock = Stopwatch.StartNew();
                    update.Invoke(component, null);
                    Require(clock.Elapsed < TimeSpan.FromSeconds(1), "The tick that queued root Advance blocked.");
                    byte[] frame;
                    do
                    {
                        frame = Exchange(pipe, hash, 0, deadline.Token);
                        CheckRootScope(frame);
                        if (frame[4] != 2) Thread.Sleep(2);
                    } while (frame[4] != 2);

                    clock.Restart();
                    update.Invoke(component, null);
                    Require(component.ForceTerminate() == NativeResult.Busy, "Paused root accepted a conflicting operation.");
                    Require(clock.Elapsed < TimeSpan.FromSeconds(1), "Owner Update or Busy check blocked on the paused root.");

                    var retained = owner.GetComponentsInChildren<MeshRenderer>();
                    Require(component.ActiveRenderScopeCount == 1 && retained.Length == 1 && retained[0] == renderer &&
                        renderer.enabled && filter && filter.sharedMesh == mesh && renderer.sharedMaterial == material &&
                        renderer.transform.localToWorldMatrix.Equals(matrix), "Paused root replaced or changed the renderer snapshot.");
                    renderer.GetPropertyBlock(properties, 0);
                    Require(properties.GetColor(colorId).Equals(tint) && CountRedPixels() > 32,
                        "Paused root did not retain its material tint and visible red frame.");
                    frame = Exchange(pipe, hash, 0, deadline.Token);
                    CheckRootScope(frame);
                    Require(frame[4] == 2, "Owner Update or camera rendering released the root pause.");

                    clock.Restart();
                    Object.DestroyImmediate(owner); // Keep the controller connected until teardown returns.
                    Require(!owner && !renderer && clock.Elapsed < TimeSpan.FromSeconds(2),
                        "Destroy did not release/join the paused root worker and remove its renderer.");
                    try
                    {
                        var eof = pipe.ReadAsync(new byte[1], 0, 1, deadline.Token).GetAwaiter().GetResult();
                        Require(eof == 0, "Destroyed root left the debugger transport open.");
                    }
                    catch (IOException) { /* A closed Windows pipe may report broken pipe rather than EOF. */ }

                    void CheckRootScope(byte[] response)
                    {
                        Require(response[5] == 0 && BitConverter.ToUInt64(response, 952) == generation &&
                            BitConverter.ToUInt64(response, 960) == 0, "Unexpected debugger generation or root instance ID.");
                        for (var i = 968; i < 984; ++i)
                            Require(response[i] == 0, "The debugger response did not have an empty root container UUID.");
                    }
                }

                int CountRedPixels()
                {
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                    pixels.Apply();
                    var count = 0;
                    foreach (var pixel in pixels.GetPixels32())
                        if (pixel.r > 128 && pixel.g < 32 && pixel.b < 32) ++count;
                    return count;
                }
            }
            finally
            {
                RenderTexture.active = previous;
                if (owner) Object.DestroyImmediate(owner);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(package);
                Object.DestroyImmediate(bindings);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(primitive);
            }
        }

        private static byte[] Exchange(NamedPipeServerStream pipe, ulong hash, byte operation, CancellationToken token) =>
            Task.Run(async () =>
            {
                var command = new byte[32];
                Encoding.ASCII.GetBytes("LFD2").CopyTo(command, 0);
                command[4] = operation;
                BitConverter.GetBytes(hash).CopyTo(command, 8);
                await pipe.WriteAsync(command, 0, command.Length, token).ConfigureAwait(false);
                var frame = new byte[984];
                for (int read = 0; read < frame.Length;)
                {
                    var n = await pipe.ReadAsync(frame, read, frame.Length - read, token).ConfigureAwait(false);
                    if (n == 0) throw new EndOfStreamException("Execution debugger closed unexpectedly.");
                    read += n;
                }
                Require(Encoding.ASCII.GetString(frame, 0, 4) == "LFD2", "Wrong debugger wire version.");
                return frame;
            }, token).GetAwaiter().GetResult();

        private static void Check(NativeResult result) => Require(result == NativeResult.Ok, result.ToString());
        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
