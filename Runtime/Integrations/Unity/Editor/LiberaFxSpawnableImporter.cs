using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LiberaFx.Unity;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    // One owner for both extensions: the envelope, not the suffix, selects the asset.
    [ScriptedImporter(2, new[] { "lfxb", "lfxp" })]
    public sealed class LiberaFxSpawnableImporter : ScriptedImporter
    {
        [SerializeField] private LiberaFxUnityRenderAssetBinding[] resourceOverrides = Array.Empty<LiberaFxUnityRenderAssetBinding>();
        [SerializeField] private LiberaFxUnityMaterialBinding[] materialOverrides = Array.Empty<LiberaFxUnityMaterialBinding>();

        [Serializable]
        private sealed class GuidLocalIdBinding
        {
            public string guid;
            public long localId;
        }

        [Serializable]
        private sealed class ImportRenderMaterial
        {
            public string path;
        }

        [Serializable]
        private sealed class ImportRenderNode
        {
            public string kind;
            public string assetPath;
            public string assetIdentity;
            public ImportRenderMaterial[] materials;
        }

        [Serializable]
        private sealed class ImportRenderConfig
        {
            public ImportRenderNode[] nodes;
        }

        public override void OnImportAsset(AssetImportContext context)
        {
            try
            {
                var bytes = File.ReadAllBytes(context.assetPath);
                if (HasSignature(bytes, "LFXP"))
                {
                    var asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                    asset.name = Path.GetFileNameWithoutExtension(context.assetPath);
                    asset.SetPackageBytes(bytes);
                    ResolvePackageBindings(bytes, context, out var resolvedAssets, out var resolvedMaterials);
                    asset.SetResolvedBindings(resolvedAssets, resourceOverrides, resolvedMaterials, materialOverrides);
                    // Keep the legacy local ID so scene/prefab references survive reimport.
                    context.AddObjectToAsset("package", asset);
                    context.SetMainObject(asset);
                }
                else
                {
                    ValidateBytes(bytes);
                    var asset = ScriptableObject.CreateInstance<LiberaFxProgramAsset>();
                    asset.name = Path.GetFileNameWithoutExtension(context.assetPath);
                    asset.SetProgramBytes(bytes);
                    context.AddObjectToAsset("program", asset);
                    context.SetMainObject(asset);
                }
            }
            catch (Exception error)
            {
                context.LogImportError("LIBERA FX import failed: " + error.Message);
            }
        }

        internal static void ResolvePackageBindings(byte[] bytes, AssetImportContext context,
            out LiberaFxUnityRenderAssetBinding[] resolvedAssets,
            out LiberaFxUnityMaterialBinding[] resolvedMaterials)
        {
            var assets = new List<LiberaFxUnityRenderAssetBinding>();
            var materials = new List<LiberaFxUnityMaterialBinding>();
            var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
            var seenMaterials = new HashSet<string>(StringComparer.Ordinal);
            using var host = new NativeSpawnableHost(bytes);
            if (host.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration, out var rootRender) == NativeResult.Ok)
                CollectFromRenderJson(rootRender, context, seenIdentities, seenMaterials, assets, materials);
            if (host.GetTemplateCount(out var templateCount) == NativeResult.Ok)
            {
                for (var i = 0u; i < templateCount; i++)
                {
                    if (host.GetTemplateId(i, out var templateId) == NativeResult.Ok &&
                        host.GetTemplatePayload(templateId, SpawnableTemplatePayloadKind.RenderConfiguration, out var tplRender) == NativeResult.Ok)
                        CollectFromRenderJson(tplRender, context, seenIdentities, seenMaterials, assets, materials);
                }
            }
            resolvedAssets = assets.ToArray();
            resolvedMaterials = materials.ToArray();
        }

        private static void CollectFromRenderJson(byte[] renderBytes, AssetImportContext context,
            HashSet<string> seenIdentities, HashSet<string> seenMaterials,
            List<LiberaFxUnityRenderAssetBinding> assets, List<LiberaFxUnityMaterialBinding> materials)
        {
            if (renderBytes == null || renderBytes.Length == 0) return;
            ImportRenderConfig config = null;
            try { config = JsonUtility.FromJson<ImportRenderConfig>(Encoding.UTF8.GetString(renderBytes)); }
            catch (ArgumentException) { return; }
            if (config?.nodes == null) return;
            foreach (var node in config.nodes)
            {
                if (node == null) continue;
                var identity = !string.IsNullOrEmpty(node.assetPath) ? node.assetPath : node.assetIdentity;
                if (!string.IsNullOrEmpty(identity) &&
                    !identity.StartsWith("builtin:", StringComparison.Ordinal) &&
                    seenIdentities.Add(identity))
                {
                    var obj = LoadEngineObject(identity, context, out _);
                    if (obj != null)
                    {
                        var mesh = obj as Mesh ?? (obj is GameObject go ? go.GetComponentInChildren<MeshFilter>()?.sharedMesh : null);
                        var tex = obj as Texture2D;
                        if (mesh != null || tex != null)
                        {
                            assets.Add(new LiberaFxUnityRenderAssetBinding
                            {
                                assetIdentity = identity,
                                mesh = mesh,
                                texture = tex
                            });
                        }
                    }
                }
                if (node.materials != null)
                {
                    foreach (var matEntry in node.materials)
                    {
                        if (matEntry == null || string.IsNullOrEmpty(matEntry.path) ||
                            matEntry.path.StartsWith("builtin:", StringComparison.Ordinal) ||
                            !seenMaterials.Add(matEntry.path)) continue;
                        var mat = LoadEngineObject(matEntry.path, context, out _) as Material;
                        if (mat != null)
                        {
                            materials.Add(new LiberaFxUnityMaterialBinding
                            {
                                assetIdentity = matEntry.path,
                                material = mat,
                                parameterMappings = Array.Empty<LiberaFxUnityMaterialPropertyBinding>()
                            });
                        }
                    }
                }
            }
        }

        private static UnityEngine.Object LoadEngineObject(string identity, AssetImportContext context, out string assetPath)
        {
            assetPath = null;
            if (identity.StartsWith("Assets/", StringComparison.Ordinal) ||
                identity.StartsWith("Packages/", StringComparison.Ordinal))
            {
                assetPath = identity;
            }
            else if (identity.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    var parsed = JsonUtility.FromJson<GuidLocalIdBinding>(identity);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.guid))
                        assetPath = AssetDatabase.GUIDToAssetPath(parsed.guid);
                }
                catch (ArgumentException) { }
            }
            else
            {
                var colon = identity.IndexOf(':');
                var guidPart = colon > 0 ? identity.Substring(0, colon) : identity;
                if (Guid.TryParse(guidPart, out var parsedGuid) && parsedGuid != Guid.Empty)
                    assetPath = AssetDatabase.GUIDToAssetPath(parsedGuid.ToString("N"));
            }
            if (string.IsNullOrEmpty(assetPath)) return null;
            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj != null && context != null) context.DependsOnSourceAsset(assetPath);
            return obj;
        }

        internal static void ValidateBytes(byte[] bytes)
        {
            if (HasSignature(bytes, "LFXP"))
            {
                // Use the existing native loader, not a second envelope parser.
                using (var validation = new NativeSpawnableHost(bytes)) { }
            }
            else if (HasSignature(bytes, "LFXB"))
            {
                using (var validation = new NativeProgram(bytes)) { }
            }
            else
            {
                throw new InvalidDataException("Expected an LFXP package or legacy LFXB program.");
            }
        }

        private static bool HasSignature(byte[] bytes, string signature)
        {
            return bytes != null && bytes.Length >= 4 &&
                bytes[0] == signature[0] && bytes[1] == signature[1] &&
                bytes[2] == signature[2] && bytes[3] == signature[3];
        }
    }

    internal sealed class LiberaFxSpawnablePostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (importedAssets == null || importedAssets.Length == 0) return;
            foreach (var path in importedAssets)
            {
                if (string.IsNullOrEmpty(path) ||
                    (!path.EndsWith(".lfxb", StringComparison.OrdinalIgnoreCase) &&
                     !path.EndsWith(".lfxp", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<LiberaFxSpawnableAsset>(path);
                if (asset == null || asset.PackageBytes == null || asset.PackageBytes.Length < 4)
                    continue;
                if ((asset.AutoResolvedAssets != null && asset.AutoResolvedAssets.Length > 0) ||
                    (asset.AutoResolvedMaterials != null && asset.AutoResolvedMaterials.Length > 0))
                    continue;

                LiberaFxSpawnableImporter.ResolvePackageBindings(
                    asset.PackageBytes, null, out var resolvedAssets, out var resolvedMaterials);
                if (resolvedAssets.Length > 0 || resolvedMaterials.Length > 0)
                {
                    asset.SetResolvedBindings(
                        resolvedAssets, asset.ResourceOverrides, resolvedMaterials, asset.MaterialOverrides);
                }
            }
        }
    }
}
