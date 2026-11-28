using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Gpu;

namespace LiberaFx.Services.Assets;

public sealed record EmbeddedResourceUse(string Location, Guid ResourceId);
public sealed record EmbeddedParameterUse(string PinId, Guid ResourceId, Guid ParameterId);
public sealed record EmbeddedResourceIdentities(int Version, IReadOnlyList<EmbeddedResourceUse> Uses,
    IReadOnlyList<EmbeddedParameterUse> ParameterUses);
public sealed record ExportedResourceUse(Guid NodeId, string Location, Guid ResourceId, string Kind,
    OpaqueBinding Binding, IReadOnlyList<Guid>? InstancePath = null);
public sealed record ExportedParameterUse(Guid NodeId, string PinId, Guid ResourceId, Guid ParameterId, string Kind,
    OpaqueBinding Binding, IReadOnlyList<Guid>? InstancePath = null);
public sealed record ResourceRenderManifest(int Version, EngineProjectIdentity Project, string Evidence,
    IReadOnlyList<ResourceDefinition> Resources, IReadOnlyList<ExportedResourceUse> Uses,
    IReadOnlyList<ExportedParameterUse> ParameterUses, byte[] Bytecode, JsonElement RenderConfiguration)
{
    public byte[]? SpawnablePackage { get; init; }
}

public static class ResourceIdentityExport
{
    public const string EmbeddedParameterId = "liberaResourceIdentities";
    public const string EmbeddedParameterType = "liberaResourceIdentities:v1";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static bool HasEmbeddedMetadata(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.Parameters?.Any(parameter => parameter.Id == EmbeddedParameterId) == true ||
        node.ChildGraph is { } child && HasEmbeddedMetadata(child));

    // Unknown parameter types are already preserved by the validated snapshot format.
    // This reads the metadata without changing node values, pins, edges, or the input document.
    public static ResourceIdentityDocument MergeEmbedded(GraphSnapshot graph, ResourceIdentityDocument document)
    {
        ResourceIdentityStore.Validate(document);
        var uses = document.Uses.ToList();
        var parameterUses = document.ParameterUses.ToList();
        void Visit(GraphSnapshot current, IReadOnlyList<Guid> instancePath)
        {
            foreach (var node in current.Nodes)
            {
                var stored = node.Parameters?.Where(item => item.Id == EmbeddedParameterId).ToArray() ?? [];
                if (stored.Length > 1 || stored.Length == 1 && stored[0].Type != EmbeddedParameterType)
                    throw new InvalidDataException($"Node {node.Id} has invalid resource identity metadata.");
                if (stored.Length == 1)
                {
                    var embedded = stored[0].Value.Deserialize<EmbeddedResourceIdentities>(Options)
                        ?? throw new InvalidDataException("Missing embedded resource identities.");
                    if (embedded.Version != 1 || embedded.Uses is null || embedded.ParameterUses is null)
                        throw new InvalidDataException("Invalid embedded resource identities.");
                    if (embedded.Uses.Any(item => item is null) || embedded.ParameterUses.Any(item => item is null) ||
                        embedded.Uses.Select(item => item.Location).Distinct(StringComparer.Ordinal).Count() != embedded.Uses.Count ||
                        embedded.ParameterUses.Select(item => item.PinId).Distinct(StringComparer.Ordinal).Count() != embedded.ParameterUses.Count)
                        throw new InvalidDataException("Duplicate or missing embedded resource identity entries.");
                    foreach (var item in embedded.Uses)
                    {
                        if (item is null) throw new InvalidDataException("Invalid embedded resource use.");
                        var use = new ResourceUse(node.Id, item.Location, item.ResourceId, instancePath);
                        var existing = uses.SingleOrDefault(value => value.NodeId == node.Id &&
                            value.Location == item.Location && ResourceIdentityScope.Equal(value.InstancePath, instancePath));
                        if (existing is null) uses.Add(use);
                        else if (existing.ResourceId != use.ResourceId)
                            throw new InvalidDataException("Embedded and external resource uses conflict.");
                    }
                    foreach (var item in embedded.ParameterUses)
                    {
                        if (item is null) throw new InvalidDataException("Invalid embedded parameter use.");
                        var use = new ParameterUse(node.Id, item.PinId, item.ResourceId, item.ParameterId, instancePath);
                        var existing = parameterUses.SingleOrDefault(value => value.NodeId == node.Id &&
                            value.PinId == item.PinId && ResourceIdentityScope.Equal(value.InstancePath, instancePath));
                        if (existing is null) parameterUses.Add(use);
                        else if (existing.ResourceId != use.ResourceId || existing.ParameterId != use.ParameterId)
                            throw new InvalidDataException("Embedded and external parameter uses conflict.");
                    }
                }
                if (node.ChildGraph is { } child)
                    Visit(child, ResourceIdentityScope.Child(instancePath, node.Id));
            }
        }
        Visit(graph, Array.Empty<Guid>());
        var merged = document with { Uses = uses.ToArray(), ParameterUses = parameterUses.ToArray() };
        ResourceIdentityStore.Validate(merged);
        return merged;
    }

