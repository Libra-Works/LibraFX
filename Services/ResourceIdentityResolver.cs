using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;

namespace LiberaFx.Services;

public sealed class ResourceIdentityResolver
{
    private readonly ResourceIdentityDocument _document;
    private readonly EngineProjectIdentity _project;
    private readonly IResourceBindingProbe _probe;

    public ResourceIdentityResolver(ResourceIdentityDocument document, EngineProjectIdentity project, IResourceBindingProbe probe)
    {
        ResourceIdentityStore.Validate(document);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(probe);
        if (project.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(project.Engine))
            throw new InvalidDataException("A stable engine project identity is required.");
        _document = document;
        _project = project;
        _probe = probe;
    }

    public ResourceIdentityDocument Document => _document;
    public EngineProjectIdentity Project => _project;

    public ResourceResolution ResolveResource(Guid resourceId, string expectedKind)
    {
        var resource = _document.Resources.SingleOrDefault(item => item.Id == resourceId);
        if (resource is null) return new(ResourceResolutionStatus.Unregistered, Detail: "Resource is not registered.");
        if (resource.Kind != expectedKind) return new(ResourceResolutionStatus.TypeMismatch, Detail: "Resource kind differs from the use.");
        var mapping = _document.ResourceBindings.SingleOrDefault(item => item.Project == _project && item.ResourceId == resourceId);
        if (mapping is null) return new(ResourceResolutionStatus.Unregistered, Detail: "Project resource mapping is not registered.");
        if (mapping.Kind != expectedKind) return new(ResourceResolutionStatus.TypeMismatch, Detail: "Project resource mapping has a different kind.");
        return Probe(mapping.Binding, expectedKind, () => _probe.QueryResource(_project, mapping.Binding));
    }

    public ResourceResolution ResolveParameter(Guid resourceId, Guid parameterId, string expectedKind)
    {
        var resource = _document.Resources.SingleOrDefault(item => item.Id == resourceId);
        var parameter = resource?.Parameters?.SingleOrDefault(item => item.Id == parameterId);
        if (resource is null || parameter is null)
            return new(ResourceResolutionStatus.Unregistered, Detail: "Material parameter is not registered.");
        if (resource.Kind != "material" || parameter.Kind != expectedKind)
            return new(ResourceResolutionStatus.TypeMismatch, Detail: "Material parameter kind differs from the use.");
        var material = ResolveResource(resourceId, "material");
        if (material.Status != ResourceResolutionStatus.Resolved) return material;
        var mapping = _document.ParameterBindings.SingleOrDefault(item =>
            item.Project == _project && item.ResourceId == resourceId && item.ParameterId == parameterId);
        if (mapping is null) return new(ResourceResolutionStatus.Unregistered, Detail: "Project parameter mapping is not registered.");
        if (mapping.Kind != expectedKind)
            return new(ResourceResolutionStatus.TypeMismatch, Detail: "Project parameter mapping has a different kind.");
        return Probe(mapping.Binding, expectedKind, () => _probe.QueryParameter(_project, material.Binding!, mapping.Binding));
    }

    private ResourceResolution Probe(OpaqueBinding binding, string expectedKind, Func<ResourceProbeResult> query)
    {
        try
        {
            if (!_probe.IsOnline(_project)) return new(ResourceResolutionStatus.Offline, Detail: "Engine project is offline.");
            var result = query();
            if (result is null) return new(ResourceResolutionStatus.QueryFailure, Detail: "Adapter returned no query result.");
            if (result.Status == ResourceResolutionStatus.Resolved)
                return result.ActualKind == expectedKind
                    ? new(ResourceResolutionStatus.Resolved, binding)
                    : new(ResourceResolutionStatus.TypeMismatch, Detail: "Adapter reported a different kind.");
            if (result.Status is ResourceResolutionStatus.Missing or ResourceResolutionStatus.QueryFailure or
                ResourceResolutionStatus.Offline or ResourceResolutionStatus.TypeMismatch)
                return new(result.Status, Detail: result.Detail);
            return new(ResourceResolutionStatus.QueryFailure, Detail: "Adapter returned an invalid query state.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(ResourceResolutionStatus.QueryFailure, Detail: exception.Message);
        }
    }
}

// Bridges explicit mappings into the existing Unreal render_config fields. It never rewrites the graph.
public sealed class ResourceIdentityRenderContext : IRenderResourceBindingContext
{
    internal sealed record UnityPreviewBindingSet(Guid ProjectId, IReadOnlyList<UnityRuntimeBindingDelivery> Resources);

    private readonly ResourceIdentityResolver resolver;
    private readonly bool requireMappings;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> instancePaths;

