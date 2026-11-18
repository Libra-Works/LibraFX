using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiberaFx.Unity.Editor
{
    /// <summary>
    /// Isolated real-Unity acceptance entry points for the committed adapter batch.
    /// These tests consume compiler-produced packages and never rewrite their metadata.
    /// </summary>
    public static class LiberaFxSourceBatchAcceptanceVerification
    {
        private const string DebugPlayPending = "LiberaFx.SourceBatchDebugDraw.Pending";
        private const string DebugPlayResult = "LiberaFx.SourceBatchDebugDraw.Result";
        private const string DebugPlayStarted = "LiberaFx.SourceBatchDebugDraw.Started";
        private const string InstanceGameIoPlayPending = "LiberaFx.SourceBatchGameIo.Pending";
        private const string InstanceGameIoPlayResult = "LiberaFx.SourceBatchGameIo.Result";
        private const string InstanceGameIoResultFile = "source-batch-instance-gameio-playmode-result.txt";
        private const string SkeletalPlayActive = "LiberaFx.SourceBatchSkeletalPlay.Active";
        private const string SkeletalPlayFinished = "LiberaFx.SourceBatchSkeletalPlay.Finished";
        private const string SkeletalPlayExitCode = "LiberaFx.SourceBatchSkeletalPlay.ExitCode";
        private const string SkeletalPlayResultPath = "LiberaFx.SourceBatchSkeletalPlay.ResultPath";
        private const string SkeletalPlayAssetFolder = "Assets/LiberaFxSourcePlayModeAcceptance";
        private static readonly string[] SkeletalMatrixPorts =
        {
            "OwnerBoneLocal", "OwnerComponent", "OwnerWorld",
            "RegisteredBoneLocal", "RegisteredComponent", "RegisteredWorld"
        };
        private static readonly string[] SkeletalValidityPorts =
        {
            "OwnerBoneLocalValid", "OwnerComponentValid", "OwnerWorldValid",
            "RegisteredBoneLocalValid", "RegisteredComponentValid", "RegisteredWorldValid"
        };
        private static readonly Dictionary<string, GameIoEvent> skeletalPlayFrame =
            new Dictionary<string, GameIoEvent>(StringComparer.Ordinal);
        private static readonly HashSet<string> skeletalPlayMatrixSeen =
            new HashSet<string>(StringComparer.Ordinal);
        private static Rig skeletalPlayOwnerRig;
        private static Rig skeletalPlayRegisteredRig;
        private static LiberaFxSpawnableComponent skeletalPlayComponent;
        private static Animation skeletalPlayOwnerAnimation;
        private static Animation skeletalPlayRegisteredAnimation;
        private static IDisposable skeletalPlayRegistration;
        private static string skeletalPlayError;
        private static int skeletalPlayPhase;
        private static int skeletalPlayFramesInPhase;
        private static int skeletalPlayEventFrame = -1;
        private static int skeletalPlayCommittedFrame = -1;
        private static double skeletalPlayStartedAt;
        private static double skeletalPlayDeadline;
        private static float skeletalOwnerWorldMin;
        private static float skeletalOwnerWorldMax;
        private static float skeletalRegisteredWorldMin;
        private static float skeletalRegisteredWorldMax;
        private static bool skeletalPlayHasWorldSample;

        [InitializeOnLoadMethod]
        private static void RegisterDebugPlayModeCallbacks()
        {
            EditorApplication.playModeStateChanged -= DebugPlayModeChanged;
            EditorApplication.playModeStateChanged += DebugPlayModeChanged;
            EditorApplication.update -= CheckDebugPlayModeTimeout;
            EditorApplication.update += CheckDebugPlayModeTimeout;
            EditorApplication.playModeStateChanged -= SkeletalPlayModeChanged;
            EditorApplication.playModeStateChanged += SkeletalPlayModeChanged;
            EditorApplication.playModeStateChanged -= InstanceGameIoPlayModeChanged;
            EditorApplication.playModeStateChanged += InstanceGameIoPlayModeChanged;
        }

        public static void RunDebugDrawPayloadBatch()
        {
            RequireIsolatedProject();
            Require(!EditorApplication.isPlayingOrWillChangePlaymode,
                "The isolated Editor is already changing Play Mode.");
            SessionState.SetBool(DebugPlayPending, true);
            SessionState.SetInt(DebugPlayResult, 2);
            SessionState.SetString(DebugPlayStarted, DateTime.UtcNow.Ticks.ToString());
            RegisterDebugPlayModeCallbacks();
            EditorApplication.EnterPlaymode();
        }

        private static void DebugPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(DebugPlayPending, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        Require(Application.isPlaying, "Debug draw acceptance must run in the actual game loop.");
                        RunDebugDrawPayloadInPlayMode();
                        SessionState.SetInt(DebugPlayResult, 0);
                    }
                    catch (Exception error)
                    {
                        File.WriteAllText(Path.Combine(ProjectRoot, "source-batch-debug-draw-result.txt"),
                            "FAIL\n" + error);
                        Debug.LogException(error);
                        SessionState.SetInt(DebugPlayResult, 2);
                    }
                    finally { EditorApplication.ExitPlaymode(); }
                };
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var result = SessionState.GetInt(DebugPlayResult, 2);
                SessionState.SetBool(DebugPlayPending, false);
                EditorApplication.Exit(result);
            }
        }

        private static void SkeletalPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(SkeletalPlayActive, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                try { StartSkeletalPlayModeRuntime(); }
                catch (Exception error) { FinishSkeletalPlayMode(error); }
            }
            else if (state == PlayModeStateChange.ExitingPlayMode &&
                !SessionState.GetBool(SkeletalPlayFinished, false))
            {
                FinishSkeletalPlayMode(new InvalidOperationException(
                    "Play Mode exited before SOURCE pose and invalidation acceptance completed."));
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                if (!SessionState.GetBool(SkeletalPlayFinished, false))
                    FinishSkeletalPlayMode(new InvalidOperationException(
                        "Play Mode ended without completing SOURCE pose acceptance."));
                var exitCode = SessionState.GetInt(SkeletalPlayExitCode, 2);
                SessionState.SetBool(SkeletalPlayActive, false);
                EditorApplication.Exit(exitCode);
            }
        }

        private static void StartSkeletalPlayModeRuntime()
        {
            Require(Application.isPlaying,
                "SOURCE runtime acceptance must start after Unity entered actual Play Mode.");
            var owner = GameObject.Find("LIBERA FX SOURCE PlayMode owner");
            var registeredOwner = GameObject.Find("LIBERA FX SOURCE PlayMode registered target");
            Require(owner && registeredOwner, "The saved SOURCE PlayMode scene did not reload its target objects.");
            skeletalPlayOwnerRig = FindRig(owner);
            skeletalPlayRegisteredRig = FindRig(registeredOwner);
            skeletalPlayOwnerAnimation = owner.GetComponent<Animation>();
            skeletalPlayRegisteredAnimation = registeredOwner.GetComponent<Animation>();
            skeletalPlayComponent = owner.GetComponent<LiberaFxSpawnableComponent>();
            Require(skeletalPlayOwnerAnimation && skeletalPlayRegisteredAnimation && skeletalPlayComponent,
                "The saved SOURCE scene lost its animation or effect components.");

            skeletalPlayFrame.Clear();
            skeletalPlayMatrixSeen.Clear();
            skeletalPlayError = null;
            skeletalPlayPhase = 0;
            skeletalPlayFramesInPhase = 0;
            skeletalPlayEventFrame = -1;
            skeletalPlayCommittedFrame = -1;
            skeletalPlayHasWorldSample = false;
            skeletalOwnerWorldMin = skeletalRegisteredWorldMin = float.MaxValue;
            skeletalOwnerWorldMax = skeletalRegisteredWorldMax = float.MinValue;

            Require(skeletalPlayOwnerRig.target.isActiveAndEnabled &&
                skeletalPlayRegisteredRig.target.isActiveAndEnabled,
                "Saved targets were not active when the PlayMode scene loaded.");
            skeletalPlayRegistration = LiberaFxSkeletalPoseTarget.RegisterTarget(
                "hero.skeleton", skeletalPlayRegisteredRig.target);
            skeletalPlayComponent.GameIoEventReceived += SkeletalPlayGameIoEvent;
            Application.logMessageReceived += SkeletalPlayLog;
            Require(skeletalPlayOwnerAnimation.Play() && skeletalPlayRegisteredAnimation.Play(),
                "The saved procedural AnimationClip did not start on both rigs.");
            Require(skeletalPlayComponent.Play() == NativeResult.Ok,
                "Compiler SOURCE package failed to start in Play Mode: " + skeletalPlayComponent.LastError);

            skeletalPlayStartedAt = EditorApplication.timeSinceStartup;
            skeletalPlayDeadline = skeletalPlayStartedAt + 45;
            EditorApplication.update += SkeletalPlayTick;
        }

        private static Rig FindRig(GameObject targetOwner)
        {
            var target = targetOwner.GetComponent<LiberaFxSkeletalPoseTarget>();
            var renderer = targetOwner.GetComponent<SkinnedMeshRenderer>();
            var root = targetOwner.transform.Find("Root");
            var hand = root != null ? root.Find("hand_r") : null;
            Require(target && renderer && renderer.sharedMesh && root && hand,
                "The serialized PlayMode skeleton rig is incomplete.");
            return new Rig(target, renderer, root, hand, renderer.sharedMesh);
        }

        private static void SkeletalPlayGameIoEvent(GameIoEvent item)
        {
            try
            {
                Require(item != null && item.InstanceId == 0 &&
                    item.ContainerId != null && item.ContainerId.All(value => value == 0),
                    "SOURCE OutputProperties were not emitted from the expected root scope.");
                if (skeletalPlayEventFrame != Time.frameCount)
                {
                    CommitSkeletalPlayFrame();
                    skeletalPlayEventFrame = Time.frameCount;
                }

                if (SkeletalMatrixPorts.Contains(item.PortName, StringComparer.Ordinal))
                {
                    Require(item.Width == 16 && item.Values != null && item.Values.Length >= 16,
                        "SOURCE Matrix output '" + item.PortName + "' did not contain 16 lanes.");
                    skeletalPlayMatrixSeen.Add(item.PortName);
                    if (ShouldVerifySkeletalMatrix(item.PortName))
                    {
                        VerifyLiveSkeletalMatrix(item, ExpectedSkeletalMatrix(item.PortName));
                        if (item.PortName == "OwnerWorld")
                        {
                            var x = (float)item.Values[3];
                            skeletalOwnerWorldMin = Mathf.Min(skeletalOwnerWorldMin, x);
                            skeletalOwnerWorldMax = Mathf.Max(skeletalOwnerWorldMax, x);
                            skeletalPlayHasWorldSample = true;
                        }
                        else if (item.PortName == "RegisteredWorld")
                        {
                            var x = (float)item.Values[3];
                            skeletalRegisteredWorldMin = Mathf.Min(skeletalRegisteredWorldMin, x);
                            skeletalRegisteredWorldMax = Mathf.Max(skeletalRegisteredWorldMax, x);
                            skeletalPlayHasWorldSample = true;
                        }
                    }
                }
                else
                {
                    Require(SkeletalValidityPorts.Contains(item.PortName, StringComparer.Ordinal),
                        "Unexpected SOURCE output port '" + item.PortName + "'.");
                    Require(item.Width >= 1 && item.Values != null && item.Values.Length >= 1,
                        "SOURCE validity output '" + item.PortName + "' did not contain a lane.");
                }
                skeletalPlayFrame[item.PortName] = item;
            }
            catch (Exception error)
            {
                skeletalPlayError = error.ToString();
            }
        }

        private static bool ShouldVerifySkeletalMatrix(string name) =>
            skeletalPlayPhase == 0 ||
            skeletalPlayPhase == 1 && name.StartsWith("Owner", StringComparison.Ordinal);

        private static Matrix4x4 ExpectedSkeletalMatrix(string name)
        {
            var rig = name.StartsWith("Owner", StringComparison.Ordinal)
                ? skeletalPlayOwnerRig : skeletalPlayRegisteredRig;
            var world = rig.hand.localToWorldMatrix;
            if (name.EndsWith("BoneLocal", StringComparison.Ordinal))
            {
                Require(rig.hand.parent != null, "A BoneLocal SOURCE sample requires a bone parent.");
                return rig.hand.parent.worldToLocalMatrix * world;
            }
            if (name.EndsWith("Component", StringComparison.Ordinal))
                return rig.renderer.transform.worldToLocalMatrix * world;
            return world;
        }

        private static void VerifyLiveSkeletalMatrix(GameIoEvent item, Matrix4x4 expected)
        {
            for (var row = 0; row < 4; ++row)
            for (var column = 0; column < 4; ++column)
            {
                var lane = row * 4 + column;
                Require(Math.Abs(item.Values[lane] - expected[row, column]) < 0.0005,
                    "PlayMode OutputProperty '" + item.PortName + "' lane " + lane +
                    " differed from the current animated Unity transform.");
            }
        }

        private static void CommitSkeletalPlayFrame()
        {
            if (skeletalPlayEventFrame < 0 ||
                skeletalPlayCommittedFrame == skeletalPlayEventFrame ||
                Time.frameCount <= skeletalPlayEventFrame)
                return;
            foreach (var name in SkeletalValidityPorts)
            {
                var expected = skeletalPlayPhase == 0 ||
                    skeletalPlayPhase == 1 && name.StartsWith("Owner", StringComparison.Ordinal);
                VerifyValidity(skeletalPlayFrame, name, expected);
            }
            skeletalPlayCommittedFrame = skeletalPlayEventFrame;
            ++skeletalPlayFramesInPhase;
        }

        private static void SkeletalPlayTick()
        {
            try
            {
                Require(Application.isPlaying, "SOURCE player-loop test left Play Mode unexpectedly.");
                Require(EditorApplication.timeSinceStartup < skeletalPlayDeadline,
                    "Timed out waiting for actual PlayMode SOURCE outputs in phase " + skeletalPlayPhase +
                    " after " + skeletalPlayFramesInPhase + " output frames.");
                Require(string.IsNullOrEmpty(skeletalPlayError), skeletalPlayError);
                CommitSkeletalPlayFrame();

                if (skeletalPlayPhase == 0 && skeletalPlayFramesInPhase >= 8 &&
                    EditorApplication.timeSinceStartup - skeletalPlayStartedAt >= 1.2)
                {
                    Require(skeletalPlayMatrixSeen.Count == SkeletalMatrixPorts.Length &&
                        skeletalPlayHasWorldSample &&
                        skeletalOwnerWorldMax - skeletalOwnerWorldMin > 0.3f &&
                        skeletalRegisteredWorldMax - skeletalRegisteredWorldMin > 0.3f,
                        "Both World SOURCE outputs did not follow real Animation player-loop samples.");
                    skeletalPlayRegistration.Dispose();
                    skeletalPlayRegistration = null;
                    AdvanceSkeletalPlayPhase();
                }
                else if (skeletalPlayPhase == 1 && skeletalPlayFramesInPhase >= 2)
                {
                    skeletalPlayOwnerRig.target.enabled = false;
                    AdvanceSkeletalPlayPhase();
                }
                // OutputProperty emits only on change. Once both targets are invalid,
                // there is no second frame of change events to wait for.
                else if (skeletalPlayPhase == 2 && skeletalPlayFramesInPhase >= 1)
                {
                    FinishSkeletalPlayMode(null);
                }
            }
            catch (Exception error)
            {
                FinishSkeletalPlayMode(error);
            }
        }

        private static void AdvanceSkeletalPlayPhase()
        {
            ++skeletalPlayPhase;
            skeletalPlayFramesInPhase = 0;
            skeletalPlayEventFrame = -1;
            skeletalPlayCommittedFrame = -1;
        }

        private static void SkeletalPlayLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                skeletalPlayError = message + Environment.NewLine + stack;
        }

        private static void FinishSkeletalPlayMode(Exception error)
        {
            if (SessionState.GetBool(SkeletalPlayFinished, false)) return;
            if (error == null && !string.IsNullOrEmpty(skeletalPlayError))
                error = new InvalidOperationException(skeletalPlayError);
            EditorApplication.update -= SkeletalPlayTick;
            Application.logMessageReceived -= SkeletalPlayLog;
            try
            {
                if (skeletalPlayComponent && skeletalPlayComponent.isActiveAndEnabled)
                    skeletalPlayComponent.Stop();
                skeletalPlayRegistration?.Dispose();
            }
            catch (Exception cleanupError)
            {
                if (error == null) error = cleanupError;
            }
            var resultPath = SessionState.GetString(SkeletalPlayResultPath, "");
            var exitCode = error == null ? 0 : 2;
            var result = error == null
                ? "PASS: actual Unity Play Mode; saved serialized skeleton assets; player-loop Animation, OnEnable and LateUpdate; six Owner/Registered BoneLocal/Component/World matrices; unregister and disable invalidation; Unity " + Application.unityVersion + "\n"
                : "FAIL\n" + error + "\n";
            File.WriteAllText(resultPath, result);
            if (error != null) Debug.LogException(error);
            SessionState.SetInt(SkeletalPlayExitCode, exitCode);
            SessionState.SetBool(SkeletalPlayFinished, true);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        private static void ResetSkeletalPlayModeState()
        {
            skeletalPlayFrame.Clear();
            skeletalPlayMatrixSeen.Clear();
            skeletalPlayOwnerRig = null;
            skeletalPlayRegisteredRig = null;
            skeletalPlayComponent = null;
            skeletalPlayOwnerAnimation = null;
            skeletalPlayRegisteredAnimation = null;
            skeletalPlayRegistration = null;
            skeletalPlayError = null;
            skeletalPlayPhase = 0;
            skeletalPlayFramesInPhase = 0;
            skeletalPlayEventFrame = -1;
            skeletalPlayCommittedFrame = -1;
            skeletalPlayStartedAt = 0;
            skeletalPlayDeadline = 0;
            skeletalOwnerWorldMin = skeletalRegisteredWorldMin = float.MaxValue;
            skeletalOwnerWorldMax = skeletalRegisteredWorldMax = float.MinValue;
            skeletalPlayHasWorldSample = false;
        }

        private static void CheckDebugPlayModeTimeout()
        {
            if (SessionState.GetBool(DebugPlayPending, false) &&
                long.TryParse(SessionState.GetString(DebugPlayStarted, ""), out var started) &&
                DateTime.UtcNow.Ticks - started > TimeSpan.FromMinutes(3).Ticks)
            {
                SessionState.SetBool(DebugPlayPending, false);
                Debug.LogError("Source-batch debug draw Play Mode verification timed out.");
                EditorApplication.Exit(2);
            }
        }

        public static void RunSynchronousRegressionBatch()
        {
            RequireIsolatedProject();
            var results = new List<string>();
            Run("resource identity", LiberaFxResourceIdentityVerification.RunBatch);
            Run("authored phase/quality/force", LiberaFxAuthoredRuntimeVerification.RunBatch);
            Run("sprite composition", LiberaFxAuthoredRuntimeVerification.RunSpriteCompositionBatch);
            Run("package debugger/worker/render", LiberaFxPackageDebuggerVerification.RunBatch);
            Run("scoped force", LiberaFxScopedForceVerification.RunBatch);
            Run("preview TextMesh", LiberaFxDebugDrawVerification.Run);
            Run("authored ModelDraw", LiberaFxModelDrawVerification.RunBatch);
            Run("material animation", LiberaFxModelDrawVerification.RunMaterialAnimationBatch);

            var resultPath = Path.Combine(ProjectRoot, "source-batch-regression.txt");
            File.WriteAllLines(resultPath, results);
            if (results.Any(item => item.StartsWith("FAIL ", StringComparison.Ordinal)))
            {
                Debug.LogError("Source-batch synchronous regression batch had failures: " + resultPath);
                EditorApplication.Exit(2);
                return;
            }
            Debug.Log("PASS source-batch synchronous regressions: " + resultPath);

            void Run(string name, Action test)
            {
                try
                {
                    test();
                    results.Add("PASS " + name);
                }
                catch (Exception error)
                {
                    results.Add("FAIL " + name + Environment.NewLine + error);
                    Debug.LogException(error);
                }
            }
        }

        public static void RunSkeletalPoseEditorBatch()
            {
                RequireIsolatedProject();
                var fixture = Environment.GetEnvironmentVariable("LIBERAFX_SKELETAL_FIXTURE");
                Require(Path.IsPathRooted(fixture ?? "") && File.Exists(fixture),
                    "Supply the compiler-produced AnimatedSkeletalSpaces.lfxp file.");

                typeof(LiberaFxSkeletalPoseTarget).GetMethod("InitializeMainThread",
                    BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var owner = new GameObject("Skeletal SOURCE owner target");
                owner.SetActive(false);
                var registeredOwner = new GameObject("Skeletal SOURCE registered target");
                registeredOwner.SetActive(false);
                Require(!owner.activeInHierarchy && !registeredOwner.activeInHierarchy,
                    "SOURCE test targets must be inactive before adding or configuring components.");
                var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                var clip = new AnimationClip { name = "Isolated hand_r animation", legacy = true };
                LiberaFxSpawnableComponent component = null;
                IDisposable registration = null;
                Rig ownerRig = null;
                Rig registeredRig = null;
                var events = new List<GameIoEvent>();
                try
                {
                    ownerRig = CreateRig(owner, "OwnerRig");
                    registeredRig = CreateRig(registeredOwner, "RegisteredRig");
                    ConfigureTarget(ownerRig.target, ownerRig.renderer, ownerRig.root);
                    ConfigureTarget(registeredRig.target, registeredRig.renderer, registeredRig.root);

                    clip.SetCurve("Root/hand_r", typeof(Transform), "m_LocalPosition.x",
                        AnimationCurve.Linear(0, 0.25f, 1, 1.25f));
                    package.SetPackageBytes(File.ReadAllBytes(fixture));
                    component = owner.AddComponent<LiberaFxSpawnableComponent>();
                    Require(component.ConfigureForSession(package, bindings) == NativeResult.Ok,
                        "Could not configure the skeletal SOURCE package.");
                    var rate = typeof(LiberaFxSpawnableComponent).GetField("playRate",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    rate.SetValue(component, 1f);
                    component.GameIoEventReceived += events.Add;

                    owner.transform.position = new Vector3(3, -2, 5);
                    owner.transform.rotation = Quaternion.Euler(12, 27, -8);
                    owner.transform.localScale = new Vector3(1.7f, 0.8f, 1.2f);
                    ownerRig.root.localPosition = new Vector3(0.5f, 0.25f, -0.75f);
                    ownerRig.root.localRotation = Quaternion.Euler(0, 18, 5);
                    registeredOwner.transform.position = new Vector3(-4, 6, 2);
                    registeredOwner.transform.rotation = Quaternion.Euler(-9, 41, 13);
                    registeredOwner.transform.localScale = new Vector3(0.65f, 1.4f, 0.9f);
                    registeredRig.root.localPosition = new Vector3(-0.2f, 0.8f, 0.1f);
                    registeredRig.root.localRotation = Quaternion.Euler(21, -6, 17);

                    owner.SetActive(true);
                    registeredOwner.SetActive(true);
                    registration = LiberaFxSkeletalPoseTarget.RegisterTarget("hero.skeleton", registeredRig.target);
                    Require(component.Play() == NativeResult.Ok,
                        "The compiler SOURCE package failed to start: " + component.LastError);

                    var lateUpdate = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var elapsed = typeof(LiberaFxSpawnableComponent).GetField("elapsed",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Require(lateUpdate != null && elapsed != null, "Could not locate the component's SOURCE publication hook.");

                    VerifyPoseAt(0);
                    var first = ReadFrame(events);
                    VerifyMatrix(first, "OwnerBoneLocal", Pose(ownerRig, 0));
                    VerifyMatrix(first, "OwnerComponent", Pose(ownerRig, 0));
                    VerifyMatrix(first, "OwnerWorld", Pose(ownerRig, 0));
                    VerifyMatrix(first, "RegisteredBoneLocal", Pose(registeredRig, 0));
                    VerifyMatrix(first, "RegisteredComponent", Pose(registeredRig, 0));
                    VerifyMatrix(first, "RegisteredWorld", Pose(registeredRig, 0));
                    VerifyValidity(first, "OwnerBoneLocalValid", true);
                    VerifyValidity(first, "OwnerComponentValid", true);
                    VerifyValidity(first, "OwnerWorldValid", true);
                    VerifyValidity(first, "RegisteredBoneLocalValid", true);
                    VerifyValidity(first, "RegisteredComponentValid", true);
                    VerifyValidity(first, "RegisteredWorldValid", true);

                    VerifyPoseAt(1);
                    var second = ReadFrame(events);
                    VerifyMatrix(second, "OwnerBoneLocal", Pose(ownerRig, 1));
                    VerifyMatrix(second, "OwnerComponent", Pose(ownerRig, 1));
                    VerifyMatrix(second, "OwnerWorld", Pose(ownerRig, 1));
                    VerifyMatrix(second, "RegisteredBoneLocal", Pose(registeredRig, 1));
                    VerifyMatrix(second, "RegisteredComponent", Pose(registeredRig, 1));
                    VerifyMatrix(second, "RegisteredWorld", Pose(registeredRig, 1));
                    VerifyValidity(second, "OwnerBoneLocalValid", true);
                    VerifyValidity(second, "OwnerComponentValid", true);
                    VerifyValidity(second, "OwnerWorldValid", true);
                    VerifyValidity(second, "RegisteredBoneLocalValid", true);
                    VerifyValidity(second, "RegisteredComponentValid", true);
                    VerifyValidity(second, "RegisteredWorldValid", true);
                    Require(Math.Abs(first["OwnerWorld"].Values[3] - second["OwnerWorld"].Values[3]) > 0.5,
                        "Owner World SOURCE did not follow the sampled hand_r animation.");
                    Require(Math.Abs(first["RegisteredWorld"].Values[3] - second["RegisteredWorld"].Values[3]) > 0.5,
                        "Registered World SOURCE did not follow the sampled hand_r animation.");

                    // Removing a registered key invalidates the existing pose lease,
                    // without invalidating the independent owner target.
                    registration.Dispose();
                    registration = null;
                    VerifyPoseAt(2);
                    var afterUnregister = ReadFrame(events);
                    VerifyValidity(afterUnregister, "OwnerWorldValid", true);
                    VerifyValidity(afterUnregister, "RegisteredWorldValid", false);
                    registration = LiberaFxSkeletalPoseTarget.RegisterTarget("hero.skeleton", registeredRig.target);
                    VerifyPoseAt(3);
                    VerifyValidity(ReadFrame(events), "RegisteredWorldValid", false);

                    // Replacing a target's registered bones invalidates its generation.
                    var replacementBone = new GameObject("hand_r replacement");
                    replacementBone.transform.SetParent(registeredRig.root, false);
                    registeredRig.renderer.bones = new[] { registeredRig.root, replacementBone.transform };
                    VerifyPoseAt(4);
                    var afterBoneChange = ReadFrame(events);
                    VerifyValidity(afterBoneChange, "OwnerWorldValid", true);
                    VerifyValidity(afterBoneChange, "RegisteredWorldValid", false);
                    Object.DestroyImmediate(replacementBone);

                    // Disabling the owner pose target likewise invalidates only its
                    // own leases; the registered target was already invalidated.
                    ownerRig.target.enabled = false;
                    VerifyPoseAt(5);
                    var afterOwnerDisable = ReadFrame(events);
                    VerifyValidity(afterOwnerDisable, "OwnerWorldValid", false);
                    VerifyValidity(afterOwnerDisable, "RegisteredWorldValid", false);

                    Require(component.Stop() == NativeResult.Ok, "Skeletal SOURCE cleanup Stop failed.");
                    File.WriteAllText(Path.Combine(ProjectRoot, "source-batch-skeletal-result.txt"),
                        "PASS: compiler SOURCE BoneLocal/Component/World owner+registered-key, two real AnimationClip samples, unregister/bone-change/disable invalidation; Edit Mode hook invocation (not Play Mode scheduling)\n");
                    Debug.Log("PASS source-batch skeletal pose SOURCE acceptance.");

                    void VerifyPoseAt(float time)
                    {
                        clip.SampleAnimation(owner, time);
                        clip.SampleAnimation(registeredOwner, time);
                        events.Clear();
                        elapsed.SetValue(component, (double)time - Time.deltaTime);
                        lateUpdate.Invoke(component, null);
                        Require(string.IsNullOrEmpty(component.LastError),
                            "SOURCE frame failed at animation time " + time + ": " + component.LastError);
                    }
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(ProjectRoot, "source-batch-skeletal-result.txt"), "FAIL\n" + error);
                    Debug.LogException(error);
                    EditorApplication.Exit(2);
                }
                finally
                {
                    registration?.Dispose();
                    if (component) component.Stop();
                    if (owner) { owner.SetActive(false); Object.DestroyImmediate(owner); }
                    if (registeredOwner) { registeredOwner.SetActive(false); Object.DestroyImmediate(registeredOwner); }
                    if (ownerRig?.mesh) Object.DestroyImmediate(ownerRig.mesh);
                    if (registeredRig?.mesh) Object.DestroyImmediate(registeredRig.mesh);
                    Object.DestroyImmediate(clip);
                    Object.DestroyImmediate(package);
                    Object.DestroyImmediate(bindings);
                }

                Rig CreateRig(GameObject targetOwner, string name)
                {
                    var root = new GameObject("Root");
                    root.transform.SetParent(targetOwner.transform, false);
                    var hand = new GameObject("hand_r");
                    hand.transform.SetParent(root.transform, false);
                    hand.transform.localPosition = new Vector3(0.25f, 0.1f, 0.3f);

                    var mesh = new Mesh { name = name + " isolated skinned test mesh" };
                    mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                    mesh.triangles = new[] { 0, 1, 2 };
                    mesh.boneWeights = new[]
                    {
                        new BoneWeight { boneIndex0 = 1, weight0 = 1 },
                        new BoneWeight { boneIndex0 = 1, weight0 = 1 },
                        new BoneWeight { boneIndex0 = 1, weight0 = 1 }
                    };
                    var renderer = targetOwner.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    renderer.bones = new[] { root.transform, hand.transform };
                    renderer.rootBone = root.transform;
                    mesh.bindposes = new[]
                    {
                        root.transform.worldToLocalMatrix * targetOwner.transform.localToWorldMatrix,
                        hand.transform.worldToLocalMatrix * targetOwner.transform.localToWorldMatrix
                    };
                    var target = targetOwner.AddComponent<LiberaFxSkeletalPoseTarget>();
                    target.enabled = false;
                    return new Rig(target, renderer, root.transform, hand.transform, mesh);
                }

                void ConfigureTarget(LiberaFxSkeletalPoseTarget target, SkinnedMeshRenderer renderer, Transform root)
                {
                    var serialized = new SerializedObject(target);
                    serialized.FindProperty("skinnedMesh").objectReferenceValue = renderer;
                    serialized.FindProperty("skeletonRoot").objectReferenceValue = root;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    target.enabled = true;
                }

                Matrix4x4 Pose(Rig rig, float time)
                {
                    clip.SampleAnimation(rig.target.gameObject, time);
                    return rig.hand.localToWorldMatrix;
                }
            }

        public static void RunSkeletalPosePlayModeAcceptance()
        {
            RequireIsolatedProject();
            Require(!EditorApplication.isPlayingOrWillChangePlaymode,
                "The isolated Editor must begin this acceptance run outside Play Mode.");
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_SKELETAL_FIXTURE");
            Require(Path.IsPathRooted(fixture ?? "") && File.Exists(fixture),
                "Supply the compiler-produced AnimatedSkeletalSpaces.lfxp file.");
            var resultPath = Path.Combine(ProjectRoot, "source-batch-skeletal-playmode-result.txt");

            try
            {
                Require(!AssetDatabase.IsValidFolder(SkeletalPlayAssetFolder),
                    "Refusing to overwrite an existing Play Mode acceptance asset folder.");
                AssetDatabase.CreateFolder("Assets", "LiberaFxSourcePlayModeAcceptance");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var owner = new GameObject("LIBERA FX SOURCE PlayMode owner");
                owner.SetActive(false);
                var registeredOwner = new GameObject("LIBERA FX SOURCE PlayMode registered target");
                registeredOwner.SetActive(false);
                var clip = new AnimationClip
                {
                    name = "SOURCE PlayMode animated hand_r",
                    legacy = true,
                    frameRate = 60,
                    wrapMode = WrapMode.Loop
                };
                clip.SetCurve("Root/hand_r", typeof(Transform), "m_LocalPosition.x",
                    AnimationCurve.Linear(0, 0.25f, 1, 1.25f));
                AssetDatabase.CreateAsset(clip, SkeletalPlayAssetFolder + "/HandMotion.anim");

                var ownerRig = CreatePersistentRig(owner, "OwnerRig");
                var registeredRig = CreatePersistentRig(registeredOwner, "RegisteredRig");
                owner.transform.position = new Vector3(3, -2, 5);
                owner.transform.rotation = Quaternion.Euler(12, 27, -8);
                owner.transform.localScale = new Vector3(1.7f, 0.8f, 1.2f);
                ownerRig.root.localPosition = new Vector3(0.5f, 0.25f, -0.75f);
                ownerRig.root.localRotation = Quaternion.Euler(0, 18, 5);
                registeredOwner.transform.position = new Vector3(-4, 6, 2);
                registeredOwner.transform.rotation = Quaternion.Euler(-9, 41, 13);
                registeredOwner.transform.localScale = new Vector3(0.65f, 1.4f, 0.9f);
                registeredRig.root.localPosition = new Vector3(-0.2f, 0.8f, 0.1f);
                registeredRig.root.localRotation = Quaternion.Euler(21, -6, 17);
                AddAnimation(owner, clip);
                AddAnimation(registeredOwner, clip);

                var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                package.name = "Compiler AnimatedSkeletalSpaces SOURCE package";
                package.SetPackageBytes(File.ReadAllBytes(fixture));
                AssetDatabase.CreateAsset(package, SkeletalPlayAssetFolder + "/SkeletalPackage.asset");
                var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                bindings.name = "SOURCE PlayMode bindings";
                AssetDatabase.CreateAsset(bindings, SkeletalPlayAssetFolder + "/SkeletalBindings.asset");

                var component = owner.AddComponent<LiberaFxSpawnableComponent>();
                var serializedComponent = new SerializedObject(component);
                serializedComponent.FindProperty("effect").objectReferenceValue = package;
                serializedComponent.FindProperty("engineBindings").objectReferenceValue = bindings;
                serializedComponent.FindProperty("playOnEnable").boolValue = false;
                serializedComponent.ApplyModifiedPropertiesWithoutUndo();
                Require(!owner.activeInHierarchy && !registeredOwner.activeInHierarchy,
                    "SOURCE target and effect references must be configured before activation.");
                owner.SetActive(true);
                registeredOwner.SetActive(true);
                Require(EditorSceneManager.SaveScene(scene,
                    SkeletalPlayAssetFolder + "/SkeletalPoseAcceptance.unity"),
                    "Could not save the persistent SOURCE acceptance scene.");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                ResetSkeletalPlayModeState();
                SessionState.SetString(SkeletalPlayResultPath, resultPath);
                SessionState.SetBool(SkeletalPlayFinished, false);
                SessionState.SetInt(SkeletalPlayExitCode, 2);
                SessionState.SetBool(SkeletalPlayActive, true);
                EditorApplication.delayCall += () =>
                {
                    if (SessionState.GetBool(SkeletalPlayActive, false))
                        EditorApplication.isPlaying = true;
                };

                Rig CreatePersistentRig(GameObject targetOwner, string name)
                {
                    var root = new GameObject("Root");
                    root.transform.SetParent(targetOwner.transform, false);
                    var hand = new GameObject("hand_r");
                    hand.transform.SetParent(root.transform, false);
                    hand.transform.localPosition = new Vector3(0.25f, 0.1f, 0.3f);

                    var mesh = new Mesh { name = name + " persistent SOURCE skinned mesh" };
                    mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                    mesh.triangles = new[] { 0, 1, 2 };
                    mesh.boneWeights = new[]
                    {
                        new BoneWeight { boneIndex0 = 1, weight0 = 1 },
                        new BoneWeight { boneIndex0 = 1, weight0 = 1 },
                        new BoneWeight { boneIndex0 = 1, weight0 = 1 }
                    };
                    mesh.bindposes = new[]
                    {
                        root.transform.worldToLocalMatrix * targetOwner.transform.localToWorldMatrix,
                        hand.transform.worldToLocalMatrix * targetOwner.transform.localToWorldMatrix
                    };
                    AssetDatabase.CreateAsset(mesh,
                        SkeletalPlayAssetFolder + "/" + name + "Mesh.asset");
                    var renderer = targetOwner.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    renderer.bones = new[] { root.transform, hand.transform };
                    renderer.rootBone = root.transform;
                    var target = targetOwner.AddComponent<LiberaFxSkeletalPoseTarget>();
                    target.enabled = false;
                    var serializedTarget = new SerializedObject(target);
                    serializedTarget.FindProperty("skinnedMesh").objectReferenceValue = renderer;
                    serializedTarget.FindProperty("skeletonRoot").objectReferenceValue = root.transform;
                    serializedTarget.ApplyModifiedPropertiesWithoutUndo();
                    target.enabled = true;
                    return new Rig(target, renderer, root.transform, hand.transform, mesh);
                }

                void AddAnimation(GameObject targetOwner, AnimationClip sourceClip)
                {
                    var animation = targetOwner.AddComponent<Animation>();
                    animation.playAutomatically = false;
                    animation.clip = sourceClip;
                    animation.cullingType = AnimationCullingType.AlwaysAnimate;
                    animation.wrapMode = WrapMode.Loop;
                }
            }
            catch (Exception error)
            {
                var message = "FAIL\n" + error;
                File.WriteAllText(resultPath, message);
                Debug.LogException(error);
                SessionState.SetBool(SkeletalPlayActive, false);
                EditorApplication.Exit(2);
            }
        }

        public static void RunInstanceGameIoPlayModeBatch()
        {
            RequireIsolatedProject();
            Require(!EditorApplication.isPlayingOrWillChangePlaymode,
                "The isolated Editor is already changing Play Mode.");
            var resultPath = Path.Combine(ProjectRoot, InstanceGameIoResultFile);
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
            SessionState.SetBool(InstanceGameIoPlayPending, true);
            SessionState.SetInt(InstanceGameIoPlayResult, 2);
            RegisterDebugPlayModeCallbacks();
            EditorApplication.EnterPlaymode();
        }

        private static void InstanceGameIoPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(InstanceGameIoPlayPending, false))
            {
                return;
            }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                var test = VerifyInstanceGameIo(InstanceGameIoResultFile, advanceInPlayMode: true);
                EditorApplication.update += AdvanceTest;

                void AdvanceTest()
                {
                    try
                    {
                        if (test.MoveNext())
                        {
                            return;
                        }
                        SessionState.SetInt(InstanceGameIoPlayResult, 0);
                    }
                    catch (Exception error)
                    {
                        File.WriteAllText(Path.Combine(ProjectRoot, InstanceGameIoResultFile), "FAIL\n" + error);
                        Debug.LogException(error);
                    }
                    EditorApplication.update -= AdvanceTest;
                    test.Dispose();
                    EditorApplication.ExitPlaymode();
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(InstanceGameIoPlayPending, false);
                EditorApplication.Exit(SessionState.GetInt(InstanceGameIoPlayResult, 2));
            }
        }

        public static void RunInstanceGameIoEditorBatch()
        {
            RequireIsolatedProject();
            const string resultFile = "source-batch-instance-gameio-result.txt";
            try
            {
                using var test = VerifyInstanceGameIo(resultFile, advanceInPlayMode: false);
                Require(!test.MoveNext(), "Edit Mode verification must complete synchronously.");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(ProjectRoot, resultFile), "FAIL\n" + error);
                Debug.LogException(error);
                EditorApplication.Exit(2);
            }
        }

        private static IEnumerator<object> VerifyInstanceGameIo(string resultFile, bool advanceInPlayMode)
        {
            Require(!advanceInPlayMode || Application.isPlaying, "Run this check only in actual Unity Play Mode.");
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_INSTANCE_GAMEIO_FIXTURE");
            Require(Path.IsPathRooted(fixture ?? "") && File.Exists(fixture),
                "Supply compiler-produced InstanceGameIo.lfxp.");
            var containerPath = Path.ChangeExtension(fixture, ".container");
            Require(File.Exists(containerPath), "The matching binary InstanceGameIo.container is required.");
            var container = File.ReadAllBytes(containerPath);
            Require(container.Length == 16, "InstanceGameIo.container must contain exactly 16 big-endian GUID bytes.");
            var templateId = Guid.ParseExact(BitConverter.ToString(container).Replace("-", ""), "N");

            var owner = new GameObject("Instance Game I/O acceptance owner");
            owner.SetActive(false);
            var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            LiberaFxSpawnableComponent component = null;
            var hostEvents = new List<SpawnableHostEvent>();
            var ioEvents = new List<GameIoEvent>();
            var resultPath = Path.Combine(ProjectRoot, resultFile);
            try
            {
                package.SetPackageBytes(File.ReadAllBytes(fixture));
                component = owner.AddComponent<LiberaFxSpawnableComponent>();
                Require(component.ConfigureForSession(package, bindings) == NativeResult.Ok,
                    "Could not configure the compiler-produced instance Game I/O package.");
                component.HostEventReceived += hostEvents.Add;
                component.GameIoEventReceived += ioEvents.Add;
                owner.SetActive(true);
                Require(component.Play() == NativeResult.Ok,
                    "Instance Game I/O package failed to play: " + component.LastError);

                for (var index = 0; index < 2; ++index)
                    Require(component.FireGameSignal("spawn") == NativeResult.Ok,
                        "Could not fire root InputSignal 'spawn'.");
                var spawned = hostEvents.Where(item => item.Kind == 1 &&
                    item.ContainerId.SequenceEqual(container)).ToArray();
                Require(spawned.Length == 2 && spawned[0].InstanceId != 0 &&
                    spawned[1].InstanceId != 0 && spawned[0].InstanceId != spawned[1].InstanceId,
                    "Two root spawn signals did not create distinct siblings in the fixture container " +
                    templateId + ".");

                var firstGeneration = 0UL;
                var secondGeneration = 0UL;
                var firstTargetResult = component.GetInstanceTarget(container, spawned[0].InstanceId,
                    out firstGeneration);
                var secondTargetResult = component.GetInstanceTarget(container, spawned[1].InstanceId,
                    out secondGeneration);
                Require(firstTargetResult == NativeResult.Ok && firstGeneration != 0 &&
                    secondTargetResult == NativeResult.Ok && secondGeneration != 0,
                    "The public instance-target facade did not resolve both generation-scoped siblings.");
                VerifyProperty(spawned[0], firstGeneration, "speed", new[] { 0d });
                VerifyProperty(spawned[1], secondGeneration, "speed", new[] { 0d });

                var firstSpeed = 17.25d;
                var secondSpeed = 29.5d;
                var firstMatrix = Enumerable.Range(0, 16).Select(index => 1.5d + index).ToArray();
                var secondMatrix = Enumerable.Range(0, 16).Select(index => 101.25d + index).ToArray();
                Require(component.SetInstanceProperty(firstGeneration, container, spawned[0].InstanceId,
                    "speed", new[] { firstSpeed }) == NativeResult.Ok &&
                    component.SetInstanceProperty(secondGeneration, container, spawned[1].InstanceId,
                    "speed", new[] { secondSpeed }) == NativeResult.Ok &&
                    component.SetInstanceProperty(firstGeneration, container, spawned[0].InstanceId,
                    "matrix", firstMatrix) == NativeResult.Ok &&
                    component.SetInstanceProperty(secondGeneration, container, spawned[1].InstanceId,
                    "matrix", secondMatrix) == NativeResult.Ok,
                    "A scoped speed or Matrix input update was rejected.");

                ioEvents.Clear();
                if (advanceInPlayMode)
                {
                    var targetTime = Time.timeAsDouble + 1;
                    var deadline = EditorApplication.timeSinceStartup + 15;
                    while (Time.timeAsDouble < targetTime)
                    {
                        Require(EditorApplication.timeSinceStartup < deadline,
                            "Unity Play Mode did not advance one second before the deadline.");
                        yield return null;
                    }
                }
                else
                {
                    var lateUpdate = typeof(LiberaFxSpawnableComponent).GetMethod("LateUpdate",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var elapsed = typeof(LiberaFxSpawnableComponent).GetField("elapsed",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Require(lateUpdate != null && elapsed != null,
                        "Could not locate the component's Unity frame-advance hook.");
                    elapsed.SetValue(component, 1d - (double)Time.deltaTime);
                    lateUpdate.Invoke(component, null);
                }
                Require(string.IsNullOrEmpty(component.LastError),
                    "Advancing the instance package failed: " + component.LastError);

                VerifyProperty(spawned[0], firstGeneration, "speed", new[] { firstSpeed });
                VerifyProperty(spawned[1], secondGeneration, "speed", new[] { secondSpeed });
                VerifyProperty(spawned[0], firstGeneration, "result", new[] { firstSpeed });
                VerifyProperty(spawned[1], secondGeneration, "result", new[] { secondSpeed });
                VerifyProperty(spawned[0], firstGeneration, "matrixResult", firstMatrix);
                VerifyProperty(spawned[1], secondGeneration, "matrixResult", secondMatrix);

                ioEvents.Clear();
                Require(component.FireInstanceSignal(firstGeneration, container,
                    spawned[0].InstanceId, "ping") == NativeResult.Ok,
                    "The targeted InstanceInputSignal 'ping' was rejected.");
                var pong = ioEvents.Where(item => item.PortName == "pong").ToArray();
                Require(pong.Length == 1 && pong[0].ContainerId.SequenceEqual(container) &&
                    pong[0].InstanceId == spawned[0].InstanceId && pong[0].Width == 0 &&
                    ioEvents.All(item => item.PortName != "pong" || item.InstanceId != spawned[1].InstanceId),
                    "Targeted ping did not produce exactly one isolated sibling InstanceOutputSignal 'pong'.");
                Require(component.GetInstanceTarget(container, spawned[1].InstanceId,
                    out var liveSiblingGeneration) == NativeResult.Ok &&
                    liveSiblingGeneration == secondGeneration,
                    "Dispatching ping retired or retargeted the sibling instance.");
                VerifyProperty(spawned[1], secondGeneration, "result", new[] { secondSpeed });
                VerifyProperty(spawned[1], secondGeneration, "matrixResult", secondMatrix);

                Require(component.Stop() == NativeResult.Ok, "Instance Game I/O cleanup Stop failed.");
                File.WriteAllText(resultPath,
                    "PASS: compiler fixture sibling targets; scoped speed/result and 16-lane matrix/matrixResult; targeted ping/pong\n");
                Debug.Log("PASS source-batch instance Game I/O acceptance (" +
                    (advanceInPlayMode ? "Play Mode" : "Edit Mode") + ").");

                void VerifyProperty(SpawnableHostEvent target, ulong generation, string name, double[] expected)
                {
                    Require(component.GetInstanceProperty(generation, container, target.InstanceId,
                        name, out var actual) == NativeResult.Ok && actual != null &&
                        actual.Length == expected.Length,
                        "Could not read scoped instance property '" + name + "'.");
                    for (var lane = 0; lane < expected.Length; ++lane)
                        Require(Math.Abs(actual[lane] - expected[lane]) < 1e-8,
                            "Scoped instance property '" + name + "' differed at lane " + lane + ".");
                }

            }
            finally
            {
                if (component) component.Stop();
                if (owner) { owner.SetActive(false); Object.DestroyImmediate(owner); }
                Object.DestroyImmediate(package);
                Object.DestroyImmediate(bindings);
            }
        }

        private static void RunDebugDrawPayloadInPlayMode()
        {
            RequireIsolatedProject();
            Require(Application.isPlaying, "Run this check only in actual Unity Play Mode.");
            var directory = Environment.GetEnvironmentVariable("LIBERAFX_DEBUG_DRAW_FIXTURE_DIR");
            Require(Path.IsPathRooted(directory ?? ""),
                "Supply the absolute directory containing compiler-produced preview/distribution LFXP files.");
            var previewPath = Path.Combine(directory, "engine-preview.lfxp");
            var textPath = Path.Combine(directory, "text-preview.lfxp");
            var distributionPath = Path.Combine(directory, "distribution.lfxp");
            Require(File.Exists(previewPath) && File.Exists(textPath) && File.Exists(distributionPath),
                "The compiler-produced preview, text-preview and distribution fixtures are required.");

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.SetActive(false);
            var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            var shader = Shader.Find("Unlit/Color");
            Require(shader && shader.isSupported, "The isolated project requires a supported Unlit/Color shader.");
            var material = new Material(shader) { color = Color.green };
            var cameraObject = new GameObject("LIBERA FX preview debug render-target camera");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 2;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = -1;
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var previousTarget = RenderTexture.active;
            LiberaFxUnityBindingAsset debugBindings = null;
            LiberaFxUnityBindingAsset modelBindings = null;
            LiberaFxSpawnableAsset previewAsset = null;
            LiberaFxSpawnableAsset textAsset = null;
            LiberaFxSpawnableAsset distributionAsset = null;
            LiberaFxSpawnableAsset modelAsset = null;
            GameObject previewOwner = null;
            GameObject textOwner = null;
            GameObject distributionOwner = null;
            GameObject modelOwner = null;
            try
            {
                Require(target.Create(), "Could not create the isolated preview render target.");
                debugBindings = CreateBindings("/Engine/BasicShapes/Cube.Cube", mesh, material);
                previewAsset = CreatePackage(previewPath);
                textAsset = CreatePackage(textPath);
                distributionAsset = CreatePackage(distributionPath);

                var preview = Start(previewAsset, debugBindings, "compiler preview debug package", scene, out previewOwner);
                var text = Start(textAsset, debugBindings, "compiler text preview package", scene, out textOwner);
                var labels = Labels(scene);
                Require(labels.Length == 2 && labels.Any(label => label.text == "PRIVATE_DEBUG_ONLY_日本語"),
                    "Preview packages did not create their real world TextMesh labels.");
                var previewModel = DebugModels(scene).SingleOrDefault();
                Require(previewModel, "Compiler preview did not opt in the Debug Model draw.");
                var filter = previewModel.GetComponent<MeshFilter>();
                var renderer = previewModel.GetComponent<MeshRenderer>();
                Require(filter && filter.sharedMesh == mesh && renderer &&
                    renderer.sharedMaterial == material && previewModel.activeInHierarchy,
                    "Preview Debug Model did not bind the actual Unity mesh/material or become visible.");
                Require(Vector3.Distance(previewModel.transform.position, new Vector3(17, 23, 31)) < 0.001f,
                    "Preview Debug Model did not apply the compiler-authored world matrix.");

                previewModel.layer = 0;
                camera.transform.position = new Vector3(17, 23, 21);
                camera.transform.LookAt(new Vector3(17, 23, 31));
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                pixels.Apply();
                var capturedPixels = pixels.GetPixels32();
                var greenPixels = capturedPixels.Count(pixel => pixel.g > 100 && pixel.g > pixel.r * 1.4f);
                var coloredPixels = capturedPixels.Count(pixel => pixel.r + pixel.g + pixel.b > 30);
                var maxGreen = capturedPixels.Max(pixel => pixel.g);
                File.WriteAllBytes(Path.Combine(ProjectRoot, "source-batch-debug-model.png"), pixels.EncodeToPNG());
                Require(greenPixels > 32,
                    "The real preview Model did not produce green pixels in the Unity camera render target: " +
                    "greenPixels=" + greenPixels + ", coloredPixels=" + coloredPixels + ", maxGreen=" + maxGreen +
                    ", rendererVisible=" + renderer.isVisible + ", bounds=" + renderer.bounds +
                    ", viewport=" + camera.WorldToViewportPoint(previewModel.transform.position) +
                    ", graphics=" + SystemInfo.graphicsDeviceType + "/" + SystemInfo.graphicsDeviceName + ".");

                var elapsed = preview.ElapsedTime;
                var scopes = preview.ActiveRenderScopeCount;
                preview.ClearPreviewDebugDraw();
                Require(Labels(scene).Length == 0 && !previewModel.activeInHierarchy && preview.ElapsedTime == elapsed &&
                    preview.ActiveRenderScopeCount == scopes && !preview.IsRootFinished,
                    "Disconnect-style debug clear removed normal playback state or retained a preview draw.");
                Require(preview.Play() == NativeResult.Ok && Labels(scene).Length == 0 &&
                    DebugModels(scene).Length == 0,
                    "Local restart resurrected preview-only draws after disconnect.");
                Require(preview.Stop() == NativeResult.Ok && preview.ActiveRenderScopeCount == 0,
                    "Stop did not retire the preview scope.");
                Require(text.Stop() == NativeResult.Ok, "Stop failed for the compiler text-preview package.");
                Require(Labels(scene).Length == 1, "Text preview Stop retained its label.");

                var distribution = Start(distributionAsset, debugBindings,
                    "compiler distribution package", scene, out distributionOwner);
                Require(distribution.ActiveRenderScopeCount == 0 && Labels(scene).Length == 0 &&
                    DebugModels(scene).Length == 0,
                    "Distribution package unexpectedly created preview-only Text or Model draws.");
                Require(distribution.Stop() == NativeResult.Ok, "Distribution package Stop failed.");

                modelBindings = CreateBindings("/Game/LiberaFxSamples/Cube.Cube", mesh, material);
                modelAsset = CreatePackage(Path.Combine(
                    Environment.GetEnvironmentVariable("LIBERAFX_MODEL_DRAW_FIXTURE_DIR") ?? "", "Cube.lfxp"));
                var ordinary = Start(modelAsset, modelBindings, "ordinary authored model", scene, out modelOwner);
                var ordinaryRenderer = modelOwner.GetComponentsInChildren<MeshRenderer>().Single();
                var ordinaryScopeCount = ordinary.ActiveRenderScopeCount;
                var ordinaryElapsed = ordinary.ElapsedTime;
                ordinary.ClearPreviewDebugDraw();
                Require(ordinary.ElapsedTime == ordinaryElapsed &&
                    ordinary.ActiveRenderScopeCount == ordinaryScopeCount &&
                    modelOwner.GetComponentsInChildren<MeshRenderer>().Single() == ordinaryRenderer,
                    "Disconnect-style debug clear stopped or replaced ordinary ModelDraw playback.");
                Require(ordinary.Stop() == NativeResult.Ok &&
                    modelOwner.GetComponentsInChildren<MeshRenderer>().Length == 0,
                    "Stop did not retire the ordinary ModelDraw renderer.");

                // Scope retirement must release both kinds of debug-owned objects.
                Object.DestroyImmediate(previewOwner);
                previewOwner = null;
                preview = Start(previewAsset, debugBindings, "preview retirement", scene, out previewOwner);
                Require(Labels(scene).Length == 1 && DebugModels(scene).Length == 1,
                    "Preview retirement setup did not create both opt-in draw types.");
                Require(preview.ForceTerminate() == NativeResult.Ok &&
                    preview.ActiveRenderScopeCount == 0 && Labels(scene).Length == 0 &&
                    DebugModels(scene).Length == 0,
                    "Effect retirement retained preview Text/Model draw resources.");
                File.WriteAllText(Path.Combine(ProjectRoot, "source-batch-debug-draw-result.txt"),
                    "PASS: compiler preview Text+Model, camera pixels, distribution exclusion, clear/disconnect preservation, Stop and retirement\n");
                Debug.Log("PASS source-batch debug-draw payload acceptance.");
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (previewOwner) { previewOwner.SetActive(false); Object.DestroyImmediate(previewOwner); }
                if (textOwner) { textOwner.SetActive(false); Object.DestroyImmediate(textOwner); }
                if (distributionOwner) { distributionOwner.SetActive(false); Object.DestroyImmediate(distributionOwner); }
                if (modelOwner) { modelOwner.SetActive(false); Object.DestroyImmediate(modelOwner); }
                if (previewAsset) Object.DestroyImmediate(previewAsset);
                if (textAsset) Object.DestroyImmediate(textAsset);
                if (distributionAsset) Object.DestroyImmediate(distributionAsset);
                if (modelAsset) Object.DestroyImmediate(modelAsset);
                if (debugBindings) Object.DestroyImmediate(debugBindings);
                if (modelBindings) Object.DestroyImmediate(modelBindings);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(primitive);
            }
        }

        private static LiberaFxSpawnableComponent Start(LiberaFxSpawnableAsset package,
            LiberaFxUnityBindingAsset bindings, string name,
            UnityEngine.SceneManagement.Scene scene, out GameObject owner)
        {
            owner = new GameObject(name);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
            owner.SetActive(false);
            var component = owner.AddComponent<LiberaFxSpawnableComponent>();
            Require(component.ConfigureForSession(package, bindings) == NativeResult.Ok,
                "Could not configure " + name + ".");
            owner.SetActive(true);
            Require(component.Play() == NativeResult.Ok, name + " failed to play: " + component.LastError);
            return component;
        }

        private sealed class Rig
        {
            internal readonly LiberaFxSkeletalPoseTarget target;
            internal readonly SkinnedMeshRenderer renderer;
            internal readonly Transform root;
            internal readonly Transform hand;
            internal readonly Mesh mesh;

            internal Rig(LiberaFxSkeletalPoseTarget poseTarget, SkinnedMeshRenderer skin,
                Transform rootBone, Transform handBone, Mesh skinnedMesh)
            {
                target = poseTarget;
                renderer = skin;
                root = rootBone;
                hand = handBone;
                mesh = skinnedMesh;
            }
        }

        private static Dictionary<string, GameIoEvent> ReadFrame(List<GameIoEvent> events) =>
            events.Where(item => !string.IsNullOrEmpty(item.PortName))
                .GroupBy(item => item.PortName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        private static void VerifyMatrix(Dictionary<string, GameIoEvent> frame, string name, Matrix4x4 expected)
        {
            Require(frame.TryGetValue(name, out var output),
                "The compiler package did not emit Output Property '" + name + "'.");
            Require(output.Width == 16 && output.Values != null && output.Values.Length >= 16,
                "Output Property '" + name + "' did not preserve all 16 Matrix lanes.");
            for (var row = 0; row < 4; ++row)
            for (var column = 0; column < 4; ++column)
            {
                var index = row * 4 + column;
                Require(Math.Abs(output.Values[index] - expected[row, column]) < 0.0002,
                    "Output Property '" + name + "' matrix lane " + index + " differed: " +
                    output.Values[index] + " != " + expected[row, column] + ".");
            }
        }

        private static void VerifyValidity(Dictionary<string, GameIoEvent> frame, string name, bool expected)
        {
            Require(frame.TryGetValue(name, out var output) && output.Width >= 1 &&
                output.Values != null && output.Values.Length >= 1,
                "The compiler package did not emit validity Output Property '" + name + "'.");
            Require(Math.Abs(output.Values[0] - (expected ? 1 : 0)) < 0.0001,
                "Validity Output Property '" + name + "' was " + output.Values[0] +
                " but expected " + (expected ? 1 : 0) + ".");
        }

        private static LiberaFxSpawnableAsset CreatePackage(string path)
        {
            var package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            package.SetPackageBytes(File.ReadAllBytes(path));
            return package;
        }

        private static LiberaFxUnityBindingAsset CreateBindings(string identity, Mesh mesh, Material material)
        {
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var serialized = new SerializedObject(bindings);
            var assets = serialized.FindProperty("renderAssets");
            assets.arraySize = 1;
            var asset = assets.GetArrayElementAtIndex(0);
            asset.FindPropertyRelative("assetIdentity").stringValue = identity;
            asset.FindPropertyRelative("mesh").objectReferenceValue = mesh;
            var defaults = asset.FindPropertyRelative("defaultMaterials");
            defaults.arraySize = 1;
            defaults.GetArrayElementAtIndex(0).objectReferenceValue = material;
            var tintNames = asset.FindPropertyRelative("defaultTintPropertyNames");
            tintNames.arraySize = 1;
            tintNames.GetArrayElementAtIndex(0).stringValue = "_Color";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return bindings;
        }

        private static TextMesh[] Labels(UnityEngine.SceneManagement.Scene scene) =>
            Resources.FindObjectsOfTypeAll<TextMesh>().Where(label => label &&
                label.gameObject.scene == scene && label.gameObject.activeInHierarchy &&
                label.name.StartsWith("LIBERA FX Preview Debug ", StringComparison.Ordinal)).ToArray();

        private static GameObject[] DebugModels(UnityEngine.SceneManagement.Scene scene) =>
            Resources.FindObjectsOfTypeAll<MeshFilter>().Where(filter => filter &&
                filter.gameObject.scene == scene && filter.gameObject.activeInHierarchy &&
                filter.name.StartsWith("LIBERA FX Preview Debug ", StringComparison.Ordinal))
                .Select(filter => filter.gameObject).ToArray();

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        private static void RequireIsolatedProject()
        {
            var expected = Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT");
            Require(Path.IsPathRooted(expected ?? "") &&
                string.Equals(Path.GetFullPath(expected).TrimEnd('\\', '/'),
                    Path.GetFullPath(ProjectRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase),
                "Run only in the explicitly named isolated verification project.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
