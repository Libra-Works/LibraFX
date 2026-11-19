using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxRenderSubmissionVerification
    {
        [MenuItem("LiberaFx/Verify direct renderer batching and lifecycle")]
        public static void Run()
        {
            GameObject cameraObject = null;
            Mesh mesh = null;
            Material opaque = null;
            Material transparent = null;
            RenderTexture target = null;
            Texture2D readback = null;
            LiberaFxRenderSubmission[] submissions = Array.Empty<LiberaFxRenderSubmission>();
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    throw new InvalidOperationException("Run the direct renderer check outside Play Mode.");
                }
                if (Environment.GetEnvironmentVariable("LIBERAFX_UNITY_REQUIRE_SRP") == "1")
                {
                    Require(GraphicsSettings.currentRenderPipeline != null, "The isolated project's SRP is not active.");
                }
                var shader = Shader.Find("Hidden/LiberaFx/Verification/InstancedUnlit");
                if (!shader || !shader.isSupported)
                {
                    throw new InvalidOperationException("The verification instancing shader is unavailable.");
                }
                cameraObject = new GameObject("LIBERA FX direct submission verification camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -5);
                camera.orthographic = true;
                camera.orthographicSize = 1;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 20;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1;
                target = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                mesh = CreateQuad();
                opaque = new Material(shader) { enableInstancing = true };
                opaque.SetFloat("_ZWrite", 1);
                transparent = new Material(shader) { enableInstancing = true };
                transparent.renderQueue = (int)RenderQueue.Transparent;
                var statistics = new LiberaFxRenderStatisticsCounter();
                submissions = new[]
                {
                    CreateSubmission(mesh, opaque, statistics, Color.red, new Vector3(-0.2f, 0, 0)),
                    CreateSubmission(mesh, opaque, statistics, Color.red, new Vector3(0.2f, 0, 0)),
                    CreateSubmission(mesh, opaque, statistics, Color.green, new Vector3(1.5f, 0, 0))
                };
                camera.Render();
                var batchSupported = SystemInfo.supportsInstancing;
                if (batchSupported)
                {
                    Require(statistics.value.instancedDrawCalls == 1 &&
                        statistics.value.instancedInstances == 2 &&
                        statistics.value.drawCalls == 2,
                        "Equal opaque state did not instance together or a different property state was merged.");
                }
                else
                {
                    Require(statistics.value.instancedDrawCalls == 0 &&
                        statistics.value.drawCalls == 3,
                        "The no-instancing fallback did not issue one direct draw per submission.");
                }
                Require(cameraObject.GetComponent<MeshRenderer>() == null &&
                    cameraObject.GetComponent<MeshFilter>() == null,
                    "The direct submission path created a renderer component.");
                readback = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                readback.Apply();
                var center = readback.GetPixel(32, 32);
                Debug.Log("Direct draw pixel " + center + ", pipeline " +
                    (GraphicsSettings.currentRenderPipeline ? GraphicsSettings.currentRenderPipeline.name : "Built-in") +
                    ", draw calls " + statistics.value.drawCalls + ", instanced " +
                    statistics.value.instancedDrawCalls + ".");
                Require(center.r > 0.8f && center.g < 0.2f,
                    "The direct instanced camera draw did not produce the expected center pixel.");
                var artifact = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GEOMETRY_PNG");
                if (!string.IsNullOrEmpty(artifact))
                {
                    System.IO.File.WriteAllBytes(artifact, readback.EncodeToPNG());
                }

                DisposeSubmissions(submissions);
                submissions = new[]
                {
                    CreateSubmission(mesh, transparent, statistics = new LiberaFxRenderStatisticsCounter(),
                        Color.red, Vector3.zero),
                    CreateSubmission(mesh, transparent, statistics, Color.red, new Vector3(0.1f, 0, 0))
                };
                camera.Render();
                Require(statistics.value.instancedDrawCalls == 0 &&
                    statistics.value.drawCalls == 2,
                    "Transparent submissions were instanced or omitted.");

                DisposeSubmissions(submissions);
                statistics = new LiberaFxRenderStatisticsCounter();
                var culled = CreateSubmission(mesh, opaque, statistics, Color.red, Vector3.zero, 8);
                camera.Render();
                Require(statistics.value.drawCalls == 0,
                    "A submission outside the camera layer mask was drawn.");
                culled.Dispose();
                Require(statistics.value.activeSubmissions == 0,
                    "Disposal left an active direct submission registered.");
                Debug.Log("PASS: direct draw pixels, opaque state batching, transparent fallback, camera layer mask, and disposal.");
            }
            catch (Exception error)
            {
                DisposeSubmissions(submissions);
                Debug.LogException(error);
                throw;
            }
            finally
            {
                RenderTexture.active = null;
                if (readback)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }
                if (target)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
                if (cameraObject)
                {
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                }
                if (mesh)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
                if (opaque)
                {
                    UnityEngine.Object.DestroyImmediate(opaque);
                }
                if (transparent)
                {
                    UnityEngine.Object.DestroyImmediate(transparent);
                }
            }
        }

        public static void RunInstancingParity()
        {
            var cameraObject = new GameObject("Instancing parity verification camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.position = new Vector3(0, 0, -5);
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.aspect = 2;
            camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var target = new RenderTexture(128, 64, 24);
            camera.targetTexture = target;
            var pixels = new Texture2D(128, 64, TextureFormat.RGBA32, false);
            var mesh = new Mesh { name = "One-sided parity verification plane" };
            mesh.vertices = new[]
            {
                new Vector3(-0.45f, -0.5f, 0), new Vector3(0.45f, -0.5f, 0),
                new Vector3(0.45f, 0.5f, 0), new Vector3(-0.45f, 0.5f, 0)
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            var material = new Material(Shader.Find("Hidden/LiberaFx/Verification/InstancedUnlit"));
            material.SetFloat("_Cull", (float)CullMode.Back);
            var stats = new LiberaFxRenderStatisticsCounter();
            var previous = RenderTexture.active;
            try
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (Environment.GetEnvironmentVariable("LIBERAFX_UNITY_REQUIRE_SRP") == "1")
                {
                    Require(pipeline, "The isolated project's SRP is not active.");
                }
                Require(SystemInfo.supportsInstancing, "The parity comparison requires actual instancing support.");
                var positive = Matrix4x4.Translate(new Vector3(-1.25f, 0, 0));
                var negative = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(-1, 1, 1));
                using var left = CreateSubmission(mesh, material, stats, Color.red, Vector3.zero, 31);
                using var mirrored = CreateSubmission(mesh, material, stats, Color.red, Vector3.zero, 31);
                using var right = CreateSubmission(mesh, material, stats, Color.red, new Vector3(1.25f, 0, 0), 31);
                left.SetMatrix(positive);
                mirrored.SetMatrix(negative);

                material.enableInstancing = false;
                var direct = Capture("direct");
                Require(stats.value.drawCalls == 3 && stats.value.instancedDrawCalls == 0 &&
                    direct[32 * 128 + 24].r > 200 && direct[32 * 128 + 104].r > 200,
                    "The direct baseline did not draw both front-facing planes.");

                material.enableInstancing = true;
                var candidate = Capture("candidate");
                var differences = 0;
                for (var index = 0; index < direct.Length; index++)
                {
                    if (!direct[index].Equals(candidate[index]))
                    {
                        differences++;
                    }
                }
                Debug.Log($"Parity comparison {(pipeline ? pipeline.name : "Built-in")}: " +
                    $"direct mirror {direct[32 * 128 + 64]}, candidate mirror {candidate[32 * 128 + 64]}, " +
                    $"{differences} different pixels; candidate calls {stats.value.drawCalls - 3}, " +
                    $"instanced {stats.value.instancedDrawCalls}, instances {stats.value.instancedInstances}.");
                Require(differences == 0 && stats.value.drawCalls == 5 &&
                    stats.value.instancedDrawCalls == 1 && stats.value.instancedInstances == 2,
                    "Only positive transforms may batch, and the mirrored draw must match the direct baseline.");

                mirrored.SetMatrix(positive);
                Require(right.CanInstanceWith(mirrored), "A matrix update to positive parity did not restore eligibility.");
                mirrored.SetMatrix(Matrix4x4.Scale(new Vector3(0, 1, 1)));
                Require(!mirrored.SupportsInstancing() && !right.CanInstanceWith(mirrored),
                    "A singular transform was eligible for instancing.");
                mirrored.SetMatrix(Matrix4x4.Scale(new Vector3(float.NaN, 1, 1)));
                Require(!mirrored.SupportsInstancing() && !right.CanInstanceWith(mirrored),
                    "A nonfinite determinant was eligible for instancing.");
                Debug.Log("PASS: one-sided direct/instanced parity and cached matrix eligibility.");

                Color32[] Capture(string label)
                {
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 128, 64), 0, 0);
                    pixels.Apply();
                    var prefix = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_PARITY_PNG_PREFIX");
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        System.IO.File.WriteAllBytes($"{prefix}-{label}.png", pixels.EncodeToPNG());
                    }
                    return pixels.GetPixels32();
                }
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (var obj in new UnityEngine.Object[] { cameraObject, pixels, target, mesh, material })
                {
                    UnityEngine.Object.DestroyImmediate(obj);
                }
            }
        }

        public static void RunFlexiblePlane()
        {
            GameObject owner = null;
            GameObject cameraObject = null;
            Material material = null;
            RenderTexture target = null;
            Texture2D readback = null;
            LiberaFxFlexiblePlaneRenderer renderer = null;
            NativeSpawnableHost host = null;
            try
            {
                var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GAMEIO_FIXTURE");
                if (string.IsNullOrEmpty(fixture) || !System.IO.File.Exists(fixture))
                {
                    throw new InvalidOperationException(
                        "Set LIBERAFX_UNITY_GAMEIO_FIXTURE to an isolated compiler-produced LFXP package.");
                }
                var shader = Shader.Find("Hidden/LiberaFx/Verification/VertexColor");
                if (!shader || !shader.isSupported)
                {
                    throw new InvalidOperationException("The verification vertex-color shader is unavailable.");
                }
                material = new Material(shader);
                owner = new GameObject("LIBERA FX object-free Flexible Plane verification");
                owner.transform.position = new Vector3(0.1f, 0, 0);
                cameraObject = new GameObject("LIBERA FX Flexible Plane verification camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -5);
                camera.orthographic = true;
                camera.orthographicSize = 1;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 20;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1;
                target = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;

                var nodeId = Guid.NewGuid().ToString("D");
                var sourceId = Guid.NewGuid().ToString("D");
                var payload = new LiberaFxFlexiblePlanePayload
                {
                    version = 1,
                    sourceKind = "list",
                    sourceNodeId = sourceId,
                    facing = "fixedUp",
                    uvMode = "stretch",
                    points = new[]
                    {
                        new LiberaFxFlexiblePointPayload
                        {
                            position = new[] { -0.7, 0.0, 0.0 },
                            color = new[] { 1.0, 0.0, 0.0, 1.0 },
                            width = 0.25
                        },
                        new LiberaFxFlexiblePointPayload
                        {
                            position = new[] { 0.7, 0.0, 0.0 },
                            color = new[] { 1.0, 0.0, 0.0, 1.0 },
                            width = 0.25
                        }
                    },
                    capacity = 0,
                    maxAge = 0,
                    repeatLength = 1,
                    up = new[] { 0.0, 0.0, 1.0 }
                };
                var node = new LiberaFxRenderNodePayload
                {
                    nodeId = nodeId,
                    kind = "flexiblePlane",
                    assetPath = "/verification/vertex-color",
                    inputs = new[]
                    {
                        Input("position0", new[] { -0.7, 0.0, 0.0 }),
                        Input("position1", new[] { 0.7, 0.0, 0.0 }),
                        Input("width", new[] { 1.0 }),
                        Input("tint", new[] { 1.0, 1.0, 1.0, 1.0 })
                    }
                };
                var prepared = LiberaFxPreparedFlexiblePlane.Prepare(node, payload,
                    Texture2D.whiteTexture, material, "_MainTex", null);
                var statistics = new LiberaFxRenderStatisticsCounter();
                var history = new LiberaFxFlexiblePlaneHistoryScope();
                renderer = new LiberaFxFlexiblePlaneRenderer(owner.transform,
                    prepared, history, statistics);
                host = new NativeSpawnableHost(System.IO.File.ReadAllBytes(fixture));
                renderer.Update(host, true, null, 0, 0.5);
                Require(statistics.value.activeSubmissions == 1 &&
                    statistics.value.geometryUploads == 1 &&
                    statistics.value.uploadedVertices == 4,
                    "Flexible Plane did not generate and upload one reusable four-vertex ribbon.");
                camera.Render();
                Require(statistics.value.drawCalls == 1,
                    "Flexible Plane did not submit one camera draw.");
                Require(owner.GetComponent<MeshRenderer>() == null &&
                    owner.GetComponent<MeshFilter>() == null,
                    "Flexible Plane created a renderer GameObject.");
                readback = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                readback.Apply();
                var center = readback.GetPixel(32, 32);
                Require(center.r > 0.8f && center.g < 0.2f,
                    "The parent-transformed Flexible Plane did not render its vertex color.");
                renderer.Dispose();
                renderer = null;
                Require(statistics.value.activeSubmissions == 0,
                    "Flexible Plane disposal left a direct submission registered.");
                Debug.Log("PASS: native Flexible Plane geometry, parent transform, vertex-color pixels, and object-free cleanup.");
            }
            finally
            {
                renderer?.Dispose();
                host?.Dispose();
                RenderTexture.active = null;
                if (readback)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }
                if (target)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
                if (cameraObject)
                {
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                }
                if (owner)
                {
                    UnityEngine.Object.DestroyImmediate(owner);
                }
                if (material)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }
        }

        public static void RunOrdering()
        {
            var cameraObject = new GameObject("Direct ordering verification camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -5);
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 1 << 31;
            var target = new RenderTexture(64, 64, 24);
            camera.targetTexture = target;
            var pixels = new Texture2D(64, 64);
            var mesh = CreateQuad();
            var shader = Shader.Find("Hidden/LiberaFx/Verification/InstancedUnlit");
            var material = new Material(shader) { renderQueue = 3000, enableInstancing = true };
            material.SetFloat("_ZWrite", 0);
            var stats = new LiberaFxRenderStatisticsCounter();
            var previous = RenderTexture.active;
            try
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (Environment.GetEnvironmentVariable("LIBERAFX_UNITY_REQUIRE_SRP") == "1")
                {
                    Require(pipeline, "The isolated project's SRP is not active.");
                }
                using var far = CreateSubmission(mesh, material, stats, Color.green, new Vector3(0.25f, 0, 1), 31);
                using var near = CreateSubmission(mesh, material, stats, Color.red, new Vector3(-0.25f, 0, -1), 31);
                using var tie = CreateSubmission(mesh, material, null, Color.red, new Vector3(-0.25f, 0, -1), 31);
                far.drawOrder = 10;
                near.drawOrder = tie.drawOrder = -10;
                var entries = new System.Collections.Generic.List<LiberaFxRenderOrdering.Entry>
                {
                    new LiberaFxRenderOrdering.Entry(tie, camera),
                    new LiberaFxRenderOrdering.Entry(far, camera),
                    new LiberaFxRenderOrdering.Entry(near, camera)
                };
                LiberaFxRenderOrdering.Sort(entries);
                Require(entries[0].submission == near && entries[1].submission == tie &&
                    entries[2].submission == far && entries[0].hasAuthoredOrder &&
                    entries[0].stableId < entries[1].stableId && entries[2].cameraDepth > entries[0].cameraDepth,
                    "The pre-batch policy must keep authored order ahead of depth and break ties by stable identity.");
                far.drawOrder = -10;
                entries[2] = new LiberaFxRenderOrdering.Entry(far, camera);
                LiberaFxRenderOrdering.Sort(entries);
                Require(entries[0].submission == far && entries[1].submission == near,
                    "At equal authored order, the camera's far entry must precede the near entry.");
                far.drawOrder = 10;
                Require(!near.CanInstanceWith(tie) && !far.SupportsInstancing(),
                    "Ordered and transparent draws must remain individual.");
                material.renderQueue = (int)RenderQueue.Geometry;
                Require(!near.CanInstanceWith(tie) && !near.SupportsInstancing(),
                    "Explicit ordering must also prevent otherwise-compatible opaque batching.");
                material.renderQueue = (int)RenderQueue.Transparent;
                tie.layer = 30;
                Capture();
                var left = pixels.GetPixel(14, 32);
                var right = pixels.GetPixel(50, 32);
                var overlap = pixels.GetPixel(32, 32);
                Require(left.r > 0.8f && left.g < 0.2f && right.g > 0.8f && right.r < 0.2f &&
                    Mathf.Max(overlap.r, overlap.g) > 0.8f &&
                    stats.value.drawCalls == 2 && stats.value.instancedDrawCalls == 0,
                    "The camera must draw each ordered layer exactly once; final overlap order is engine-owned.");
                var artifact = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GEOMETRY_PNG");
                if (!string.IsNullOrEmpty(artifact))
                {
                    System.IO.File.WriteAllBytes(artifact, pixels.EncodeToPNG());
                }
                near.Dispose();
                far.Dispose();
                Capture();
                Require(stats.value.activeSubmissions == 0 && stats.value.drawCalls == 2 &&
                    pixels.GetPixel(32, 32).maxColorComponent < 0.05f,
                    "Retired layers still submitted draws or left visible pixels.");
                Debug.Log($"PASS: direct Sprite order keys, depth/ties, two draws and cleanup; pipeline " +
                    $"{(pipeline ? pipeline.name : "Built-in")}, overlap {overlap} (engine-sorted).");

                void Capture()
                {
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                    pixels.Apply();
                }
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (var obj in new UnityEngine.Object[] { cameraObject, pixels, target, mesh, material })
                {
                    UnityEngine.Object.DestroyImmediate(obj);
                }
            }
        }

        public static void RunCpuPackage() => RunPublicGeometry(false);
        public static void RunFlexiblePackage() => RunPublicGeometry(true);

        private static void RunPublicGeometry(bool flexible)
        {
            var path = Environment.GetEnvironmentVariable(flexible
                ? "LIBERAFX_UNITY_FLEXIBLE_FIXTURE" : "LIBERAFX_UNITY_CPU_FIXTURE");
            var bytes = System.IO.File.ReadAllBytes(path);
            var owner = new GameObject("Public geometry package verification");
            owner.SetActive(false);
            owner.layer = 31;
            owner.transform.position = new Vector3(flexible ? -1 : -0.5f, 0, 0);
            var cameraObject = new GameObject("Public geometry verification camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 1 << 31;
            camera.transform.position = new Vector3(0, 0, -5);
            camera.orthographic = true;
            camera.orthographicSize = 1;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var target = new RenderTexture(64, 64, 24);
            camera.targetTexture = target;
            var pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            var asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var material = new Material(Shader.Find("Hidden/LiberaFx/Verification/VertexColor"));
            var player = owner.AddComponent<LiberaFxSpawnableComponent>();
            try
            {
                if (flexible)
                {
                    var serialized = new SerializedObject(bindings);
                    var assets = serialized.FindProperty("renderAssets");
                    assets.arraySize = 1;
                    var ribbon = assets.GetArrayElementAtIndex(0);
                    ribbon.FindPropertyRelative("assetIdentity").stringValue = "/Game/FX/T_Ribbon";
                    ribbon.FindPropertyRelative("texture").objectReferenceValue = Texture2D.whiteTexture;
                    var materials = ribbon.FindPropertyRelative("defaultMaterials");
                    materials.arraySize = 1;
                    materials.GetArrayElementAtIndex(0).objectReferenceValue = material;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                asset.SetPackageBytes(bytes);
                Require(player.ConfigureForSession(asset, bindings, true) == NativeResult.Ok,
                    "Could not configure the package.");
                owner.SetActive(true);
                Require(player.Play() == NativeResult.Ok, "Package startup failed: " + player.LastError);
                Require(player.AdvancePreview(0.05) == NativeResult.Ok, "Package advance failed: " + player.LastError);
                var statistics = player.RenderStatistics;
                Require(statistics.activeSubmissions == 1 && statistics.geometryUploads > 0 &&
                    owner.transform.childCount == 0 && !owner.GetComponent<MeshRenderer>(),
                    "Expected reusable geometry and one object-free submission.");
                if (!flexible)
                {
                    Require(player.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration, out var payload) == NativeResult.Ok,
                        "Could not read the CPU fixture's root configuration.");
                    var config = JsonUtility.FromJson<LiberaFxRenderPayload>(
                        System.Text.Encoding.UTF8.GetString(payload));
                    var id = new NativeSignal(config.cpuParticles[0].nodeId).Id;
                    Require(player.InspectCpuParticleCount(id, out var count) == NativeResult.Ok && count == 3 &&
                        statistics.uploadedVertices >= 12, "Expected three actual native snapshots and twelve uploaded vertices.");
                    for (var index = 0u; index < count; index++)
                    {
                        Require(player.InspectCpuParticle(id, index, out var particle) == NativeResult.Ok &&
                            Math.Abs(particle.PositionX - 0.5) < 0.001 && particle.ColorA > 0.99 &&
                            particle.SizeX > 0 && particle.SizeY > 0,
                            "Expected all three native particles to advance with the authored velocity, size and alpha.");
                    }
                }
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                pixels.Apply();
                var center = pixels.GetPixel(32, 32);
                Require(center.r > 0.15f && center.g > 0.15f &&
                    (flexible ? center.b < 0.1f : center.b > 0.15f),
                    "Unexpected public geometry package center pixel " + center);
                var artifact = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GEOMETRY_PNG");
                if (!string.IsNullOrEmpty(artifact))
                {
                    System.IO.File.WriteAllBytes(artifact, pixels.EncodeToPNG());
                }
                Require(player.Stop() == NativeResult.Ok && player.RenderStatistics.activeSubmissions == 0,
                    "Stop retained a package submission.");
                Debug.Log($"PASS: public {(flexible ? "FlexiblePlane" : "CPU particle")} package, pixel {center}, " +
                    $"uploads {statistics.geometryUploads}, vertices {statistics.uploadedVertices}.");
            }
            finally
            {
                player.Stop();
                RenderTexture.active = previous;
                foreach (var obj in new UnityEngine.Object[] { cameraObject, owner, pixels, target, material, bindings, asset })
                {
                    UnityEngine.Object.DestroyImmediate(obj);
                }
            }
        }

        [MenuItem("LiberaFx/Verify CPU particle renderer lifecycle")]
        public static void RunCpuParticleLifecycle()
        {
            GameObject owner = null;
            LiberaFxCpuParticleRenderInstance renderer = null;
            try
            {
                owner = new GameObject("LIBERA FX object-free CPU particle verification");
                var nodeId = Guid.NewGuid().ToString("D");
                var definition = new LiberaFxPreparedCpuParticleRenderer
                {
                    nodeId = nodeId,
                    nodeIdBytes = new NativeSignal(nodeId).Id,
                    texture = Texture2D.whiteTexture,
                    additive = false,
                    drawScale = 1
                };
                var statistics = new LiberaFxRenderStatisticsCounter();
                renderer = LiberaFxCpuParticleRenderInstance.Create(
                    owner.transform, definition, statistics);
                Require(statistics.value.activeSubmissions == 1 &&
                    owner.GetComponentsInChildren<MeshRenderer>().Length == 0 &&
                    owner.GetComponentsInChildren<MeshFilter>().Length == 0 &&
                    owner.transform.childCount == 0,
                    "CPU emitter setup created renderer components or a child GameObject.");
                renderer.Dispose();
                renderer = null;
                Require(statistics.value.activeSubmissions == 0,
                    "CPU emitter cleanup left a direct submission registered.");
                Debug.Log("PASS: CPU particle emitter resources use one direct submission and no particle/render GameObjects.");
            }
            finally
            {
                renderer?.Dispose();
                if (owner)
                {
                    UnityEngine.Object.DestroyImmediate(owner);
                }
            }
        }

        private static LiberaFxRenderSubmission CreateSubmission(Mesh mesh, Material material,
            LiberaFxRenderStatisticsCounter statistics, Color color, Vector3 position, int layer = 0)
        {
            var state = new LiberaFxDrawPropertyState(1);
            state.SetVector(Shader.PropertyToID("_Color"), color);
            var submission = new LiberaFxRenderSubmission(state, statistics);
            submission.Configure(mesh, material, 0, layer, statistics);
            submission.SetMatrix(Matrix4x4.TRS(position, Quaternion.identity, Vector3.one));
            submission.enabled = true;
            return submission;
        }

        private static LiberaFxRenderInputPayload Input(string id, double[] defaults)
        {
            return new LiberaFxRenderInputPayload
            {
                id = id,
                width = defaults.Length,
                defaults = defaults
            };
        }

        private static Mesh CreateQuad()
        {
            var mesh = new Mesh { name = "Libera FX direct submission test quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
                new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0)
            };
            mesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void DisposeSubmissions(LiberaFxRenderSubmission[] values)
        {
            foreach (var submission in values)
            {
                if (submission != null)
                {
                    submission.Dispose();
                }
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
