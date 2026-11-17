using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private static readonly List<string> proof = new List<string>();
        private static GameObject target;
        private static LiberaFxSpawnableComponent component;
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
                errors = 0;
                phase = 0;
                waitedForChildren = false;
                proof.Add("Unity: " + Application.unityVersion);
                proof.Add("Mode: actual Play Mode; automatic MonoBehaviour callbacks");
                proof.Add("LFXP importer preserved compiler-produced package bytes: PASS");
                Application.logMessageReceived += OnLog;
                Time.captureDeltaTime = 0.25f;
                Time.timeScale = 1;
                target = new GameObject("LIBERA FX Spawnable Play Mode verification");
                target.SetActive(false);
                component = target.AddComponent<LiberaFxSpawnableComponent>();
                var settings = new SerializedObject(component);
                settings.FindProperty("effect").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(AssetPath);
                settings.FindProperty("playRate").floatValue = 0.5f;
                settings.ApplyModifiedPropertiesWithoutUndo();
                component.HostEventReceived += item => events.Add(item);
                component.RuntimeError += error => throw new InvalidOperationException("Runtime error: " + error);
                target.SetActive(true);
                startUnityTime = Time.timeAsDouble;
                Require(events.Count == 1 && events[0].Kind == 1 && events[0].Time == 0,
                    "Automatic OnEnable did not spawn Launch at zero.");
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
                        proof.Add("Automatic Update, half-rate playback, child draining and exactly-once natural Destroy: PASS");
                        stoppedAt = component.ElapsedTime;
                        Next(1, 3);
                        break;
                    case 1:
                        Require(events.Count == 6 && component.ElapsedTime == stoppedAt,
                            "Finished root continued updating or emitted duplicate events.");
                        Require(component.Stop() == NativeResult.Ok && events.Count == 6,
                            "Stop after natural completion emitted extra events.");
                        events.Clear();
                        SetPlayRate(1);
                        Require(component.Play() == NativeResult.Ok, "Could not restart completed effect.");
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
                        VerifyInterrupted();
                        Require(component.FireGameSignal("unused") == NativeResult.NotReady,
                            "Manual Stop retained the native host.");
                        proof.Add("Manual Stop releases host without natural Destroy: PASS");
                        events.Clear();
                        Require(component.Play() == NativeResult.Ok, "Restart after Stop failed.");
                        component.enabled = false;
                        VerifyInterrupted();
                        stoppedAt = component.ElapsedTime;
                        Next(5, 3);
                        break;
                    case 5:
                        Require(events.Count == 2 && component.ElapsedTime == stoppedAt,
                            "Disabled component kept updating.");
                        events.Clear();
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

        private static void SetPlayRate(float value)
        {
            var settings = new SerializedObject(component);
            settings.FindProperty("playRate").floatValue = value;
            settings.ApplyModifiedPropertiesWithoutUndo();
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
