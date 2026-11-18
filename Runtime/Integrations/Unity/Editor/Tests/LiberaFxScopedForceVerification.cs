using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    // Called explicitly by an isolated project's verification runner. No launch,
    // imports, user settings, asset writes, global time, or existing objects are
    // changed. Supply a package with a repeatable root spawn signal and bindings.
    public static class LiberaFxScopedForceVerification
    {
        [Serializable]
        private sealed class FixtureIds { public string spawnSignal = string.Empty; }

        // Explicit batch entry point for a dedicated local verification project only.
        public static void RunBatch()
        {
            var resultPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "scoped-force-result.txt");
            LiberaFxSpawnableAsset package = null;
            LiberaFxUnityBindingAsset bindings = null;
            Material material = null;
            GameObject primitive = null;
            try
            {
                var directory = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_SCOPED_FIXTURE_DIR");
                Require(!string.IsNullOrEmpty(directory) && Path.IsPathRooted(directory),
                    "Supply an absolute compiler-produced adapter fixture directory.");
                var ids = JsonUtility.FromJson<FixtureIds>(File.ReadAllText(Path.Combine(directory, "GameIo.identities.json")));
                package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                package.SetPackageBytes(File.ReadAllBytes(Path.Combine(directory, "GameIo.lfxp")));
                var shader = Shader.Find("Standard");
                Require(shader && shader.isSupported, "The isolated project requires a supported Standard shader.");
                material = new Material(shader);
                primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                primitive.SetActive(false);
                bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                var serialized = new SerializedObject(bindings);
                var assets = serialized.FindProperty("renderAssets");
                assets.arraySize = 1;
                var asset = assets.GetArrayElementAtIndex(0);
                asset.FindPropertyRelative("assetIdentity").stringValue = "/Engine/BasicShapes/Cube.Cube";
                asset.FindPropertyRelative("mesh").objectReferenceValue = primitive.GetComponent<MeshFilter>().sharedMesh;
                var defaults = asset.FindPropertyRelative("defaultMaterials");
                defaults.arraySize = 1;
                defaults.GetArrayElementAtIndex(0).objectReferenceValue = material;
                var tintNames = asset.FindPropertyRelative("defaultTintPropertyNames");
                tintNames.arraySize = 1;
                tintNames.GetArrayElementAtIndex(0).stringValue = "_Color";
                var materials = serialized.FindProperty("materials");
                materials.arraySize = 1;
                var binding = materials.GetArrayElementAtIndex(0);
                binding.FindPropertyRelative("assetIdentity").stringValue = "builtin:unlit-color";
                binding.FindPropertyRelative("material").objectReferenceValue = material;
                binding.FindPropertyRelative("tintPropertyName").stringValue = "_Color";
                binding.FindPropertyRelative("parameterMappings").arraySize = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Run(package, bindings, ids.spawnSignal, false);
                File.WriteAllText(resultPath, "PASS: actual native scoped force, independent EndRequest, renderer cleanup and restart\n");
            }
            catch (Exception error)
            {
                File.WriteAllText(resultPath, "FAIL\n" + error);
                Debug.LogException(error);
                EditorApplication.Exit(2);
            }
            finally
            {
                if (primitive) UnityEngine.Object.DestroyImmediate(primitive);
                if (bindings) UnityEngine.Object.DestroyImmediate(bindings);
                if (package) UnityEngine.Object.DestroyImmediate(package);
                if (material) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        public static void Run(LiberaFxSpawnableAsset package, LiberaFxUnityBindingAsset bindings,
            string rootSpawnSignalGuid, bool requireGpu)
        {
            if (!package || !Guid.TryParse(rootSpawnSignalGuid, out _))
                throw new ArgumentException("Supply an isolated package and its repeatable root spawn entry UUID.");
            var owner = new GameObject("LIBERA FX scoped force isolated verification");
            owner.SetActive(false);
            try
            {
                var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                var serialized = new SerializedObject(component);
                serialized.FindProperty("effect").objectReferenceValue = package;
                serialized.FindProperty("engineBindings").objectReferenceValue = bindings;
                serialized.FindProperty("playOnEnable").boolValue = false;
                serialized.FindProperty("playRate").floatValue = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var events = new List<SpawnableHostEvent>();
                component.HostEventReceived += events.Add;
                owner.SetActive(true);
                Require(component.Play() == NativeResult.Ok, "Initial playback failed.");
                events.Clear();
                Require(component.NotifyEndRequest(rootSpawnSignalGuid) == NativeResult.Ok &&
                    component.NotifyEndRequest(rootSpawnSignalGuid) == NativeResult.Ok,
                    "Root EndRequest did not execute both user signal arrivals.");
                var spawns = events.Where(item => item.EventKind == SpawnableHostEventKind.Spawn).ToArray();
                Require(spawns.Length == 2 && spawns[0].ContainerId.SequenceEqual(spawns[1].ContainerId),
                    "Fixture must create exactly one same-template child per request.");
                var first = spawns[0];
                var firstScope = FindScope(owner, first);
                var siblingScope = FindScope(owner, spawns[1]);
                var rootScope = owner.transform.Find("Libera FX Root Render Scope");
                Require(firstScope && siblingScope && rootScope, "Expected real root/child render scope objects.");
                var gpu = firstScope.GetComponentsInChildren<LibraGpuParticleRuntime>(true);
                if (requireGpu) Require(gpu.Length > 0 && gpu.All(item => item.IsRunning),
                    "GPU fixture must own running GPU work in each child.");
                var before = component.ActiveRenderScopeCount;
                events.Clear();
                Require(component.ForceTerminateChild(first.ContainerId, first.InstanceId) == NativeResult.Ok,
                    "Child force failed.");
                Require(component.ActiveRenderScopeCount == before - 1 &&
                    (!firstScope || !firstScope.gameObject.activeInHierarchy) &&
                    siblingScope && siblingScope.gameObject.activeInHierarchy &&
                    rootScope && rootScope.gameObject.activeInHierarchy,
                    "Force did not retire exactly the affected scope.");
                Require(gpu.All(item => !item || (!item.IsRunning && !item.IsConfigured)),
                    "Forced GPU work remained running/configured.");
                Require(events.Count == 1 && events[0].EventKind == SpawnableHostEventKind.ForceTerminated &&
                    events[0].InstanceId == first.InstanceId &&
                    events[0].ContainerId.SequenceEqual(first.ContainerId),
                    "Force was not forwarded once with the exact identity (or synthesized Destroy).");
                Require(component.ForceTerminateChild(first.ContainerId, first.InstanceId) == NativeResult.Ok &&
                    events.Count == 1, "Duplicate force replayed a notification.");
                events.Clear();
                Require(component.ForceTerminate() == NativeResult.Ok && component.IsForceTerminated &&
                    component.IsRootFinished && component.ActiveRenderScopeCount == 0,
                    "Whole-effect force retained root/child render scopes.");
                Require(events.Count == 1 && events[0].EventKind == SpawnableHostEventKind.ForceTerminated &&
                    events[0].InstanceId == 0 && events[0].ContainerId.All(value => value == 0),
                    "Whole-effect kind 5 invalidation identity was not forwarded.");
                Require(component.ForceTerminate() == NativeResult.Ok && events.Count == 1,
                    "Whole-effect duplicate replayed an event.");
                Require(component.Play() == NativeResult.Ok && !component.IsForceTerminated,
                    "Restart after force failed.");
                var reentrantForce = NativeResult.NotReady;
                Action<SpawnableHostEvent> forceOnSpawn = item =>
                {
                    if (item.EventKind == SpawnableHostEventKind.Spawn)
                        reentrantForce = component.ForceTerminate();
                };
                component.HostEventReceived += forceOnSpawn;
                Require(component.NotifyEndRequest(rootSpawnSignalGuid) == NativeResult.Ok &&
                    reentrantForce == NativeResult.Ok && component.ActiveRenderScopeCount == 0,
                    "Force from an event callback retained/resurrected a stale render scope.");
                component.HostEventReceived -= forceOnSpawn;
                Require(component.Stop() == NativeResult.Ok, "Cleanup Stop failed after restart.");
            }
            finally
            {
                owner.SetActive(false); // stop immediately, even when Destroy is deferred
                if (Application.isPlaying) UnityEngine.Object.Destroy(owner);
                else UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static Transform FindScope(GameObject owner, SpawnableHostEvent item)
        {
            var hex = string.Concat(item.ContainerId.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            var template = Guid.ParseExact(hex, "N").ToString();
            return owner.transform.Find("Libera FX Spawnable " + template + " #" +
                item.InstanceId.ToString(CultureInfo.InvariantCulture));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