    public ResourceIdentityRenderContext(ResourceIdentityResolver resolver, bool requireMappings = false,
        GraphSnapshot? graph = null)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.requireMappings = requireMappings;
        instancePaths = graph is null ? new Dictionary<Guid, IReadOnlyList<Guid>>() : CollectInstancePaths(graph);
    }

    public IReadOnlyList<Guid> GetInstancePath(Guid nodeId) =>
        instancePaths.TryGetValue(nodeId, out var path) ? path : Array.Empty<Guid>();

    internal UnityPreviewBindingSet UnityPreviewBindings()
    {
        if (resolver.Project.Engine != "Unity")
        {
            throw new InvalidDataException("Unity preview requires a Unity project identity.");
        }
        var document = resolver.Document;
        var resources = document.Uses.Select(use => use.ResourceId).Distinct().Select(id =>
        {
            var resource = document.Resources.Single(item => item.Id == id);
            var binding = Require(resolver.ResolveResource(id, resource.Kind), id, "Unity preview");
            var parameters = document.ParameterUses.Where(use => use.ResourceId == id)
                .Select(use => use.ParameterId).Distinct().Select(parameterId =>
                {
                    var parameter = resource.Parameters!.Single(item => item.Id == parameterId);
                    return new UnityRuntimeParameterBindingDelivery(parameterId, parameter.Name, parameter.Kind,
                        Require(resolver.ResolveParameter(id, parameterId, parameter.Kind), id, parameter.Name));
                }).ToArray();
            return new UnityRuntimeBindingDelivery(id, resource.Kind, binding, parameters);
        }).ToArray();
        return new(resolver.Project.ProjectId, resources);
    }

    public bool HasResourceUse(Guid nodeId, string location) =>
        HasResourceUse(nodeId, location, Array.Empty<Guid>());

    public bool HasResourceUse(Guid nodeId, string location, IReadOnlyList<Guid> instancePath) =>
        resolver.Document.Uses.Any(item => item.NodeId == nodeId && item.Location == location &&
            ResourceIdentityScope.Equal(item.InstancePath, instancePath));

