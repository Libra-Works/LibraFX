using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    [Serializable] internal sealed class LiberaFxRenderPayload
    {
        public string type;
        public long programSequence;
        public LiberaFxRenderNodePayload[] nodes;
        public LiberaFxCpuParticlePayload[] cpuParticles;
        public int cpuParticleVersion;
        public LiberaFxGpuDrawTransformPayload[] gpuDrawTransforms;
        public LiberaFxGpuDrawTransformPayload[] gpuEmitterTransforms;
        public int proceduralVersion;
        public LiberaFxProceduralResourcePayload[] proceduralResources;
    }

    [Serializable] internal sealed class LiberaFxCpuParticlePayload
    {
        public ParticleSpriteAnimation spriteAnimation;
        public bool __liberafxHasSpriteAnimation;
        public ParticleAtlas flipbook;
        public string[] drawMatrixRowNodeIds;
        public string nodeId;
        public string assetPath;
        public string blendMode;
        public double drawScale;
        public bool billboard;
    }

    [Serializable] internal sealed class LiberaFxGpuDrawTransformPayload
    {
        public string nodeId;
        public string[] rowNodeIds;
        [NonSerialized] public byte[][] rowNodeBytes;
    }

    [Serializable] internal sealed class LiberaFxRenderNodePayload
    {
        public string nodeId;
        public string kind;
        public string proceduralModel, proceduralTexture, proceduralMaterial;
        public LiberaFxFlexiblePlanePayload flexiblePlane;
        public string assetPath;
        public LiberaFxRenderMaterialPayload[] materials;
        public LiberaFxRenderInputPayload[] inputs;
        public LiberaFxRenderTexturePayload[] textures;
        public LiberaFxGeneratedRenderTexturePayload[] generatedTextures;
        public LiberaFxRenderSpriteAnimationPayload spriteAnimation;
        public bool __liberafxHasSpriteAnimation;
        public bool billboard;
        public string rotationConvention;
        public bool previewOnly;
        public int debugVersion;
        public string text;
        public double size;
    }

    [Serializable] internal sealed class LiberaFxGeneratedRenderTexturePayload
    {
        public string nodeId, producerNodeId, outputId, name;
        public int slot, association, index;
        public bool mainTexture;
    }

    [Serializable] internal sealed class LiberaFxRenderSpriteAnimationPayload
    {
        public int version;
        public int textureWidth;
        public int textureHeight;
        public double fps;
        public int startFrame;
        public int endFrame;
        public bool loop;
        public LiberaFxRenderSpriteCellPayload[] cells;
        public LiberaFxRenderSpriteFramePayload[] frames;
        public double durationFrames;
        public int drawOrder;
        public LiberaFxRenderSpriteKeyPayload[] keys;
    }

    [Serializable] internal sealed class LiberaFxRenderSpriteKeyPayload
    {
        public string id;
        public double frame, x, y, rotationDegrees, scaleX, scaleY;
    }

    [Serializable] internal sealed class LiberaFxRenderSpriteCellPayload
    {
        public string id;
        public string name;
        public int x;
        public int y;
        public int width;
        public int height;
        public double u;
        public double v;
        public double uvWidth;
        public double uvHeight;
    }

    [Serializable] internal sealed class LiberaFxRenderSpriteFramePayload
    {
        public string id;
        public string cellId;
        public int cellIndex;
        public double durationFrames;
    }

    [Serializable] internal sealed class LiberaFxRenderMaterialPayload
    {
        public int slot;
        public string path;
    }

    [Serializable] internal sealed class LiberaFxRenderInputPayload
    {
        public string id;
        public int width;
        public string sourceNodeId;
        [NonSerialized] public byte[] sourceNodeBytes;
        public int sourceComponent;
        public double[] defaults;
        public LiberaFxMaterialParameterPayload materialParameter;
    }

    [Serializable] internal sealed class LiberaFxMaterialParameterPayload
    {
        public string name;
        public string kind;
        public int slot;
        public int association;
        public int index;
    }

    [Serializable] internal sealed class LiberaFxRenderTexturePayload
    {
        public string name;
        public int slot;
        public string path;
        public int association;
        public int index;
    }

    [Serializable] internal sealed class LiberaFxGpuManifestPayload
    {
        public int version;
        public LiberaFxGpuManifestProgram[] programs;
        public LiberaFxFluidManifestProgram[] fluidPrograms;
    }

    [Serializable] internal sealed class LiberaFxFluidManifestProgram
    {
        public string nodeId;
        public LibraFluid2DSettings settings;
        public LiberaFxFluidRenderer renderer;
        public bool automaticPlane = true;
    }

    [Serializable] internal sealed class LiberaFxFluidRenderer
    {
        public string material;
        public float sizeScale;
    }

    [Serializable] internal sealed class LiberaFxGpuManifestProgram
    {
        public string nodeId;
        public LiberaFxGpuUnrealProgram unreal;
        public string unityMetadataJson;
        public string unityShaderSource;
        [NonSerialized] internal LiberaFxGeneratedGpuResource[] generatedResources = Array.Empty<LiberaFxGeneratedGpuResource>();
    }

    [Serializable] internal sealed class LiberaFxGpuUnrealProgram
    {
        public int rendererBindingsVersion;
        public string canonicalJson;
        public LiberaFxGpuUnrealMetadata metadata;
        public LiberaFxGpuRendererBinding[] rendererBindings;
    }

    [Serializable] internal sealed class LiberaFxGpuUnrealMetadata
    {
        public string programId;
        public string contentHash;
    }

    [Serializable] internal sealed class LiberaFxGpuRendererBinding
    {
        public ParticleSpriteAnimation spriteAnimation;
        public bool __liberafxHasSpriteAnimation;
        public string flowId;
        public string renderer;
        public string material;
        public double sizeScale;
    }

    [Serializable] internal sealed class LiberaFxGpuContainerSnapshot
    {
        public string id;
    }

    [Serializable] internal sealed class LiberaFxGpuContainerSnapshotList
    {
        public LiberaFxGpuContainerSnapshot[] items;
    }

    internal sealed class LiberaFxPreparedRenderNode
    {
        public LiberaFxRenderNodePayload proceduralDraw;
        public LiberaFxPreparedFlexiblePlane flexiblePlane;
        public LiberaFxRenderNodePayload debugDraw;
        public string nodeId;
        public bool isSprite;
        public bool frameConnected;
        public bool billboard;
        public Camera billboardCamera;
        public bool worldRotation;
        public Mesh mesh;
        public Texture texture;
        public Material[] materials;
        public string[] tintPropertyNames;
        public string[] materialParameterPropertyNames;
        public LiberaFxRenderInputPayload[] inputs;
        public LiberaFxPreparedTextureParameter[] textures;
        public LiberaFxRenderSpriteAnimationPayload spriteAnimation;
        public double[] spriteKeys;
        public double[] spriteDurations;
        public LiberaFxGeneratedRenderTexturePayload[] generatedTextures = Array.Empty<LiberaFxGeneratedRenderTexturePayload>();
    }

    internal sealed class LiberaFxPreparedTextureParameter
    {
        public string name;
        public int slot;
        public Texture texture;
    }

    internal sealed class LiberaFxPreparedGpuProgram
    {
        public ParticleSpriteAnimation spriteAnimation;
        public float spawnRate;
        public string nodeId;
        public LiberaFxUnityGpuProgramBinding binding;
        public LibraGpuMetadata packageMetadata;
        public float sizeScale;
        public byte[][] drawMatrixRowNodeBytes;
        public byte[][] emitterMatrixRowNodeBytes;
        internal LiberaFxGeneratedGpuResource[] generatedResources = Array.Empty<LiberaFxGeneratedGpuResource>();
    }

    internal sealed class LiberaFxPreparedFluidProgram
    {
        public string nodeId;
        public LibraFluid2DSettings settings;
        public float sizeScale;
        public byte[][] drawMatrixRowNodeBytes;
        internal bool automaticPlane = true;
    }

    internal sealed class LiberaFxPreparedScope
    {
        public LiberaFxProceduralResourcePayload[] proceduralResources;
        public string templateId;
        public LiberaFxPreparedRenderNode[] renderNodes;
        public LiberaFxPreparedCpuParticleRenderer[] cpuParticles;
        public LiberaFxPreparedGpuProgram[] gpuPrograms;
        public LiberaFxPreparedFluidProgram[] fluidPrograms;
    }

    internal sealed class LiberaFxPreparedCpuParticleRenderer
    {
        public ParticleSpriteAnimation spriteAnimation;
        public ParticleAtlas flipbook;
        public byte[][] drawRows;
        public string nodeId;
        public byte[] nodeIdBytes;
        public Texture texture;
        public bool additive;
        public float drawScale;
        public Camera billboardCamera;
    }

    internal sealed class LiberaFxUnityBindingLookup
    {
        private const string NullMaterialParameterSentinel = "__LIBERAFX_NULL_MATERIAL_PARAMETER__";
        private readonly Dictionary<string, LiberaFxUnityRenderAssetBinding> renderAssets;
        private readonly Dictionary<string, LiberaFxUnityMaterialBinding> materials;
        private readonly Dictionary<string, Dictionary<string, string>> materialPropertyMappings;
        private readonly Dictionary<string, LiberaFxUnityGpuProgramBinding> gpuPrograms;
        public uint RootGpuSeed { get; }

        public LiberaFxUnityBindingLookup(LiberaFxUnityBindingAsset source)
        {
            renderAssets = new Dictionary<string, LiberaFxUnityRenderAssetBinding>(StringComparer.Ordinal);
            materials = new Dictionary<string, LiberaFxUnityMaterialBinding>(StringComparer.Ordinal);
            materialPropertyMappings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            gpuPrograms = new Dictionary<string, LiberaFxUnityGpuProgramBinding>(StringComparer.Ordinal);
            RootGpuSeed = source ? source.RootGpuSeed : 1;
            if (!source) return;
            foreach (var binding in source.RenderAssets ?? Array.Empty<LiberaFxUnityRenderAssetBinding>())
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.assetIdentity) ||
                    renderAssets.ContainsKey(binding.assetIdentity))
                    throw new InvalidOperationException("Unity render asset bindings contain an empty or duplicate identity.");
                renderAssets.Add(binding.assetIdentity, binding);
            }
            foreach (var binding in source.Materials ?? Array.Empty<LiberaFxUnityMaterialBinding>())
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.assetIdentity) || !binding.material ||
                    materials.ContainsKey(binding.assetIdentity))
                    throw new InvalidOperationException("Unity material bindings contain an empty/duplicate identity or missing Material.");
                var authoredToUnity = new Dictionary<string, string>(StringComparer.Ordinal);
                var unityTargets = new HashSet<string>(StringComparer.Ordinal);
                var parameterMappings = binding.parameterMappings ?? Array.Empty<LiberaFxUnityMaterialPropertyBinding>();
                if (parameterMappings.Length > 256)
                    throw new InvalidOperationException("Unity material parameter mapping count exceeds 256.");
                foreach (var mapping in parameterMappings)
                {
                    if (mapping == null || string.IsNullOrWhiteSpace(mapping.authoredName) ||
                        mapping.authoredName.Length > 256 || string.IsNullOrWhiteSpace(mapping.unityPropertyName) ||
                        mapping.unityPropertyName.Length > 256)
                        throw new InvalidOperationException("Unity material property mappings contain an empty or oversized name.");
                    if (!authoredToUnity.TryAdd(mapping.authoredName, mapping.unityPropertyName))
                        throw new InvalidOperationException("Unity material property mappings contain duplicate authored name '" +
                            mapping.authoredName + "'.");
                    if (!unityTargets.Add(mapping.unityPropertyName))
                        throw new InvalidOperationException("Unity material property mappings contain duplicate Unity target '" +
                            mapping.unityPropertyName + "'.");
                    if (!binding.material.HasProperty(mapping.unityPropertyName))
                        throw new InvalidOperationException("Mapped Unity shader property '" + mapping.unityPropertyName +
                            "' does not exist on Material binding '" + binding.assetIdentity + "'.");
                }
                materials.Add(binding.assetIdentity, binding);
                materialPropertyMappings.Add(binding.assetIdentity, authoredToUnity);
            }
            foreach (var binding in source.GpuPrograms ?? Array.Empty<LiberaFxUnityGpuProgramBinding>())
            {
                if (binding == null || !TryCanonicalGuid(binding.nodeId, out var key) ||
                    gpuPrograms.ContainsKey(key))
                    throw new InvalidOperationException("Unity GPU bindings contain an invalid or duplicate node GUID.");
                gpuPrograms.Add(key, binding);
            }
        }

        public LiberaFxPreparedScope Prepare(string templateId, byte[] renderBytes,
            byte[] gpuContainerBytes, byte[] gpuProgramBytes, bool isRoot = false)
        {
            var renderConfiguration = ParseRender(renderBytes);
            var renderNodes = renderConfiguration.nodes;
            var cpuParticles = ResolveCpuParticles(renderConfiguration.cpuParticles, renderNodes);
            var authoredGpuIds = ParseGpuContainerIds(gpuContainerBytes);
            var gpuPrograms = ParseGpuPrograms(gpuProgramBytes, authoredGpuIds,
                renderConfiguration.gpuDrawTransforms, out var fluidPrograms, out var generatedDrawBindings);
            var renderIds = renderNodes.ToDictionary(node => CanonicalGuid(node.nodeId), StringComparer.Ordinal);
            foreach (var binding in generatedDrawBindings)
                if (!renderIds.ContainsKey(binding.nodeId)) throw new InvalidOperationException("Generated texture target is outside this render scope.");
            foreach (var node in renderNodes)
                node.generatedTextures = generatedDrawBindings.Where(binding => binding.nodeId == CanonicalGuid(node.nodeId)).ToArray();
            var transforms = (renderConfiguration.gpuDrawTransforms ?? Array.Empty<LiberaFxGpuDrawTransformPayload>())
                .ToDictionary(transform => transform.nodeId, StringComparer.Ordinal);
            return new LiberaFxPreparedScope
            {
                templateId = templateId,
                proceduralResources = renderConfiguration.proceduralResources,
                renderNodes = renderNodes.Select(ResolveRenderNode).ToArray(),
                cpuParticles = cpuParticles,
                gpuPrograms = gpuPrograms.Select(program =>
                {
                    var prepared = ResolveGpuProgram(program, transforms);
                    prepared.emitterMatrixRowNodeBytes = renderConfiguration.gpuEmitterTransforms?
                        .SingleOrDefault(transform => transform.nodeId == prepared.nodeId)?.rowNodeBytes;
                    if (prepared.packageMetadata.emitterMatrix != (prepared.emitterMatrixRowNodeBytes != null))
                        throw new InvalidOperationException("Emitter Matrix shader and scope binding do not match.");
                    return prepared;
                }).ToArray(),
                fluidPrograms = fluidPrograms.Select(program =>
                {
                    transforms.TryGetValue(program.nodeId, out var transform);
                    return new LiberaFxPreparedFluidProgram
                    {
                        nodeId = program.nodeId, settings = program.settings,
                        sizeScale = program.renderer.sizeScale, automaticPlane = program.automaticPlane,
                        drawMatrixRowNodeBytes = transform?.rowNodeBytes
                    };
                }).ToArray()
            };
        }

        private LiberaFxPreparedCpuParticleRenderer[] ResolveCpuParticles(
            LiberaFxCpuParticlePayload[] payloads, LiberaFxRenderNodePayload[] renderNodes)
        {
            payloads = payloads ?? Array.Empty<LiberaFxCpuParticlePayload>();
            if (payloads.Length > 128)
                throw new InvalidOperationException("CPU particle renderer count exceeds 128.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in renderNodes)
                ids.Add(node.nodeId);
            var result = new LiberaFxPreparedCpuParticleRenderer[payloads.Length];
            for (var index = 0; index < payloads.Length; ++index)
            {
                var item = payloads[index];
                if (item == null || !TryCanonicalGuid(item.nodeId, out var nodeId) ||
                    !ids.Add(nodeId) || string.IsNullOrWhiteSpace(item.assetPath) ||
                    item.assetPath.Length > 512 || item.blendMode != "Alpha" && item.blendMode != "Additive" ||
                    !item.billboard || double.IsNaN(item.drawScale) || double.IsInfinity(item.drawScale) ||
                    item.drawScale <= 0 || !IsFiniteFloat(item.drawScale))
                    throw new InvalidOperationException("CPU particle renderer metadata is invalid or unsupported.");
                Texture texture;
                if (item.assetPath == "builtin:white")
                    texture = Texture2D.whiteTexture;
                else if (item.assetPath == "builtin:radial-glow")
                    texture = LiberaFxBuiltinParticleTextures.RadialGlow;
                else if (!renderAssets.TryGetValue(item.assetPath, out var asset) || !asset.texture)
                    throw new InvalidOperationException(
                        "No Unity Texture binding exists for CPU particle asset '" + item.assetPath + "'.");
                else texture = asset.texture;
                if (item.spriteAnimation!=null && !item.spriteAnimation.TextureMatches(texture.width,texture.height))
                    throw new InvalidOperationException("CPU particle sprite source image dimensions do not match its authored cells.");
                result[index] = new LiberaFxPreparedCpuParticleRenderer
                {
                    spriteAnimation = item.spriteAnimation,
                    flipbook = item.flipbook != null && item.flipbook.enabled
                        ? item.flipbook.IsValid ? item.flipbook : throw new InvalidOperationException("Invalid CPU Flipbook settings.") : null,
                    nodeId = nodeId,
                    nodeIdBytes = new NativeSignal(nodeId).Id,
                    texture = texture,
                    additive = item.blendMode == "Additive",
                    drawScale = (float)item.drawScale
                    // JsonUtility can materialize an omitted optional array as
                    // an empty array. Both represent the default owner frame.
                    , drawRows = item.drawMatrixRowNodeIds == null || item.drawMatrixRowNodeIds.Length == 0 ? null : item.drawMatrixRowNodeIds.Length == 4
                        ? item.drawMatrixRowNodeIds.Select(id => TryCanonicalGuid(id, out var canonical)
                            ? new NativeSignal(canonical).Id : throw new InvalidOperationException("Invalid CPU Draw Matrix row.")).ToArray()
                        : throw new InvalidOperationException("CPU Draw Matrix requires four rows.")
                };
            }
            return result;
        }

        private LiberaFxPreparedRenderNode ResolveRenderNode(LiberaFxRenderNodePayload node)
        {
            if (node.kind == "proceduralModel")
                return new LiberaFxPreparedRenderNode { nodeId = node.nodeId, inputs = node.inputs, proceduralDraw = node };
            var generated = node.generatedTextures ?? Array.Empty<LiberaFxGeneratedRenderTexturePayload>();
            var generatedMain = generated.Any(binding => binding.mainTexture);
            LiberaFxUnityRenderAssetBinding builtIn = null;
            if (node.assetPath == "builtin:generated-texture")
            {
                if (!generatedMain || node.kind != "sprite" && node.kind != "flexiblePlane")
                    throw new InvalidOperationException("The generated texture renderer requires an explicit main-texture producer.");
                var material = Resources.Load<Material>("LibraGeneratedTextureMaterial");
                if (!material || !material.shader || !material.shader.isSupported)
                    throw new InvalidOperationException("The shipped generated-texture material is unavailable.");
                builtIn = new LiberaFxUnityRenderAssetBinding { assetIdentity = node.assetPath, texture = Texture2D.whiteTexture,
                    defaultMaterials = new[] { material }, defaultTintPropertyNames = new[] { "_Color" } };
            }
            if (node.kind == "flexiblePlane")
            {
                if (generated.Any(binding => !binding.mainTexture))
                    throw new InvalidOperationException("Flexible Plane supports only its generated main texture.");
                if ((node.materials ?? Array.Empty<LiberaFxRenderMaterialPayload>()).Length > 1)
                    throw new InvalidOperationException("Unity Flexible Plane rendering supports only material slot zero.");
                var planeTexture = Texture2D.whiteTexture;
                Material planeMaterial = null;
                LiberaFxUnityMaterialBinding slotZeroBinding = null;
                var useProceduralShader = false;
                if (node.assetPath == "builtin:generated-texture")
                {
                    planeMaterial = builtIn.defaultMaterials[0];
                }
                else if (node.assetPath == "builtin:procedural-texture" || node.assetPath == "builtin:procedural-material")
                {
                    useProceduralShader = true;
                }
                else
                {
                    if (!renderAssets.TryGetValue(node.assetPath, out var planeAsset) ||
                        !planeAsset.texture || planeAsset.defaultMaterials == null ||
                        planeAsset.defaultMaterials.Length != 1 || !planeAsset.defaultMaterials[0])
                    {
                        throw new InvalidOperationException(
                            "Flexible Plane requires a Texture binding with exactly one vertex-colored Material.");
                    }
                    planeTexture = planeAsset.texture;
                    planeMaterial = planeAsset.defaultMaterials[0];
                }
                if (node.materials != null && node.materials.Length == 1)
                {
                    var mat = node.materials[0];
                    if (mat == null || mat.slot != 0 ||
                        !materials.TryGetValue(mat.path ?? string.Empty, out var binding) || !binding.material)
                    {
                        throw new InvalidOperationException("No Unity Material binding exists for Flexible Plane material slot 0.");
                    }
                    planeMaterial = binding.material;
                    slotZeroBinding = binding;
                    useProceduralShader = false;
                }
                var planeTexParams = new List<LiberaFxPreparedTextureParameter>();
                foreach (var texture in node.textures ?? Array.Empty<LiberaFxRenderTexturePayload>())
                {
                    ValidateUnityAssociation(texture.association, texture.index);
                    if (texture.slot != 0 || string.IsNullOrWhiteSpace(texture.name) || planeMaterial == null)
                        throw new InvalidOperationException("Flexible Plane texture parameter has an invalid slot or material.");
                    if (string.IsNullOrEmpty(texture.path)) continue;
                    if (!renderAssets.TryGetValue(texture.path, out var textureBinding) || !textureBinding.texture)
                        throw new InvalidOperationException("No Unity Texture binding exists for material texture '" + texture.path + "'.");
                    var unityPropertyName = ResolveMappedProperty(slotZeroBinding, texture.name, 0, "texture");
                    ValidateMappedPropertyType(planeMaterial, unityPropertyName, texture.name, "texture");
                    planeTexParams.Add(new LiberaFxPreparedTextureParameter
                    {
                        name = unityPropertyName,
                        slot = 0,
                        texture = textureBinding.texture
                    });
                }
                var planeMatPropNames = new List<string>();
                foreach (var input in node.inputs ?? Array.Empty<LiberaFxRenderInputPayload>())
                {
                    if (input?.materialParameter == null) continue;
                    ValidateUnityAssociation(input.materialParameter.association, input.materialParameter.index);
                    if (input.materialParameter.slot != 0 || string.IsNullOrWhiteSpace(input.materialParameter.name) || planeMaterial == null)
                        throw new InvalidOperationException("Flexible Plane material parameter has an invalid slot or material.");
                    if (input.materialParameter.kind == "scalar" && input.width != 1 ||
                        input.materialParameter.kind == "vector" && input.width != 4 ||
                        input.materialParameter.kind != "scalar" && input.materialParameter.kind != "vector")
                        throw new InvalidOperationException("Flexible Plane material parameter type is unsupported by Unity.");
                    var propertyKind = input.materialParameter.kind == "scalar" ? "scalar" : "vector";
                    var unityPropertyName = ResolveMappedProperty(slotZeroBinding, input.materialParameter.name, 0, propertyKind);
                    ValidateMappedPropertyType(planeMaterial, unityPropertyName, input.materialParameter.name, propertyKind);
                    planeMatPropNames.Add(unityPropertyName);
                }
                return new LiberaFxPreparedRenderNode
                {
                    nodeId = node.nodeId,
                    inputs = node.inputs,
                    generatedTextures = generated,
                    flexiblePlane = LiberaFxPreparedFlexiblePlane.Prepare(node, node.flexiblePlane,
                        planeTexture, planeMaterial, "_MainTex", Camera.main,
                        useProceduralShader, planeTexParams.ToArray(), planeMatPropNames.ToArray())
                };
            }
            if (node.kind == "debugText" || node.kind == "debugModel")
            {
                Mesh debugMesh = null;
                var debugMaterials = Array.Empty<Material>();
                if (node.kind == "debugModel")
                {
                    if (!renderAssets.TryGetValue(node.assetPath, out var model) || !model.mesh)
                        throw new InvalidOperationException("Debug Model requires a bound Unity Mesh: " + node.assetPath);
                    debugMesh = model.mesh;
                    debugMaterials = model.defaultMaterials;
                    if (debugMaterials == null || debugMaterials.Length != debugMesh.subMeshCount ||
                        debugMaterials.Any(item => !item))
                        throw new InvalidOperationException("Debug Model requires explicit material bindings for every submesh.");
                }
                return new LiberaFxPreparedRenderNode { nodeId = node.nodeId, debugDraw = node,
                    inputs = node.inputs, mesh = debugMesh, materials = debugMaterials };
            }
            var asset = builtIn;
            if (asset == null) renderAssets.TryGetValue(node.assetPath, out asset);
            if (asset == null)
                throw new InvalidOperationException("No Unity asset binding exists for render asset '" + node.assetPath + "'.");
            var isSprite = node.kind == "sprite";
            if (isSprite ? !asset.texture : !asset.mesh)
                throw new InvalidOperationException("Unity render asset binding '" + node.assetPath +
                    "' does not supply the required " + (isSprite ? "Texture" : "Mesh") + ".");

            var slots = isSprite ? 1 : asset.mesh.subMeshCount;
            if (slots < 1 || slots > 256)
                throw new InvalidOperationException("Render node material slot count is outside supported bounds.");
            if (asset.defaultMaterials != null && asset.defaultMaterials.Length > slots ||
                asset.defaultTintPropertyNames != null && asset.defaultTintPropertyNames.Length > slots)
                throw new InvalidOperationException("Unity render asset binding defines unused material slots.");
            var resolvedMaterials = new Material[slots];
            var tintPropertyNames = new string[slots];
            var materialBindings = new LiberaFxUnityMaterialBinding[slots];
            if (asset.defaultMaterials != null)
                Array.Copy(asset.defaultMaterials, resolvedMaterials,
                    Math.Min(asset.defaultMaterials.Length, resolvedMaterials.Length));
            if (asset.defaultTintPropertyNames != null)
                Array.Copy(asset.defaultTintPropertyNames, tintPropertyNames,
                    Math.Min(asset.defaultTintPropertyNames.Length, tintPropertyNames.Length));
            foreach (var material in node.materials ?? Array.Empty<LiberaFxRenderMaterialPayload>())
            {
                if (material == null || material.slot < 0 || material.slot >= resolvedMaterials.Length ||
                    !materials.TryGetValue(material.path ?? string.Empty, out var binding) || !binding.material)
                    throw new InvalidOperationException("No Unity Material binding exists for render material slot " +
                        (material == null ? "<null>" : material.slot.ToString(CultureInfo.InvariantCulture)) + ".");
                if (materialBindings[material.slot] != null)
                    throw new InvalidOperationException("Render node has duplicate material bindings for slot " + material.slot + ".");
                resolvedMaterials[material.slot] = binding.material;
                tintPropertyNames[material.slot] = binding.tintPropertyName;
                materialBindings[material.slot] = binding;
            }
            var requiredSlots = isSprite ? 1 : asset.mesh.subMeshCount;
            for (var slot = 0; slot < requiredSlots; ++slot)
            {
                if (!resolvedMaterials[slot])
                    throw new InvalidOperationException("Render node '" + node.nodeId +
                        "' has no Unity Material bound for slot " + slot + ".");
                if (string.IsNullOrWhiteSpace(tintPropertyNames[slot]) ||
                    !resolvedMaterials[slot].HasProperty(tintPropertyNames[slot]))
                    throw new InvalidOperationException("Unity Material slot " + slot +
                        " has no configured tint property for render node " + node.nodeId + ".");
            }

            var textureParameters = new List<LiberaFxPreparedTextureParameter>();
            var generatedParameters = new List<LiberaFxGeneratedRenderTexturePayload>();
            foreach (var reference in generated)
            {
                if (reference.slot < 0 || reference.slot >= slots || reference.mainTexture && !isSprite)
                    throw new InvalidOperationException("Generated texture target slot is invalid.");
                ValidateUnityAssociation(reference.association, reference.index);
                var name = reference.mainTexture ? "_MainTex" : ResolveMappedProperty(materialBindings[reference.slot], reference.name, reference.slot, "texture");
                ValidateMappedPropertyType(resolvedMaterials[reference.slot], name, reference.name, "texture");
                generatedParameters.Add(new LiberaFxGeneratedRenderTexturePayload { nodeId = reference.nodeId, producerNodeId = reference.producerNodeId,
                    outputId = reference.outputId, mainTexture = reference.mainTexture, slot = reference.slot, name = name,
                    association = reference.association, index = reference.index });
            }
            var materialParameterPropertyNames = new string[(node.inputs ?? Array.Empty<LiberaFxRenderInputPayload>()).Length];
            foreach (var texture in node.textures ?? Array.Empty<LiberaFxRenderTexturePayload>())
            {
                ValidateUnityAssociation(texture.association, texture.index);
                if (texture.slot < 0 || texture.slot >= resolvedMaterials.Length ||
                    string.IsNullOrWhiteSpace(texture.name))
                    throw new InvalidOperationException("Render texture parameter has an invalid name or slot.");
                if (string.IsNullOrEmpty(texture.path)) continue;
                if (!renderAssets.TryGetValue(texture.path, out var textureBinding) || !textureBinding.texture)
                    throw new InvalidOperationException("No Unity Texture binding exists for material texture '" + texture.path + "'.");
                var targetMaterial = resolvedMaterials[texture.slot];
                var unityPropertyName = ResolveMappedProperty(materialBindings[texture.slot],
                    texture.name, texture.slot, "texture");
                ValidateMappedPropertyType(targetMaterial, unityPropertyName, texture.name, "texture");
                textureParameters.Add(new LiberaFxPreparedTextureParameter
                {
                    name = unityPropertyName,
                    slot = texture.slot,
                    texture = textureBinding.texture
                });
            }
            for (var inputIndex = 0; inputIndex < (node.inputs ?? Array.Empty<LiberaFxRenderInputPayload>()).Length; ++inputIndex)
            {
                var input = node.inputs[inputIndex];
                if (input.materialParameter == null) continue;
                ValidateUnityAssociation(input.materialParameter.association, input.materialParameter.index);
                if (input.materialParameter.slot < 0 || input.materialParameter.slot >= resolvedMaterials.Length ||
                    string.IsNullOrWhiteSpace(input.materialParameter.name))
                    throw new InvalidOperationException("Render material parameter has an invalid name or slot.");
                var material = resolvedMaterials[input.materialParameter.slot];
                if (input.materialParameter.kind == "scalar" && input.width != 1 ||
                    input.materialParameter.kind == "vector" && input.width != 4 ||
                    input.materialParameter.kind != "scalar" && input.materialParameter.kind != "vector")
                    throw new InvalidOperationException("Render material parameter type is unsupported by Unity.");
                var propertyKind = input.materialParameter.kind == "scalar" ? "scalar" : "vector";
                var unityPropertyName = ResolveMappedProperty(materialBindings[input.materialParameter.slot],
                    input.materialParameter.name, input.materialParameter.slot, propertyKind);
                ValidateMappedPropertyType(material, unityPropertyName, input.materialParameter.name, propertyKind);
                materialParameterPropertyNames[inputIndex] = unityPropertyName;
            }

            if (isSprite)
            {
                if ((node.materials ?? Array.Empty<LiberaFxRenderMaterialPayload>()).Length > 1 ||
                    textureParameters.Exists(parameter => parameter.slot != 0) ||
                    (node.inputs ?? Array.Empty<LiberaFxRenderInputPayload>())
                        .Any(input => input.materialParameter != null && input.materialParameter.slot != 0))
                    throw new InvalidOperationException("Unity sprite rendering supports only material slot zero.");
                foreach (var material in resolvedMaterials)
                    if (!material.HasProperty("_MainTex") || !material.HasProperty("_AtlasRect"))
                        throw new InvalidOperationException(
                            "Unity sprite Materials must expose _MainTex and _AtlasRect.");
                if (node.billboard && !Camera.main)
                    throw new InvalidOperationException("A billboard Sprite requires an enabled MainCamera before Play.");
            }
            if (!string.IsNullOrEmpty(node.rotationConvention) && node.rotationConvention != "liberafx-world")
                throw new InvalidOperationException("Unsupported render rotation convention.");
            return new LiberaFxPreparedRenderNode
            {
                nodeId = node.nodeId,
                isSprite = isSprite,
                frameConnected = (node.inputs ?? Array.Empty<LiberaFxRenderInputPayload>())
                    .Any(input => input.id == "frame" && !string.IsNullOrEmpty(input.sourceNodeId)),
                billboard = node.billboard,
                worldRotation = node.rotationConvention == "liberafx-world",
                mesh = asset.mesh,
                texture = asset.texture,
                materials = resolvedMaterials,
                tintPropertyNames = tintPropertyNames,
                materialParameterPropertyNames = materialParameterPropertyNames,
                billboardCamera = node.billboard ? Camera.main : null,
                inputs = node.inputs ?? Array.Empty<LiberaFxRenderInputPayload>(),
                textures = textureParameters.ToArray(),
                spriteAnimation = node.spriteAnimation,
                spriteKeys = node.spriteAnimation?.version == 2 ? node.spriteAnimation.keys.SelectMany(key =>
                    new[] { key.frame, key.x, key.y, key.rotationDegrees, key.scaleX, key.scaleY }).ToArray() : null,
                spriteDurations = node.spriteAnimation?.frames?.Select(frame => frame.durationFrames).ToArray()
                    ?? Array.Empty<double>(),
                generatedTextures = generatedParameters.ToArray()
            };
        }

        private string ResolveMappedProperty(LiberaFxUnityMaterialBinding binding,
            string authoredName, int slot, string propertyKind)
        {
            if (binding == null ||
                !materialPropertyMappings.TryGetValue(binding.assetIdentity, out var mappings) ||
                !mappings.TryGetValue(authoredName, out var unityPropertyName))
                throw new InvalidOperationException("Render material " + propertyKind + " '" + authoredName +
                    "' in slot " + slot + " has no explicit Unity shader-property mapping.");
            return unityPropertyName;
        }

        private static void ValidateMappedPropertyType(Material material, string unityPropertyName,
            string authoredName, string propertyKind)
        {
            if (!material || !material.shader)
                throw new InvalidOperationException("Mapped Unity Material or shader is missing.");
            var shader = material.shader;
            ShaderPropertyType? targetType = null;
            for (var index = 0; index < shader.GetPropertyCount(); ++index)
                if (shader.GetPropertyName(index) == unityPropertyName)
                {
                    targetType = shader.GetPropertyType(index);
                    break;
                }
            if (!targetType.HasValue)
                throw new InvalidOperationException("Mapped Unity shader property '" + unityPropertyName +
                    "' for authored parameter '" + authoredName + "' is not declared by the Material shader.");

            var compatible = propertyKind switch
            {
                "scalar" => targetType == ShaderPropertyType.Float || targetType == ShaderPropertyType.Range,
                "vector" => targetType == ShaderPropertyType.Color || targetType == ShaderPropertyType.Vector,
                "texture" => targetType == ShaderPropertyType.Texture,
                _ => false
            };
            if (!compatible)
                throw new InvalidOperationException("Mapped Unity shader property '" + unityPropertyName +
                    "' has type " + targetType.Value + ", incompatible with authored " + propertyKind + " parameter '" +
                    authoredName + "'.");
        }

        private LiberaFxPreparedGpuProgram ResolveGpuProgram(LiberaFxGpuManifestProgram program,
            Dictionary<string, LiberaFxGpuDrawTransformPayload> drawTransforms)
        {
            var nodeId = CanonicalGuid(program.nodeId);
            if (!gpuPrograms.TryGetValue(nodeId, out var binding))
                throw new InvalidOperationException("No Unity GPU binding exists for node " + nodeId + ".");
            if (!binding.computeShader || !binding.metadata || !binding.billboardMaterial)
                throw new InvalidOperationException("Unity GPU binding " + nodeId +
                    " requires an imported ComputeShader, metadata TextAsset and billboard Material.");
            if (string.IsNullOrWhiteSpace(program.unityMetadataJson) ||
                string.IsNullOrWhiteSpace(program.unityShaderSource))
                throw new InvalidOperationException("GPU executable payload is missing Unity metadata or source for " + nodeId + ".");
            var packageMetadata = LibraGpuMetadata.Parse(program.unityMetadataJson);
            var importedMetadata = LibraGpuMetadata.Parse(binding.metadata.text);
            if (program.unreal == null || program.unreal.metadata == null ||
                program.unreal.canonicalJson == null ||
                program.unreal.metadata.programId != packageMetadata.programId ||
                program.unreal.metadata.contentHash != packageMetadata.contentHash ||
                HashUtf8(program.unreal.canonicalJson) != packageMetadata.contentHash ||
                HashUtf8(program.unityShaderSource) != packageMetadata.sourceHash ||
                importedMetadata.programId != packageMetadata.programId ||
                importedMetadata.contentHash != packageMetadata.contentHash ||
                importedMetadata.sourceHash != packageMetadata.sourceHash)
                throw new InvalidOperationException("GPU program identity/hash does not match the preimported Unity assets for " + nodeId + ".");

            var configured = gpuPrograms[nodeId];
            if (float.IsNaN(configured.spawnRate) || float.IsInfinity(configured.spawnRate) || configured.spawnRate < 0 ||
                float.IsNaN(configured.sizeScale) || float.IsInfinity(configured.sizeScale) ||
                configured.sizeScale <= 0 || configured.sizeScale > 10000)
                throw new InvalidOperationException("Unity GPU spawn rate or size scale is invalid for " + nodeId + ".");
            drawTransforms.TryGetValue(nodeId, out var drawTransform);
            if (drawTransform != null &&
                !LibraGpuParticleRuntime.HasDrawTransformProperties(configured.billboardMaterial))
                throw new InvalidOperationException("Unity GPU billboard Material must expose all four _LibraGpuDrawMatrixRow0..Row3 properties for the connected Matrix input on " + nodeId + ".");
            var generated = program.generatedResources ?? Array.Empty<LiberaFxGeneratedGpuResource>();
            var generatedIds = new HashSet<string>(generated.Select(reference => reference.resourceId), StringComparer.Ordinal);
            ValidateGpuTextures(packageMetadata, configured.textures, nodeId, generatedIds);
            foreach (var reference in generated)
                if (!packageMetadata.resources.Any(resource => resource.id == reference.resourceId &&
                    ((reference.outputId == "DyeOutput" || reference.outputId == "ColorOutput" || reference.outputId == "FieldOutput") && resource.kind == "Texture2D" ||
                     reference.outputId == "VelocityOutput" && resource.kind == "FlowMap2D" && resource.flowMap?.encoding == "FloatVelocity" ||
                     reference.outputId == "DensityOutput" && resource.kind == "FlowMap2D" && resource.flowMap?.encoding == "FloatDensity")))
                    throw new InvalidOperationException("Generated output does not match the declared texture/sampling encoding: " + reference.resourceId);
            var renderBindings = program.unreal.rendererBindings ?? Array.Empty<LiberaFxGpuRendererBinding>();
            if (program.unreal.rendererBindingsVersion!=1 && program.unreal.rendererBindingsVersion!=2)
                throw new NotSupportedException("Unsupported GPU renderer bindings version.");
            var renderFlowCount = 0;
            foreach (var flow in packageMetadata.flow)
                if (flow.kind == "RenderBillboard") ++renderFlowCount;
            if (renderBindings.Length != renderFlowCount)
                throw new InvalidOperationException("GPU renderer binding count does not match the compiled billboard flow.");
            if (renderBindings.Length > 1)
                throw new InvalidOperationException("Unity GPU binding currently supports one billboard flow per GPU node.");
            var sizeScale = 1f;
            foreach (var renderer in renderBindings)
            {
                if (renderer!=null)
                {
                    if (!renderer.__liberafxHasSpriteAnimation) renderer.spriteAnimation=null;
                    renderer.spriteAnimation?.Validate();
                    if (renderer.spriteAnimation!=null && program.unreal.rendererBindingsVersion!=2)
                        throw new InvalidOperationException("GPU sprite animation requires renderer bindings version 2.");
                }
                if (renderer == null || renderer.renderer != "builtin:billboard" ||
                    renderer.material != configured.rendererMaterialIdentity ||
                    (renderer.material != "builtin:unlit-alpha" && renderer.material != "builtin:unlit-additive") ||
                    double.IsNaN(renderer.sizeScale) || double.IsInfinity(renderer.sizeScale) ||
                    renderer.sizeScale <= 0 ||
                    renderer.sizeScale > 10000 || Math.Abs(renderer.sizeScale - configured.sizeScale) > 0.0001)
                    throw new InvalidOperationException("Unity GPU renderer material/size binding is unsupported or mismatched for " + nodeId + ".");
                sizeScale = (float)renderer.sizeScale;
                var firstSprite = renderBindings[0].spriteAnimation;
                if ((renderer.spriteAnimation == null) != (firstSprite == null) ||
                    renderer.spriteAnimation != null &&
                    (renderer.spriteAnimation.partCount != firstSprite.partCount ||
                     !renderer.spriteAnimation.values.SequenceEqual(firstSprite.values)))
                {
                    throw new InvalidOperationException("Unity GPU billboard renderers in one program must share the same Sprite editor animation.");
                }
            }
            LibraGpuParticleRuntime.ValidateSpawnableConfiguration(configured.computeShader,
                configured.metadata, configured.billboardMaterial, configured.textures, generatedIds);
            return new LiberaFxPreparedGpuProgram
            {
                nodeId = nodeId,
                binding = configured,
                packageMetadata = packageMetadata,
                spawnRate = packageMetadata.version >= 3 ? packageMetadata.spawnRate : configured.spawnRate,
                sizeScale = sizeScale,
                spriteAnimation=renderBindings.Length==0 ? null : renderBindings[0].spriteAnimation,
                generatedResources = generated,
                drawMatrixRowNodeBytes = drawTransform?.rowNodeBytes
            };
        }

        private static void ValidateGpuTextures(LibraGpuMetadata metadata,
            LibraGpuTextureBinding[] bindings, string nodeId, ISet<string> generated = null)
        {
            var byId = new Dictionary<string, LibraGpuTextureBinding>(StringComparer.Ordinal);
            foreach (var binding in bindings ?? Array.Empty<LibraGpuTextureBinding>())
                if (binding == null || string.IsNullOrWhiteSpace(binding.resourceId) || !binding.texture ||
                    byId.ContainsKey(binding.resourceId))
                    throw new InvalidOperationException("Unity GPU texture bindings contain an unresolved or duplicate resource for " + nodeId + ".");
                else byId.Add(binding.resourceId, binding);
            generated = generated ?? new HashSet<string>();
            if (generated.Any(byId.ContainsKey) || metadata.resources.Length != byId.Count + generated.Count)
                throw new InvalidOperationException("Unity GPU texture binding count does not match metadata for " + nodeId + ".");
            foreach (var resource in metadata.resources)
                if (!byId.ContainsKey(resource.id) && !generated.Contains(resource.id))
                    throw new InvalidOperationException("Unity GPU resource '" + resource.id + "' is unbound for " + nodeId + ".");
        }

        private static LiberaFxRenderPayload ParseRender(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return new LiberaFxRenderPayload { nodes = Array.Empty<LiberaFxRenderNodePayload>() };
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Render configuration exceeds 4 MiB.");
            LiberaFxRenderPayload payload;
            try
            {
                var json = Encoding.UTF8.GetString(bytes).Replace("\"materialParameter\":null",
                    "\"materialParameter\":{\"name\":\"" + NullMaterialParameterSentinel +
                    "\",\"kind\":\"" + NullMaterialParameterSentinel + "\"}");
                // JsonUtility materializes explicit null class fields as empty objects.
                json = System.Text.RegularExpressions.Regex.Replace(json, @"""spriteAnimation""\s*:\s*\{",
                    "\"__liberafxHasSpriteAnimation\":true,\"spriteAnimation\":{",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                payload = JsonUtility.FromJson<LiberaFxRenderPayload>(json);
            }
            catch (Exception error) { throw new InvalidOperationException("Render configuration JSON is malformed.", error); }
            if (payload == null || payload.type != "render_config" || payload.nodes == null || payload.nodes.Length > 128)
                throw new InvalidOperationException("Render configuration type or node count is invalid.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in payload.nodes)
            {
                if (node == null || !TryCanonicalGuid(node.nodeId, out var id) ||
                    !ids.Add(id) || (node.kind != "model" && node.kind != "sprite" &&
                        node.kind != "debugText" && node.kind != "debugModel" && node.kind != "flexiblePlane" && node.kind != "proceduralModel") ||
                    node.kind != "debugText" && (string.IsNullOrWhiteSpace(node.assetPath) || node.assetPath.Length > 512))
                    throw new InvalidOperationException("Render configuration contains an invalid node identity, kind or asset.");
                node.nodeId = id;
                if (!node.__liberafxHasSpriteAnimation) node.spriteAnimation = null;
                foreach (var input in node.inputs ?? Array.Empty<LiberaFxRenderInputPayload>())
                    if (input != null && input.materialParameter != null &&
                        input.materialParameter.name == NullMaterialParameterSentinel &&
                        input.materialParameter.kind == NullMaterialParameterSentinel)
                        input.materialParameter = null;
                ValidateInputs(node);
                ValidateSpriteAnimation(node);
                if ((node.materials?.Length ?? 0) > 256 || (node.textures?.Length ?? 0) > 256)
                    throw new InvalidOperationException("Render node material metadata exceeds the supported bound.");
            }
            payload.proceduralResources = payload.proceduralResources ?? Array.Empty<LiberaFxProceduralResourcePayload>();
            if (payload.proceduralVersion != 1 && (payload.proceduralVersion != 0 || payload.proceduralResources.Length != 0) ||
                payload.proceduralResources.Length > 64)
                throw new NotSupportedException("Unsupported procedural resource metadata version or count.");
            var resourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in payload.proceduralResources)
            {
                var recipe = resource?.recipe;
                if (resource == null || !TryCanonicalGuid(resource.nodeId, out var resourceId) || !resourceIds.Add(resourceId) ||
                    recipe == null || recipe.version != 1 ||
                    resource.kind != "ProceduralModel" && resource.kind != "ProceduralTexture" && resource.kind != "ProceduralMaterial" ||
                    recipe.modifiers == null || recipe.modifiers.Length > 16 || recipe.operations == null || recipe.operations.Length > 32 ||
                    recipe.resolution < 16 || recipe.resolution > 1024 || (recipe.resolution & (recipe.resolution - 1)) != 0 ||
                    recipe.blend != "alpha" && recipe.blend != "additive" || !IsFiniteFloat(recipe.emission) || recipe.emission < 0 || recipe.emission > 64)
                    throw new InvalidOperationException("Invalid procedural resource recipe.");
                resource.nodeId = resourceId;
                ValidateInputs(new LiberaFxRenderNodePayload { nodeId = resource.nodeId, kind = "flexiblePlane", inputs = resource.inputs });
                var textureSlots = new HashSet<int>();
                foreach (var reference in resource.textures ?? Array.Empty<LiberaFxProceduralTextureReference>())
                {
                    if (reference == null || resource.kind != "ProceduralMaterial" || reference.slot < 0 || reference.slot > 3 ||
                        !textureSlots.Add(reference.slot) || !TryCanonicalGuid(reference.producerNodeId, out var source))
                        throw new InvalidOperationException("Invalid procedural texture binding.");
                    reference.producerNodeId = source;
                }
                if (recipe.modifiers.Any(m => m == null) || recipe.operations.Any(op => op == null))
                    throw new InvalidOperationException("Null procedural operation.");
            }
            foreach (var draw in payload.nodes.Where(n => n.kind == "proceduralModel" || n.kind == "flexiblePlane"))
            {
                if (draw.kind == "proceduralModel")
                {
                    if (!TryCanonicalGuid(draw.proceduralModel, out var source) || !resourceIds.Contains(source))
                        throw new InvalidOperationException("Procedural draw is missing its scoped producer.");
                    draw.proceduralModel = source;
                }
                if (!string.IsNullOrEmpty(draw.proceduralTexture))
                {
                    if (!TryCanonicalGuid(draw.proceduralTexture, out var textureId) || !resourceIds.Contains(textureId))
                        throw new InvalidOperationException("Procedural draw texture is missing its scoped producer.");
                    draw.proceduralTexture = textureId;
                }
                if (!string.IsNullOrEmpty(draw.proceduralMaterial))
                {
                    if (!TryCanonicalGuid(draw.proceduralMaterial, out var material) || !resourceIds.Contains(material))
                        throw new InvalidOperationException("Procedural draw material is missing its scoped producer.");
                    draw.proceduralMaterial = material;
                }
            }
            payload.cpuParticles = payload.cpuParticles ?? Array.Empty<LiberaFxCpuParticlePayload>();
            foreach (var particle in payload.cpuParticles)
            {
                if (particle==null) throw new InvalidOperationException("Missing CPU particle renderer.");
                if (!particle.__liberafxHasSpriteAnimation) particle.spriteAnimation=null;
                particle.spriteAnimation?.Validate();
                if (particle.spriteAnimation!=null && (payload.cpuParticleVersion!=4 || particle.flipbook?.enabled==true))
                    throw new InvalidOperationException("CPU sprite animation needs metadata version 4 and cannot combine with Flipbook.");
            }
            if (payload.cpuParticleVersion != 1 && payload.cpuParticleVersion != 2 && payload.cpuParticleVersion != 3 && payload.cpuParticleVersion != 4 && (payload.cpuParticleVersion != 0 || payload.cpuParticles.Length != 0))
                throw new NotSupportedException("Unsupported CPU particle scene metadata version.");
            var drawTransforms = new Dictionary<string, LiberaFxGpuDrawTransformPayload>(StringComparer.Ordinal);
            foreach (var transform in payload.gpuDrawTransforms ?? Array.Empty<LiberaFxGpuDrawTransformPayload>())
            {
                if (transform == null || !TryCanonicalGuid(transform.nodeId, out var nodeId) ||
                    !drawTransforms.TryAdd(nodeId, transform) || transform.rowNodeIds == null ||
                    transform.rowNodeIds.Length != 4)
                    throw new InvalidOperationException("Render configuration contains an invalid or duplicate GPU draw Matrix binding.");
                transform.nodeId = nodeId;
                transform.rowNodeBytes = new byte[4][];
                for (var row = 0; row < 4; ++row)
                {
                    if (!TryCanonicalGuid(transform.rowNodeIds[row], out var rowNodeId))
                        throw new InvalidOperationException("GPU draw Matrix row " + row + " has an invalid runtime node GUID.");
                    transform.rowNodeIds[row] = rowNodeId;
                    transform.rowNodeBytes[row] = new NativeSignal(rowNodeId).Id;
                }
            }
            if (drawTransforms.Count > 16)
                throw new InvalidOperationException("Render configuration exceeds the 16 GPU draw Matrix binding limit.");
            payload.gpuDrawTransforms = drawTransforms.Values.ToArray();
            var emitterIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transform in payload.gpuEmitterTransforms ?? Array.Empty<LiberaFxGpuDrawTransformPayload>())
            {
                if (transform == null || !TryCanonicalGuid(transform.nodeId, out var nodeId) ||
                    !emitterIds.Add(nodeId) || transform.rowNodeIds == null || transform.rowNodeIds.Length != 4)
                    throw new InvalidOperationException("Invalid Emitter Matrix binding.");
                transform.nodeId = nodeId;
                transform.rowNodeBytes = transform.rowNodeIds.Select(id =>
                    TryCanonicalGuid(id, out var canonical) ? new NativeSignal(canonical).Id :
                    throw new InvalidOperationException("Invalid Emitter Matrix row identity.")).ToArray();
            }
            if (emitterIds.Count > 16) throw new InvalidOperationException("Too many Emitter Matrix bindings.");
            return payload;
        }

        private static void ValidateInputs(LiberaFxRenderNodePayload node)
        {
            if (node.inputs == null || node.inputs.Length > 256)
                throw new InvalidOperationException("Render node input count is invalid.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in node.inputs)
            {
                if (input == null) throw new InvalidOperationException("Render node contains a null input.");
                if (string.IsNullOrWhiteSpace(input.id) || input.id.Length > 128 || !ids.Add(input.id))
                    throw new InvalidOperationException("Render node contains an empty or duplicate input ID.");
                if (input.width < 1 || input.width > 4 || input.defaults == null ||
                    input.defaults.Length != input.width)
                    throw new InvalidOperationException("Render input '" + input.id +
                        "' has invalid width/default lanes (width=" + input.width + ", defaults=" +
                        (input.defaults == null ? "null" : input.defaults.Length.ToString(CultureInfo.InvariantCulture)) + ").");
                if (input.sourceComponent < 0 || input.sourceComponent > 3)
                    throw new InvalidOperationException("Render input '" + input.id + "' has an invalid source component.");
                if (!string.IsNullOrEmpty(input.sourceNodeId) && !TryCanonicalGuid(input.sourceNodeId, out _))
                    throw new InvalidOperationException("Render input '" + input.id + "' has an invalid source node GUID.");
                if (!string.IsNullOrEmpty(input.sourceNodeId))
                    input.sourceNodeBytes = new NativeSignal(input.sourceNodeId).Id;
                foreach (var lane in input.defaults)
                    if (!IsFiniteFloat(lane))
                        throw new InvalidOperationException("Render node default exceeds finite Unity float range.");
            }
            if (node.kind == "flexiblePlane")
            {
                return;
            }
            if (node.kind == "debugText" || node.kind == "debugModel")
            {
                if (!node.previewOnly || node.debugVersion != 1 || !IsFiniteFloat(node.size) ||
                    node.size <= 0 || node.size > 10000 || node.inputs.Length != 5 ||
                    node.kind == "debugText" && (node.text == null ||
                        Encoding.UTF8.GetByteCount(node.text) > 1024 || node.text.IndexOf('\0') >= 0))
                    throw new InvalidOperationException("Unsupported preview-only Debug draw descriptor.");
                for (var row = 0; row < 4; ++row) RequireInput(node, "row" + row, 4);
                RequireInput(node, "visible", 1);
                return;
            }
            RequireInput(node, "position", 3);
            RequireInput(node, "rotation", 3);
            RequireInput(node, "scale", 3);
            RequireInput(node, "tint", 4);
            if (node.kind == "sprite")
            {
                RequireInput(node, "frame", 1);
                var columns = StaticInput(node, "columns", 1);
                var rows = StaticInput(node, "rows", 1);
                var fps = StaticInput(node, "fps", 1);
                StaticInput(node, "playing", 1);
                if (!string.IsNullOrEmpty(columns.sourceNodeId) || !string.IsNullOrEmpty(rows.sourceNodeId) ||
                    !string.IsNullOrEmpty(fps.sourceNodeId) || !string.IsNullOrEmpty(StaticInput(node, "playing", 1).sourceNodeId) ||
                    node.spriteAnimation == null && (columns.defaults[0] < 1 || columns.defaults[0] > 65536 ||
                    rows.defaults[0] < 1 || rows.defaults[0] > 65536 || fps.defaults[0] < 0 ||
                    Math.Floor(columns.defaults[0]) != columns.defaults[0] ||
                    Math.Floor(rows.defaults[0]) != rows.defaults[0]) ||
                    (StaticInput(node, "playing", 1).defaults[0] != 0 &&
                     StaticInput(node, "playing", 1).defaults[0] != 1))
                    throw new InvalidOperationException("Sprite atlas settings are unsupported or invalid.");
            }
            foreach (var input in node.inputs)
                if (!IsStandardInput(input.id) && input.materialParameter == null)
                    throw new InvalidOperationException("Unsupported render input '" + input.id + "'.");
        }

        private static void ValidateSpriteAnimation(LiberaFxRenderNodePayload node)
        {
            var data = node.spriteAnimation;
            if (data == null) return;
            if (node.kind != "sprite" || data.version != 1 && data.version != 2 ||
                data.textureWidth < 1 || data.textureWidth > 8192 ||
                data.textureHeight < 1 || data.textureHeight > 8192 ||
                !IsFiniteFloat(data.fps) || data.fps <= 0 || data.fps > 10000 ||
                data.cells == null || data.cells.Length < 1 || data.cells.Length > 512 ||
                data.frames == null || data.frames.Length < 1 || data.frames.Length > 4096 ||
                data.startFrame < 0 || data.endFrame < data.startFrame || data.endFrame >= data.frames.Length)
                throw new InvalidOperationException("Sprite animation metadata is missing, unsupported or out of range.");
            var cells = new Dictionary<string, LiberaFxRenderSpriteCellPayload>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in data.cells)
            {
                if (cell == null || !TryCanonicalGuid(cell.id, out var id) ||
                    string.IsNullOrWhiteSpace(cell.name) || cell.name.Length > 80 ||
                    !names.Add(cell.name.Trim()) || cell.x < 0 || cell.y < 0 ||
                    cell.width <= 0 || cell.height <= 0 ||
                    (long)cell.x + cell.width > data.textureWidth ||
                    (long)cell.y + cell.height > data.textureHeight ||
                    !IsFiniteFloat(cell.u) || !IsFiniteFloat(cell.v) ||
                    !IsFiniteFloat(cell.uvWidth) || !IsFiniteFloat(cell.uvHeight) ||
                    Math.Abs(cell.u - (double)cell.x / data.textureWidth) > 1e-9 ||
                    Math.Abs(cell.v - (double)cell.y / data.textureHeight) > 1e-9 ||
                    Math.Abs(cell.uvWidth - (double)cell.width / data.textureWidth) > 1e-9 ||
                    Math.Abs(cell.uvHeight - (double)cell.height / data.textureHeight) > 1e-9 ||
                    !cells.TryAdd(id, cell))
                    throw new InvalidOperationException("Sprite animation contains an invalid or duplicate pixel/UV cell.");
                cell.id = id;
            }
            var frameIds = new HashSet<string>(StringComparer.Ordinal);
            var duration = 0d;
            foreach (var frame in data.frames)
            {
                if (frame == null || !TryCanonicalGuid(frame.id, out var frameId) ||
                    !frameIds.Add(frameId) || !TryCanonicalGuid(frame.cellId, out var cellId) ||
                    !cells.ContainsKey(cellId) || frame.cellIndex < 0 ||
                    frame.cellIndex >= data.cells.Length || data.cells[frame.cellIndex].id != cellId ||
                    !IsFiniteFloat(frame.durationFrames) ||
                    frame.durationFrames <= 0 || frame.durationFrames > 1_000_000)
                    throw new InvalidOperationException("Sprite animation contains a missing cell reference or invalid frame timing.");
                frame.id = frameId;
                frame.cellId = cellId;
                duration += frame.durationFrames;
            }
            if (data.version == 2)
            {
                if (node.billboard || data.startFrame != 0 || data.endFrame != data.frames.Length - 1 ||
                    !IsFiniteFloat(data.durationFrames) || data.durationFrames < Math.Max(1, duration) ||
                    data.durationFrames > 4_096_000_000d || data.drawOrder < 0 || data.drawOrder >= 32 ||
                    data.keys == null || data.keys.Length > 256)
                    throw new InvalidOperationException("Layered sprite clip bounds or draw order are invalid.");
                var keyIds = new HashSet<string>(StringComparer.Ordinal);
                var previous = -1d;
                bool Bounded(double value) => IsFiniteFloat(value) && Math.Abs(value) <= 1_000_000;
                bool ValidScale(double value) => IsFiniteFloat(value) && value != 0 && Math.Abs(value) <= 10_000;
                foreach (var key in data.keys)
                {
                    if (key == null || !TryCanonicalGuid(key.id, out var keyId) || !keyIds.Add(keyId) ||
                        !IsFiniteFloat(key.frame) || key.frame < 0 || key.frame <= previous || key.frame > data.durationFrames ||
                        !Bounded(key.x) || !Bounded(key.y) || !Bounded(key.rotationDegrees) ||
                        !ValidScale(key.scaleX) || !ValidScale(key.scaleY))
                        throw new InvalidOperationException("Layered sprite pose key is invalid.");
                    previous = key.frame;
                }
            }
        }

        private static bool IsStandardInput(string id) =>
            id == "position" || id == "rotation" || id == "scale" || id == "tint" ||
            id == "frame" || id == "columns" || id == "rows" || id == "fps" || id == "playing";

        private static void RequireInput(LiberaFxRenderNodePayload node, string id, int width)
        {
            var input = node.inputs.SingleOrDefault(value => value.id == id);
            if (input == null || input.width != width)
                throw new InvalidOperationException("Render node '" + node.nodeId + "' is missing " + id + " input.");
        }

        private static LiberaFxRenderInputPayload StaticInput(LiberaFxRenderNodePayload node, string id, int width)
        {
            var input = node.inputs.SingleOrDefault(value => value.id == id);
            if (input == null || input.width != width)
                throw new InvalidOperationException("Sprite node is missing " + id + " setting.");
            return input;
        }

        private static HashSet<string> ParseGpuContainerIds(byte[] bytes)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (bytes == null || bytes.Length == 0) return ids;
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("GPU authored snapshot exceeds 4 MiB.");
            var json = Encoding.UTF8.GetString(bytes);
            LiberaFxGpuContainerSnapshotList wrapper;
            try { wrapper = JsonUtility.FromJson<LiberaFxGpuContainerSnapshotList>("{\"items\":" + json + "}"); }
            catch (Exception error) { throw new InvalidOperationException("GPU authored snapshot JSON is malformed.", error); }
            if (wrapper == null || wrapper.items == null || wrapper.items.Length > 16)
                throw new InvalidOperationException("GPU authored snapshot count is invalid.");
            foreach (var snapshot in wrapper.items)
                if (snapshot == null || !TryCanonicalGuid(snapshot.id, out var id) || !ids.Add(id))
                    throw new InvalidOperationException("GPU authored snapshot has an invalid or duplicate node GUID.");
            return ids;
        }

        internal static bool HasGpuContainers(byte[] bytes) => ParseGpuContainerIds(bytes).Count != 0;

        private LiberaFxGpuManifestProgram[] ParseGpuPrograms(byte[] bytes, HashSet<string> authoredIds,
            LiberaFxGpuDrawTransformPayload[] drawTransforms, out LiberaFxFluidManifestProgram[] fluids,
            out LiberaFxGeneratedRenderTexturePayload[] generatedDrawBindings)
        {
            fluids = Array.Empty<LiberaFxFluidManifestProgram>();
            generatedDrawBindings = Array.Empty<LiberaFxGeneratedRenderTexturePayload>();
            var transformIds = new HashSet<string>(
                (drawTransforms ?? Array.Empty<LiberaFxGpuDrawTransformPayload>()).Select(transform => transform.nodeId),
                StringComparer.Ordinal);
            if (transformIds.Any(id => !authoredIds.Contains(id)))
                throw new InvalidOperationException("GPU draw Matrix binding does not match an authored GPU container.");
            if (bytes == null || bytes.Length == 0)
            {
                if (authoredIds.Count != 0)
                    throw new InvalidOperationException("GPU authored nodes have no executable GpuPrograms payload.");
                return Array.Empty<LiberaFxGpuManifestProgram>();
            }
            if (bytes.Length > 16 * 1024 * 1024)
                throw new InvalidOperationException("GPU executable payload exceeds 16 MiB.");
            LiberaFxGpuManifestPayload manifest;
            var json = Encoding.UTF8.GetString(bytes);
            json=System.Text.RegularExpressions.Regex.Replace(json,@"""spriteAnimation""\s*:\s*\{",
                "\"__liberafxHasSpriteAnimation\":true,\"spriteAnimation\":{",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
            try { manifest = JsonUtility.FromJson<LiberaFxGpuManifestPayload>(json); }
            catch (Exception error) { throw new InvalidOperationException("GPU executable payload JSON is malformed.", error); }
            if (manifest == null || (manifest.version != 1 && manifest.version != 2 && manifest.version != 3 && manifest.version != 4) ||
                manifest.programs == null || manifest.programs.Length > 16)
                throw new InvalidOperationException("GPU executable payload version or program count is invalid.");
            if (manifest.version == 1 && manifest.fluidPrograms != null && manifest.fluidPrograms.Length != 0)
                throw new InvalidOperationException("Fluid programs require GPU executable payload version 2.");
            if (manifest.version >= 2)
            {
                var resources = LibraFluid2DManifestValidation.Read(json);
                generatedDrawBindings = resources.drawBindings;
                foreach (var program in manifest.programs)
                    program.generatedResources = resources.generated.TryGetValue(CanonicalGuid(program.nodeId), out var generated)
                        ? generated : Array.Empty<LiberaFxGeneratedGpuResource>();
                fluids = manifest.fluidPrograms;
                if (fluids == null || (fluids.Length == 0 && manifest.version!=4) || fluids.Length + manifest.programs.Length > 16)
                    throw new InvalidOperationException("GPU fluid program count is invalid.");
                foreach (var fluid in fluids)
                {
                    fluid.automaticPlane = resources.automaticPlanes[CanonicalGuid(fluid.nodeId)];
                    if (fluid.settings == null) throw new InvalidOperationException("Missing fluid settings.");
                    fluid.settings.ApplyModel(resources.models[CanonicalGuid(fluid.nodeId)]);
                }
            }
            var programs = new Dictionary<string, LiberaFxGpuManifestProgram>(StringComparer.Ordinal);
            foreach (var program in manifest.programs)
            {
                if (program == null || !TryCanonicalGuid(program.nodeId, out var id) ||
                    programs.ContainsKey(id))
                    throw new InvalidOperationException("GPU executable payload has an invalid or duplicate node GUID.");
                program.nodeId = id;
                programs.Add(id, program);
            }
            var ids = new HashSet<string>(programs.Keys, StringComparer.Ordinal);
            foreach (var fluid in fluids)
            {
                if (fluid == null || !TryCanonicalGuid(fluid.nodeId, out var id) || !ids.Add(id) ||
                    fluid.settings == null || fluid.renderer == null || fluid.renderer.material != LibraFluid2DSettings.MaterialFor(fluid.settings.ModelId) ||
                    float.IsNaN(fluid.renderer.sizeScale) || float.IsInfinity(fluid.renderer.sizeScale) ||
                    fluid.renderer.sizeScale <= 0 || fluid.renderer.sizeScale > 10000)
                    throw new InvalidOperationException("GPU fluid program is invalid or duplicated.");
                fluid.nodeId = id;
                fluid.settings.Validate();
            }
            if (ids.Count != authoredIds.Count || authoredIds.Any(id => !ids.Contains(id)))
                throw new InvalidOperationException("GPU authored node IDs do not match compiled executable programs.");
            if (fluids.Length != 0) LibraFluid2DInstance.ValidateAssets();
            return programs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
        }

        private static void ValidateUnityAssociation(int association, int index)
        {
            // Unity MaterialPropertyBlock addresses shader properties by name only;
            // Unreal Layer/Blend association/index semantics have no Unity equivalent.
            if (association != 2 || index != -1)
                throw new InvalidOperationException("Unity supports only global material parameters (association 2, index -1).");
        }

        internal static bool IsFiniteFloat(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) &&
            value >= -float.MaxValue && value <= float.MaxValue;

        private static string HashUtf8(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static bool TryCanonicalGuid(string value, out string canonical)
        {
            canonical = null;
            if (!Guid.TryParse(value, out var guid)) return false;
            canonical = guid.ToString("D").ToLowerInvariant();
            return true;
        }

        private static string CanonicalGuid(string value)
        {
            if (!TryCanonicalGuid(value, out var canonical))
                throw new InvalidOperationException("Invalid GPU node GUID.");
            return canonical;
        }
    }
}
