using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using LiberaFx.Unity;

#if UNITY_6000_5_OR_NEWER
[global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
[InitializeOnLoad]
public static class LiberaFxParticleSpriteCameraVerification
{
    const string Key = "LiberaFxParticleSpriteCameraVerification.Active";
    static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("LIBRA_PARTICLE_SPRITE_FIXTURES") ?? throw new InvalidOperationException("Set LIBRA_PARTICLE_SPRITE_FIXTURES to a new authoring fixture directory.")) + Path.DirectorySeparatorChar;
    static string Output => Path.GetFullPath(Environment.GetEnvironmentVariable("LIBRA_PARTICLE_SPRITE_VERIFICATION_DIR") ?? throw new InvalidOperationException("Set LIBRA_PARTICLE_SPRITE_VERIFICATION_DIR to an isolated output directory."));
    static string Report => Path.Combine(Output, "camera-verification.txt");

    const string Identity = "/Game/LibraTests/ParticleSpriteAtlas.ParticleSpriteAtlas";
    [Serializable]
    class Manifest
    {
        public Program[] programs;
    }

    [Serializable]
    class Program
    {
        public string nodeId, unityShaderSource, unityMetadataJson;
    }

    static readonly string[] Names =
    {
        "cpu-sequence",
        "cpu-composition",
        "gpu-sequence",
        "gpu-composition",
        "gpu-disabled"
    };
    static LiberaFxSpawnableComponent effect;
    static Camera camera;
    static RenderTexture target;
    static Texture2D atlas;
    static int item, phase, after;
    static double elapsed, deadline;
    static string report = "";
    static int[] second;
    static int heldDraws, heldDispatches;
    static uint heldFrame;
    static uint GpuFrame => gpu.Length == 0 ? 0 : (uint)typeof(LibraGpuParticleRuntime).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(gpu[0]);

    static LibraGpuParticleRuntime[] gpu;
    static LiberaFxParticleSpriteCameraVerification()
    {
        EditorApplication.playModeStateChanged += Changed;
    }

    public static void Run()
    {
        Require(Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("LIBRA_PARTICLE_SPRITE_FIXTURES")), "Use an absolute fixture directory.");
        Require(Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("LIBRA_PARTICLE_SPRITE_VERIFICATION_DIR")), "Use an absolute output directory.");
        Require(!Application.unityVersion.StartsWith("7000.", StringComparison.Ordinal) ||
            Array.Exists(Environment.GetCommandLineArgs(), value => value == "-enable-builtin-rp-fallback"),
            "Unity 7 alpha needs -enable-builtin-rp-fallback for these sample shaders.");
        Directory.CreateDirectory(Output);
        Directory.CreateDirectory("Assets/ParticleSprite");
        File.Copy(Root + "atlas.png", "Assets/ParticleSprite/atlas.png", true);
        foreach (var name in Names)
        {
            if (!name.StartsWith("gpu"))
            {
                continue;
            }

            var program = JsonUtility.FromJson<Manifest>(File.ReadAllText(Root + name + ".json")).programs[0];
            File.WriteAllText("Assets/ParticleSprite/" + name + ".compute", program.unityShaderSource);
            File.WriteAllText("Assets/ParticleSprite/" + name + ".json", program.unityMetadataJson);
        }

        AssetDatabase.Refresh();
        var importer = (TextureImporter)AssetImporter.GetAtPath("Assets/ParticleSprite/atlas.png");
        importer.textureType = TextureImporterType.Default;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.filterMode = FilterMode.Point;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        SessionState.SetBool(Key, true);
        EditorApplication.isPlaying = true;
    }

    static void Require(bool ok, string message)
    {
        if (!ok)
        {
            throw new Exception(message);
        }
    }

    static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    static void Changed(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Key, false) || state != PlayModeStateChange.EnteredPlayMode)
        {
            return;
        }

        try
        {
            Time.captureDeltaTime = 1f / 60;
            var pipeline = string.Equals(Environment.GetEnvironmentVariable("LIBRA_PARTICLE_SPRITE_PIPELINE"),
                "builtin", StringComparison.OrdinalIgnoreCase) ? null :
                ScriptableObject.CreateInstance<LiberaFxParticleSpriteCameraPipeline>();
            UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            camera = new GameObject("Sprite camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.orthographic = true;
            camera.orthographicSize = 1.5f;
            camera.transform.position = new Vector3(0, 0, -10);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.overrideSceneCullingMask = UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(camera.gameObject.scene);
            target = new RenderTexture(512, 512, 24);
            target.Create();
            camera.targetTexture = target;
            atlas = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ParticleSprite/atlas.png");
            EditorApplication.update += Tick;
            deadline = EditorApplication.timeSinceStartup + 90;
            item = 0;
            Setup();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    static void Setup()
    {
        var name = Names[item];
        var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
        Set(bindings, "renderAssets", new[] { new LiberaFxUnityRenderAssetBinding { assetIdentity = Identity, texture = atlas } });
        if (name.StartsWith("gpu"))
        {
            var p = JsonUtility.FromJson<Manifest>(File.ReadAllText(Root + name + ".json")).programs[0];
            var material = new Material(Shader.Find("Libra/GPU Particle Billboard"))
            {
                enableInstancing = true
            };
            Set(bindings, "gpuPrograms", new[] { new LiberaFxUnityGpuProgramBinding { nodeId = p.nodeId, computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/ParticleSprite/" + name + ".compute"), metadata = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/ParticleSprite/" + name + ".json"), billboardMaterial = material, spawnRate = 1, sizeScale = name == "gpu-disabled" ? 2 : 1, textures = new[] { new LibraGpuTextureBinding { resourceId = "billboardTexture", texture = atlas } } } });
        }

        var asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
        asset.SetPackageBytes(File.ReadAllBytes(Root + name + ".lfxb"));
        var owner = new GameObject(name);
        owner.SetActive(false);
        effect = owner.AddComponent<LiberaFxSpawnableComponent>();
        Require(effect.ConfigureForSession(asset, bindings, true) == NativeResult.Ok, "Configure " + name);
        owner.SetActive(true);
        Require(effect.Play() == NativeResult.Ok, "Play " + name);
        gpu = owner.GetComponentsInChildren<LibraGpuParticleRuntime>();
        elapsed = 0;
        Advance(name.StartsWith("gpu") ? 61 : 1);
        phase = 0;
        after = Time.frameCount + 2;
    }

    static void Advance(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            elapsed += 1.0 / 60;
            Require(effect.AdvancePreview(elapsed) == NativeResult.Ok, "Advance " + Names[item]);
        }
    }

    static int[] Pixels()
    {
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var image = new Texture2D(512, 512, TextureFormat.RGBA32, false);
        image.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
        image.Apply();
        RenderTexture.active = previous;
        var counts = new int[3];
        foreach (var c in image.GetPixels32())
        {
            if (c.r > 38 && c.g < c.r * .3 && c.b < c.r * .3)
            {
                counts[0]++;
            }

            if (c.b > 38 && c.r < c.b * .3 && c.g < c.b * .3)
            {
                counts[1]++;
            }

            if (c.r > 38 && c.g > 38 && c.b < Math.Min(c.r, c.g) * .3)
            {
                counts[2]++;
            }
        }

        Debug.Log(Names[item] + " phase=" + phase + " pixels=" + string.Join(",", counts));
        File.WriteAllBytes(Path.Combine(Output, Names[item] + "-" + phase + ".png"), image.EncodeToPNG());
        UnityEngine.Object.Destroy(image);
        return counts;
    }

    static void Tick()
    {
        try
        {
            Require(EditorApplication.timeSinceStartup < deadline, "Timeout");
            if (Time.frameCount >= after)
            {
                var pixels = Pixels();
                if (phase == 0)
                {
                    Require(pixels[0] > 20, "Initial red cell " + Names[item]);
                    if (Names[item].EndsWith("composition"))
                    {
                        Require(pixels[2] > 20, "Gold part");
                    }

                    heldDraws = gpu.Length == 0 ? 0 : gpu[0].DrawCount;
                    heldDispatches = gpu.Length == 0 ? 0 : gpu[0].DispatchCount;
                    heldFrame = GpuFrame;
                    camera.cullingMask = 0;
                    phase = 10;
                }
                else if (phase == 10)
                {
                    Require(pixels[0] + pixels[1] + pixels[2] == 0, "Camera layer filtering left pixels");
                    Require(gpu.Length == 0 || gpu[0].DrawCount == heldDraws, "Excluded camera submitted GPU draw");
                    camera.cullingMask = -1;
                    phase = 11;
                }
                else if (phase == 11)
                {
                    Require(pixels[0] > 20, "Retained frame did not redraw");
                    Require((gpu.Length == 0 || gpu[0].DispatchCount == heldDispatches) && GpuFrame == heldFrame, "Camera rendering dispatched compute");
                    Advance(30);
                    phase = 1;
                }
                else if (phase == 1)
                {
                    Require(pixels[1] > 20, "Blue frame " + Names[item]);
                    second = pixels;
                    heldDispatches = gpu.Length == 0 ? 0 : gpu[0].DispatchCount;
                    heldFrame = GpuFrame;
                    phase = 2;
                }
                else if (phase == 2)
                {
                    Require(string.Join(",", pixels) == string.Join(",", second), "Zero delta changed frame");
                    Require((gpu.Length == 0 || gpu[0].DispatchCount == heldDispatches) && GpuFrame == heldFrame, "Held camera frame advanced compute");
                    Advance(16);
                    phase = 3;
                }
                else if (phase == 3)
                {
                    Require(pixels[Names[item].EndsWith("sequence") || Names[item] == "gpu-disabled" ? 0 : 2] > 20, "Final frame");
                    effect.Stop();
                    gpu = Array.Empty<LibraGpuParticleRuntime>();
                    phase = 4;
                }
                else if (phase == 4)
                {
                    Require(pixels[0] + pixels[1] + pixels[2] == 0, "Stop did not clear");
                    Require(effect.Play() == NativeResult.Ok, "Replay");
                    elapsed = 0;
                    Advance(Names[item].StartsWith("gpu") ? 61 : 1);
                    gpu = effect.GetComponentsInChildren<LibraGpuParticleRuntime>();
                    phase = 5;
                }
                else
                {
                    Require(pixels[0] > 20, "Replay clock");
                    effect.Stop();
                    UnityEngine.Object.Destroy(effect.gameObject);
                    report += Names[item] + ": frames, composition, zero delta, Stop and replay PASS\n";
                    if (++item == Names.Length)
                    {
                        File.WriteAllText(Report, "PASS Unity " + Application.unityVersion + "; " +
                            SystemInfo.graphicsDeviceType + "; " + SystemInfo.graphicsDeviceName + "; " +
                            (GraphicsSettings.currentRenderPipeline == null ? "Built-In" : "isolated SRP") +
                            " ordinary camera submission; no test-side GPU draws or compute; manual preview\n" + report);
                        EditorApplication.update -= Tick;
                        SessionState.EraseBool(Key);
                        EditorApplication.Exit(0);
                        return;
                    }

                    Setup();
                }

                after = Time.frameCount + 2;
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    static void Fail(Exception error)
    {
        File.WriteAllText(Report, "FAIL\n" + error);
        Debug.LogException(error);
        EditorApplication.update -= Tick;
        SessionState.EraseBool(Key);
        EditorApplication.Exit(1);
    }
}

public sealed class LiberaFxParticleSpriteCameraPipeline : RenderPipelineAsset
{
    public override Type pipelineType => typeof(Pipeline);
    public override string renderPipelineShaderTag => "LiberaFxCameraVerification";
    protected override RenderPipeline CreatePipeline() => new Pipeline();
    private sealed class Pipeline : RenderPipeline
    {
        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            foreach (var camera in cameras)
            {
                BeginCameraRendering(context, camera);
                if (camera.TryGetCullingParameters(out var parameters))
                {
                    var culled = context.Cull(ref parameters);
                    context.SetupCameraProperties(camera);
                    var buffer = new CommandBuffer
                    {
                        name = "Isolated shared effect verification"
                    };
                    if (camera.targetTexture)
                    {
                        buffer.SetRenderTarget(camera.targetTexture);
                    }

                    buffer.ClearRenderTarget(true, true, camera.backgroundColor);
                    context.ExecuteCommandBuffer(buffer);
                    buffer.Release();
                    var drawing = new DrawingSettings(new ShaderTagId("SRPDefaultUnlit"), new SortingSettings(camera) { criteria = SortingCriteria.CommonTransparent });
                    drawing.enableInstancing = true;
                    var filtering = new FilteringSettings(RenderQueueRange.all, camera.cullingMask);
                    context.DrawRenderers(culled, ref drawing, ref filtering);
                    context.Submit();
                }

                EndCameraRendering(context, camera);
            }
        }
    }
}