    public void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters)
        => ValidateRenderNode(nodeId, parameters, Array.Empty<Guid>());

    public void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters,
        IReadOnlyList<Guid> instancePath)
    {
        var entries = parameters.ToArray();
        ValidateRenderNode(nodeId, entries, instancePath, entries.Select(entry => entry.Slot).Distinct().ToArray());
    }

    public void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters,
        IReadOnlyList<Guid> instancePath, IReadOnlyList<int> materialSlots)
    {
        var entries = parameters.ToArray();
        var locations = new HashSet<string>(StringComparer.Ordinal) { "assetPath" };
        foreach (var slot in materialSlots) locations.Add($"material:{slot}");
        foreach (var entry in entries)
        {
            if (entry.Kind == "texture") locations.Add("texture:" + entry.Id);
        }
        var pins = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        if (resolver.Document.Uses.Any(use => use.NodeId == nodeId &&
                ResourceIdentityScope.Equal(use.InstancePath, instancePath) && !locations.Contains(use.Location)) ||
            resolver.Document.ParameterUses.Any(use => use.NodeId == nodeId &&
                ResourceIdentityScope.Equal(use.InstancePath, instancePath) && !pins.Contains(use.PinId)))
            throw new InvalidDataException($"Node {nodeId} has a resource mapping for a missing render location or material pin.");
        foreach (var use in resolver.Document.ParameterUses.Where(use => use.NodeId == nodeId &&
                     ResourceIdentityScope.Equal(use.InstancePath, instancePath)))
        {
            var entry = entries.Single(item => item.Id == use.PinId);
            if (!resolver.Document.Uses.Any(item => item.NodeId == nodeId &&
                    item.Location == $"material:{entry.Slot}" && item.ResourceId == use.ResourceId &&
                    ResourceIdentityScope.Equal(item.InstancePath, instancePath)))
                throw new InvalidDataException($"Material parameter '{use.PinId}' requires an explicit matching material mapping.");
        }
    }

    public string ResolvePath(Guid nodeId, string location, string kind, string legacyValue)
        => ResolvePath(nodeId, location, kind, legacyValue, Array.Empty<Guid>());

    public string ResolvePath(Guid nodeId, string location, string kind, string legacyValue,
        IReadOnlyList<Guid> instancePath)
    {
        var use = resolver.Document.Uses.SingleOrDefault(item => item.NodeId == nodeId &&
            item.Location == location && ResourceIdentityScope.Equal(item.InstancePath, instancePath));
        if (use is null)
        {
            if (requireMappings && (location == "assetPath" ||
                legacyValue.Length > 0 && legacyValue != "builtin:unlit-color"))
                throw new InvalidDataException($"Resource at node {nodeId}, '{location}' has no explicit portable mapping.");
            return legacyValue;
        }
        var binding = Require(resolver.ResolveResource(use.ResourceId, kind), nodeId, location);
        if (resolver.Project.Engine == "Unreal" && binding.Format == "unreal.object-path")
        {
            if (!binding.Value.StartsWith("/Game/", StringComparison.Ordinal) &&
                !binding.Value.StartsWith("/Engine/", StringComparison.Ordinal))
                throw new InvalidDataException($"Resource {use.ResourceId}: invalid Unreal object path.");
            EngineAssetPath.Validate(binding.Value);
        }
        else if (resolver.Project.Engine == "Unity" && binding.Format == "unity.asset-guid-local-id.v1")
            return NormalizeUnityAssetBinding(binding.Value);
        else if (resolver.Project.Engine == "Godot" && binding.Format == "godot.res-path.v1")
        {
            if (!binding.Value.StartsWith("res://", StringComparison.Ordinal))
                throw new InvalidDataException($"Resource {use.ResourceId}: invalid Godot res:// path.");
            EngineAssetPath.Validate(binding.Value);
        }
        else
            throw new InvalidDataException($"Resource {use.ResourceId}: render_config cannot deliver '{binding.Format}' for {resolver.Project.Engine}.");
        return binding.Value;
    }

    public (string Name, int Association, int Index)? ResolveParameter(
        Guid nodeId, string pinId, int slot, string kind)
        => ResolveParameter(nodeId, pinId, slot, kind, Array.Empty<Guid>());

    public (string Name, int Association, int Index)? ResolveParameter(
        Guid nodeId, string pinId, int slot, string kind, IReadOnlyList<Guid> instancePath)
    {
        var use = resolver.Document.ParameterUses.SingleOrDefault(item => item.NodeId == nodeId &&
            item.PinId == pinId && ResourceIdentityScope.Equal(item.InstancePath, instancePath));
        var materialUse = resolver.Document.Uses.SingleOrDefault(item =>
            item.NodeId == nodeId && item.Location == $"material:{slot}" &&
            ResourceIdentityScope.Equal(item.InstancePath, instancePath));
        if (use is null)
        {
            if (materialUse is not null || requireMappings)
                throw new InvalidDataException($"Material parameter '{pinId}' has no explicit parameter mapping.");
            return null;
        }
        if (materialUse is null || materialUse.ResourceId != use.ResourceId)
            throw new InvalidDataException($"Material parameter '{pinId}' requires the same explicitly mapped material in slot {slot}.");
        var binding = Require(resolver.ResolveParameter(use.ResourceId, use.ParameterId, kind), nodeId, pinId);
        if (resolver.Project.Engine == "Unreal" && binding.Format != "unreal.material-parameter.v1" ||
            resolver.Project.Engine == "Unity" && binding.Format != "unity.shader-property.v1" ||
            resolver.Project.Engine is not ("Unreal" or "Unity"))
            throw new InvalidDataException($"Material parameter {use.ParameterId}: unsupported render binding format '{binding.Format}'.");
        try
        {
            using var json = JsonDocument.Parse(binding.Value);
            var root = json.RootElement;
            var name = root.GetProperty("name").GetString();
            var association = root.GetProperty("association").GetInt32();
            var index = root.GetProperty("index").GetInt32();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) ||
                association < 0 || association > 4096 || index < -1 || index > 65535)
                throw new InvalidDataException("Invalid Unreal material parameter binding.");
            var renderName = resolver.Project.Engine == "Unity"
                ? resolver.Document.Resources.Single(item => item.Id == use.ResourceId)
                    .Parameters!.Single(item => item.Id == use.ParameterId).Name
                : name;
            return (renderName, association, index);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Invalid Unreal material parameter binding.", exception);
        }
    }

    private static string NormalizeUnityAssetBinding(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out var guid) || guid == Guid.Empty ||
            !long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var localId) || localId < 0)
            throw new InvalidDataException("Invalid Unity asset GUID/local-file-ID binding.");
        return guid.ToString("D") + ":" + localId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> CollectInstancePaths(GraphSnapshot graph)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        void Visit(GraphSnapshot current, IReadOnlyList<Guid> scope)
        {
            foreach (var node in current.Nodes)
            {
                if (!result.TryAdd(node.Id, scope))
                    throw new InvalidDataException($"Graph contains duplicate node ID '{node.Id}'.");
                if (node.ChildGraph is { } child)
                    Visit(child, ResourceIdentityScope.Child(scope, node.Id));
            }
        }
        Visit(graph, Array.Empty<Guid>());
        return result;
    }

    private static OpaqueBinding Require(ResourceResolution result, Guid nodeId, string location)
    {
        if (result.Status != ResourceResolutionStatus.Resolved || result.Binding is null)
            throw new InvalidDataException($"Resource at node {nodeId}, '{location}': {result.Status}: {result.Detail}");
        return result.Binding;
    }
}
