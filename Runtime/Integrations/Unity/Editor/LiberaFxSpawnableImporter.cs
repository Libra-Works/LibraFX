using System.IO;
using LiberaFx.Unity;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    [ScriptedImporter(1, "lfxp")]
    public sealed class LiberaFxSpawnableImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
            asset.name = Path.GetFileNameWithoutExtension(context.assetPath);
            asset.SetPackageBytes(File.ReadAllBytes(context.assetPath));
            context.AddObjectToAsset("package", asset);
            context.SetMainObject(asset);
        }
    }
}