    public static byte[] CompileMappedBinary(GraphSnapshot graph, ResourceIdentityDocument document,
        EngineProjectIdentity project, ResourceIdentitySnapshotProbe probe)
    {
        var (_, _, cleanedGraph, context) = PrepareContext(graph, document, project, probe);
        return EffectBinary.Compile(cleanedGraph, context);
    }

    public static byte[] CreateManifest(GraphSnapshot graph, ResourceIdentityDocument document,
        EngineProjectIdentity project, ResourceIdentitySnapshotProbe probe)
    {
        var (merged, resolver, cleanedGraph, context) = PrepareContext(graph, document, project, probe);
        graph = cleanedGraph;
        byte[] bytecode;
        byte[] renderBytes;
        byte[]? spawnablePackage = null;
        if (ContainsSpawnable(graph))
        {
            var package = SpawnableGroupPackage.Compile(graph, resourceContext: context);
            bytecode = package.ParentBytecode;
            renderBytes = package.RootRenderConfiguration;
            spawnablePackage = SpawnableGroupPackageBinary.Serialize(package);
        }
        else
        {
            bytecode = VfxProgram.Compile(graph, out renderBytes, context).ToBinary();
        }

        static OpaqueBinding Require(ResourceResolution result)
        {
            if (result.Status != ResourceResolutionStatus.Resolved || result.Binding is null)
                throw new InvalidDataException($"Resource resolution failed: {result.Status}: {result.Detail}");
            return result.Binding;
        }
        var resources = merged.Resources.ToDictionary(resource => resource.Id);
        var uses = merged.Uses.Select(use =>
        {
            var definition = resources[use.ResourceId];
            return new ExportedResourceUse(use.NodeId, use.Location, use.ResourceId, definition.Kind,
                Require(resolver.ResolveResource(use.ResourceId, definition.Kind)), ResourceIdentityScope.Normalize(use.InstancePath));
        }).ToArray();
        var parameters = merged.ParameterUses.Select(use =>
        {
            var definition = resources[use.ResourceId].Parameters!.Single(item => item.Id == use.ParameterId);
            return new ExportedParameterUse(use.NodeId, use.PinId, use.ResourceId, use.ParameterId, definition.Kind,
                Require(resolver.ResolveParameter(use.ResourceId, use.ParameterId, definition.Kind)),
                ResourceIdentityScope.Normalize(use.InstancePath));
        }).ToArray();
        var referencedIds = uses.Select(use => use.ResourceId).Concat(parameters.Select(use => use.ResourceId)).ToHashSet();
        using var render = JsonDocument.Parse(renderBytes);
        var manifest = new ResourceRenderManifest(1, project, "caller-supplied-snapshot-not-live",
            merged.Resources.Where(item => referencedIds.Contains(item.Id)).ToArray(), uses, parameters,
            bytecode, render.RootElement.Clone())
        {
            SpawnablePackage = spawnablePackage
        };
        return JsonSerializer.SerializeToUtf8Bytes(manifest, Options);
    }

