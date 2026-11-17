using System;
using UnityEngine;

namespace LiberaFx.Unity
{
    [Serializable]
    public sealed class LiberaFxUnityRenderAssetBinding
    {
        public string assetIdentity;
        public Mesh mesh;
        public Texture texture;
        public Material[] defaultMaterials = Array.Empty<Material>();
        public string[] defaultTintPropertyNames = Array.Empty<string>();
    }

    [Serializable]
    public sealed class LiberaFxUnityMaterialPropertyBinding
    {
        public string authoredName;
        public string unityPropertyName;
    }

    [Serializable]
    public sealed class LiberaFxUnityMaterialBinding
    {
        public string assetIdentity;
        public Material material;
        public string tintPropertyName = "_Color";
        public LiberaFxUnityMaterialPropertyBinding[] parameterMappings =
            Array.Empty<LiberaFxUnityMaterialPropertyBinding>();
    }

    [Serializable]
    public sealed class LiberaFxUnityGpuProgramBinding
    {
        public string nodeId;
        public ComputeShader computeShader;
        public TextAsset metadata;
        public Material billboardMaterial;
        public LibraGpuTextureBinding[] textures = Array.Empty<LibraGpuTextureBinding>();
        [Min(0)] public float spawnRate = 64;
        public string rendererMaterialIdentity = "builtin:unlit-alpha";
        [Min(0.0001f)] public float sizeScale = 1;
    }

    // Engine asset identities remain opaque in the package. This project-local
    // ScriptableObject is the only place they are resolved to Unity objects.
    [CreateAssetMenu(menuName = "LiberaFx/Unity Bindings", fileName = "LiberaFxUnityBindings")]
    public sealed class LiberaFxUnityBindingAsset : ScriptableObject
    {
        [SerializeField] private LiberaFxUnityRenderAssetBinding[] renderAssets =
            Array.Empty<LiberaFxUnityRenderAssetBinding>();
        [SerializeField] private LiberaFxUnityMaterialBinding[] materials =
            Array.Empty<LiberaFxUnityMaterialBinding>();
        [SerializeField] private LiberaFxUnityGpuProgramBinding[] gpuPrograms =
            Array.Empty<LiberaFxUnityGpuProgramBinding>();
        [SerializeField] private uint rootGpuSeed = 1;

        public LiberaFxUnityRenderAssetBinding[] RenderAssets => renderAssets;
        public LiberaFxUnityMaterialBinding[] Materials => materials;
        public LiberaFxUnityGpuProgramBinding[] GpuPrograms => gpuPrograms;
        public uint RootGpuSeed => rootGpuSeed;
    }
}
