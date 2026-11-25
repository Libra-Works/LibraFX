using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    // Explicit entrypoint: dedicated project + an existing compiler-produced package.
    public static class LiberaFxUnifiedImportVerification
    {
        public static void RunBatch()
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            Require(string.Equals(project, Environment.GetEnvironmentVariable("LIBERAFX_VERIFICATION_PROJECT"),
                StringComparison.OrdinalIgnoreCase), "Set LIBERAFX_VERIFICATION_PROJECT to this dedicated project's absolute path.");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Do not run during Play Mode.");
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_SPAWNABLE_FIXTURE");
            Require(!string.IsNullOrEmpty(fixture) && File.Exists(fixture),
                "Supply an existing compiler-produced LFXP package in LIBERAFX_UNITY_SPAWNABLE_FIXTURE.");
            var bytes = File.ReadAllBytes(fixture);
            Require(bytes.Length >= 4 && bytes[0] == 'L' && bytes[1] == 'F' && bytes[2] == 'X' && bytes[3] == 'P',
                "Fixture must be a package, regardless of its extension.");
            var folder = "Assets/LiberaFxUnifiedImport_" + Guid.NewGuid().ToString("N");
            Require(!Directory.Exists(folder), "Refusing to overwrite an existing folder.");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            GameObject target = null;
            try
            {
                var canonicalPath = folder + "/Effect.lfxp";
                var secondPath = folder + "/Copy.lfxp";
                foreach (var path in new[] { canonicalPath, secondPath })
                {
                    File.WriteAllBytes(path, bytes);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var imported = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(path);
                    Require(imported && imported.PackageBytes.SequenceEqual(bytes),
                        "Each .lfxp import must preserve the complete package in the existing asset type.");
                }

                var effect = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(canonicalPath);
                Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(effect, out string guid, out long localId),
                    "Imported package must have a stable asset identity.");
                var bindings = ScriptableObject.CreateInstance<LiberaFxUnityBindingAsset>();
                var texture = new Texture2D(1, 1);
                AssetDatabase.CreateAsset(texture, folder + "/Texture.asset");
                AssetDatabase.CreateAsset(bindings, folder + "/Bindings.asset");
                var serializedBindings = new SerializedObject(bindings);
                var renderAssets = serializedBindings.FindProperty("renderAssets");
                renderAssets.arraySize = 1;
                var entry = renderAssets.GetArrayElementAtIndex(0);
                entry.FindPropertyRelative("assetIdentity").stringValue = "verification:reference-only";
                entry.FindPropertyRelative("texture").objectReferenceValue = texture;
                serializedBindings.ApplyModifiedPropertiesWithoutUndo();
                target = new GameObject("LIBERA FX import reference verification");
                target.SetActive(false);
                var component = target.AddComponent<LiberaFxSpawnableComponent>();
                Require(component.ConfigureForSession(effect, bindings) == NativeResult.Ok,
                    "Existing package component must accept the imported asset.");
                var prefabPath = folder + "/References.prefab";
                PrefabUtility.SaveAsPrefabAsset(target, prefabPath);
                AssetDatabase.SaveAssets();

                AssetDatabase.ImportAsset(canonicalPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var reimported = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(canonicalPath);
                Require(reimported && reimported.PackageBytes.SequenceEqual(bytes) &&
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reimported, out string newGuid, out long newLocalId) &&
                    newGuid == guid && newLocalId == localId, "Reimport must preserve the package local ID and bytes.");
                var saved = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var savedComponent = new SerializedObject(saved.GetComponent<LiberaFxSpawnableComponent>());
                Require(savedComponent.FindProperty("effect").objectReferenceValue == reimported &&
                    savedComponent.FindProperty("engineBindings").objectReferenceValue == bindings &&
                    bindings.RenderAssets[0].texture == texture,
                    "Package reimport must preserve prefab, binding asset and resource references.");

                RequireRejected(new byte[] { (byte)'L', (byte)'F', (byte)'X', (byte)'B' });
                RequireRejected(new byte[] { 0, 0, 0, 0 });
                Debug.Log("PASS: .lfxp-only package import, reimport references and raw-input rejection.");
            }
            finally
            {
                if (target)
                {
                    UnityEngine.Object.DestroyImmediate(target);
                }
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static void RequireRejected(byte[] bytes)
        {
            try
            {
                LiberaFxSpawnableImporter.ValidateBytes(bytes);
            }
            catch (InvalidDataException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            throw new InvalidOperationException("Corrupted input passed the import validator.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
