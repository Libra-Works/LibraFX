using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    [InitializeOnLoad]
    public static class LiberaFxSpawnableGpuIntegrationVerification
    {
        private const string SessionKey = "LiberaFxSpawnableGpuIntegrationVerification.Active";
        private const string ReloadOptionsKey = "LiberaFxSpawnableGpuIntegrationVerification.ReloadOptions";
        private const string AssetFolder = "Assets/LiberaFxGpuIntegrationCheck";
        private const string ScopePackagePath = AssetFolder + "/FiniteGpu.lfxp";
        private const string RootPackagePath = AssetFolder + "/RootGpu.lfxp";
        private const string RootAutoIdlePackagePath = AssetFolder + "/RootGpuAutoIdle.lfxp";
        private const string LegacyPackagePath = AssetFolder + "/LegacyGpu.lfxp";
        private const string ScopeComputePath = AssetFolder + "/FiniteGpu.compute";
        private const string ScopeMetadataPath = AssetFolder + "/FiniteGpu.json";
        private const string RootComputePath = AssetFolder + "/RootGpu.compute";
        private const string RootMetadataPath = AssetFolder + "/RootGpu.json";

        private static readonly List<SpawnableHostEvent> hostEvents = new List<SpawnableHostEvent>();
        private static readonly List<string> proof = new List<string>();
        private static GameObject target;
        private static Camera testCamera;
        private static LiberaFxSpawnableComponent component;
        private static LiberaFxUnityBindingAsset bindingsAsset;
        private static Material cpuMaterial, gpuMaterial;
        private static Mesh cubeMesh;
        private static LibraGpuParticleRuntime zeroDeltaRuntime, rootGpuRuntimeAtEnd;
        private static LibraGpuParticle[] zeroDeltaBaseline;
        private static uint zeroDeltaFrame;
        private static int zeroDeltaDispatchCount, zeroDeltaDrawCount, rootGpuDispatchCountBeforeEnd;
        private static string finiteGpuNodeId, rootGpuNodeId;
        private static int phase, afterFrame, errors;
        private static double deadline, phaseStart;
        private static bool releasedFinitePlayRate, requestedNaturalEnd;

        static LiberaFxSpawnableGpuIntegrationVerification()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("LiberaFx/Verify scoped GPU Spawnable integration (D3D11)")]
        public static void Run()
        {
            try
            {
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11,
                    "Run this verification with -force-d3d11; -nographics is unsupported.");
                Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Already in Play Mode.");
                Require(!Directory.Exists(AssetFolder), "GPU verification asset folder already exists; refusing to overwrite it.");
                var scopePackage = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_SCOPE_PACKAGE");
                var scopeIds = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_SCOPE_IDS");
                var scopeCompute = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_SCOPE_COMPUTE");
                var scopeMetadata = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_SCOPE_METADATA");
                var rootPackage = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_ROOT_PACKAGE");
                var rootAutoIdlePackage = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_ROOT_AUTOIDLE_PACKAGE");
                var legacyPackage = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_LEGACY_PACKAGE");
                var rootNodeId = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_ROOT_NODE_ID");
                var rootCompute = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_ROOT_COMPUTE");
                var rootMetadata = RequiredEnvironmentPath("LIBERAFX_UNITY_GPU_ROOT_METADATA");
                foreach (var path in new[] { scopePackage, scopeIds, scopeCompute, scopeMetadata, rootPackage,
                    rootAutoIdlePackage, legacyPackage, rootNodeId, rootCompute, rootMetadata })
                    Require(File.Exists(path), "GPU verification input is missing: " + path);

                var ids = JsonUtility.FromJson<GpuFixtureIdentities>(File.ReadAllText(scopeIds));
                Require(ids != null && IsGuid(ids.gpuNodeId),
                    "Finite GPU fixture identity file is invalid.");
                finiteGpuNodeId = ids.gpuNodeId;
                rootGpuNodeId = File.ReadAllText(rootNodeId).Trim();
                Require(IsGuid(rootGpuNodeId), "Root GPU node identity is invalid.");

                proof.Clear();
                proof.Add("Unity: " + Application.unityVersion);
                proof.Add("Requested graphics API: Direct3D11");
                SessionState.SetBool(ReloadOptionsKey, EditorSettings.enterPlayModeOptionsEnabled);
                SessionState.SetBool(SessionKey, true);
                Directory.CreateDirectory(AssetFolder);
                CopyFixture(scopePackage, ScopePackagePath);
                CopyFixture(rootPackage, RootPackagePath);
                CopyFixture(rootAutoIdlePackage, RootAutoIdlePackagePath);
                CopyFixture(legacyPackage, LegacyPackagePath);
                CopyFixture(scopeCompute, ScopeComputePath);
                CopyFixture(scopeMetadata, ScopeMetadataPath);
                CopyFixture(rootCompute, RootComputePath);
                CopyFixture(rootMetadata, RootMetadataPath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                foreach (var path in new[] { ScopePackagePath, RootPackagePath, RootAutoIdlePackagePath,
                    LegacyPackagePath, ScopeComputePath, ScopeMetadataPath, RootComputePath, RootMetadataPath })
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                Require(AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(ScopePackagePath) &&
                    AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(RootPackagePath) &&
                    AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(RootAutoIdlePackagePath) &&
                    AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(LegacyPackagePath),
                    "Unity did not import all GPU Spawnable packages.");
                WriteResult("STARTED\n");
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
            }
            catch (Exception error) { Finish(error); }
        }

        private static string ResultPath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "spawnable-gpu-integration-verification.txt");

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(SessionKey, false)) return;
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Finish(new InvalidOperationException("Play Mode ended before scoped GPU verification completed."));
                return;
            }
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            try
            {
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11,
                    "GPU Spawnable verification requires a D3D11 device.");
                var ids = JsonUtility.FromJson<GpuFixtureIdentities>(
                    File.ReadAllText(Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GPU_SCOPE_IDS")));
                finiteGpuNodeId = ids.gpuNodeId;
                rootGpuNodeId = File.ReadAllText(
                    Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GPU_ROOT_NODE_ID")).Trim();
                Require(IsGuid(finiteGpuNodeId) && IsGuid(rootGpuNodeId),
                    "GPU fixture identities could not be restored after domain reload.");

                hostEvents.Clear();
                errors = 0;
                phase = 0;
                releasedFinitePlayRate = requestedNaturalEnd = false;
                zeroDeltaRuntime = rootGpuRuntimeAtEnd = null;
                zeroDeltaBaseline = null;
                Time.captureDeltaTime = 0.1f;
                Time.timeScale = 1;
                CreateBindings();
                testCamera = new GameObject("LIBERA FX GPU verification camera").AddComponent<Camera>();
                testCamera.tag = "MainCamera";
                testCamera.transform.position = new Vector3(0, 0, -15);
                testCamera.clearFlags = CameraClearFlags.SolidColor;
                testCamera.backgroundColor = Color.black;

                target = new GameObject("LIBERA FX scoped GPU verification");
                target.SetActive(false);
                component = target.AddComponent<LiberaFxSpawnableComponent>();
                var settings = new SerializedObject(component);
                settings.FindProperty("effect").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(ScopePackagePath);
                settings.FindProperty("engineBindings").objectReferenceValue = bindingsAsset;
                // Keep the finite Spawnable alive until the verification update has
                // observed and inspected its newly created native-owned scope.
                settings.FindProperty("playRate").floatValue = 0;
                settings.ApplyModifiedPropertiesWithoutUndo();
                component.HostEventReceived += item =>
                {
                    hostEvents.Add(item);
                    if (item.Kind != 1 || !ReferenceEquals(zeroDeltaRuntime, null)) return;
                    var runtimes = ActiveGpuRuntimes();
                    if (runtimes.Length != 1) return;
                    zeroDeltaRuntime = runtimes[0];
                    zeroDeltaBaseline = new LibraGpuParticle[zeroDeltaRuntime.Capacity];
                    zeroDeltaRuntime.ReadbackForDiagnostics(zeroDeltaBaseline);
                    zeroDeltaFrame = ReadFrame(zeroDeltaRuntime);
                    zeroDeltaDispatchCount = zeroDeltaRuntime.DispatchCount;
                    zeroDeltaDrawCount = zeroDeltaRuntime.DrawCount;
                };
                component.RuntimeError += result => ++errors;
                Application.logMessageReceived += OnLog;
                target.SetActive(true);
                deadline = EditorApplication.timeSinceStartup + 45;
                phaseStart = EditorApplication.timeSinceStartup;
                EditorApplication.update += Tick;
            }
            catch (Exception error) { Finish(error); }
        }

        private static void Tick()
        {
            try
            {
                Require(EditorApplication.timeSinceStartup < deadline, "Timed out in GPU verification phase " + phase + ".");
                Require(errors == 0, "Spawnable reported a Unity GPU/runtime failure.");
                if (Time.frameCount < afterFrame) return;
                switch (phase)
                {
                    case 0:
                    {
                        var runtimes = ActiveGpuRuntimes();
                        if (runtimes.Length == 0 && EditorApplication.timeSinceStartup - phaseStart > 5)
                        {
                            component.GetTemplateCount(out var count);
                            throw new InvalidOperationException("Finite GPU Spawnable produced no active GPU runtime. " +
                                "hostEvents=" + string.Join(",", hostEvents.Select(item => item.Kind.ToString())) +
                                ", templateCount=" + count + ", rootFinished=" + component.IsRootFinished +
                                ", scopes=" + component.ActiveRenderScopeCount + ", LastError=" + component.LastError);
                        }
                        if (runtimes.Length == 0) return;
                        Require(hostEvents.Any(item => item.Kind == 1),
                            "Finite GPU fixture did not create its GPU Spawnable from OnStart.");
                        var runtime = runtimes[0];
                        if (!releasedFinitePlayRate)
                        {
                            Require(zeroDeltaRuntime != null && zeroDeltaBaseline != null &&
                                component.ElapsedTime == 0 && ReferenceEquals(runtime, zeroDeltaRuntime) &&
                                runtime.DispatchCount == zeroDeltaDispatchCount &&
                                runtime.DrawCount == zeroDeltaDrawCount && ReadFrame(runtime) == zeroDeltaFrame &&
                                ReadbackEquals(runtime, zeroDeltaBaseline),
                                "PlayRate=0 dispatched GPU work, advanced its frame counter, or mutated its particle buffer.");
                            proof.Add("PASS: PlayRate=0 left GPU dispatch/draw counters, frame counter and particle buffer untouched.");
                            SetPlayRate(1);
                            releasedFinitePlayRate = true;
                            Next(0);
                            return;
                        }
                        if (!runtime.RuntimeExecuted || runtime.DispatchCount == 0 || runtime.DrawCount == 0) return;
                        if (!HasLiveParticles(runtime)) return;
                        VerifyGpuReadback(runtime);
                        proof.Add("PASS: Finite Spawnable GPU payload matched imported ComputeShader/metadata; " +
                            "D3D11 dispatched compute, submitted billboard draw, and read back particle buffer.");
                        Require(component.Stop() == NativeResult.Ok,
                            "Stopping the finite GPU Spawnable failed.");
                        Require(ActiveGpuRuntimes().Length == 0,
                            "Stopping the finite GPU Spawnable left an active GPU runtime.");
                        Require(component.ActiveRenderScopeCount == 0,
                            "Stopping the finite GPU Spawnable left an owned render scope.");
                        SetEffect(RootPackagePath);
                        Require(component.Play() == NativeResult.Ok,
                            "Could not start the root GPU package with AutoDestroyWhenIdle=false.");
                        Next(1);
                        break;
                    }
                    case 1:
                    {
                        var runtimes = ActiveGpuRuntimes();
                        if (runtimes.Length == 0 || !runtimes[0].RuntimeExecuted ||
                            runtimes[0].DispatchCount == 0 || runtimes[0].DrawCount == 0) return;
                        VerifyGpuReadback(runtimes[0]);
                        Require(!component.IsRootFinished,
                            "Root GPU completed before an explicit natural end request.");
                        rootGpuRuntimeAtEnd = runtimes[0];
                        rootGpuDispatchCountBeforeEnd = rootGpuRuntimeAtEnd.DispatchCount;
                        Require(component.RequestNaturalEnd() == NativeResult.Ok,
                            "RequestNaturalEnd failed for the root GPU effect.");
                        requestedNaturalEnd = true;
                        Next(2);
                        break;
                    }
                    case 2:
                        if (!component.IsRootFinished) return;
                        Require(requestedNaturalEnd && component.ActiveRenderScopeCount == 0 &&
                            ActiveGpuRuntimes().Length == 0 && !ReferenceEquals(rootGpuRuntimeAtEnd, null) &&
                            rootGpuRuntimeAtEnd.DispatchCount == rootGpuDispatchCountBeforeEnd,
                            "Root GPU scope/resources did not retire before any post-completion dispatch.");
                        proof.Add("PASS: Root GPU Play/Tick/readback/RequestNaturalEnd cleanup.");
                        proof.Add("PASS: Root completion was queried before GPU update; no post-completion dispatch occurred.");
                        VerifyPreflightRejections();
                        Finish(null);
                        break;
                }
            }
            catch (Exception error) { Finish(error); }
        }

        private static void VerifyPreflightRejections()
        {
            var eventCount = hostEvents.Count;
            SetEffect(RootAutoIdlePackagePath);
            var autoIdle = component.Play();
            Require(autoIdle == NativeResult.UnsupportedBinary &&
                component.LastError != null && component.LastError.Contains("AutoDestroyWhenIdle=false") &&
                component.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration, out _) == NativeResult.NotReady &&
                hostEvents.Count == eventCount,
                "Root GPU with AutoDestroyWhenIdle was not clearly rejected before native OnStart.");
            proof.Add("PASS: Root GPU + AutoDestroyWhenIdle rejected with actionable policy diagnostic.");

            SetEffect(LegacyPackagePath);
            var legacy = component.Play();
            Require(legacy == NativeResult.InvalidArgument &&
                component.LastError != null &&
                component.LastError.Contains("executable GpuPrograms payload") &&
                component.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration, out _) == NativeResult.NotReady &&
                hostEvents.Count == eventCount,
                "Legacy GPU authored snapshots without executable payload were not rejected before OnStart.");
            proof.Add("PASS: Legacy authored GPU snapshot without executable payload rejected before native OnStart.");
        }

        private static void VerifyGpuReadback(LibraGpuParticleRuntime runtime)
        {
            Require(runtime && runtime.IsConfigured && runtime.IsRunning &&
                runtime.SourceContentHash != null && runtime.SourceHash != null,
                "GPU runtime did not retain the imported executable program.");
            var particles = new LibraGpuParticle[runtime.Capacity];
            runtime.ReadbackForDiagnostics(particles);
            Require(particles.Any(particle => particle.alive != 0),
                "D3D11 particle-buffer readback contained no live spawned particles.");
        }

        private static bool HasLiveParticles(LibraGpuParticleRuntime runtime)
        {
            var particles = new LibraGpuParticle[runtime.Capacity];
            runtime.ReadbackForDiagnostics(particles);
            return particles.Any(particle => particle.alive != 0);
        }

        private static uint ReadFrame(LibraGpuParticleRuntime runtime)
        {
            var field = typeof(LibraGpuParticleRuntime).GetField("frame",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Require(field != null, "GPU frame diagnostic field is missing.");
            return (uint)field.GetValue(runtime);
        }

        private static bool ReadbackEquals(LibraGpuParticleRuntime runtime, LibraGpuParticle[] expected)
        {
            var actual = new LibraGpuParticle[runtime.Capacity];
            runtime.ReadbackForDiagnostics(actual);
            if (actual.Length != expected.Length) return false;
            for (var index = 0; index < actual.Length; ++index)
            {
                var left = actual[index];
                var right = expected[index];
                if (!left.positionAge.Equals(right.positionAge) ||
                    !left.velocityLifetime.Equals(right.velocityLifetime) ||
                    !left.color.Equals(right.color) || !left.sizeRotation.Equals(right.sizeRotation) ||
                    left.seed != right.seed || left.alive != right.alive ||
                    left.spare0 != right.spare0 || left.spare1 != right.spare1)
                    return false;
            }
            return true;
        }

        private static void CreateBindings()
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
            var cpuShader = Shader.Find("Unlit/Color");
            var gpuShader = Shader.Find("Libra/GPU Particle Billboard");
            Require(cpuShader != null && cpuShader.isSupported &&
                gpuShader != null && gpuShader.isSupported,
                "CPU or GPU verification shader is missing or unsupported.");
            cpuMaterial = new Material(cpuShader);
            gpuMaterial = new Material(gpuShader) { enableInstancing = true };
            Require(cpuMaterial.HasProperty("_Color") && gpuMaterial.HasProperty("_Color"),
                "Verification materials do not expose required tint properties.");

            var scopeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ScopeComputePath);
            var scopeMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>(ScopeMetadataPath);
            var rootCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(RootComputePath);
            var rootMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>(RootMetadataPath);
            Require(scopeCompute && scopeMetadata && rootCompute && rootMetadata,
                "Imported GPU ComputeShader or metadata TextAsset is missing.");

            bindingsAsset = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var settings = new SerializedObject(bindingsAsset);
            var assets = settings.FindProperty("renderAssets");
            assets.arraySize = 1;
            var asset = assets.GetArrayElementAtIndex(0);
            asset.FindPropertyRelative("assetIdentity").stringValue = "/Engine/BasicShapes/Cube.Cube";
            asset.FindPropertyRelative("mesh").objectReferenceValue = cubeMesh;
            var defaults = asset.FindPropertyRelative("defaultMaterials");
            defaults.arraySize = 1;
            defaults.GetArrayElementAtIndex(0).objectReferenceValue = cpuMaterial;
            var tints = asset.FindPropertyRelative("defaultTintPropertyNames");
            tints.arraySize = 1;
            tints.GetArrayElementAtIndex(0).stringValue = "_Color";

            var programs = settings.FindProperty("gpuPrograms");
            programs.arraySize = 2;
            SetGpuBinding(programs.GetArrayElementAtIndex(0), finiteGpuNodeId, scopeCompute,
                scopeMetadata, 20);
            SetGpuBinding(programs.GetArrayElementAtIndex(1), rootGpuNodeId, rootCompute,
                rootMetadata, 20);
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetGpuBinding(SerializedProperty binding, string nodeId,
            ComputeShader compute, TextAsset metadata, float spawnRate)
        {
            binding.FindPropertyRelative("nodeId").stringValue = nodeId;
            binding.FindPropertyRelative("computeShader").objectReferenceValue = compute;
            binding.FindPropertyRelative("metadata").objectReferenceValue = metadata;
            binding.FindPropertyRelative("billboardMaterial").objectReferenceValue = gpuMaterial;
            binding.FindPropertyRelative("spawnRate").floatValue = spawnRate;
            binding.FindPropertyRelative("rendererMaterialIdentity").stringValue = "builtin:unlit-alpha";
            binding.FindPropertyRelative("sizeScale").floatValue = 100;
            var textures = binding.FindPropertyRelative("textures");
            textures.arraySize = 1;
            textures.GetArrayElementAtIndex(0).FindPropertyRelative("resourceId").stringValue = "billboardTexture";
            textures.GetArrayElementAtIndex(0).FindPropertyRelative("texture").objectReferenceValue =
                Texture2D.whiteTexture;
        }

        private static LibraGpuParticleRuntime[] ActiveGpuRuntimes() =>
            target ? target.GetComponentsInChildren<LibraGpuParticleRuntime>()
                .Where(runtime => runtime && runtime.isActiveAndEnabled && runtime.IsConfigured)
                .ToArray() : Array.Empty<LibraGpuParticleRuntime>();

        private static void SetEffect(string path)
        {
            var settings = new SerializedObject(component);
            settings.FindProperty("effect").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(path);
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetPlayRate(float value)
        {
            var settings = new SerializedObject(component);
            settings.FindProperty("playRate").floatValue = value;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Next(int nextPhase)
        {
            phase = nextPhase;
            afterFrame = Time.frameCount + 2;
            phaseStart = EditorApplication.timeSinceStartup;
        }

        private static void CopyFixture(string source, string destination)
        {
            var fullDestination = Path.GetFullPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination));
            if (File.Exists(fullDestination))
                throw new InvalidOperationException("Refusing to overwrite GPU verification asset " + destination + ".");
            File.Copy(source, fullDestination);
        }

        private static string RequiredEnvironmentPath(string name)
        {
            var path = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Set " + name + " for isolated D3D11 verification.");
            return path;
        }

        private static bool IsGuid(string value) => Guid.TryParse(value, out _);

        private static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) ++errors;
        }

        private static void WriteResult(string result) => File.WriteAllText(ResultPath, result);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            var ownsAssets = SessionState.GetBool(SessionKey, false);
            SessionState.EraseBool(SessionKey);
            Time.captureDeltaTime = 0;
            Time.timeScale = 1;
            if (target) UnityEngine.Object.Destroy(target);
            if (testCamera) UnityEngine.Object.Destroy(testCamera.gameObject);
            if (bindingsAsset) UnityEngine.Object.DestroyImmediate(bindingsAsset);
            if (cpuMaterial) UnityEngine.Object.DestroyImmediate(cpuMaterial);
            if (gpuMaterial) UnityEngine.Object.DestroyImmediate(gpuMaterial);
            if (ownsAssets)
            {
                EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(ReloadOptionsKey, false);
                SessionState.EraseBool(ReloadOptionsKey);
                AssetDatabase.DeleteAsset(AssetFolder);
            }
            if (error != null)
            {
                WriteResult("FAIL\n" + error);
                Debug.LogException(error);
            }
            else WriteResult(string.Join("\n", proof) + "\n");
            EditorApplication.Exit(error == null ? 0 : 1);
        }

        [Serializable]
        private sealed class GpuFixtureIdentities
        {
            public string gpuNodeId;
        }
    }
}
