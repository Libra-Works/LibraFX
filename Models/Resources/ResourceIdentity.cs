using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Models.Resources;

// These identifiers are allocated explicitly, never derived from a path, name, or connection.
public sealed record ResourceDefinition(Guid Id, string Kind, string Name,
    IReadOnlyList<MaterialParameterDefinition>? Parameters = null);
public sealed record MaterialParameterDefinition(Guid Id, string Kind, string Name,
    JsonElement Default, JsonElement? Minimum = null, JsonElement? Maximum = null);
public sealed record EngineProjectIdentity(string Engine, Guid ProjectId);
public sealed record OpaqueBinding(string Format, string Value);
public sealed record ProjectResourceBinding(EngineProjectIdentity Project, Guid ResourceId,
    string Kind, OpaqueBinding Binding);
public sealed record ProjectParameterBinding(EngineProjectIdentity Project, Guid ResourceId,
    Guid ParameterId, string Kind, OpaqueBinding Binding);

// Explicit opt-in: absence means the original graph value remains a legacy value.
// InstancePath is the ordered chain of containing Group/Spawnable node IDs. Older
// documents omit it and remain root-scoped; node IDs alone are not globally unique
// across reusable nested group instances.
public sealed record ResourceUse(Guid NodeId, string Location, Guid ResourceId,
    IReadOnlyList<Guid>? InstancePath = null);
public sealed record ParameterUse(Guid NodeId, string PinId, Guid ResourceId, Guid ParameterId,
    IReadOnlyList<Guid>? InstancePath = null);
public sealed record ResourceIdentityDocument(int Version, IReadOnlyList<ResourceDefinition> Resources,
    IReadOnlyList<ProjectResourceBinding> ResourceBindings,
    IReadOnlyList<ProjectParameterBinding> ParameterBindings,
    IReadOnlyList<ResourceUse> Uses, IReadOnlyList<ParameterUse> ParameterUses);
public sealed record EngineProjectConnectionState(EngineProjectIdentity? Project, string ProjectState,
    string Status, string? Detail = null);
public sealed record EngineMaterialParameterMetadata(string Name, string Kind, JsonElement Default,
    JsonElement? Minimum, JsonElement? Maximum, int Association, int Index);
public sealed record UnityRuntimeParameterBindingDelivery(Guid ParameterId, string Name, string Kind,
    OpaqueBinding Binding);
public sealed record UnityRuntimeBindingDelivery(Guid ResourceId, string Kind, OpaqueBinding Binding,
    IReadOnlyList<UnityRuntimeParameterBindingDelivery> Parameters);

public enum ResourceResolutionStatus { Resolved, Unregistered, Offline, Missing, QueryFailure, TypeMismatch }
public sealed record ResourceProbeResult(ResourceResolutionStatus Status, string? ActualKind = null, string? Detail = null);
public sealed record ResourceResolution(ResourceResolutionStatus Status, OpaqueBinding? Binding = null, string? Detail = null);

// A probe must query the explicitly selected project, not the current editor connection's session ID.
public interface IResourceBindingProbe
{
    bool IsOnline(EngineProjectIdentity project);
    ResourceProbeResult QueryResource(EngineProjectIdentity project, OpaqueBinding binding);
    ResourceProbeResult QueryParameter(EngineProjectIdentity project, OpaqueBinding resourceBinding, OpaqueBinding parameterBinding);
}

