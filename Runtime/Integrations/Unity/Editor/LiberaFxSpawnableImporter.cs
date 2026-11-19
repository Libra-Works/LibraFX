using System;
using System.IO;
using LiberaFx.Unity;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace LiberaFx.Unity.Editor
{
    // One owner for both extensions: the envelope, not the suffix, selects the asset.
    [ScriptedImporter(2, new[] { "lfxb", "lfxp" })]
    public sealed class LiberaFxSpawnableImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            try
            {
                var bytes = File.ReadAllBytes(context.assetPath);
                ValidateBytes(bytes);
                if (HasSignature(bytes, "LFXP"))
                {
                    var asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                    asset.name = Path.GetFileNameWithoutExtension(context.assetPath);
                    asset.SetPackageBytes(bytes);
                    // Keep the legacy local ID so scene/prefab references survive reimport.
                    context.AddObjectToAsset("package", asset);
                    context.SetMainObject(asset);
                }
                else
                {
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
}
