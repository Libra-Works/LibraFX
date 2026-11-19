using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxLiveGameVerification
    {
        private const string PlayPending = "LiberaFx.LiveGameVerification.Pending";
        private const string PlayResult = "LiberaFx.LiveGameVerification.Result";
        private const string PlayStarted = "LiberaFx.LiveGameVerification.Started";
        [InitializeOnLoadMethod]
        private static void ResumePlayVerification()
        {
            EditorApplication.playModeStateChanged -= PlayChanged;
            EditorApplication.playModeStateChanged += PlayChanged;
            EditorApplication.update -= CheckTimeout;
            EditorApplication.update += CheckTimeout;
        }
        // Launch ONLY in the dedicated verification project, without -quit.
        // SessionState survives the play-domain reload but does not touch prefs.
        public static void RunPlayModeBatch()
        {
            var expected = Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT");
            var actual = Directory.GetParent(Application.dataPath).FullName;
            Require(!string.IsNullOrEmpty(expected) && Path.GetFullPath(expected).TrimEnd('\\', '/') ==
                Path.GetFullPath(actual).TrimEnd('\\', '/'), "Refusing Play Mode outside the explicitly named isolated verification project.");
            SessionState.SetBool(PlayPending, true);
            SessionState.SetInt(PlayResult, 2);
            SessionState.SetString(PlayStarted, DateTime.UtcNow.Ticks.ToString());
            ResumePlayVerification();
            EditorApplication.EnterPlaymode();
        }
        private static void PlayChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(PlayPending, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                EditorApplication.delayCall += () => {
                    try { Require(Application.isPlaying, "Verification must execute in the real game loop."); RunBatch(); SessionState.SetInt(PlayResult, 0); }
                    catch (Exception error) { Debug.LogException(error); SessionState.SetInt(PlayResult, 2); }
                    finally { EditorApplication.ExitPlaymode(); }
                };
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var result = SessionState.GetInt(PlayResult, 2);
                SessionState.SetBool(PlayPending, false);
                EditorApplication.Exit(result);
            }
        }
        private static void CheckTimeout()
        {
            if (SessionState.GetBool(PlayPending, false) &&
                long.TryParse(SessionState.GetString(PlayStarted, ""), out var started) &&
                DateTime.UtcNow.Ticks - started > TimeSpan.FromMinutes(3).Ticks)
            {
                SessionState.SetBool(PlayPending, false);
                Debug.LogError("Live game Play Mode verification timed out.");
                EditorApplication.Exit(2);
            }
        }
        [Serializable] private sealed class Ids { public string position; }
        private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
        // Dedicated project only. This test constructs REAL components/renderers,
        // uses the same spool as a connected tool, and renders actual camera pixels.
        public static void RunBatch()
        {
            var directory = Environment.GetEnvironmentVariable("LIBERAFX_LIVE_FIXTURE_DIR");
            var root = Environment.GetEnvironmentVariable("LIBERAFX_LIVE_SESSION_ROOT");
            Require(!string.IsNullOrEmpty(directory) && Path.IsPathRooted(directory), "Supply absolute compiler fixture directory.");
            Require(!string.IsNullOrEmpty(root) && Directory.Exists(root), "Supply existing isolated private session root.");
            var before = File.ReadAllBytes(Path.Combine(directory, "Live.before.lfxp"));
            var after = File.ReadAllBytes(Path.Combine(directory, "Live.after.lfxp"));
            var unsupported = File.ReadAllBytes(Path.Combine(directory, "Live.unsupported.lfxp"));
            var ids = JsonUtility.FromJson<Ids>(File.ReadAllText(Path.Combine(directory, "Live.identities.json")));
            var owner = new GameObject("Live game isolated verification");
            owner.SetActive(false);
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.SetActive(false);
            var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
            var shader = Shader.Find("Unlit/Color");
            Require(shader && shader.isSupported, "GPU verification requires supported Unlit/Color shader.");
            var material = new Material(shader);
            var cameraObject = new GameObject("Live game verification camera");
            var target = new RenderTexture(128, 128, 24);
            var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            var previousTarget = RenderTexture.active;
            LiberaFxLiveGameSession session = null;
            var debugOwner = "";
            try
            {
                var serialized = new SerializedObject(bindings);
                var assets = serialized.FindProperty("renderAssets");
                assets.arraySize = 1;
                var asset = assets.GetArrayElementAtIndex(0);
                asset.FindPropertyRelative("assetIdentity").stringValue = "/Engine/BasicShapes/Cube.Cube";
                asset.FindPropertyRelative("mesh").objectReferenceValue = primitive.GetComponent<MeshFilter>().sharedMesh;
                asset.FindPropertyRelative("defaultMaterials").arraySize = 1;
                asset.FindPropertyRelative("defaultMaterials").GetArrayElementAtIndex(0).objectReferenceValue = material;
                asset.FindPropertyRelative("defaultTintPropertyNames").arraySize = 1;
                asset.FindPropertyRelative("defaultTintPropertyNames").GetArrayElementAtIndex(0).stringValue = "_Color";
                var materials = serialized.FindProperty("materials");
                materials.arraySize = 1;
                var binding = materials.GetArrayElementAtIndex(0);
                binding.FindPropertyRelative("assetIdentity").stringValue = "builtin:unlit-color";
                binding.FindPropertyRelative("material").objectReferenceValue = material;
                binding.FindPropertyRelative("tintPropertyName").stringValue = "_Color";
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var endpoint = owner.AddComponent<LiberaFxLiveGameEndpoint>();
                endpoint.Configure(true, root, bindings);
                owner.SetActive(true);
                session = endpoint.Session;
                Require(session != null, "Endpoint failed: " + endpoint.LastError);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 5;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31; // transient fixture renderers only
                camera.transform.position = new Vector3(2, 2, -10);
                camera.targetTexture = target;
                material.SetColor("_Color", Color.white);
                var created = Send("create", null, before);
                Require(created.result == "Ok" && !string.IsNullOrEmpty(created.target), "Spool create failed: " + created.error);
                var live = session.List().Single();
                var original = session.ResolveComponent(live.Id);
                Require(original && original.RenderStatistics.activeSubmissions == 1, "No live mesh draw submission.");
                foreach (var child in original.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                Require(original.ReadRootValue(ids.position, out var first) == NativeResult.Ok && first.X == 1, "Initial compiler output missing.");
                Require(Sample(new Vector3(1, 1, 1)).r > 0.5f, "Initial real game renderer produced no expected pixels.");
                var rejected = Send("replace", created.target, unsupported);
                Require(rejected.result == "UnsupportedBinary" && session.ResolveComponent(live.Id) == original,
                    "Unsupported migration disturbed old component.");
                var bad = Send("replace", created.target, new byte[] { 1, 2, 3 });
                Require(bad.result != "Ok" && session.ResolveComponent(live.Id) == original, "Malformed replacement disturbed old component.");
                var changed = Send("replace", created.target, after);
                Require(changed.result == "Ok", "Supported graph replacement failed: " + changed.error);
                var replacement = session.ResolveComponent(live.Id);
                Require(replacement && replacement != original && replacement.ReadRootValue(ids.position, out var second) ==
                    NativeResult.Ok && second.X == 3, "Added graph nodes did not reach actual component.");
                foreach (var child in replacement.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                Require(Sample(new Vector3(3, 3, 3)).r > 0.5f && Sample(new Vector3(1, 1, 1)).r < 0.1f,
                    "Graph replacement did not move actual rendered pixels.");
                // Multiple actual worker submissions, owner pump, queue credit and
                // safe SDK ownership. No Unity API is used inside worker lambdas.
                var workerRequests = new SessionRequestId[8];
                var workers = new List<Thread>();
                var workerFailure = 0;
                for (var index = 0; index < workerRequests.Length; ++index)
                {
                    var slot = index;
                    var thread = new Thread(() => {
                        if (session.SubmitCreate(before, "worker-" + slot, out workerRequests[slot]) != NativeResult.Ok)
                            Interlocked.Increment(ref workerFailure);
                    });
                    workers.Add(thread); thread.Start();
                }
                foreach (var worker in workers) Require(worker.Join(10000), "Worker failed to return.");
                Require(workerFailure == 0, "Worker create rejected.");
                session.Process(64);
                foreach (var request in workerRequests)
                {
                    Require(session.TakeCompletion(request, out var done) == NativeResult.Ok && done.Result == NativeResult.Ok,
                        "Worker creation failed at SDK boundary.");
                    Require(session.ResolveComponent(done.Instance).RenderStatistics.activeSubmissions == 1,
                        "Worker request produced no engine renderer.");
                    var destroy = default(SessionRequestId);
                    var worker = new Thread(() => {
                        if (session.SubmitDestroy(done.Instance, out destroy) != NativeResult.Ok) Interlocked.Increment(ref workerFailure);
                    });
                    worker.Start(); Require(worker.Join(10000), "Destroy worker stalled.");
                    session.Process();
                    Require(session.TakeCompletion(destroy, out var destroyed) == NativeResult.Ok && destroyed.Result == NativeResult.Ok &&
                        !session.ResolveComponent(done.Instance), "Worker destroy did not retire real component.");
                    Require(session.SubmitDestroy(done.Instance, out destroy) == NativeResult.Ok, "Stale validation should complete asynchronously.");
                    session.Process();
                    Require(session.TakeCompletion(destroy, out destroyed) == NativeResult.Ok &&
                        destroyed.Result == NativeResult.InvalidHandle, "Old generation targeted reused engine slot.");
                    Require(session.ClearPreviewDebugDraw(done.Instance) == NativeResult.InvalidHandle,
                        "Stale generation cleared another instance's debug.");
                }
                Require(workerFailure == 0, "Worker destroy rejected.");
                Require(Send("list", null, null).instances.Length == 1, "Live selection list leaked destroyed instances.");
                var elapsed = replacement.ElapsedTime;
                var scopes = replacement.ActiveRenderScopeCount;
                debugOwner = new string('a', 32);
                Require(Send("debug-attach", null, null).result == "Ok", "Debug lease attach failed.");
                debugOwner = new string('b', 32);
                Require(Send("debug-attach", null, null).result == "Ok", "Debug lease takeover failed.");
                debugOwner = new string('a', 32);
                Require(Send("debug-disconnect", null, null).result == "InvalidHandle" &&
                    Send("replace", created.target, after).result == "InvalidHandle", "Old window changed a new transfer.");
                debugOwner = new string('b', 32);
                Require(Send("debug-disconnect", null, null).result == "Ok", "Debug cleanup was not acknowledged.");
                Require(session.ResolveComponent(live.Id) == replacement && replacement.ElapsedTime == elapsed &&
                    replacement.ActiveRenderScopeCount == scopes &&
                    replacement.RenderStatistics.activeSubmissions == 1,
                    "Debug disconnect stopped/reset normal playback or removed ordinary renderers.");
                Require(session.Close() == NativeResult.NotReady && session.ResolveComponent(live.Id) == replacement,
                    "Graceful close silently converted lifetime into forced termination.");
                // A separate bounded session verifies queued requests and
                // unconsumed completions both retain queue credits.
                var bounded = new LiberaFxLiveGameSession(true, owner.transform, bindings, 1, 2);
                try
                {
                    var firstSubmit = bounded.SubmitCreate(before, "credit-one", out var one);
                    var secondSubmit = bounded.SubmitCreate(before, "credit-two", out var two);
                    Require(firstSubmit == NativeResult.Ok && secondSubmit == NativeResult.Ok &&
                        bounded.SubmitCreate(before, "over-capacity", out _) == NativeResult.QueueRejected, "Queue capacity not enforced.");
                    bounded.Process();
                    Require(bounded.SubmitCreate(before, "unconsumed", out _) == NativeResult.QueueRejected,
                        "Unconsumed completion did not retain queue credit.");
                    Require(bounded.TakeCompletion(one, out var firstCredit) == NativeResult.Ok && firstCredit.Result == NativeResult.Ok &&
                        bounded.TakeCompletion(two, out var secondCredit) == NativeResult.Ok && secondCredit.Result == NativeResult.QueueRejected,
                        "Actual component capacity not enforced asynchronously.");
                }
                finally { bounded.ShutdownAndForceDestroy(); }
                // A fresh session exercises teardown while work is still queued.
                var teardown = new LiberaFxLiveGameSession(true, owner.transform, bindings);
                var pending = default(SessionRequestId);
                Require(teardown.SubmitCreate(before, "queued-at-shutdown", out pending) == NativeResult.Ok, "Queued shutdown setup failed.");
                Require(teardown.ShutdownAndForceDestroy() == NativeResult.Ok &&
                    teardown.TakeCompletion(pending, out var cancelled) == NativeResult.Ok && cancelled.Result == NativeResult.NotReady &&
                    teardown.List().Length == 0, "Shutdown did not safely cancel queued work and release actual instances.");
                Debug.Log("PASS LiveGame: compiler graph -> isolated tool spool -> real component/pixels; failed replacement protects old playback; worker create/destroy/generation/shutdown.");
                return;

                LiberaFxLiveGameEndpoint.Response Send(string operation, string selected, byte[] package)
                {
                    var descriptor = JsonUtility.FromJson<LiberaFxLiveGameEndpoint.Response>(
                        File.ReadAllText(Path.Combine(endpoint.EndpointDirectory, "endpoint.json")));
                    var command = new LiberaFxLiveGameEndpoint.Command { session = descriptor.session,
                        sequence = DateTime.UtcNow.Ticks.ToString(), operation = operation, target = selected, debugOwner = debugOwner,
                        packageBase64 = package == null ? null : Convert.ToBase64String(package), label = "live fixture", policy = "restart" };
                    File.WriteAllText(Path.Combine(endpoint.EndpointDirectory, "request.json"), JsonUtility.ToJson(command));
                    endpoint.Pump(); endpoint.Pump();
                    var response = JsonUtility.FromJson<LiberaFxLiveGameEndpoint.Response>(
                        File.ReadAllText(Path.Combine(endpoint.EndpointDirectory, "response.json")));
                    Require(response.sequence == command.sequence, "Response did not correlate to submitted operation.");
                    return response;
                }
                Color Sample(Vector3 world)
                {
                    camera.Render();
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
                    var point = camera.WorldToScreenPoint(world);
                    return pixels.GetPixel(Mathf.RoundToInt(point.x), Mathf.RoundToInt(point.y));
                }
            }
            finally
            {
                session?.ShutdownAndForceDestroy();
                RenderTexture.active = previousTarget;
                Object.DestroyImmediate(owner); Object.DestroyImmediate(primitive); Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); Object.DestroyImmediate(material); Object.DestroyImmediate(bindings);
            }
        }
    }
}
