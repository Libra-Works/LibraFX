using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity.Editor
{
    public static class LibraProceduralVerification
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        public static async void Run()
        {
            var exit = 0;
            GameObject owner = null, cameraObject = null;
            RenderTexture target = null; Texture2D image = null;
            try
            {
                var fixtures = Environment.GetEnvironmentVariable("LIBERAFX_PROCEDURAL_FIXTURES");
                var output = Environment.GetEnvironmentVariable("LIBERAFX_PROCEDURAL_OUTPUT");
                Require(Path.IsPathRooted(fixtures) && Path.IsPathRooted(output), "Absolute fixture/output paths required.");
                Directory.CreateDirectory(output);
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11, "Expected a real D3D11 graphics device.");
                var compute = Resources.Load<ComputeShader>("LibraProcedural");
                Require(compute && ShaderUtil.GetComputeShaderMessages(compute).All(m => m.severity.ToString() != "Error"), "Procedural compute failed.");
                var shader = Resources.Load<Shader>("LibraProcedural");
                Require(shader && !ShaderUtil.ShaderHasError(shader), "Procedural draw shader failed.");
                LiberaFxProceduralCacheVerification.Run();
                Directory.CreateDirectory("Assets/ProceduralFixtures");
                foreach (var name in new[] { "Slash", "Shockwave", "Beam" })
                    File.Copy(Path.Combine(fixtures, name + ".lfxp"), "Assets/ProceduralFixtures/" + name + ".lfxp", true);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                cameraObject = new GameObject("Procedural verification camera") { tag = "MainCamera" };
                var camera = cameraObject.AddComponent<Camera>(); camera.transform.position = new Vector3(4, 3, 6);
                camera.transform.LookAt(new Vector3(0, .8f, 0)); camera.orthographic = true; camera.orthographicSize = 2.5f;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 8; camera.nearClipPlane = .01f; camera.farClipPlane = 100;
                target = new RenderTexture(320, 240, 24, RenderTextureFormat.ARGB32); target.Create(); camera.targetTexture = target;
                image = new Texture2D(320, 240, TextureFormat.RGBA32, false);
                foreach (var name in new[] { "Slash", "Shockwave", "Beam" })
                {
                    var package = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>("Assets/ProceduralFixtures/" + name + ".lfxp");
                    Require(package, "Package importer did not produce a compiled asset.");
                    owner = new GameObject("Procedural " + name) { layer = 8 }; owner.SetActive(false);
                    var player = owner.AddComponent<LiberaFxSpawnableComponent>();
                    Require(player.ConfigureForSession(package, null, true) == NativeResult.Ok, "Configure failed.");
                    owner.SetActive(true);
                    Require(player.Play() == NativeResult.Ok, "Play: " + player.LastError);
                    Require(player.AdvancePreview(.1) == NativeResult.Ok, "Advance: " + player.LastError);
                    await NextFrame();
                    var first = Capture(camera, target, image);
                    Require(first.Count(c => c.r > 2 || c.g > 2 || c.b > 2) > 20, name + " produced no visible pixels.");
                    File.WriteAllBytes(Path.Combine(output, name + "-first.png"), image.EncodeToPNG());
                    Require(player.AdvancePreview(.6) == NativeResult.Ok, "Animation: " + player.LastError);
                    await NextFrame();
                    var second = Capture(camera, target, image);
                    Require(first.Where((c, i) => !c.Equals(second[i])).Any(), name + " did not animate.");
                    File.WriteAllBytes(Path.Combine(output, name + "-animated.png"), image.EncodeToPNG());
                    Require(player.Stop() == NativeResult.Ok, "Stop failed."); await NextFrame();
                    Require(Capture(camera, target, image).All(c => c.r <= 2 && c.g <= 2 && c.b <= 2), "Stopped producer left visible draw pixels.");
                    UnityEngine.Object.DestroyImmediate(owner); owner = null;
                }
                File.WriteAllText(Path.Combine(output, "proof.txt"), "PASS: compiler-produced Slash/Shockwave/Beam packages; D3D11 compute, Model Draw, animated frames and stop disappearance.\n");
                Debug.Log("LIBERAFX PROCEDURAL VERIFICATION PASS");
            }
            catch (Exception error) { Debug.LogException(error); exit = 1; }
            finally
            {
                RenderTexture.active = null;
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (image) UnityEngine.Object.DestroyImmediate(image);
                EditorApplication.Exit(exit);
            }
        }
        private static Task NextFrame()
        {
            var completion = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () => completion.SetResult(true);
            return completion.Task;
        }
        private static Color32[] Capture(Camera camera, RenderTexture target, Texture2D image)
        {
            camera.Render(); RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            RenderTexture.active = null; return image.GetPixels32();
        }
    }
}
