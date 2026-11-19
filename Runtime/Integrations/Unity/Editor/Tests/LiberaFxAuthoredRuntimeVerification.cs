using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxAuthoredRuntimeVerification
    {
        [Serializable] private sealed class QualityIds { public string quality; public string result; }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Check(NativeResult result) => Require(result == NativeResult.Ok, result.ToString());

        [Serializable] private sealed class SpriteFixture { public SpriteFrame[] frames; }
        [Serializable] private sealed class SpriteFrame { public double timeSeconds; public SpritePart[] nodes; }
        [Serializable] private sealed class SpritePart
        {
            public string nodeId;
            public int drawOrder;
            public double[] position, rotation, scale;
            public SpriteParameter[] parameters;
        }
        [Serializable] private sealed class SpriteParameter { public double[] value; }

        public static void RunSpriteCompositionBatch()
        {
            var project = Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT");
            Require(!string.IsNullOrEmpty(project) && Path.GetFullPath(project).TrimEnd('\\') ==
                Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName).TrimEnd('\\'),
                "Run only in the explicitly named isolated verification project.");
            var directory = Environment.GetEnvironmentVariable("LIBERAFX_SPRITE_COMPOSITION_FIXTURE_DIR");
            Require(Path.IsPathRooted(directory ?? ""), "Supply the absolute compiler-produced sprite fixture directory.");
            var fixture = JsonUtility.FromJson<SpriteFixture>(File.ReadAllText(Path.Combine(directory, "expected.json")));
            var shader = Shader.Find("LiberaFx/Atlas Unlit");
            Require(shader, "The Unity atlas shader was not imported.");
            var material = new Material(shader);
            var texture = new Texture2D(64, 32);
            texture.SetPixels(Enumerable.Repeat(Color.cyan, texture.width * texture.height).ToArray());
            texture.Apply();
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            try
            {
                var serialized = new SerializedObject(bindings);
                var assets = serialized.FindProperty("renderAssets");
                assets.arraySize = 1;
                var asset = assets.GetArrayElementAtIndex(0);
                asset.FindPropertyRelative("assetIdentity").stringValue = "/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture";
                asset.FindPropertyRelative("texture").objectReferenceValue = texture;
                var defaults = asset.FindPropertyRelative("defaultMaterials");
                defaults.arraySize = 1;
                defaults.GetArrayElementAtIndex(0).objectReferenceValue = material;
                var names = asset.FindPropertyRelative("defaultTintPropertyNames");
                names.arraySize = 1;
                names.GetArrayElementAtIndex(0).stringValue = "_Color";
                serialized.ApplyModifiedPropertiesWithoutUndo();
                WithComponent(File.ReadAllBytes(Path.Combine(directory, "composition.lfxp")), (owner, component) =>
                {
                    owner.transform.position = new Vector3(3, -8, 2);
                    owner.layer = 31;
                    var playResult = component.Play();
                    Require(playResult == NativeResult.Ok,
                        "Exported composition package failed Unity render preflight/startup: " + playResult + ": " + component.LastError);
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var elapsed = typeof(LiberaFxSpawnableComponent).GetField("elapsed", flags);
                    var update = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate", flags);
                    var renderers = component.InspectDrawNodes().ToArray();
                    Require(component.ActiveRenderScopeCount == 1 && renderers.Length == 2 &&
                        component.RenderStatistics.activeSubmissions == 2 && owner.transform.childCount == 0 &&
                        !owner.GetComponent<MeshRenderer>() && !owner.GetComponent<MeshFilter>(),
                        "Exported package did not initialize exactly two object-free layer submissions.");
                    var firstId = renderers[0].Submissions.Single().stableId;
                    var secondId = renderers[1].Submissions.Single().stableId;
                    Require(firstId != 0 && secondId != 0 && firstId != secondId,
                        "Layers did not receive distinct submission-lifetime identities.");
                    var previousTime = component.ElapsedTime;
                    foreach (var frame in fixture.frames)
                    {
                        Require(frame.timeSeconds >= previousTime, "The composition fixture must advance without rewinding.");
                        elapsed.SetValue(component, frame.timeSeconds - Time.deltaTime);
                        update.Invoke(component, null);
                        Require(string.IsNullOrEmpty(component.LastError) && !component.IsRootFinished &&
                            Math.Abs(component.ElapsedTime - frame.timeSeconds) < 1e-8,
                            "Exported package failed its fixed-time update at " + frame.timeSeconds + ": " + component.LastError);
                        previousTime = frame.timeSeconds;
                        var currentRenderers = component.InspectDrawNodes().ToArray();
                        Require(component.ActiveRenderScopeCount == 1 && currentRenderers.Length == 2 &&
                            renderers[0].Submissions.Single().stableId == firstId &&
                            renderers[1].Submissions.Single().stableId == secondId &&
                            renderers.All(renderer => currentRenderers.Contains(renderer) && renderer.Submissions.Single().enabled),
                            "Forward playback replaced a sprite renderer at " + frame.timeSeconds + ".");
                        foreach (var part in frame.nodes)
                        {
                            var item = renderers.Single(renderer => string.Equals(renderer.nodeId, part.nodeId, StringComparison.OrdinalIgnoreCase));
                            var submission = item.Submissions.Single();
                            Vector3 Vector(double[] values) => new Vector3((float)values[0], (float)values[1], (float)values[2]);
                            var scale = Vector(part.scale);
                            scale.x *= 100;
                            scale.y *= 100;
                            Require((item.LocalPosition - Vector(part.position)).sqrMagnitude < 0.0001f &&
                                ((Vector3)item.worldMatrix.GetColumn(3) - owner.transform.TransformPoint(Vector(part.position))).sqrMagnitude < 0.0001f &&
                                (item.LocalScale - scale).sqrMagnitude < 0.0001f &&
                                Quaternion.Angle(item.LocalRotation, Quaternion.AngleAxis((float)part.rotation[2], Vector3.forward)) < 0.1f &&
                                submission.drawOrder == part.drawOrder, "Layered sprite pose, root transform or sort order differed.");
                            var block = submission.properties;
                            var size = part.parameters[0].value;
                            var offset = part.parameters[1].value;
                            var expected = new Vector4((float)offset[0], (float)(1 - offset[1] - size[1]), (float)size[0], (float)size[1]);
                            Require((block.GetVector("_AtlasRect") - expected).sqrMagnitude < 0.0001f &&
                                block.GetTexture("_MainTex") == texture, "Layered sprite cell was not applied to the actual material property block.");
                        }
                    }
                    VerifyCompositionPixelsAndStop(owner, component);
                }, bindings);
            }
            finally
            {
                Object.DestroyImmediate(bindings);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(material);
            }
            Debug.Log("PASS SpriteComposition: compiled package, two object-free layers, keyed atlas/pose, drawOrder, pixels and Stop.");
        }

        private static void VerifyCompositionPixelsAndStop(GameObject owner, LiberaFxSpawnableComponent component)
        {
            var cameraObject = new GameObject("Authored Sprite verification camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.position = owner.transform.position + Vector3.back * 10;
            camera.orthographic = true;
            camera.orthographicSize = 16;
            camera.cullingMask = 1 << owner.layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var target = new RenderTexture(64, 64, 24);
            var pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            camera.targetTexture = target;
            try
            {
                var before = component.RenderStatistics;
                var color = Capture();
                var after = component.RenderStatistics;
                Require(color.r < 0.1f && color.g > 0.8f && color.b > 0.8f &&
                    after.drawCalls == before.drawCalls + 2 && after.instancedDrawCalls == before.instancedDrawCalls,
                    $"Expected cyan atlas pixels and exactly two individual draws, got {color}.");
                var artifact = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GEOMETRY_PNG");
                if (!string.IsNullOrEmpty(artifact))
                {
                    File.WriteAllBytes(artifact, pixels.EncodeToPNG());
                }
                Check(component.Stop());
                Require(component.ActiveRenderScopeCount == 0 && component.RenderStatistics.activeSubmissions == 0 &&
                    owner.transform.childCount == 0 && Capture().maxColorComponent < 0.05f &&
                    component.RenderStatistics.drawCalls == after.drawCalls,
                    "Stopping the exported package did not retire the layers or clear their pixels.");
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(pixels);
            }

            Color Capture()
            {
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                pixels.Apply();
                return pixels.GetPixel(32, 32);
            }
        }

        public static void RunBatch()
        {
            var project = Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT");
            Require(!string.IsNullOrEmpty(project) && Path.GetFullPath(project).TrimEnd('\\') ==
                Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName).TrimEnd('\\'),
                "Run only in the explicitly named isolated verification project.");
            var directory = Environment.GetEnvironmentVariable("LIBERAFX_LIFECYCLE_FIXTURE_DIR");
            var qualityDirectory = Environment.GetEnvironmentVariable("LIBERAFX_QUALITY_FIXTURE_DIR");
            Require(Path.IsPathRooted(directory ?? "") && Path.IsPathRooted(qualityDirectory ?? ""),
                "Supply absolute compiler-produced lifecycle and quality fixtures.");
            VerifyQuality(qualityDirectory);
            foreach (var name in new[] { "ForceCurrent", "ForceEffect" }) VerifyForce(directory, name);
            VerifyPreset(directory);
            VerifyRenderedPreset(directory);
            Debug.Log("PASS AuthoredRuntime: Unity host quality mapping, compiled quality/value Signals, " +
                "authored scoped Force with real component scope cleanup, single-instance phase preset.");
        }

        private static void VerifyQuality(string directory)
        {
            var ids = JsonUtility.FromJson<QualityIds>(File.ReadAllText(Path.Combine(directory, "AuthoredQuality.json")));
            var package = File.ReadAllBytes(Path.Combine(directory, "AuthoredQuality.lfxp"));
            foreach (var tier in new[] { HostQualityTier.Low, HostQualityTier.High })
            using (var host = new NativeSpawnableHost(package))
            {
                var mapping = Enumerable.Repeat(tier, QualitySettings.names.Length).ToArray();
                var snapshot = UnityHostQuality.Read(mapping, (ulong)Time.frameCount);
                Require(snapshot.Availability == HostQualityAvailability.Known && snapshot.Tier == tier &&
                    snapshot.Source == HostQualitySource.EngineEffects, "Actual Unity quality mapping failed.");
                Check(host.SetHostQuality(snapshot));
                Check(host.Advance(0, 0));
                Check(host.ReadRootValue(ids.quality, out var condition));
                Check(host.ReadRootValue(ids.result, out var value));
                Require(condition.X == (tier == HostQualityTier.High ? 1 : 0) && value.X == 2,
                    "Compiled quality branch or per-arrival Vector4 propagation failed in Unity.");
            }
            using (var host = new NativeSpawnableHost(package))
            {
                var unknown = UnityHostQuality.Read(null, 1);
                Require(unknown.Availability == HostQualityAvailability.Unknown, "Unmapped quality became a default tier.");
                Check(host.SetHostQuality(unknown));
                Require(host.Advance(0, 0) == NativeResult.InvalidValueType,
                    "Unknown quality must fail explicitly when the authored branch executes.");
            }
        }

        private static string Id(byte[] bytes, int offset)
        {
            return Guid.ParseExact(BitConverter.ToString(bytes, offset, 16).Replace("-", ""), "N").ToString("D");
        }

        private static void WithComponent(byte[] bytes, Action<GameObject, LiberaFxSpawnableComponent> action,
            LiberaFxUnityBindingAsset suppliedBindings = null)
        {
            var owner = new GameObject("LIBERA FX authored isolated verification");
            owner.SetActive(false);
            var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var bindings = suppliedBindings ? suppliedBindings : ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            try
            {
                package.SetPackageBytes(bytes);
                var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                Check(component.ConfigureForSession(package, bindings));
                owner.SetActive(true);
                action(owner, component);
            }
            finally
            {
                Object.DestroyImmediate(owner);
                Object.DestroyImmediate(package);
                if (!suppliedBindings) Object.DestroyImmediate(bindings);
            }
        }

        private static void VerifyForce(string directory, string name)
        {
            var ids = File.ReadAllBytes(Path.Combine(directory, name + ".ids"));
            WithComponent(File.ReadAllBytes(Path.Combine(directory, name + ".lfxp")), (owner, component) => {
                var events = new List<SpawnableHostEvent>();
                component.HostEventReceived += events.Add;
                Check(component.Play());
                var first = events.Single(item => item.EventKind == SpawnableHostEventKind.Spawn);
                Check(component.DispatchSignal(Id(ids, 0)));
                Require(events.Count(item => item.EventKind == SpawnableHostEventKind.Spawn) == 2 &&
                    component.ActiveRenderScopeCount == 3, "Authored fixture did not create real root/two child scopes.");
                events.Clear();
                Check(component.NotifyChildEndRequest(first.ContainerId, first.InstanceId, Id(ids, 16)));
                Require(events.Count == 1 && events[0].EventKind == SpawnableHostEventKind.ForceTerminated,
                    "Authored Force synthesized a natural lifecycle event.");
                if (name == "ForceCurrent")
                {
                    Require(events[0].InstanceId == first.InstanceId && component.ActiveRenderScopeCount == 2 &&
                        !component.IsForceTerminated, "Authored ForceCurrent removed a sibling/root scope.");
                    events.Clear();
                    Check(component.DispatchSignal(Id(ids, 32)));
                    Require(events.Count == 1 && events[0].InstanceId == 0,
                        "Authored root ForceEffect did not invalidate the effect.");
                }
                Require(component.IsForceTerminated && component.ActiveRenderScopeCount == 0,
                    "Authored ForceEffect retained Unity render scope objects.");
                Check(component.Play());
                Require(!component.IsForceTerminated && component.ActiveRenderScopeCount == 2,
                    "A forced authored effect could not restart.");
            });
        }

        private static void VerifyPreset(string directory)
        {
            var program = File.ReadAllBytes(Path.Combine(directory, "PhasePreset.lfxb"));
            var phase = "";
            var clock = "";
            for (var index = 0; index < BitConverter.ToUInt32(program, 8); ++index)
            {
                var offset = 16 + index * 256;
                if (program[offset + 16] == 55) phase = Id(program, offset);
                if (program[offset + 16] == 56) clock = Id(program, offset);
            }
            Require(phase != "" && clock != "", "Preset lost its authored phase nodes.");
            WithComponent(File.ReadAllBytes(Path.Combine(directory, "PhasePreset.lfxp")), (owner, component) => {
                Check(component.Play());
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var elapsed = typeof(LiberaFxSpawnableComponent).GetField("elapsed", flags);
                var update = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate", flags);
                void At(double time, double expected)
                {
                    elapsed.SetValue(component, time - Time.deltaTime);
                    update.Invoke(component, null);
                    Check(component.ReadRootValue(phase, out var value));
                    Require(value.X == expected && !component.IsRootFinished,
                        "Single-instance phase preset selected the wrong phase or destroyed implicitly.");
                }
                At(0, 0);
                At(1, 0);
                At(1.5, 1);
                At(4, 1);
                At(4.5, 2);
                At(100, 2);
                Check(component.ReadRootValue(clock, out var phaseTime));
                Require(phaseTime.X == 96 && component.ActiveRenderScopeCount == 1,
                    "End phase clock/lifetime or single-instance ownership changed.");
            });
        }

        private static void VerifyRenderedPreset(string directory)
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.SetActive(false);
            var material = new Material(Shader.Find("Unlit/Color"));
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var cameraObject = new GameObject("LIBERA FX isolated phase camera");
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
                asset.FindPropertyRelative("mesh").objectReferenceValue = primitive.GetComponent<MeshFilter>().sharedMesh;
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
                target.Create();
                WithComponent(File.ReadAllBytes(Path.Combine(directory, "PhasePresetRender.lfxp")), (owner, component) => {
                    Check(component.Play());
                    foreach (var transform in owner.GetComponentsInChildren<Transform>(true))
                        transform.gameObject.layer = 31;
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var elapsed = typeof(LiberaFxSpawnableComponent).GetField("elapsed", flags);
                    var update = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate", flags);
                    Sample(0, 0);
                    Sample(1, 0);
                    Sample(1.5, 1);
                    Sample(4, 1);
                    Sample(4.5, 2);
                    Sample(100, 2);
                    Require(component.ActiveRenderScopeCount == 1 && !component.IsRootFinished,
                        "Rendered phase group spawned extra instances or acquired an implicit lifetime.");
                    void Sample(double time, int phase)
                    {
                        elapsed.SetValue(component, time - Time.deltaTime);
                        update.Invoke(component, null);
                        camera.Render();
                        RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                        pixels.Apply();
                        var visible = pixels.GetPixels32().Count(pixel => phase == 0
                            ? pixel.r > 128 && pixel.g < 32 && pixel.b < 32
                            : phase == 1 ? pixel.g > 128 && pixel.r < 32 && pixel.b < 32
                            : pixel.b > 128 && pixel.r < 32 && pixel.g < 32);
                        Require(visible > 32, "Authored phase " + phase + " did not reach actual Unity camera pixels.");
                    }
                }, bindings);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(primitive);
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(bindings);
                Object.DestroyImmediate(material);
            }
        }
    }
}
