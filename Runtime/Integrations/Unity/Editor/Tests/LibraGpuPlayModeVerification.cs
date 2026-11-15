using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    [InitializeOnLoad]
    public static class LibraGpuPlayModeVerification
    {
        const string SessionKey = "LibraGpuPlayModeVerification.Active";
        static GameObject emitter, cameraObject;
        static LibraGpuParticleRuntime runtime;
        static ComputeShader orangeCompute, blueCompute;
        static TextAsset orangeMetadata, blueMetadata;
        static Material material;
        static RenderTexture target;
        static Texture2D image;
        static LibraGpuTextureBinding[] bindings;
        static int phase, afterFrame;
        static double deadline;

        static LibraGpuPlayModeVerification()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void Run()
        {
            File.WriteAllText("playmode-verification.txt", "STARTED\n");
            SessionState.SetBool(SessionKey, true);
            EditorApplication.isPlaying = true;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(SessionKey, false) || state != PlayModeStateChange.EnteredPlayMode) return;
            try
            {
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11, "D3D11 required");
                orangeCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Fixtures/Emitter.compute");
                orangeMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Fixtures/Emitter.json");
                blueCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Fixtures/BlueEmitter.compute");
                blueMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Fixtures/BlueEmitter.json");
                Require(orangeCompute != null && orangeMetadata != null && blueCompute != null && blueMetadata != null, "Missing generated assets");
                var shader = Shader.Find("Libra/GPU Particle Billboard");
                Require(shader != null && shader.isSupported, "Missing billboard shader");
                material = new Material(shader) { enableInstancing = true };
                bindings = new[] { new LibraGpuTextureBinding { resourceId = "billboardTexture", texture = Texture2D.whiteTexture } };
                cameraObject = new GameObject("GPU PlayMode camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.orthographic = true;
                camera.orthographicSize = 2;
                camera.transform.position = new Vector3(0, 0, -5);
                target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                image = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                Time.captureDeltaTime = 1f / 60f;
                NewEmitter(blueCompute, blueMetadata);
                deadline = EditorApplication.timeSinceStartup + 60;
                phase = 0;
                afterFrame = Time.frameCount + 8;
                EditorApplication.update += Tick;
            }
            catch (Exception e) { Fail(e); }
        }

        static void NewEmitter(ComputeShader compute, TextAsset metadata)
        {
            emitter = new GameObject("GPU PlayMode emitter");
            runtime = emitter.AddComponent<LibraGpuParticleRuntime>();
            typeof(LibraGpuParticleRuntime).GetField("spawnRate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(runtime, 1000f);
            runtime.Configure(compute, metadata, material, bindings);
            Require(!runtime.RuntimeExecuted, "Source export is not runtime execution");
            runtime.StartSimulation();
        }

        static void Tick()
        {
            try
            {
                Require(EditorApplication.timeSinceStartup < deadline, "PlayMode verification timed out in phase " + phase);
                if (Time.frameCount < afterFrame) return;
                switch (phase)
                {
                    case 0:
                        Require(runtime.IsRunning && runtime.RuntimeExecuted && runtime.DispatchCount > 0 && runtime.DrawCount > 0, "Component Update did not execute");
                        Capture(true, "playmode-blue.png");
                        string oldHash = runtime.SourceContentHash;
                        runtime.Configure(orangeCompute, orangeMetadata, material, bindings);
                        Require(!runtime.RuntimeExecuted && runtime.SourceContentHash != oldHash && LibraGpuParticleRuntime.PendingRetirements > 0,
                            "Reconfigure did not retire previous GPU buffers");
                        runtime.StartSimulation();
                        phase = 1; afterFrame = Time.frameCount + 8;
                        break;
                    case 1:
                        Require(runtime.RuntimeExecuted && LibraGpuParticleRuntime.PendingRetirements == 0, "Reconfigured buffers did not retire");
                        Capture(false, "playmode-orange.png");
                        runtime.enabled = false;
                        Require(!runtime.IsConfigured && !runtime.IsRunning && LibraGpuParticleRuntime.PendingRetirements > 0, "OnDisable did not retire buffers");
                        phase = 2; afterFrame = Time.frameCount + 5;
                        break;
                    case 2:
                        Require(LibraGpuParticleRuntime.PendingRetirements == 0, "Disabled component retirement did not complete");
                        runtime.enabled = true;
                        Require(runtime.IsConfigured && runtime.IsRunning && !runtime.RuntimeExecuted, "OnEnable did not restart configured program");
                        phase = 3; afterFrame = Time.frameCount + 6;
                        break;
                    case 3:
                        Require(runtime.RuntimeExecuted, "Reenabled component did not update");
                        UnityEngine.Object.Destroy(emitter);
                        phase = 4; afterFrame = Time.frameCount + 5;
                        break;
                    case 4:
                        Require(emitter == null && LibraGpuParticleRuntime.PendingRetirements == 0, "Destroyed component retirement did not complete");
                        NewEmitter(blueCompute, blueMetadata);
                        phase = 5; afterFrame = Time.frameCount + 6;
                        break;
                    case 5:
                        Require(runtime.RuntimeExecuted, "Replacement component did not update");
                        UnityEngine.Object.Destroy(emitter);
                        phase = 6; afterFrame = Time.frameCount + 5;
                        break;
                    case 6:
                        Require(emitter == null && LibraGpuParticleRuntime.PendingRetirements == 0, "Replacement retirement did not complete");
                        Finish();
                        break;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        static void Capture(bool blue, string file)
        {
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
            image.Apply();
            RenderTexture.active = null;
            bool found = false;
            foreach (var pixel in image.GetPixels32())
                if (blue ? pixel.b > 100 && pixel.r < 50 : pixel.r > 100 && pixel.g > 50 && pixel.b < pixel.r)
                { found = true; break; }
            Require(found, "Automatic camera did not render " + file);
            File.WriteAllBytes(file, image.EncodeToPNG());
        }

        static void Require(bool value, string message) { if (!value) throw new Exception(message); }

        static void Finish()
        {
            File.WriteAllText("playmode-verification.txt", "PASS\nDevice: " + SystemInfo.graphicsDeviceName +
                "\nAPI: " + SystemInfo.graphicsDeviceType + "\nAutomatic Update, camera framebuffer, reconfigure, disable, reenable and destroy: passed\nPending retirements: " +
                LibraGpuParticleRuntime.PendingRetirements + "\nAsync compute: " + SystemInfo.supportsAsyncCompute + "\n");
            Cleanup();
            EditorApplication.Exit(0);
        }

        static void Fail(Exception e)
        {
            File.WriteAllText("playmode-verification.txt", "FAIL\n" + e);
            Debug.LogException(e);
            Cleanup();
            EditorApplication.Exit(1);
        }

        static void Cleanup()
        {
            EditorApplication.update -= Tick;
            SessionState.EraseBool(SessionKey);
            Time.captureDeltaTime = 0;
            if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
            if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            if (image != null) UnityEngine.Object.Destroy(image);
            if (material != null) UnityEngine.Object.Destroy(material);
        }
    }
}