public static class ResourceIdentityStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ResourceIdentityDocument Read(Stream stream)
    {
        var document = JsonSerializer.Deserialize<ResourceIdentityDocument>(stream, Options)
            ?? throw new InvalidDataException("Missing resource identity document.");
        Validate(document);
        return document;
    }

    public static void Write(Stream stream, ResourceIdentityDocument document)
    {
        Validate(document);
        JsonSerializer.Serialize(stream, document, Options);
    }

    public static bool ContentEquals(ResourceIdentityDocument left, ResourceIdentityDocument right)
    {
        Validate(left);
        Validate(right);
        return JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);
    }

    public static void Validate(ResourceIdentityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != 1 || document.Resources is null || document.ResourceBindings is null ||
            document.ParameterBindings is null || document.Uses is null || document.ParameterUses is null)
            throw new InvalidDataException("Unsupported or incomplete resource identity document.");
        static void Id(Guid id) { if (id == Guid.Empty) throw new InvalidDataException("An identity is empty."); }
        static void Text(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
                throw new InvalidDataException("An identity field is invalid.");
        }
        static void Project(EngineProjectIdentity project)
        {
            if (project is null) throw new InvalidDataException("Project identity is missing.");
            Text(project.Engine); Id(project.ProjectId);
        }
        static void Binding(OpaqueBinding binding)
        {
            if (binding is null) throw new InvalidDataException("Opaque binding is missing.");
            Text(binding.Format); Text(binding.Value);
        }
        static void Unique<T>(IEnumerable<T> keys)
        {
            var set = new HashSet<T>();
            foreach (var key in keys)
                if (!set.Add(key)) throw new InvalidDataException("Duplicate resource identity entry.");
        }

        foreach (var resource in document.Resources)
        {
            if (resource is null) throw new InvalidDataException("Missing resource.");
            Id(resource.Id); Text(resource.Kind); Text(resource.Name);
            if (resource.Kind != "material" && resource.Parameters is { Count: > 0 })
                throw new InvalidDataException("Only a material can declare material parameters.");
            foreach (var parameter in resource.Parameters ?? [])
            {
                if (parameter is null) throw new InvalidDataException("Missing material parameter.");
                Id(parameter.Id); Text(parameter.Kind); Text(parameter.Name);
                ValidateParameterValue(parameter.Kind, parameter.Default);
                if (parameter.Minimum is { } min) ValidateParameterValue(parameter.Kind, min);
                if (parameter.Maximum is { } max) ValidateParameterValue(parameter.Kind, max);
                if (parameter.Kind == "texture" && (parameter.Minimum is not null || parameter.Maximum is not null))
                    throw new InvalidDataException("Texture parameters cannot have numeric ranges.");
                var defaults = NumericLanes(parameter.Kind, parameter.Default);
                var minimum = parameter.Minimum is { } low ? NumericLanes(parameter.Kind, low) : null;
                var maximum = parameter.Maximum is { } high ? NumericLanes(parameter.Kind, high) : null;
                if (defaults is not null)
                    for (var i = 0; i < defaults.Length; i++)
                        if ((minimum is not null && defaults[i] < minimum[i]) ||
                            (maximum is not null && defaults[i] > maximum[i]) ||
                            (minimum is not null && maximum is not null && minimum[i] > maximum[i]))
                            throw new InvalidDataException("Material parameter default or range is invalid.");
            }
            Unique((resource.Parameters ?? []).Select(parameter => parameter.Id));
        }
        Unique(document.Resources.Select(resource => resource.Id));
        var resources = document.Resources.ToDictionary(resource => resource.Id);
        foreach (var item in document.ResourceBindings)
        {
            if (item is null) throw new InvalidDataException("Missing resource binding.");
            Project(item.Project); Id(item.ResourceId); Text(item.Kind); Binding(item.Binding);
            if (!resources.ContainsKey(item.ResourceId)) throw new InvalidDataException("Binding references an unknown resource.");
        }
        Unique(document.ResourceBindings.Select(item => (item.Project, item.ResourceId)));
        foreach (var item in document.ParameterBindings)
        {
            if (item is null) throw new InvalidDataException("Missing parameter binding.");
            Project(item.Project); Id(item.ResourceId); Id(item.ParameterId); Text(item.Kind); Binding(item.Binding);
            if (!resources.TryGetValue(item.ResourceId, out var resource) ||
                !(resource.Parameters ?? []).Any(parameter => parameter.Id == item.ParameterId))
                throw new InvalidDataException("Binding references an unknown material parameter.");
        }
        Unique(document.ParameterBindings.Select(item => (item.Project, item.ResourceId, item.ParameterId)));
        foreach (var use in document.Uses)
        {
            if (use is null) throw new InvalidDataException("Missing resource use.");
            Id(use.NodeId); Id(use.ResourceId); Text(use.Location);
            ResourceIdentityScope.Validate(use.InstancePath);
            if (!resources.ContainsKey(use.ResourceId)) throw new InvalidDataException("Use references an unknown resource.");
        }
        Unique(document.Uses.Select(use =>
            (ResourceIdentityScope.Key(use.InstancePath), use.NodeId, use.Location)));
        foreach (var use in document.ParameterUses)
        {
            if (use is null) throw new InvalidDataException("Missing parameter use.");
            Id(use.NodeId); Id(use.ResourceId); Id(use.ParameterId); Text(use.PinId);
            ResourceIdentityScope.Validate(use.InstancePath);
            if (!resources.TryGetValue(use.ResourceId, out var resource) ||
                !(resource.Parameters ?? []).Any(parameter => parameter.Id == use.ParameterId))
                throw new InvalidDataException("Use references an unknown material parameter.");
        }
        Unique(document.ParameterUses.Select(use =>
            (ResourceIdentityScope.Key(use.InstancePath), use.NodeId, use.PinId)));
    }

    private static void ValidateParameterValue(string kind, JsonElement value)
    {
        if (kind == "texture")
        {
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("Invalid texture parameter default.");
            return;
        }
        if (NumericLanes(kind, value) is null)
            throw new InvalidDataException("Unsupported material parameter kind.");
    }

    private static double[]? NumericLanes(string kind, JsonElement value)
    {
        if (kind is not ("scalar" or "vector")) return null;
        if (kind == "scalar")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new InvalidDataException("Invalid scalar parameter value.");
            return [number];
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
            throw new InvalidDataException("Invalid vector parameter value.");
        return value.EnumerateArray().Select(lane =>
        {
            if (lane.ValueKind != JsonValueKind.Number || !lane.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new InvalidDataException("Invalid vector parameter value.");
            return number;
        }).ToArray();
    }
}

