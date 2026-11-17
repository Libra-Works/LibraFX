using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    [InitializeOnLoad]
    public static class LiberaFxSpawnableRenderVerification
    {
        private const string SessionKey = "LiberaFxSpawnableRenderVerification.Active";
        private const string ReloadOptionsKey = "LiberaFxSpawnableRenderVerification.ReloadOptions";
        private const string AssetPath = "Assets/LiberaFxSpawnableRenderCheck.lfxp";
        private static readonly List<SpawnableHostEvent> hostEvents = new List<SpawnableHostEvent>();
        private static readonly List<GameIoEvent> gameIoEvents = new List<GameIoEvent>();
        private static GameObject target;
        private static LiberaFxSpawnableComponent component;
        private static LiberaFxUnityBindingAsset bindingsAsset;
        private static Material sharedMaterial;
        private static Mesh cubeMesh;
        private static string rootDrawId, captureDrawId, liveDrawId, templateId;
        private static int phase, afterFrame, errors;
        private static double deadline;

        static LiberaFxSpawnableRenderVerification()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("LiberaFx/Verify root and Spawnable rendering")]
        public static void Run()
        {
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Already in Play Mode.");
                var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GAMEIO_FIXTURE");
                Require(!string.IsNullOrEmpty(fixture) && File.Exists(fixture),
                    "Set LIBERAFX_UNITY_GAMEIO_FIXTURE to the compiler-produced GameIo.lfxp file.");
                Require(!File.Exists(AssetPath), "Verification asset already exists; refusing to overwrite it.");
                var identitiesPath = Path.ChangeExtension(fixture, ".identities.json");
                Require(File.Exists(identitiesPath), "The matching GameIo.identities.json file is missing.");
                var identities = JsonUtility.FromJson<FixtureIdentities>(File.ReadAllText(identitiesPath));
                Require(identities != null && IsGuid(identities.template) &&
                    IsGuid(identities.rootDraw) && IsGuid(identities.captureDraw) && IsGuid(identities.liveDraw),
                    "The Game I/O fixture identity manifest is invalid.");
                rootDrawId = identities.rootDraw;
                captureDrawId = identities.captureDraw;
                liveDrawId = identities.liveDraw;
                templateId = identities.template;

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

        private static string ResultPath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "spawnable-render-verification.txt");

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(SessionKey, false)) return;
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Finish(new InvalidOperationException("Play Mode ended before rendering verification completed."));
                return;
            }
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            try
            {
                LoadFixtureIdentities();
                hostEvents.Clear();
                gameIoEvents.Clear();
                errors = 0;
                phase = 0;
                Time.captureDeltaTime = 0.1f;
                Time.timeScale = 1;
                target = new GameObject("LIBERA FX root and Spawnable rendering verification");
                target.SetActive(false);
                component = target.AddComponent<LiberaFxSpawnableComponent>();
                var settings = new SerializedObject(component);
                settings.FindProperty("effect").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(AssetPath);
                settings.FindProperty("engineBindings").objectReferenceValue = CreateBindings();
                settings.FindProperty("playRate").floatValue = 1;
                settings.ApplyModifiedPropertiesWithoutUndo();
                component.HostEventReceived += hostEvents.Add;
                component.GameIoEventReceived += gameIoEvents.Add;
                component.RuntimeError += result =>
                    throw new InvalidOperationException("Spawnable runtime reported " + result + ".");
                Application.logMessageReceived += OnLog;
                target.SetActive(true);
                Require(component.SetGameProperty("position", new[] { 10d }) == NativeResult.Ok,
                    "Could not set root Game Property 'position'.");
                deadline = EditorApplication.timeSinceStartup + 60;
                EditorApplication.update += Tick;
            }
            catch (Exception error) { Finish(error); }
        }

        private static void LoadFixtureIdentities()
        {
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GAMEIO_FIXTURE");
            Require(!string.IsNullOrEmpty(fixture) && File.Exists(fixture),
                "The Game I/O fixture is not available after the Play Mode domain reload.");
            var identitiesPath = Path.ChangeExtension(fixture, ".identities.json");
            Require(File.Exists(identitiesPath), "The matching GameIo.identities.json file is missing.");
            var identities = JsonUtility.FromJson<FixtureIdentities>(File.ReadAllText(identitiesPath));
            Require(identities != null && IsGuid(identities.template) &&
                IsGuid(identities.rootDraw) && IsGuid(identities.captureDraw) && IsGuid(identities.liveDraw),
                "The Game I/O fixture identity manifest is invalid after the Play Mode domain reload.");
            rootDrawId = identities.rootDraw;
            captureDrawId = identities.captureDraw;
            liveDrawId = identities.liveDraw;
            templateId = identities.template;
        }

        private static void Tick()
        {
            try
            {
                Require(EditorApplication.timeSinceStartup < deadline, "Timed out in render phase " + phase + ".");
                Require(errors == 0, "Unity reported an error during rendering verification.");
                if (Time.frameCount < afterFrame) return;
                switch (phase)
                {
                    case 0:
                        if (!Near(FindOne(rootDrawId).transform.localPosition.x, 10)) return;
                        Require(gameIoEvents.Any(item => item.PortName == "echo" &&
                            item.Values.Length > 0 && Near(item.Values[0], 10)),
                            "Root named Output Property 'echo' did not expose its port name and value.");
                        Require(component.FireGameSignal("spawn") == NativeResult.Ok,
                            "Could not fire root Game Signal 'spawn'.");
                        Next(1, 1);
                        break;
                    case 1:
                        if (hostEvents.Count(item => item.Kind == 1) < 1 ||
                            CountNamed(captureDrawId) < 1 || CountNamed(liveDrawId) < 1) return;
                        Require(hostEvents[0].ContainerId.SequenceEqual(GuidBytes(templateId)),
                            "The first Spawn event did not identify the expected template.");
                        Require(Near(FindOne(captureDrawId).transform.localPosition.x, 10) &&
                            Near(FindOne(liveDrawId).transform.localPosition.x, 10),
                            "Captured and live draw values did not begin at the first Game Property value.");
                        Require(component.SetGameProperty("position", new[] { 20d }) == NativeResult.Ok,
                            "Could not update root Game Property 'position'.");
                        Next(2, 1);
                        break;
                    case 2:
                        if (!Near(FindOne(rootDrawId).transform.localPosition.x, 20) ||
                            !Near(FindOne(liveDrawId).transform.localPosition.x, 20)) return;
                        Require(Near(FindOne(captureDrawId).transform.localPosition.x, 10),
                            "Captured Spawn Input changed after the root property update.");
                        Require(gameIoEvents.Any(item => item.PortName == "echo" &&
                            item.Values.Length > 0 && Near(item.Values[0], 20)),
                            "Updated root Output Property event was not visible with its named port.");
                        Require(component.FireGameSignal("spawn") == NativeResult.Ok,
                            "Could not fire the second root Game Signal.");
                        Next(3, 1);
                        break;
                    case 3:
                        if (hostEvents.Count(item => item.Kind == 1) < 2 ||
                            CountNamed(captureDrawId) < 2 || CountNamed(liveDrawId) < 2) return;
                        var captures = FindAll(captureDrawId);
                        var lives = FindAll(liveDrawId);
                        Require(captures.Count == 2 && lives.Count == 2 &&
                            captures.Any(item => Near(item.transform.localPosition.x, 10)) &&
                            captures.Any(item => Near(item.transform.localPosition.x, 20)) &&
                            lives.All(item => Near(item.transform.localPosition.x, 20)),
                            "Per-instance captured/live input values were not isolated across two Spawn events.");
                        Require(hostEvents[0].InstanceId != hostEvents[1].InstanceId &&
                            hostEvents[0].ContainerId.SequenceEqual(hostEvents[1].ContainerId),
                            "Two Spawn events did not create separate instances of the same template.");
                        Require(CountRenderers() == 5,
                            "Expected one root renderer and two renderers per Spawnable instance.");
                        Require(component.Stop() == NativeResult.Ok && CountRenderers() == 0,
                            "Explicit Stop left root or per-instance Unity renderers behind.");
                        WriteProof("Root render follows named Game Property; Output Property emits 'echo'; " +
                            "captured/live Spawn Inputs remain independent across two instances; Stop releases all five renderers.");
                        Finish(null);
                        break;
                }
            }
            catch (Exception error) { Finish(error); }
        }

        private static LiberaFxUnityBindingAsset CreateBindings()
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
            var shader = Shader.Find("Unlit/Color");
            Require(shader != null && shader.isSupported,
                "The isolated verification project needs a supported Unlit/Color shader.");
            sharedMaterial = new Material(shader);
            Require(sharedMaterial.HasProperty("_Color"),
                "The isolated verification shader must expose _Color.");
            bindingsAsset = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var settings = new SerializedObject(bindingsAsset);
            var assets = settings.FindProperty("renderAssets");
            assets.arraySize = 1;
            var asset = assets.GetArrayElementAtIndex(0);
            asset.FindPropertyRelative("assetIdentity").stringValue = "/Engine/BasicShapes/Cube.Cube";
            asset.FindPropertyRelative("mesh").objectReferenceValue = cubeMesh;
            var defaults = asset.FindPropertyRelative("defaultMaterials");
            defaults.arraySize = 1;
            defaults.GetArrayElementAtIndex(0).objectReferenceValue = sharedMaterial;
            var tintNames = asset.FindPropertyRelative("defaultTintPropertyNames");
            tintNames.arraySize = 1;
            tintNames.GetArrayElementAtIndex(0).stringValue = "_Color";
            settings.FindProperty("gpuPrograms").arraySize = 0;
            settings.ApplyModifiedPropertiesWithoutUndo();
            return bindingsAsset;
        }

        private static Transform FindOne(string nodeId)
        {
            var values = FindAll(nodeId);
            Require(values.Count == 1, "Expected exactly one active renderer for node " + nodeId +
                "; found " + values.Count + ".");
            return values[0].transform;
        }

        private static List<GameObject> FindAll(string nodeId)
        {
            var name = "Libera FX Model " + nodeId;
            return target.GetComponentsInChildren<MeshRenderer>()
                .Where(renderer => renderer.gameObject.name == name)
                .Select(renderer => renderer.gameObject).ToList();
        }

        private static int CountNamed(string nodeId) => FindAll(nodeId).Count;
        private static int CountRenderers() => target.GetComponentsInChildren<MeshRenderer>().Length;

        private static byte[] GuidBytes(string value)
        {
            var compact = value.Replace("-", "");
            var bytes = new byte[16];
            for (var index = 0; index < bytes.Length; ++index)
                bytes[index] = byte.Parse(compact.Substring(index * 2, 2),
                    System.Globalization.NumberStyles.HexNumber);
            return bytes;
        }

        private static bool IsGuid(string value) => Guid.TryParse(value, out _);
        private static bool Near(double left, double right) => Math.Abs(left - right) < 0.001;

        private static void Next(int nextPhase, int frames)
        {
            phase = nextPhase;
            afterFrame = Time.frameCount + frames;
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            ++errors;
        }

        private static void WriteProof(string line)
        {
            File.WriteAllText(ResultPath,
                "Unity: " + Application.unityVersion + "\nMode: actual Play Mode\nPASS\n" + line + "\n");
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
            if (error != null)
            {
                File.WriteAllText(ResultPath, "FAIL\n" + error);
                Debug.LogException(error);
            }
            EditorApplication.Exit(error == null ? 0 : 1);
        }

        [Serializable]
        private sealed class FixtureIdentities
        {
            public string template;
            public string rootDraw;
            public string captureDraw;
            public string liveDraw;
        }
    }
}
