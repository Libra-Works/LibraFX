using System;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    internal static class LiberaFxSampleMenu
    {
        private const string Folder = "Assets/LiberaFxSampleAssets";
        [MenuItem("GameObject/LiberaFx/Native cube sample", false, 10)]
        private static void Cube()
        {
            var material = SampleMaterial();
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.name = "LiberaFx native cube";
            target.SetActive(false);
            UnityEngine.Object.DestroyImmediate(target.GetComponent<Collider>());
            target.AddComponent<LiberaFxRenderer>().SetupCubeSample(target.GetComponent<MeshFilter>().sharedMesh, material);
            Complete(target);
        }
        [MenuItem("GameObject/LiberaFx/Native animated atlas sample", false, 11)]
        private static void Atlas() => Sprite(false);
        [MenuItem("GameObject/LiberaFx/Native dynamic vertex buffer sample", false, 12)]
        private static void Dynamic() => Sprite(true);

        private static void Sprite(bool dynamic)
        {
            var material = SampleMaterial();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/Atlas.asset");
            if (!texture)
            {
                texture = new Texture2D(64, 64) { name = "LiberaFx 4x4 atlas", filterMode = FilterMode.Point };
                var pixels = new Color[64 * 64];
                for (var y = 0; y < 64; ++y)
                    for (var x = 0; x < 64; ++x)
                    {
                        var frame = (3 - y / 16) * 4 + x / 16;
                        pixels[y * 64 + x] = Color.HSVToRGB(frame / 16f, 0.8f, 1);
                    }
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                AssetDatabase.CreateAsset(texture, Folder + "/Atlas.asset");
                AssetDatabase.SaveAssets();
            }
            var target = new GameObject(dynamic ? "LiberaFx native dynamic quad" : "LiberaFx native atlas");
            target.SetActive(false);
            target.AddComponent<LiberaFxRenderer>().SetupAtlasSample(texture, material, 4, 4, dynamic);
            Complete(target);
        }
        private static Material SampleMaterial()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "LiberaFxSampleAssets");
            var material = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Atlas.mat");
            if (material) return material;
            var shader = Shader.Find("LiberaFx/Atlas Unlit");
            if (!shader) throw new InvalidOperationException("Import LiberaFxAtlas.shader before creating a sample.");
            material = new Material(shader) { name = "LiberaFx shared atlas material" };
            AssetDatabase.CreateAsset(material, Folder + "/Atlas.mat");
            AssetDatabase.SaveAssets();
            return material;
        }
        private static void Complete(GameObject target)
        {
            Undo.RegisterCreatedObjectUndo(target, "Create LiberaFx sample");
            target.SetActive(true);
            Selection.activeGameObject = target;
            SceneView.lastActiveSceneView?.FrameSelected();
        }
    }
}
