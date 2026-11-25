#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    public static class LibraFluid2DFieldConsumersVerification
    {
        public static async void Run() => EditorApplication.Exit(await Verify());
        internal static async Task<int> Verify()
        {
            var fixtures = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_FIXTURES");
            const string folder = "Assets/LibraFluidFieldsVerification";
            GameObject owner = null;
            Material material = null;
            LiberaFxUnityBindingAsset bindings = null;
            var created = false;
            var exit = 0;
            try
            {
                Require(Path.IsPathRooted(fixtures) && !Directory.Exists(folder), "Fresh absolute field fixture paths required.");
                Directory.CreateDirectory(folder); created = true;
                foreach (var mode in new[] { "generated-fields", "generated-mask" })
                {
                    var manifest = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(File.ReadAllText(Path.Combine(fixtures, mode + ".json")));
                    var particle = manifest.programs[0];
                    File.WriteAllText(folder + "/Particle.compute", particle.unityShaderSource);
                    File.WriteAllText(folder + "/Particle.json", particle.unityMetadataJson);
                    var packagePath = folder + "/" + mode + ".lfxp";
                    File.Copy(Path.Combine(fixtures, mode + ".lfxp"), packagePath);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(folder + "/Particle.compute");
                    var metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/Particle.json");
                    var package = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(packagePath);
                    Require(shader && metadata && package, "Generated field assets did not import.");
                    foreach (var message in ShaderUtil.GetComputeShaderMessages(shader))
                        Require(message.severity.ToString() != "Error", message.message);
                    material = new Material(Shader.Find("Libra/GPU Particle Billboard")) { enableInstancing = true };
                    bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                    var serialized = new SerializedObject(bindings);
                    var programs = serialized.FindProperty("gpuPrograms"); programs.arraySize = 1;
                    var binding = programs.GetArrayElementAtIndex(0);
                    binding.FindPropertyRelative("nodeId").stringValue = particle.nodeId;
                    binding.FindPropertyRelative("computeShader").objectReferenceValue = shader;
                    binding.FindPropertyRelative("metadata").objectReferenceValue = metadata;
                    binding.FindPropertyRelative("billboardMaterial").objectReferenceValue = material;
                    binding.FindPropertyRelative("spawnRate").floatValue = 64;
                    binding.FindPropertyRelative("rendererMaterialIdentity").stringValue = particle.unreal.rendererBindings[0].material;
                    binding.FindPropertyRelative("sizeScale").floatValue = (float)particle.unreal.rendererBindings[0].sizeScale;
                    var textures = binding.FindPropertyRelative("textures"); textures.arraySize = 1;
                    textures.GetArrayElementAtIndex(0).FindPropertyRelative("resourceId").stringValue = "billboardTexture";
                    textures.GetArrayElementAtIndex(0).FindPropertyRelative("texture").objectReferenceValue = Texture2D.whiteTexture;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    owner = new GameObject("Generated raw field consumer");
                    owner.SetActive(false);
                    var player = owner.AddComponent<LiberaFxSpawnableComponent>();
                    Require(player.ConfigureForSession(package, bindings, true) == NativeResult.Ok, "Field scope configuration failed.");
                    owner.SetActive(true);
                    Require(player.Play() == NativeResult.Ok, "Field scope Play failed: " + player.LastError);
                    for (var frame = 1; frame <= 120; frame++)
                    {
                        Require(player.AdvancePreview(frame / 60d) == NativeResult.Ok, "Field scope advance failed: " + player.LastError);
                        await NextFrame();
                    }
                    VerifySample(player, owner, manifest.fluidPrograms[0].nodeId, mode == "generated-mask");
                    owner.transform.SetPositionAndRotation(new Vector3(5,-2,0), Quaternion.Euler(0,0,30));
                    owner.transform.localScale = new Vector3(1.2f,.8f,1);
                    Require(player.AdvancePreview(121d / 60) == NativeResult.Ok, "Transformed field advance failed.");
                    await NextFrame();
                    VerifySample(player, owner, manifest.fluidPrograms[0].nodeId, mode == "generated-mask");
                    Require(player.Stop() == NativeResult.Ok, "Field scope Stop failed.");
                    await NextFrame();
                    Require(player.Play() == NativeResult.Ok, "Field scope replay failed.");
                    var restarted = new LibraGpuParticle[1];
                    owner.GetComponentInChildren<LibraGpuParticleRuntime>().ReadbackForDiagnostics(restarted);
                    Require(restarted[0].alive == 0, "Restart retained old particle contents.");
                    player.Stop();
                    UnityEngine.Object.DestroyImmediate(owner); owner = null;
                    UnityEngine.Object.DestroyImmediate(bindings); bindings = null;
                    UnityEngine.Object.DestroyImmediate(material); material = null;
                    Debug.Log("PASS generated field consumer: " + mode + ", same-frame numeric sample, producer translation/rotation/nonuniform scale, Stop and clean restart.");
                }
            }
            catch (Exception error) { Debug.LogException(error); exit = 1; }
            finally
            {
                RenderTexture.active = null;
                if (owner) UnityEngine.Object.DestroyImmediate(owner);
                if (material) UnityEngine.Object.DestroyImmediate(material);
                if (bindings) UnityEngine.Object.DestroyImmediate(bindings);
                if (created) AssetDatabase.DeleteAsset(folder);
            }
            return exit;
        }
        private static void VerifySample(LiberaFxSpawnableComponent player, GameObject owner, string producer, bool mask)
        {
            Require(player.TryGetFluidFieldOutput(producer, 0, out var output), "Raw field output unavailable.");
            var data = new Texture2D(output.width, output.height, TextureFormat.RGBAFloat, false, true);
            Color[] values;
            try
            {
                RenderTexture.active = output;
                data.ReadPixels(new Rect(0,0,output.width,output.height),0,0); data.Apply();
                values = data.GetPixels();
            }
            finally { RenderTexture.active = null; UnityEngine.Object.DestroyImmediate(data); }
            var state = new LibraGpuParticle[1];
            owner.GetComponentInChildren<LibraGpuParticleRuntime>().ReadbackForDiagnostics(state);
            Require(mask || state[0].alive != 0, "Velocity sampling requires a live GPU particle.");
            var position = new Vector3(state[0].positionAge.x, state[0].positionAge.y, state[0].positionAge.z);
            var local = owner.transform.worldToLocalMatrix.MultiplyPoint3x4(position);
            var u = local.x / 100 + .5f; var v = local.y / 100 + .5f;
            var sampled = Sample(values, output.width, output.height, u, v);
            if (mask) Require((state[0].alive != 0) == (sampled.z > .01f), "GPU Alive disagrees with raw density mask.");
            else
            {
                var expected = owner.transform.localToWorldMatrix.MultiplyVector(new Vector3(sampled.x, sampled.y, 0) * 100);
                var actual = new Vector3(state[0].velocityLifetime.x, state[0].velocityLifetime.y, state[0].velocityLifetime.z);
                Require((actual - expected).magnitude < .02f, "Same-frame raw velocity mismatch: " + expected + " vs " + actual);
            }
        }
        private static Vector4 Sample(Color[] data, int width, int height, float u, float v)
        {
            var x = Mathf.Clamp(u * width - .5f, 0, width - 1); var y = Mathf.Clamp(v * height - .5f, 0, height - 1);
            var x0 = Mathf.FloorToInt(x); var y0 = Mathf.FloorToInt(y);
            var x1 = Math.Min(x0 + 1, width - 1); var y1 = Math.Min(y0 + 1, height - 1);
            return Vector4.Lerp(Vector4.Lerp(data[y0 * width + x0], data[y0 * width + x1], x - x0),
                Vector4.Lerp(data[y1 * width + x0], data[y1 * width + x1], x - x0), y - y0);
        }
        private static Task NextFrame()
        {
            var completion = new TaskCompletionSource<bool>();
            EditorApplication.delayCall += () => completion.SetResult(true);
            EditorApplication.QueuePlayerLoopUpdate();
            return completion.Task;
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
