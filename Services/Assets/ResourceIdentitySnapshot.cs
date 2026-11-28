using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services.Assets;

// Explicit assertions from a caller-supplied snapshot, not a live engine query.
public sealed record ResourceSnapshotEntry([property: JsonRequired] OpaqueBinding Binding,
    [property: JsonRequired] ResourceResolutionStatus Status, string? ActualKind = null, string? Detail = null);
public sealed record ParameterSnapshotEntry([property: JsonRequired] OpaqueBinding ResourceBinding,
    [property: JsonRequired] OpaqueBinding ParameterBinding, [property: JsonRequired] ResourceResolutionStatus Status,
    string? ActualKind = null, string? Detail = null);
public sealed record ResourceBindingSnapshot([property: JsonRequired] int Version, [property: JsonRequired] EngineProjectIdentity Project,
    [property: JsonRequired] bool Available, [property: JsonRequired] IReadOnlyList<ResourceSnapshotEntry> Resources,
    [property: JsonRequired] IReadOnlyList<ParameterSnapshotEntry> Parameters);

public sealed class ResourceIdentitySnapshotProbe : IResourceBindingProbe
{
    private readonly ResourceBindingSnapshot _snapshot;
    public EngineProjectIdentity Project => _snapshot.Project;

    public ResourceIdentitySnapshotProbe(ResourceBindingSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Version != 1 || snapshot.Project is null ||
            snapshot.Project.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Project.Engine) ||
            snapshot.Resources is null || snapshot.Parameters is null)
            throw new InvalidDataException("Invalid binding snapshot header.");
        static void ValidateBinding(OpaqueBinding binding)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.Format) ||
                string.IsNullOrWhiteSpace(binding.Value) || binding.Format.Length > 4096 ||
                binding.Value.Length > 4096 || binding.Format.Any(char.IsControl) || binding.Value.Any(char.IsControl))
                throw new InvalidDataException("Invalid snapshot binding.");
        }
        static void ValidateResult(ResourceResolutionStatus status, string? kind)
        {
            if (status is not (ResourceResolutionStatus.Resolved or ResourceResolutionStatus.Missing or
                ResourceResolutionStatus.QueryFailure or ResourceResolutionStatus.TypeMismatch) ||
                status == ResourceResolutionStatus.Resolved && string.IsNullOrWhiteSpace(kind))
                throw new InvalidDataException("Invalid snapshot query result.");
        }
        var resources = new HashSet<OpaqueBinding>();
        foreach (var item in snapshot.Resources)
        {
            if (item is null) throw new InvalidDataException("Missing snapshot entry.");
            ValidateBinding(item.Binding); ValidateResult(item.Status, item.ActualKind);
            if (!resources.Add(item.Binding)) throw new InvalidDataException("Duplicate snapshot resource binding.");
        }
        var parameters = new HashSet<(OpaqueBinding, OpaqueBinding)>();
        foreach (var item in snapshot.Parameters)
        {
            if (item is null) throw new InvalidDataException("Missing snapshot entry.");
            ValidateBinding(item.ResourceBinding); ValidateBinding(item.ParameterBinding);
            ValidateResult(item.Status, item.ActualKind);
            if (!parameters.Add((item.ResourceBinding, item.ParameterBinding)))
                throw new InvalidDataException("Duplicate snapshot parameter binding.");
        }
        _snapshot = snapshot;
    }

    public static ResourceIdentitySnapshotProbe Read(Stream stream)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };
        var snapshot = JsonSerializer.Deserialize<ResourceBindingSnapshot>(stream, options)
            ?? throw new InvalidDataException("Missing binding snapshot.");
        return new(snapshot);
    }

    public bool IsOnline(EngineProjectIdentity project) => project == _snapshot.Project && _snapshot.Available;
    public ResourceProbeResult QueryResource(EngineProjectIdentity project, OpaqueBinding binding)
    {
        if (project != _snapshot.Project || !_snapshot.Available) return new(ResourceResolutionStatus.Offline);
        var entry = _snapshot.Resources.SingleOrDefault(item => item.Binding == binding);
        return entry is null ? new(ResourceResolutionStatus.QueryFailure, Detail: "No explicit snapshot result for binding.")
            : new(entry.Status, entry.ActualKind, entry.Detail);
    }
    public ResourceProbeResult QueryParameter(EngineProjectIdentity project, OpaqueBinding resourceBinding, OpaqueBinding parameterBinding)
    {
        if (project != _snapshot.Project || !_snapshot.Available) return new(ResourceResolutionStatus.Offline);
        var entry = _snapshot.Parameters.SingleOrDefault(item =>
            item.ResourceBinding == resourceBinding && item.ParameterBinding == parameterBinding);
        return entry is null ? new(ResourceResolutionStatus.QueryFailure, Detail: "No explicit snapshot result for parameter binding.")
            : new(entry.Status, entry.ActualKind, entry.Detail);
    }
}
