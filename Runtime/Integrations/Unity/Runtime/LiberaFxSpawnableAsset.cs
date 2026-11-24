using System;
using UnityEngine;

namespace LiberaFx.Unity
{
    [CreateAssetMenu(menuName = "LiberaFx/Spawnable Package", fileName = "SpawnableEffect")]
    public sealed class LiberaFxSpawnableAsset : ScriptableObject
    {
        [SerializeField] private byte[] packageBytes;
        [SerializeField] private LiberaFxUnityRenderAssetBinding[] autoResolvedAssets = Array.Empty<LiberaFxUnityRenderAssetBinding>();
        [SerializeField] private LiberaFxUnityRenderAssetBinding[] resourceOverrides = Array.Empty<LiberaFxUnityRenderAssetBinding>();
        [SerializeField] private LiberaFxUnityMaterialBinding[] autoResolvedMaterials = Array.Empty<LiberaFxUnityMaterialBinding>();
        [SerializeField] private LiberaFxUnityMaterialBinding[] materialOverrides = Array.Empty<LiberaFxUnityMaterialBinding>();

        public byte[] PackageBytes => packageBytes;
        public LiberaFxUnityRenderAssetBinding[] AutoResolvedAssets => autoResolvedAssets;
        public LiberaFxUnityRenderAssetBinding[] ResourceOverrides => resourceOverrides;
        public LiberaFxUnityMaterialBinding[] AutoResolvedMaterials => autoResolvedMaterials;
        public LiberaFxUnityMaterialBinding[] MaterialOverrides => materialOverrides;

        public void SetPackageBytes(byte[] bytes)
        {
            packageBytes = bytes;
        }

        public void SetResolvedBindings(
            LiberaFxUnityRenderAssetBinding[] resolvedAssets,
            LiberaFxUnityRenderAssetBinding[] overrides,
            LiberaFxUnityMaterialBinding[] resolvedMaterials,
            LiberaFxUnityMaterialBinding[] overrideMaterials)
        {
            autoResolvedAssets = resolvedAssets ?? Array.Empty<LiberaFxUnityRenderAssetBinding>();
            resourceOverrides = overrides ?? Array.Empty<LiberaFxUnityRenderAssetBinding>();
            autoResolvedMaterials = resolvedMaterials ?? Array.Empty<LiberaFxUnityMaterialBinding>();
            materialOverrides = overrideMaterials ?? Array.Empty<LiberaFxUnityMaterialBinding>();
        }
    }
}
