using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    [InitializeOnLoad]
    public static class LiberaFxSpawnablePlayModeVerification
    {
        private const string SessionKey = "LiberaFxSpawnablePlayModeVerification.Active";
        private const string ReloadOptionsKey = "LiberaFxSpawnablePlayModeVerification.ReloadOptions";
        private const string AssetPath = "Assets/LiberaFxSpawnablePlayModeCheck.lfxp";
        private static readonly List<SpawnableHostEvent> events = new List<SpawnableHostEvent>();
        private static readonly List<int> eventFrames = new List<int>();
        private static readonly List<string> proof = new List<string>();
        private static GameObject target;
        private static LiberaFxSpawnableComponent component;
        private static LiberaFxUnityBindingAsset bindingsAsset;
        private static Material sharedMaterial;
        private static Mesh sphereMesh;
        private static int phase, afterFrame, errors;
        private static bool waitedForChildren;
        private static double deadline, stoppedAt, startUnityTime;
        private static string ResultPath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "spawnable-playmode-verification.txt");

        static LiberaFxSpawnablePlayModeVerification()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        // Use only in a dedicated project; Run enters Play Mode and exits the Editor.
        public static void Run()
        {
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Already in Play Mode.");
                var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_SPAWNABLE_FIXTURE");
                Require(!string.IsNullOrEmpty(fixture) && File.Exists(fixture),
                    "Set LIBERAFX_UNITY_SPAWNABLE_FIXTURE to the compiler-produced Fireworks.lfxp file.");
                Require(!File.Exists(AssetPath), "Verification asset already exists; refusing to overwrite it.");
                File.WriteAllText(ResultPath, "STARTED\n");
                File.Copy(fixture, AssetPath);
                SessionState.SetBool(ReloadOptionsKey, EditorSettings.enterPlayModeOptionsEnabled);
                SessionState.SetBool(SessionKey, true);
                AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(AssetPath);
                Require(asset && asset.PackageBytes.SequenceEqual(File.ReadAllBytes(fixture)),
                    "LFXP importer did not preserve the complete binary.");
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
            }
            catch (Exception error) { Finish(error); }
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(SessionKey, false)) return;
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Finish(new InvalidOperationException("Play Mode ended before verification completed."));
                return;
            }
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            try
            {
                proof.Clear();
                events.Clear();
                eventFrames.Clear();
                errors = 0;
                phase = 0;
                waitedForChildren = false;
                proof.Add("Unity: " + Application.unityVersion);
                proof.Add("Mode: actual Play Mode; automatic MonoBehaviour callbacks");
                proof.Add("LFXP importer preserved compiler-produced package bytes: PASS");
                Time.captureDeltaTime = 0.25f;
                Time.timeScale = 1;
                target = new GameObject("LIBERA FX Spawnable Play Mode verification");
                target.SetActive(false);
                component = target.AddComponent<LiberaFxSpawnableComponent>();
                Require(component.GetTemplateCount(out _) == NativeResult.NotReady &&
                    component.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration, out _) == NativeResult.NotReady,
                    "An inactive component exposed template metadata without a host.");
                var settings = new SerializedObject(component);
                settings.FindProperty("effect").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(AssetPath);
                settings.FindProperty("playRate").floatValue = 0.5f;
                settings.ApplyModifiedPropertiesWithoutUndo();
                component.HostEventReceived += item =>
                {
                    events.Add(item);
                    eventFrames.Add(Time.frameCount);
                };
                var invalidBindingResult = component.Play();
                Require(invalidBindingResult == NativeResult.InvalidArgument && events.Count == 0 &&
                    component.LastError != null &&
                    component.LastError.Contains("No Unity asset binding exists", StringComparison.Ordinal) &&
                    component.GetTemplateCount(out _) == NativeResult.NotReady &&
                    target.GetComponentsInChildren<MeshRenderer>(true).Length == 0,
                    "Unresolved render bindings must fail before OnStart/spawn forwarding or visible objects.");
                proof.Add("Missing Unity asset binding rejected before native OnStart or renderer creation: PASS");

                CreateBindings();
                settings = new SerializedObject(component);
                settings.FindProperty("engineBindings").objectReferenceValue = bindingsAsset;
                settings.ApplyModifiedPropertiesWithoutUndo();
                VerifyRejectedPreflight("no explicit Unity shader-property mapping");
                proof.Add("Missing authored material-property mapping rejected before native OnStart: PASS");
                SetMaterialPropertyMappings("Color", "_Color", "Color", "_Gain");
                VerifyRejectedPreflight("duplicate authored name 'Color'");
                proof.Add("Duplicate authored material-property mappings rejected before native OnStart: PASS");
                SetMaterialPropertyMappings("Color", "_Color", "Tint", "_Color");
                VerifyRejectedPreflight("duplicate Unity target '_Color'");
                proof.Add("Duplicate Unity shader-property targets rejected before native OnStart: PASS");
                SetMaterialPropertyMappings("Color", "_Glossiness");
                VerifyRejectedPreflight("incompatible with authored vector parameter 'Color'");
                proof.Add("Mapped shader-property type mismatch rejected before native OnStart: PASS");
                SetMaterialPropertyMappings("Color", "_MissingProperty");
                VerifyRejectedPreflight("does not exist on Material binding 'builtin:unlit-color'");
                proof.Add("Missing Unity shader-property target rejected before native OnStart: PASS");
                SetMaterialPropertyMappings("Color", "_Color");
                component.RuntimeError += error => throw new InvalidOperationException("Runtime error: " + error);
                Application.logMessageReceived += OnLog;
                target.SetActive(true);
                startUnityTime = Time.timeAsDouble;
                Require(events.Count == 1 && events[0].Kind == 1 && events[0].Time == 0,
                    "Automatic OnEnable did not spawn Launch at zero.");
                VerifySignalEventForwarding();
                VerifyVisibleRenderers();
                proof.Add("Spawn created Unity mesh objects using borrowed shared assets: PASS");
                Require(component.GetTemplateCount(out var templates) == NativeResult.Ok && templates == 2 &&
                    component.GetTemplateId(0, out var templateId) == NativeResult.Ok &&
                    component.GetTemplatePayload(templateId, SpawnableTemplatePayloadKind.RenderConfiguration,
                        out var render) == NativeResult.Ok && render.Length > 0 &&
                    System.Text.Encoding.UTF8.GetString(render).Contains("\"name\":\"Color\"", StringComparison.Ordinal) &&
                    component.GetTemplatePayload(events[0].ContainerId, SpawnableTemplatePayloadKind.GpuContainers,
                        out var gpu) == NativeResult.Ok && System.Text.Encoding.UTF8.GetString(gpu) == "[]",
                    "The running component did not preserve the authored material parameter name or expose payloads.");
                proof.Add("Template identities and owned render/GPU payload access through the component: PASS");
                Require(component.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration,
                    out var rootRender) == NativeResult.Ok && rootRender.Length > 0 &&
                    component.GetRootPayload(SpawnableTemplatePayloadKind.GpuContainers,
                    out var rootGpu) == NativeResult.Ok && System.Text.Encoding.UTF8.GetString(rootGpu) == "[]",
                    "The running component did not expose root payloads.");
                proof.Add("LFXP v2 root render/GPU payload access through the component: PASS");
                deadline = EditorApplication.timeSinceStartup + 60;
                EditorApplication.update += Tick;
            }
            catch (Exception error) { Finish(error); }
        }

        private static void Tick()
        {
            try
            {
                Require(EditorApplication.timeSinceStartup < deadline, "Timed out in phase " + phase);
                Require(errors == 0, "Unity reported an error during verification.");
                if (Time.frameCount < afterFrame) return;
                switch (phase)
                {
                    case 0:
                        Require(Math.Abs(component.ElapsedTime - (Time.timeAsDouble - startUnityTime) * 0.5) <= 0.25,
                            "Effect time did not advance at half Unity's game-time rate.");
                        if (component.ElapsedTime >= 2 && component.ElapsedTime < 3.5)
                        {
                            Require(!component.IsRootFinished, "Root finished before its child drained.");
                            waitedForChildren = true;
                        }
                        if (!component.IsRootFinished) return;
                        Require(waitedForChildren, "Did not observe root waiting for its child.");
                        VerifyNaturalEnd();
                        Require(target.GetComponentsInChildren<MeshRenderer>().Length == 0,
                            "Natural scope completion left a visible renderer behind.");
                        proof.Add("Automatic Update, half-rate playback, child draining and exactly-once natural Destroy: PASS");
                        stoppedAt = component.ElapsedTime;
                        Next(1, 3);
                        break;
                    case 1:
                        Require(events.Count == 6 && component.ElapsedTime == stoppedAt,
                            "Finished root continued updating or emitted duplicate events.");
                        Require(component.Stop() == NativeResult.Ok && events.Count == 6,
                            "Stop after natural completion emitted extra events.");
                        Require(component.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration,
                            out _) == NativeResult.NotReady, "Stopped component retained its root payload host.");
                        ClearEvents();
                        Time.captureDeltaTime = 5;
                        SetPlayRate(1);
                        Require(component.Play() == NativeResult.Ok,
                            "Could not restart completed effect for the skipped-frame regression.");
                        Next(8, 1);
                        break;
                    case 8:
                        Require(component.IsRootFinished && component.LastError == null &&
                            component.ActiveRenderScopeCount == 0 &&
                            target.GetComponentsInChildren<MeshRenderer>(true).Length == 0,
                            "A large frame crossing a finite lifetime left stale render scopes or failed playback.");
                        Require(HasSameFrameSpawnAndTerminalEvent(),
                            "The large frame did not deliver a Spawn and its terminal event in one native batch.");
                        proof.Add("Large frame crossing a complete finite lifetime skipped retired scopes while forwarding events: PASS");
                        Time.captureDeltaTime = 0.25f;
                        ClearEvents();
                        Require(component.Stop() == NativeResult.Ok, "Stop after skipped-frame completion failed.");
                        Require(component.Play() == NativeResult.Ok,
                            "Could not restart after skipped-frame completion.");
                        Time.timeScale = 0;
                        Next(2, 4);
                        break;
                    case 2:
                        Require(component.ElapsedTime == 0 && events.Count == 1,
                            "Unity timeScale zero did not pause lifecycle time.");
                        SetPlayRate(0);
                        Time.timeScale = 1;
                        Next(3, 4);
                        break;
                    case 3:
                        Require(component.ElapsedTime == 0 && events.Count == 1,
                            "PlayRate zero did not pause lifecycle time.");
                        proof.Add("Unity timeScale zero and PlayRate zero pause/resume: PASS");
                        SetPlayRate(1);
                        Next(4, 1);
                        break;
                    case 4:
                        if (component.ElapsedTime == 0) return;
                        Require(component.ElapsedTime < 2 && !component.IsRootFinished,
                            "Did not resume before the natural deadline.");
                        Require(component.Stop() == NativeResult.Ok, "Manual Stop failed.");
                        Require(component.GetTemplateCount(out _) == NativeResult.NotReady,
                            "Manual Stop retained metadata access to a released host.");
                        VerifyInterrupted();
                        Require(target.GetComponentsInChildren<MeshRenderer>().Length == 0,
                            "Manual Stop left a visible renderer behind.");
                        Require(component.FireGameSignal("unused") == NativeResult.NotReady,
                            "Manual Stop retained the native host.");
                        proof.Add("Manual Stop releases host without natural Destroy: PASS");
                        ClearEvents();
                        Require(component.Play() == NativeResult.Ok, "Restart after Stop failed.");
                        component.enabled = false;
                        VerifyInterrupted();
                        stoppedAt = component.ElapsedTime;
                        Next(5, 3);
                        break;
                    case 5:
                        Require(events.Count == 2 && component.ElapsedTime == stoppedAt,
                            "Disabled component kept updating.");
                        ClearEvents();
                        component.enabled = true;
                        Require(events.Count == 1 && events[0].Kind == 1 && component.ElapsedTime == 0,
                            "Automatic OnEnable did not restart from zero.");
                        proof.Add("Automatic OnDisable cleanup and OnEnable restart: PASS");
                        Next(6, 1);
                        break;
                    case 6:
                        Require(component.Play() == NativeResult.Ok, "Explicit restart failed.");
                        Require(events.Count == 3 && events[1].Kind == 2 && events[2].Kind == 1 &&
                            events[2].Time == 0 && component.ElapsedTime == 0,
                            "Restart did not interrupt the old instance and spawn a new one at zero.");
                        Require(component.Play(false) == NativeResult.Ok && events.Count == 3,
                            "Play without restart duplicated OnStart.");
                        UnityEngine.Object.Destroy(target);
                        Next(7, 3);
                        break;
                    case 7:
                        Require(target == null && events.Count == 4 && events[3].Kind == 2 &&
                            !events.Any(item => item.Kind == 3), "Destroy did not clean up without natural Destroy.");
                        proof.Add("Explicit restart, non-restarting Play and actual object destruction: PASS");
                        Finish(null);
                        break;
                }
            }
            catch (Exception error) { Finish(error); }
        }

        private static void VerifyNaturalEnd()
        {
            var kinds = new uint[] { 1, 3, 2, 1, 3, 2 };
            var times = new double[] { 0, 2, 2, 2, 3.5, 3.5 };
            Require(events.Count == kinds.Length, "Unexpected natural lifecycle event count.");
            for (var index = 0; index < events.Count; ++index)
            {
                var spawn = events[index < 3 ? 0 : 3];
                Require(events[index].Kind == kinds[index] && events[index].Time == times[index] &&
                    events[index].InstanceId == spawn.InstanceId &&
                    events[index].ContainerId.SequenceEqual(spawn.ContainerId),
                    "Natural lifecycle ordering, time or instance identity mismatch at event " + index);
            }
            Require(!events[0].ContainerId.SequenceEqual(events[3].ContainerId),
                "Launch did not spawn the distinct Burst template.");
        }

        private static void VerifyInterrupted()
        {
            Require(events.Count == 2 && events[0].Kind == 1 && events[1].Kind == 2 &&
                events[0].InstanceId == events[1].InstanceId &&
                events[0].ContainerId.SequenceEqual(events[1].ContainerId),
                "Interruption must emit Spawn/Stop, not natural Destroy.");
        }

        private static void VerifySignalEventForwarding()
        {
            var spawn = events[0];
            var spawnFrame = eventFrames[0];
            var scopeCount = component.ActiveRenderScopeCount;
            var instanceId = spawn.InstanceId + 0x10000;
            var signalNodeId = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
            var constructor = typeof(SpawnableHostEvent).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(uint), typeof(byte[]), typeof(ulong), typeof(double), typeof(ulong), typeof(byte[]) },
                null);
            Require(constructor != null, "Could not create the synthetic native Signal event for regression testing.");
            var signal = (SpawnableHostEvent)constructor.Invoke(new object[]
            {
                4u, spawn.ContainerId, instanceId, 0.125, 0xABCDEFUL, signalNodeId
            });
            var host = typeof(LiberaFxSpawnableComponent)
                .GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component);
            var batchHandler = typeof(LiberaFxSpawnableComponent).GetMethod(
                "ProcessHostEventBatch", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(host != null && batchHandler != null,
                "Could not access the event batch pipeline for the Signal regression.");
            batchHandler.Invoke(component, new object[] { host, new List<SpawnableHostEvent> { signal } });
            Require(events.Count == 2 && ReferenceEquals(events[1], signal) &&
                events[1].Kind == 4 && events[1].ContainerId.SequenceEqual(spawn.ContainerId) &&
                events[1].InstanceId == instanceId && events[1].SignalNodeId.SequenceEqual(signalNodeId) &&
                component.ActiveRenderScopeCount == scopeCount && component.LastError == null,
                "Signal events must be forwarded with their container/instance identity without changing render scopes.");
            ClearEvents();
            events.Add(spawn);
            eventFrames.Add(spawnFrame);
            proof.Add("Signal event kind 4 forwarded with container/instance/node identity and no scope mutation: PASS");
        }

        private static bool HasSameFrameSpawnAndTerminalEvent()
        {
            for (var spawnIndex = 0; spawnIndex < events.Count; ++spawnIndex)
            {
                var spawn = events[spawnIndex];
                if (spawn.Kind != 1) continue;
                for (var terminalIndex = 0; terminalIndex < events.Count; ++terminalIndex)
                {
                    var terminal = events[terminalIndex];
                    if ((terminal.Kind == 2 || terminal.Kind == 3) &&
                        terminal.InstanceId == spawn.InstanceId &&
                        terminal.ContainerId.SequenceEqual(spawn.ContainerId) &&
                        eventFrames[terminalIndex] == eventFrames[spawnIndex])
                        return true;
                }
            }
            return false;
        }

        private static void ClearEvents()
        {
            events.Clear();
            eventFrames.Clear();
        }

        private static void SetPlayRate(float value)
        {
            var settings = new SerializedObject(component);
            settings.FindProperty("playRate").floatValue = value;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateBindings()
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
            var shader = Shader.Find("Standard");
            Require(shader != null && shader.isSupported,
                "The isolated verification project needs a supported Standard shader.");
            sharedMaterial = new Material(shader);
            Require(sharedMaterial.HasProperty("_Color") && sharedMaterial.HasProperty("_Glossiness"),
                "The verification Standard shader must expose Color and Range properties.");
            bindingsAsset = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var settings = new SerializedObject(bindingsAsset);
            var assets = settings.FindProperty("renderAssets");
            assets.arraySize = 1;
            var asset = assets.GetArrayElementAtIndex(0);
            asset.FindPropertyRelative("assetIdentity").stringValue = "/Engine/BasicShapes/Sphere.Sphere";
            asset.FindPropertyRelative("mesh").objectReferenceValue = sphereMesh;
            var defaults = asset.FindPropertyRelative("defaultMaterials");
            defaults.arraySize = 1;
            defaults.GetArrayElementAtIndex(0).objectReferenceValue = sharedMaterial;
            var tintNames = asset.FindPropertyRelative("defaultTintPropertyNames");
            tintNames.arraySize = 1;
            tintNames.GetArrayElementAtIndex(0).stringValue = "_Color";

            var materials = settings.FindProperty("materials");
            materials.arraySize = 1;
            var material = materials.GetArrayElementAtIndex(0);
            material.FindPropertyRelative("assetIdentity").stringValue = "builtin:unlit-color";
            material.FindPropertyRelative("material").objectReferenceValue = sharedMaterial;
            material.FindPropertyRelative("tintPropertyName").stringValue = "_Color";
            material.FindPropertyRelative("parameterMappings").arraySize = 0;
            settings.FindProperty("gpuPrograms").arraySize = 0;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetMaterialPropertyMappings(params string[] authoredToUnity)
        {
            Require(authoredToUnity.Length % 2 == 0, "Property mapping test pairs are malformed.");
            var settings = new SerializedObject(bindingsAsset);
            var mappings = settings.FindProperty("materials").GetArrayElementAtIndex(0)
                .FindPropertyRelative("parameterMappings");
            mappings.arraySize = authoredToUnity.Length / 2;
            for (var index = 0; index < mappings.arraySize; ++index)
            {
                var mapping = mappings.GetArrayElementAtIndex(index);
                mapping.FindPropertyRelative("authoredName").stringValue = authoredToUnity[index * 2];
                mapping.FindPropertyRelative("unityPropertyName").stringValue = authoredToUnity[index * 2 + 1];
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void VerifyRejectedPreflight(string expectedDiagnostic)
        {
            var result = component.Play();
            Require(result == NativeResult.InvalidArgument && component.LastError != null &&
                component.LastError.Contains(expectedDiagnostic, StringComparison.Ordinal) && events.Count == 0 &&
                component.GetTemplateCount(out _) == NativeResult.NotReady &&
                component.ActiveRenderScopeCount == 0 &&
                target.GetComponentsInChildren<MeshRenderer>(true).Length == 0,
                "Preflight did not reject '" + expectedDiagnostic + "' before native OnStart.");
        }

        private static void VerifyVisibleRenderers()
        {
            var renderers = target.GetComponentsInChildren<MeshRenderer>();
            Require(renderers.Length > 0, "Spawn did not create an actual Unity MeshRenderer.");
            var found = false;
            foreach (var renderer in renderers)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter && filter.sharedMesh == sphereMesh && renderer.sharedMaterial == sharedMaterial)
                {
                    found = true;
                    var block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block, 0);
                    var color = block.GetVector("_Color");
                    Require(Math.Abs(color.x - 1) < 0.001 && Math.Abs(color.y - 0.75) < 0.001 &&
                        Math.Abs(color.z - 0.1) < 0.001 && Math.Abs(color.w - 1) < 0.001,
                        "Authored Color was not applied to the mapped Unity _Color property.");
                }
            }
            Require(found, "Spawned render object did not use the explicitly bound shared Mesh and Material.");
        }

        private static void Next(int nextPhase, int frames)
        {
            phase = nextPhase;
            afterFrame = Time.frameCount + frames;
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            ++errors;
            proof.Add("Unity error: " + message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            var ownsAsset = SessionState.GetBool(SessionKey, false);
            SessionState.EraseBool(SessionKey);
            Time.captureDeltaTime = 0;
            Time.timeScale = 1;
            if (target) UnityEngine.Object.Destroy(target);
            if (bindingsAsset) UnityEngine.Object.DestroyImmediate(bindingsAsset);
            if (sharedMaterial) UnityEngine.Object.DestroyImmediate(sharedMaterial);
            if (ownsAsset)
            {
                EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(ReloadOptionsKey, false);
                SessionState.EraseBool(ReloadOptionsKey);
                AssetDatabase.DeleteAsset(AssetPath);
            }
            proof.Add(error == null ? "result=PASS" : "result=FAIL\n" + error);
            File.WriteAllLines(ResultPath, proof);
            if (error != null) Debug.LogException(error);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}
