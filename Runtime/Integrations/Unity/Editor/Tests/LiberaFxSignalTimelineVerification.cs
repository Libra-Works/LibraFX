using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    [InitializeOnLoad]
    public static class LiberaFxSignalTimelineVerification
    {
        private const string ActiveKey = "LiberaFxSignalTimelineVerification.Active";
        private const string ReloadKey = "LiberaFxSignalTimelineVerification.Reload";
        private const string RootAsset = "Assets/LiberaFxTimelineCheck.lfxp";
        private const string ScopedAsset = "Assets/LiberaFxScopedTimelineCheck.lfxp";
        private static readonly List<SpawnableHostEvent> events = new List<SpawnableHostEvent>();
        private static readonly List<GameIoEvent> outputs = new List<GameIoEvent>();
        private static readonly List<GameObject> objects = new List<GameObject>();
        private static LiberaFxSpawnableComponent root, scoped;
        private static double deadline;
        private static int phase, stoppedEvents, stoppedFrame, errors;
        private static string ResultPath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "signal-timeline-verification.txt");

        static LiberaFxSignalTimelineVerification()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void Run()
        {
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Already in Play Mode.");
                var directory = Environment.GetEnvironmentVariable("LIBERAFX_SIGNAL_TIMELINE_FIXTURES");
                Require(!string.IsNullOrEmpty(directory) && Path.IsPathRooted(directory),
                    "Set LIBERAFX_SIGNAL_TIMELINE_FIXTURES to the isolated compiler fixture directory.");
                Require(!File.Exists(Path.Combine(Application.dataPath, Path.GetFileName(RootAsset))) &&
                    !File.Exists(Path.Combine(Application.dataPath, Path.GetFileName(ScopedAsset))),
                    "Verification assets already exist.");
                File.WriteAllText(ResultPath, "STARTED\n");
                SessionState.SetBool(ReloadKey, EditorSettings.enterPlayModeOptionsEnabled);
                SessionState.SetBool(ActiveKey, true);
                Import(directory, "Timeline.lfxp", RootAsset);
                Import(directory, "ScopedTimeline.lfxp", ScopedAsset);
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
            }
            catch (Exception error) { Finish(error); }
        }

        private static void Import(string directory, string file, string assetPath)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, file));
            File.WriteAllBytes(Path.Combine(Application.dataPath, Path.GetFileName(assetPath)), bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(assetPath);
            Require(asset && asset.PackageBytes.SequenceEqual(bytes), "Importer changed compiler-produced package bytes.");
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false)) return;
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Finish(new InvalidOperationException("Play Mode exited before verification completed."));
                return;
            }
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            try
            {
                phase = errors = 0;
                events.Clear();
                outputs.Clear();
                objects.Clear();
                Time.captureDeltaTime = 0.125f;
                Time.timeScale = 1;
                Application.logMessageReceived += OnLog;
                root = Create(RootAsset);
                scoped = Create(ScopedAsset);
                root.GameIoEventReceived += outputs.Add;
                scoped.HostEventReceived += events.Add;
                foreach (var target in objects) target.SetActive(true);
                Require(outputs.Count == 0, "A preview-only time-zero key leaked into runtime export.");
                Require(events.Count(item => item.Kind == 1) == 1, "Runtime time-zero key did not spawn once.");
                deadline = EditorApplication.timeSinceStartup + 90;
                EditorApplication.update += Tick;
            }
            catch (Exception error) { Finish(error); }
        }

        private static LiberaFxSpawnableComponent Create(string assetPath)
        {
            var target = new GameObject("LIBERA FX Signal timeline verification");
            objects.Add(target);
            target.SetActive(false);
            var component = target.AddComponent<LiberaFxSpawnableComponent>();
            var settings = new SerializedObject(component);
            settings.FindProperty("effect").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(assetPath);
            settings.ApplyModifiedPropertiesWithoutUndo();
            return component;
        }

        private static void Tick()
        {
            try
            {
                Require(errors == 0, "Unity reported an error.");
                Require(EditorApplication.timeSinceStartup < deadline, "Signal timeline verification timed out.");
                Require(root.LastError == null && scoped.LastError == null, "Runtime component reported a failure.");
                if (phase == 0)
                {
                    if (!scoped.IsRootFinished || root.ElapsedTime < 1.125) return;
                    Require(outputs.Count == 1 && outputs[0].PortName == "echo" &&
                        outputs[0].Kind == 3 && outputs[0].Width == 0 && outputs[0].InstanceId == 0 &&
                        outputs[0].ContainerId.All(value => value == 0),
                        "Runtime root key/name/scope or preview-key exclusion failed.");
                    var signals = events.Where(item => item.Kind == 4).ToArray();
                    var spawns = events.Where(item => item.Kind == 1).ToArray();
                    Require(spawns.Length == 2 && signals.Length == 2 &&
                        signals[0].Time == 0.25 && signals[1].Time == 0.75 &&
                        signals[0].InstanceId != signals[1].InstanceId &&
                        signals[0].ContainerId.SequenceEqual(signals[1].ContainerId) &&
                        signals[0].SignalNodeId.Any(value => value != 0) &&
                        signals[0].SignalNodeId.SequenceEqual(signals[1].SignalNodeId),
                        "Native-originating local Signal outputs lost their clocks or identities.");
                    foreach (var signal in signals)
                        Require(spawns.Any(spawn => spawn.InstanceId == signal.InstanceId &&
                            spawn.ContainerId.SequenceEqual(signal.ContainerId) && signal.Time - spawn.Time == 0.25),
                            "Signal was not relative to its own instance birth.");
                    Require(scoped.ActiveRenderScopeCount == 0, "Completed effect retained scopes.");
                    Require(root.FireGameSignal("trigger") == NativeResult.Ok && outputs.Count == 2,
                        "Timeline scheduling replaced the named game-input entry.");
                    events.Clear();
                    Require(scoped.Play() == NativeResult.Ok && events.Count(item => item.Kind == 1) == 1,
                        "Restart did not include the time-zero runtime key.");
                    Require(scoped.Stop() == NativeResult.Ok, "Stop failed.");
                    stoppedEvents = events.Count;
                    stoppedFrame = Time.frameCount;
                    phase = 1;
                }
                else if (Time.frameCount >= stoppedFrame + 4)
                {
                    Require(events.Count == stoppedEvents && scoped.ActiveRenderScopeCount == 0,
                        "Stopped effect emitted pending Signal keys or retained scopes.");
                    Finish(null);
                }
            }
            catch (Exception error) { Finish(error); }
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) ++errors;
        }

        private static void Finish(Exception error)
        {
            var ownsAssets = SessionState.GetBool(ActiveKey, false);
            SessionState.SetBool(ActiveKey, false);
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            Time.captureDeltaTime = 0;
            Time.timeScale = 1;
            foreach (var target in objects)
                if (target) UnityEngine.Object.DestroyImmediate(target);
            objects.Clear();
            if (ownsAssets)
            {
                EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(ReloadKey, false);
                if (AssetDatabase.LoadMainAssetAtPath(RootAsset)) AssetDatabase.DeleteAsset(RootAsset);
                if (AssetDatabase.LoadMainAssetAtPath(ScopedAsset)) AssetDatabase.DeleteAsset(ScopedAsset);
            }
            File.WriteAllText(ResultPath, error == null
                ? "PASS\nUnity: " + Application.unityVersion +
                    "\nActual Play Mode, compiler-produced LFXP importer, automatic Update, preview-key exclusion," +
                    " named Game I/O, native local Signal clocks/identities, completion, restart and Stop.\n"
                : "FAIL\n" + error + "\n");
            if (error != null) Debug.LogException(error);
            EditorApplication.Exit(error == null ? 0 : 1);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