public static class ResourceIdentityScope
{
    public const int MaximumDepth = 12;

    public static IReadOnlyList<Guid> Normalize(IReadOnlyList<Guid>? instancePath)
    {
        Validate(instancePath);
        return instancePath is { Count: > 0 } ? instancePath.ToArray() : Array.Empty<Guid>();
    }

    public static void Validate(IReadOnlyList<Guid>? instancePath)
    {
        if (instancePath is null) return;
        if (instancePath.Count > MaximumDepth)
            throw new InvalidDataException("Resource instance scope exceeds the graph nesting limit.");
        var seen = new HashSet<Guid>();
        foreach (var id in instancePath)
            if (id == Guid.Empty || !seen.Add(id))
                throw new InvalidDataException("Resource instance scope contains an empty or repeated Group ID.");
    }

    public static string Key(IReadOnlyList<Guid>? instancePath)
    {
        Validate(instancePath);
        return instancePath is { Count: > 0 }
            ? string.Join("/", instancePath.Select(id => id.ToString("N")))
            : string.Empty;
    }

    public static bool Equal(IReadOnlyList<Guid>? left, IReadOnlyList<Guid>? right) =>
        string.Equals(Key(left), Key(right), StringComparison.Ordinal);

    public static IReadOnlyList<Guid> Child(IReadOnlyList<Guid>? parentPath, Guid groupNodeId)
    {
        Validate(parentPath);
        if (groupNodeId == Guid.Empty || parentPath?.Contains(groupNodeId) == true)
            throw new InvalidDataException("Invalid containing Group ID in resource scope.");
        var result = parentPath is { Count: > 0 }
            ? parentPath.Append(groupNodeId).ToArray()
            : new[] { groupNodeId };
        Validate(result);
        return result;
    }
}
