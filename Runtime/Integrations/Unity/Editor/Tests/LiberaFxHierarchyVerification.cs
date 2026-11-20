using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxHierarchyVerification
    {
        public static void Run()
        {
            const string assetPath = "Assets/LibraHierarchyVerification.lfxp";
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_HIERARCHY_FIXTURE");
            var output = Environment.GetEnvironmentVariable("LIBERAFX_HIERARCHY_OUTPUT");
            var parentId = new NativeSignal("40000000-0000-4000-8000-000000000002").Id;
            var childId = new NativeSignal("40000000-0000-4000-8000-000000000005").Id;
            var systemId = new NativeSignal("40000000-0000-4000-8000-000000000008").Id;
            GameObject owner = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D pixels = null;
            var previous = RenderTexture.active;
            var imported = false;
            var exitCode = 0;
            try
            {
                Require(!string.IsNullOrEmpty(fixture) && Path.IsPathRooted(fixture) &&
                    File.Exists(fixture), "An absolute compiler nested CPU fixture path is required.");
                Require(!File.Exists(assetPath), "Refusing to overwrite an existing verification asset.");
                File.Copy(fixture, assetPath);
                imported = true;
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(assetPath);
                Require(asset, "Nested CPU fixture import failed.");
                owner = new GameObject("Nested CPU verification owner") { layer = 8 };
                owner.SetActive(false);
                var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                Require(component.ConfigureForSession(asset, null, true) == NativeResult.Ok,
                    "Nested CPU setup failed.");
                ulong parentInstance = 0, childInstance = 0;
                component.HostEventReceived += item =>
                {
                    if (item.Kind != 1)
                    {
                        return;
                    }
                    if (item.ContainerId.SequenceEqual(parentId))
                    {
                        parentInstance = item.InstanceId;
                    }
                    if (item.ContainerId.SequenceEqual(childId))
                    {
                        childInstance = item.InstanceId;
                    }
                };
                owner.SetActive(true);
                cameraObject = new GameObject("Nested CPU verification camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -10);
                camera.orthographic = true;
                camera.orthographicSize = 4;
                camera.nearClipPlane = .1f;
                camera.farClipPlane = 50;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 8;
                target = new RenderTexture(128, 128, 16, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                for (var restart = 0; restart < 2; ++restart)
                {
                    parentInstance = childInstance = 0;
                    Require(component.Play() == NativeResult.Ok, "Nested Play failed: " + component.LastError);
                    for (var tick = 1; tick <= 30; ++tick)
                    {
                        Require(component.AdvancePreview(tick * .1) == NativeResult.Ok,
                            "Scoped CPU advance failed: " + component.LastError);
                    }
                    Require(parentInstance != 0 && childInstance != 0,
                        "Both hierarchy levels must produce native Spawn notices.");
                    Require(component.InspectCpuParticleCount(childId, childInstance, systemId,
                        out var count) == NativeResult.Ok && count == 3,
                        "Child CPU particles did not survive parent Life=1 and child Life=2.");
                    Require(component.InspectCpuParticle(childId, childInstance, systemId, 0,
                        out var snapshot) == NativeResult.Ok && snapshot.Id != 0,
                        "Scoped CPU snapshot is unavailable.");
                    Debug.Log("Retained CPU position " + snapshot.PositionX + ", " + snapshot.PositionY +
                        ", " + snapshot.PositionZ + "; size " + snapshot.SizeX + ", " + snapshot.SizeY +
                        "; RGBA " + snapshot.ColorR + ", " + snapshot.ColorG + ", " +
                        snapshot.ColorB + ", " + snapshot.ColorA + ".");
                    Require(component.ActiveRenderScopeCount == 3 &&
                        component.RenderStatistics.activeSubmissions == 1 &&
                        component.RenderStatistics.uploadedVertices >= 12,
                        "The retained child must own exactly one populated direct CPU batch.");
                    Require(owner.GetComponentsInChildren<Renderer>(true).Length == 0,
                        "CPU particles must not create renderer components.");
                    Require(Capture(camera, target, pixels) > 0, "Retained CPU particles produced no pixels.");
                    if (!string.IsNullOrEmpty(output))
                    {
                        Require(Path.IsPathRooted(output), "Use an absolute evidence directory.");
                        File.WriteAllBytes(Path.Combine(output, "unity-nested-cpu-" + restart + ".png"),
                            pixels.EncodeToPNG());
                    }
                    if (restart == 0)
                    {
                        for (var tick = 31; tick <= 45; ++tick)
                        {
                            Require(component.AdvancePreview(tick * .1) == NativeResult.Ok,
                                "Natural particle completion failed.");
                        }
                    }
                    else
                    {
                        Require(component.ForceTerminateChild(parentId, parentInstance) == NativeResult.Ok,
                            "Forcing the parent subtree failed.");
                        Require(component.RenderStatistics.activeSubmissions == 0 &&
                            component.ActiveRenderScopeCount == 1,
                            "Forcing a parent did not immediately release its descendant CPU rendering.");
                        Require(component.AdvancePreview(3.1) == NativeResult.Ok,
                            "Root idle completion after subtree force failed.");
                    }
                    Require(component.ActiveRenderScopeCount == 0 &&
                        component.RenderStatistics.activeSubmissions == 0,
                        "Nested retirement left a child CPU draw scope or submission.");
                    Require(Capture(camera, target, pixels) == 0, "Retirement left visible CPU pixels.");
                    Require(component.Stop() == NativeResult.Ok, "Stop failed.");
                }
                Debug.Log("PASS: compiler nested CPU snapshots, pixels, lifetime retention, subtree force and restart.");
            }
            catch (Exception error)
            {
                exitCode = 1;
                Debug.LogException(error);
            }
            finally
            {
                RenderTexture.active = previous;
                if (owner)
                {
                    UnityEngine.Object.DestroyImmediate(owner);
                }
                if (cameraObject)
                {
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                }
                if (target)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
                if (pixels)
                {
                    UnityEngine.Object.DestroyImmediate(pixels);
                }
                if (imported)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(exitCode);
                }
            }
        }

        private static int Capture(Camera camera, RenderTexture target, Texture2D pixels)
        {
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            pixels.Apply();
            return pixels.GetPixels32().Count(pixel => pixel.r > 25 || pixel.g > 25 || pixel.b > 25);
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
