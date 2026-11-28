using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Sprite;

namespace LiberaFx.Services.Compilation;

public static class RenderProgramBindings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static bool IsRenderNode(GraphNodeKind kind) => GraphNodeDefinition.All.Any(definition =>
        definition.Kind == kind && definition.TypeName is "ModelDraw" or "SpriteDraw" or "SpriteAnimation" or "SpriteComposition")
        || PreviewDebugDraw.IsDebugNode(kind) || FlexiblePlaneDrawSchema.IsNode(kind);

    internal static bool HasGpuDrawMatrix(GraphSnapshot graph)
    {
        var gpuIds = graph.Nodes.Where(node => GpuParticleEditorService.IsGpu(node.Kind)).Select(node => node.Id).ToHashSet();
        return graph.Connections.Any(edge => edge.TargetPortId is "drawMatrix" or "emitterMatrix" && gpuIds.Contains(edge.TargetNodeId)) ||
            graph.Nodes.Any(node => node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind) &&
                HasGpuDrawMatrix(child));
    }

    public static byte[] CreateScene(GraphSnapshot graph, long programSequence,
        IRenderResourceBindingContext? resourceContext = null, VfxCompileOptions? options = null) =>
        CreateSceneCore(graph, programSequence, resourceContext, options, allowSourceMetadata: false);

    internal static byte[] CreateProceduralScene(GraphSnapshot graph, long programSequence,
        IRenderResourceBindingContext? resourceContext = null, VfxCompileOptions? options = null) =>
        CreateSceneCore(graph, programSequence, resourceContext, options, allowSourceMetadata: false, allowProceduralScene: true);

    internal static byte[] CreateSceneForPackage(GraphSnapshot graph, long programSequence,
        IRenderResourceBindingContext? resourceContext = null, VfxCompileOptions? options = null) =>
        CreateSceneCore(graph, programSequence, resourceContext, options, allowSourceMetadata: true);

    private static byte[] CreateSceneCore(GraphSnapshot graph, long programSequence,
        IRenderResourceBindingContext? resourceContext, VfxCompileOptions? options, bool allowSourceMetadata,
        bool allowProceduralScene = false)
    {
        if (!allowSourceMetadata && CpuParticleCompilation.Contains(graph))
        {
            throw new NotSupportedException("CPU Particle rendering requires effect-package playback with matching updated SDK adapters.");
        }
        if (SkeletalPoseSchema.ContainsAuthoredSource(graph) ||
            !allowSourceMetadata && SkeletalPoseSchema.ContainsSource(graph))
            throw new NotSupportedException(
                "Desktop render_scene evaluation cannot resolve SOURCE skeletal poses; use Unity or Unreal Spawnable package playback.");
        if (options?.IncludePreviewDebugDraw != true) graph = PreviewDebugDraw.Strip(graph);
        var generatedTextures = GpuProgramDeliveryService.GeneratedDrawBindings(graph);
        if (!allowSourceMetadata && generatedTextures.Count != 0)
            throw new NotSupportedException("Generated draw textures require whole-effect package playback.");
        var generatedByNode = generatedTextures.ToLookup(binding => binding.NodeId);
        var mappedGenerated = generatedTextures.ToDictionary(binding => (binding.NodeId, binding.Slot, binding.Name, binding.MainTexture));
        var instanceScopes = CollectInstanceScopes(graph);
        graph = GroupExposedItems.Materialize(graph);
        graph = SkeletalPoseSchema.Lower(graph);
        if (GameIoSchema.HasCurrentCamera(graph))
        {
            graph = GameIoSchema.Lower(graph).Graph;
        }
        graph = GraphPolymorphism.Lower(GroupBoundaryLowering.Lower(graph));
        var nodes = Flatten(graph).ToDictionary(node => node.Id);
        var connections = Connections(graph).ToLookup(connection => connection.TargetNodeId);
        var proceduralResources = ProceduralProgramBindings.Resources(nodes, connections, instanceScopes);
        if (!allowSourceMetadata && !allowProceduralScene && proceduralResources.Length != 0)
        {
            throw new NotSupportedException("Procedural resources require whole-effect package playback with updated SDK adapters.");
        }
        bool IsWorldRotation(Guid id, HashSet<Guid> visiting)
        {
            if (!visiting.Add(id) || !nodes.TryGetValue(id, out var source)) return false;
            if (source.Kind.Value == "MatrixRuntimeDecomposeRotation") return true;
            if (source.Kind != GraphNodeKind.FloatX3) return false;
            var incoming = connections[id].ToArray();
            return incoming.Length == 1 && IsWorldRotation(incoming[0].SourceNodeId, visiting);
        }
        var renderNodes = new List<object>();
        var gpuDrawTransforms = new List<object>();
        var gpuEmitterTransforms = new List<object>();
        foreach (var node in nodes.Values.Where(node => GpuParticleEditorService.IsGpu(node.Kind)))
        {
            var emitterBindings = connections[node.Id].Where(edge => edge.TargetPortId?.StartsWith("emitterRow", StringComparison.Ordinal) == true).ToArray();
            if (emitterBindings.Length != 0)
            {
                if (emitterBindings.Length != 4) throw new InvalidDataException("Emitter Matrix requires four rows.");
                gpuEmitterTransforms.Add(new { nodeId = node.Id, rowNodeIds = Enumerable.Range(0, 4)
                    .Select(row => emitterBindings.Single(edge => edge.TargetPortId == $"emitterRow{row}").SourceNodeId).ToArray() });
            }
            var bindings = connections[node.Id].Where(edge =>
                edge.TargetPortId?.StartsWith("drawRow", StringComparison.Ordinal) == true).ToArray();
            if (bindings.Length == 0)
            {
                continue;
            }
            if (bindings.Length != 4)
            {
                throw new InvalidDataException($"GPU Draw Matrix on node '{node.Id}' does not have four row bindings.");
            }
            var rowNodeIds = Enumerable.Range(0, 4)
                .Select(row => bindings.Single(edge => edge.TargetPortId == $"drawRow{row}").SourceNodeId)
                .ToArray();
            gpuDrawTransforms.Add(new { nodeId = node.Id, rowNodeIds });
        }
        var drawIds = nodes.Keys.ToHashSet();
        foreach (var node in nodes.Values.Where(node => IsRenderNode(node.Kind)))
        {
            var localPath = instanceScopes.GetValueOrDefault(node.Id) ?? Array.Empty<Guid>();
            var instancePath = resourceContext?.GetInstancePath(node.Id, localPath) ?? localPath;
            if (GraphNodeDefinition.All.Single(d => d.Kind == node.Kind).TypeName == "ModelDraw")
            {
                var model = ProceduralProgramBindings.Source(node, "proceduralModel", "ProceduralModel", nodes, connections, instanceScopes);
                var material = ProceduralProgramBindings.Source(node, "proceduralMaterial", "ProceduralMaterial", nodes, connections, instanceScopes);
                if (model is { } modelId)
                {
                    if (renderNodes.Count >= 128) throw new InvalidDataException("A scope supports at most 128 draws.");
                    renderNodes.Add(ProceduralProgramBindings.Draw(node, modelId, material, nodes, connections));
                    continue;
                }
                if (material is not null)
                    throw new NotSupportedException("A procedural material currently requires a Procedural Model input.");
            }
            if (FlexiblePlaneDrawSchema.IsNode(node.Kind))
            {
                if (renderNodes.Count >= 128)
                {
                    throw new InvalidDataException("A render scope supports at most 128 draws.");
                }
                renderNodes.Add(FlexiblePlaneDrawSchema.CreateDraw(node, nodes, connections, resourceContext, instancePath,
                    generatedByNode[node.Id].Any(binding => binding.MainTexture), instanceScopes));
                continue;
            }
            if (PreviewDebugDraw.IsDebugNode(node.Kind))
            {
                if (!PreviewDebugDraw.Enabled(node)) continue;
                if (renderNodes.Count >= 128) throw new InvalidDataException("A render scope supports at most 128 draws.");
                renderNodes.Add(PreviewDebugDraw.CreateDraw(node, nodes, connections, resourceContext, instancePath));
                continue;
            }
            var definition = RenderNodeSchema.Effective(GraphNodeDefinition.All.Single(item => item.Kind == node.Kind), node.Parameters);
            JsonElement Parameter(string id)
            {
                var declared = definition.Parameters.Single(item => item.Id == id);
                return node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == declared.Type)?.Value
                    ?? declared.DefaultValue;
            }
            var compositionNode = definition.TypeName == "SpriteComposition";
            var sprite = compositionNode || definition.TypeName is "SpriteDraw" or "SpriteAnimation";
            var generatedMain = generatedByNode[node.Id].Any(binding => binding.MainTexture);
            var path = generatedMain ? "builtin:generated-texture" : Parameter("assetPath").GetString() ?? "";
            if (!generatedMain) path = resourceContext?.ResolvePath(node.Id, "assetPath", sprite ? "texture" : "model", path, instancePath) ?? path;
            var materialEntries = RenderNodeSchema.Read(definition, node.Parameters);
            var materialSlots = RenderNodeSchema.ReadMaterials(definition, node.Parameters);
            resourceContext?.ValidateRenderNode(node.Id,
                materialEntries.Select(entry => (entry.Id, entry.Kind, entry.Slot)), instancePath,
                materialSlots.Select(slot => slot.Slot).ToArray());
            if (path.Length == 0)
            {
                if (sprite) throw new InvalidDataException($"Sprite node '{node.Id}' has no bound texture resource.");
                if (resourceContext is not null && (materialSlots.Any(slot =>
                    resourceContext.HasResourceUse(node.Id, $"material:{slot.Slot}", instancePath)) ||
                    materialEntries.Any(entry => entry.Kind == "texture" &&
                        resourceContext.HasResourceUse(node.Id, "texture:" + entry.Id, instancePath))))
                    throw new InvalidDataException($"Model node '{node.Id}' has mapped materials or textures but no model.");
                continue;
            }
            if (!generatedMain && (resourceContext is null ||
                !resourceContext.HasResourceUse(node.Id, "assetPath", instancePath))
                ) EngineAssetPath.Validate(path);
            var composition = compositionNode
                ? SpriteCompositionData.Deserialize(Parameter("compositionData")) ??
                    throw new InvalidDataException($"Sprite composition '{node.Id}' has no composition data.")
                : null;
            var spriteData = sprite && !compositionNode ? SpriteAnimationData.Deserialize(Parameter("spriteData")) : null;
            var spriteCellIndexes = spriteData?.Cells
                .Select((cell, index) => (Id: cell.Id, Index: index))
                .ToDictionary(item => item.Id, item => item.Index);
            var spriteAnimation = spriteData is null ? null : new
            {
                version = spriteData.Version,
                textureWidth = spriteData.TextureWidth,
                textureHeight = spriteData.TextureHeight,
                fps = spriteData.Fps,
                startFrame = spriteData.StartFrame,
                endFrame = spriteData.EndFrame,
                loop = spriteData.Loop,
                cells = spriteData.Cells.Select(cell =>
                {
                    var uv = spriteData.UvRect(cell);
                    return new
                    {
                        id = cell.Id.ToString("D"), name = cell.Name,
                        x = cell.X, y = cell.Y, width = cell.Width, height = cell.Height,
                        u = uv[0], v = uv[1], uvWidth = uv[2], uvHeight = uv[3]
                    };
                }).ToArray(),
                frames = spriteData.Frames.Select(frame => new
                {
                    id = frame.Id.ToString("D"), cellId = frame.CellId.ToString("D"),
                    cellIndex = spriteCellIndexes![frame.CellId],
                    durationFrames = frame.DurationFrames
                }).ToArray()
            };
            var mappedParameters = materialEntries.ToDictionary(entry => entry.Id, entry =>
                resourceContext?.ResolveParameter(node.Id, entry.Id, entry.Slot, entry.Kind, instancePath));
            foreach (var binding in generatedByNode[node.Id].Where(binding => !binding.MainTexture))
            {
                var entry = materialEntries.Single(entry => entry.Kind == "texture" && entry.Slot == binding.Slot && entry.Name == binding.Name);
                var mapped = mappedParameters[entry.Id];
                mappedGenerated[(binding.NodeId, binding.Slot, binding.Name, binding.MainTexture)] = binding with
                {
                    Name = mapped?.Name ?? entry.Name, Association = mapped?.Association ?? RenderNodeSchema.MaterialAssociation(entry),
                    Index = mapped?.Index ?? entry.Index
                };
            }
            object? MaterialParameter(string id)
            {
                var entry = materialEntries.FirstOrDefault(value => value.Id == id);
                if (entry is null) return null;
                var mapped = mappedParameters[entry.Id];
                return new
                {
                    name = mapped?.Name ?? entry.Name, kind = entry.Kind, slot = entry.Slot,
                    association = mapped?.Association ?? RenderNodeSchema.MaterialAssociation(entry), index = mapped?.Index ?? entry.Index
                };
            }
            var inputs = new List<object>();
            foreach (var port in definition.InputPorts ?? [])
            {
                var type = port.PortType ?? definition.PortType;
                var width = type switch { "Float" => 1, "FloatX2" => 2, "FloatX3" => 3, "FloatX4" or "Color" => 4, _ => 0 };
                if (width == 0) continue;
                var matches = connections[node.Id].Where(connection => connection.TargetPortId == port.Id).ToArray();
                if (matches.Length > 1) throw new InvalidDataException("A render pin has multiple sources.");
                Guid? sourceId = null;
                var component = 0;
                if (matches.Length == 1)
                {
                    var connection = matches[0];
                    if (!nodes.TryGetValue(connection.SourceNodeId, out var source) || IsRenderNode(source.Kind))
                        throw new InvalidDataException("Render inputs require numeric runtime sources.");
                    sourceId = source.Id;
                    var sourceDefinition = GraphNodeDefinition.All.Single(item => item.Kind == source.Kind);
                    if (sourceDefinition.TypeName.StartsWith("BreakVector", StringComparison.Ordinal))
                    {
                        component = sourceDefinition.OutputPorts!.ToList().FindIndex(output => output.Id == connection.SourcePortId);
                        if (component < 0) throw new InvalidDataException("Invalid render source component.");
                    }
                }
                var value = Parameter(port.Id);
                var defaults = width == 1 ? new[] { value.GetDouble() } : value.EnumerateArray().Select(lane => lane.GetDouble()).ToArray();
                inputs.Add(new { id = port.Id, width, sourceNodeId = sourceId, sourceComponent = component,
                    defaults, materialParameter = MaterialParameter(port.Id) });
            }
            var worldRotation = connections[node.Id].Any(edge => edge.TargetPortId == "rotation" &&
                IsWorldRotation(edge.SourceNodeId, []));
            var billboard = sprite && !compositionNode && Parameter("billboard").GetBoolean();
            if (worldRotation && billboard)
                throw new InvalidDataException("Disable renderer Billboard when supplying a decomposed world rotation; automatic Billboard would rotate it twice.");
            if (composition is not null)
            {
                // Keep the shared sprite input schema. V2 samples pose and frame
                // from the composition clock instead of these compatibility defaults.
                AddDefault("position", 0, 0, 0);
                AddDefault("rotation", 0, 0, 0);
                AddDefault("scale", 1, 1, 1);
                AddDefault("frame", -1);
                AddDefault("columns", 1);
                AddDefault("rows", 1);
                AddDefault("fps", composition.Fps);
                AddDefault("playing", 1);
            }
            else if (sprite)
            {
                foreach (var id in new[] { "columns", "rows", "fps", "playing" })
                {
                    var value = Parameter(id);
                    inputs.Add(new { id, width = 1, sourceNodeId = (Guid?)null, sourceComponent = 0,
                        defaults = new[] { value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() ? 1d : 0d : value.GetDouble() },
                        materialParameter = (object?)null });
                }
                if (spriteData is null &&
                    (Parameter("columns").GetInt32() < 1 || Parameter("rows").GetInt32() < 1 ||
                     Parameter("fps").GetDouble() < 0))
                    throw new InvalidDataException("Invalid legacy sprite atlas settings.");
            }
            void AddDefault(string id, params double[] defaults) => inputs.Add(new
            {
                id, width = defaults.Length, sourceNodeId = (Guid?)null, sourceComponent = 0,
                defaults, materialParameter = (object?)null
            });
            var textures = materialEntries.Where(entry => entry.Kind == "texture")
                .Select(entry => new { name = mappedParameters[entry.Id]?.Name ?? entry.Name, slot = entry.Slot,
                    path = generatedByNode[node.Id].Any(binding => !binding.MainTexture && binding.Slot == entry.Slot && binding.Name == entry.Name) ? "" : resourceContext?.ResolvePath(node.Id, "texture:" + entry.Id, "texture",
                        Parameter(entry.Id).GetString() ?? "", instancePath) ?? Parameter(entry.Id).GetString(),
                    association = mappedParameters[entry.Id]?.Association ?? RenderNodeSchema.MaterialAssociation(entry),
                    index = mappedParameters[entry.Id]?.Index ?? entry.Index }).ToArray();
            var materials = materialSlots
                .Where(slot => !string.IsNullOrEmpty(slot.Path) ||
                    resourceContext?.HasResourceUse(node.Id, $"material:{slot.Slot}", instancePath) == true)
                .Select(slot =>
                {
                    var materialPath = resourceContext?.ResolvePath(node.Id, $"material:{slot.Slot}", "material",
                        slot.Path, instancePath) ?? slot.Path;
                    if (sprite && materialPath == "builtin:unlit-color")
                        throw new InvalidDataException("The builtin color material is only supported for models.");
                    return new { slot = slot.Slot, path = materialPath };
                }).ToArray();
            void AddDraw(Guid id, object? animation)
            {
                if (renderNodes.Count >= 128) throw new InvalidDataException("A render scope supports at most 128 draws, including sprite parts.");
                renderNodes.Add(new { nodeId = id, kind = sprite ? "sprite" : "model", assetPath = path,
                    materials, inputs, textures, spriteAnimation = animation, billboard,
                    rotationConvention = worldRotation ? "liberafx-world" : null });
            }
            if (composition is null) AddDraw(node.Id, spriteAnimation);
            else
            {
                var duration = composition.DurationFrames;
                for (var order = 0; order < composition.Tracks.Count; order++)
                {
                    var track = composition.Tracks[order];
                    if (!track.Visible) continue;
                    var id = PartDrawId(node.Id, track.Id);
                    if (!drawIds.Add(id)) throw new InvalidDataException("Duplicate sprite part draw identity.");
                    var usedCells = track.Frames.Select(frame => frame.CellId).ToHashSet();
                    var cells = composition.Cells.Where(cell => usedCells.Contains(cell.Id)).ToArray();
                    var indexes = cells.Select((cell, index) => (cell.Id, index)).ToDictionary(item => item.Id, item => item.index);
                    AddDraw(id, new
                    {
                        version = 2,
                        textureWidth = composition.TextureWidth, textureHeight = composition.TextureHeight,
                        fps = composition.Fps, loop = composition.Loop, durationFrames = duration, drawOrder = order,
                        startFrame = 0, endFrame = track.Frames.Count - 1,
                        cells = cells.Select(cell => new
                        {
                            cell.Id, cell.Name, cell.X, cell.Y, cell.Width, cell.Height,
                            u = (double)cell.X / composition.TextureWidth, v = (double)cell.Y / composition.TextureHeight,
                            uvWidth = (double)cell.Width / composition.TextureWidth, uvHeight = (double)cell.Height / composition.TextureHeight
                        }),
                        frames = track.Frames.Select(frame => new { frame.Id, frame.CellId,
                            cellIndex = indexes[frame.CellId], frame.DurationFrames }),
                        keys = track.Keys
                    });
                }
            }
            }
            var bytes = gpuDrawTransforms.Count == 0
                ? JsonSerializer.SerializeToUtf8Bytes(
                    new { type = "render_config", programSequence, nodes = renderNodes }, JsonOptions)
                : JsonSerializer.SerializeToUtf8Bytes(
                    new { type = "render_config", programSequence, nodes = renderNodes, gpuDrawTransforms }, JsonOptions);
            if (gpuEmitterTransforms.Count != 0)
            {
                var document = JsonNode.Parse(bytes)!;
                document["gpuEmitterTransforms"] = JsonSerializer.SerializeToNode(gpuEmitterTransforms, JsonOptions);
                bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            }
            if (proceduralResources.Length != 0)
            {
                var document = JsonNode.Parse(bytes)!;
                document["proceduralVersion"] = 1;
                document["proceduralResources"] = JsonSerializer.SerializeToNode(proceduralResources, JsonOptions);
                bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            }
            if (generatedTextures.Count != 0)
            {
                var document = JsonNode.Parse(bytes)!;
                foreach (var draw in document["nodes"]!.AsArray())
                {
                    var id = draw!["nodeId"]!.GetValue<Guid>();
                    var bindings = generatedByNode[id].Select(binding => mappedGenerated[(binding.NodeId, binding.Slot, binding.Name, binding.MainTexture)]).ToArray();
                    if (bindings.Length == 0) continue;
                    draw["generatedTextures"] = JsonSerializer.SerializeToNode(bindings, JsonOptions);
                }
                bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            }
            if (bytes.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Render configuration exceeds the 4 MiB engine payload limit.");
            return bytes;
        }

        private static Guid PartDrawId(Guid nodeId, Guid partId)
        {
            // Node UUIDs are document-unique, and graph instantiation/paste remap
            // them. Resource delivery or graph lowering must not change draw IDs.
            var identity = $"LiberaFx.SpriteComposition.Draw.v1/{nodeId:D}/{partId:D}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash.AsSpan(0, 16), bigEndian: true);
        }
    private static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> CollectInstanceScopes(GraphSnapshot graph)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        void Visit(GraphSnapshot current, IReadOnlyList<Guid> scope)
        {
            foreach (var node in current.Nodes)
            {
                if (!result.TryAdd(node.Id, scope))
                    throw new InvalidDataException($"Graph contains duplicate node ID '{node.Id}'.");
                if (node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind) &&
                    !CpuParticleSchema.HasChild(node.Kind) &&
                    node.Kind != GraphNodeKind.SpawnableGroup)
                    Visit(child, ResourceIdentityScope.Child(scope, node.Id));
            }
        }
        Visit(graph, Array.Empty<Guid>());
        return result;
    }

    private static IEnumerable<GraphNodeSnapshot> Flatten(GraphSnapshot graph) => graph.Nodes.SelectMany(node =>
        node.Kind == GraphNodeKind.SpawnableGroup ? Array.Empty<GraphNodeSnapshot>() :
        !GpuParticleEditorService.IsGpu(node.Kind) && !CpuParticleSchema.HasChild(node.Kind) &&
        node.ChildGraph is { } child ? Flatten(child) : new[] { node });
    private static IEnumerable<GraphConnectionSnapshot> Connections(GraphSnapshot graph) =>
        graph.Connections.Concat(graph.Nodes.Where(node => node.ChildGraph is not null &&
            !GpuParticleEditorService.IsGpu(node.Kind) && !CpuParticleSchema.HasChild(node.Kind) &&
            node.Kind != GraphNodeKind.SpawnableGroup).SelectMany(node => Connections(node.ChildGraph!)));
}