    internal static Dictionary<(string Scope, Guid NodeId), GraphNodeSnapshot> ScopedNodes(GraphSnapshot graph)
    {
        var result = new Dictionary<(string Scope, Guid NodeId), GraphNodeSnapshot>();
        void Visit(GraphSnapshot current, IReadOnlyList<Guid> path)
        {
            foreach (var node in current.Nodes)
            {
                if (!result.TryAdd((ResourceIdentityScope.Key(path), node.Id), node))
                    throw new InvalidDataException($"Graph contains duplicate node ID '{node.Id}'.");
                if (node.ChildGraph is { } child)
                    Visit(child, ResourceIdentityScope.Child(path, node.Id));
            }
        }
        Visit(graph, Array.Empty<Guid>());
        return result;
    }

    private static (ResourceIdentityDocument Merged, ResourceIdentityResolver Resolver,
        GraphSnapshot CleanedGraph, ResourceIdentityRenderContext Context) PrepareContext(
        GraphSnapshot graph, ResourceIdentityDocument document,
        EngineProjectIdentity project, ResourceIdentitySnapshotProbe probe)
    {
        if (project.Engine is not ("Unreal" or "Unity" or "Godot"))
            throw new InvalidDataException("This render export supports Unreal, Unity, and Godot only.");
        if (probe.Project != project)
            throw new InvalidDataException("Binding snapshot belongs to a different engine project.");
        GraphValidator.ValidateDocument(graph);
        var merged = MergeEmbedded(graph, document);
        var debugKeys = ScopedNodes(graph).Where(item => PreviewDebugDraw.IsDebugNode(item.Value.Kind))
            .Select(item => item.Key).ToHashSet();
        if (merged.Uses.Count == 0 && debugKeys.Count == 0)
            throw new InvalidDataException("No explicit resource uses were provided.");
        graph = PreviewDebugDraw.Strip(graph);
        merged = merged with
        {
            Uses = merged.Uses.Where(use => !debugKeys.Contains(
                (ResourceIdentityScope.Key(use.InstancePath), use.NodeId))).ToArray(),
            ParameterUses = merged.ParameterUses.Where(use => !debugKeys.Contains(
                (ResourceIdentityScope.Key(use.InstancePath), use.NodeId))).ToArray()
        };
        static bool SupportsResourceBinding(GraphNodeKind kind) =>
            RenderProgramBindings.IsRenderNode(kind) ||
            CpuParticleSchema.Name(kind) == "CpuParticleSpriteDraw" ||
            GpuParticleEditorService.IsGpu(kind);
        var nodes = ScopedNodes(graph);
        foreach (var use in merged.Uses)
            if (!nodes.TryGetValue((ResourceIdentityScope.Key(use.InstancePath), use.NodeId), out var node) ||
                !SupportsResourceBinding(node.Kind))
                throw new InvalidDataException($"Resource mapping refers to a missing or unsupported render node {use.NodeId}.");
        foreach (var use in merged.ParameterUses)
            if (!nodes.TryGetValue((ResourceIdentityScope.Key(use.InstancePath), use.NodeId), out var node) ||
                !SupportsResourceBinding(node.Kind))
                throw new InvalidDataException($"Parameter mapping refers to a missing or unsupported render node {use.NodeId}.");
        var resolver = new ResourceIdentityResolver(merged, project, probe);
        var context = new ResourceIdentityRenderContext(resolver, requireMappings: true, graph: graph);
        return (merged, resolver, graph, context);
    }

    private static bool ContainsSpawnable(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.Kind == GraphNodeKind.SpawnableGroup ||
        node.ChildGraph is { } child && ContainsSpawnable(child));
}
