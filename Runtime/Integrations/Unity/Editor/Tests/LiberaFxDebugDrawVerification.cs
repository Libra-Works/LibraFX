using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    public static class LiberaFxDebugDrawVerification
    {
        public static void Run()
        {
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_DEBUG_DRAW_FIXTURE");
            if (string.IsNullOrEmpty(fixture) || !Path.IsPathRooted(fixture) || !File.Exists(fixture))
                throw new InvalidOperationException("Set LIBERAFX_DEBUG_DRAW_FIXTURE to isolated text-preview.lfxp.");
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            var owner = new GameObject("Isolated Debug Draw regression");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
            owner.SetActive(false);
            var component = owner.AddComponent<LiberaFxSpawnableComponent>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            TextMesh[] Labels() => Resources.FindObjectsOfTypeAll<TextMesh>().Where(label =>
                label && label.gameObject.scene == scene && label.name.StartsWith("LIBERA FX Preview Debug ", StringComparison.Ordinal)).ToArray();
            try
            {
                asset.SetPackageBytes(File.ReadAllBytes(fixture));
                typeof(LiberaFxSpawnableComponent).GetField("effect", flags).SetValue(component, asset);
                typeof(LiberaFxSpawnableComponent).GetField("playOnEnable", flags).SetValue(component, false);
                owner.SetActive(true);
                Require(component.Play() == NativeResult.Ok, component.LastError);
                var labels = Labels();
                Require(labels.Length == 1 && labels[0].text == "PRIVATE_DEBUG_ONLY_日本語",
                    "Real world TextMesh was not created from compiler preview payload.");
                Require(labels[0].transform.position == new Vector3(17, 23, 31), "World Matrix translation lost.");
                owner.transform.position = new Vector3(100, 200, 300);
                Require(labels[0].transform.position == new Vector3(17, 23, 31), "Debug text incorrectly inherits owner position.");
                component.Stop();
                Require(Labels().Length == 0, "Stop retained debug text.");
                Require(component.Play() == NativeResult.Ok && Labels().Length == 1, "Retransfer/restart accumulated draws.");
                var elapsed = component.ElapsedTime;
                var scopes = component.ActiveRenderScopeCount;
                component.ClearPreviewDebugDraw();
                Require(Labels().Length == 0 && component.ElapsedTime == elapsed &&
                    component.ActiveRenderScopeCount == scopes, "Debug-only clear stopped or reset ordinary playback.");
                Require(component.Play() == NativeResult.Ok && Labels().Length == 0,
                    "Local restart resurrected debug after disconnect without a new transfer.");
                owner.SetActive(false);
                Require(Labels().Length == 0, "Disable/disconnect owner cleanup retained debug text.");
            }
            finally
            {
                if (component) component.Stop();
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(asset);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
