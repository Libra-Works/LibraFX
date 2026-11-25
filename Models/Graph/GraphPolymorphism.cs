using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace LiberaFx.Models.Graph;

public sealed record PolymorphicFamily(
    GraphNodeKind BaseKind,
    IReadOnlyDictionary<string, GraphNodeDefinition> Variants,
    IReadOnlySet<string> DomainInputs,
    IReadOnlySet<string> BroadcastInputs,
    string FallbackType,
    string? DisplayNameKey,
    string? DefaultMigration);

public static class GraphPolymorphism
{
    private sealed class RegistryFile
    {
        public int SchemaVersion { get; set; }
        public string ScalarType { get; set; } = "";
        public Dictionary<string, string> BroadcastConversions { get; set; } = [];
        public List<FamilyFile> Families { get; set; } = [];
    }

    private sealed class FamilyFile
    {
        public string BaseKind { get; set; } = "";
        public string? FallbackType { get; set; }
        public string? OutputType { get; set; }
        public string? DisplayNameKey { get; set; }
        public string? DefaultMigration { get; set; }
        public List<string> DomainInputs { get; set; } = [];
        public List<string> BroadcastInputs { get; set; } = [];
        public Dictionary<string, string> Variants { get; set; } = [];
    }

    private sealed record Registry(string Scalar, IReadOnlyDictionary<string, GraphNodeDefinition> Converters,
        IReadOnlyDictionary<GraphNodeKind, PolymorphicFamily> Families, IReadOnlySet<GraphNodeKind> HiddenVariants);
    private static readonly Lazy<Registry> Data = new(Load);
    public static IEnumerable<PolymorphicFamily> Families => Data.Value.Families.Values;
    public static bool TryFamily(GraphNodeKind kind, out PolymorphicFamily family) =>
        Data.Value.Families.TryGetValue(kind, out family!);
    public static bool IsHiddenVariant(GraphNodeKind kind) => Data.Value.HiddenVariants.Contains(kind);
    public static string ScalarType => Data.Value.Scalar;

    private static Registry Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Polymorphism", "Math.json");
        using var stream = File.Exists(path) ? File.OpenRead(path) : BuiltInAssets.Open("Polymorphism", "Math");
        var file = JsonSerializer.Deserialize<RegistryFile>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing polymorphic math registry.");
        if (file.SchemaVersion != 1 || string.IsNullOrWhiteSpace(file.ScalarType) ||
            file.Families is null || file.Families.Count == 0 || file.BroadcastConversions is null)
            throw new InvalidDataException("Invalid polymorphic math registry version or scalar type.");
        var core = GraphNodeDefinition.Core.ToDictionary(item => item.Kind.Value, StringComparer.Ordinal);
        GraphNodeDefinition Find(string name) => core.TryGetValue(name, out var item) ? item :
            throw new InvalidDataException($"Unknown polymorphic node kind '{name}'.");
        var converters = new Dictionary<string, GraphNodeDefinition>(StringComparer.Ordinal);
        foreach (var (type, kind) in file.BroadcastConversions)
        {
            var converter = Find(kind);
            if (string.IsNullOrWhiteSpace(type) || type == file.ScalarType || converter.PortType != type ||
                converter.GetInputPortType("a") != file.ScalarType || converter.GetOutputPortType(null) != type)
                throw new InvalidDataException($"Invalid broadcast conversion for '{type}'.");
            converters.Add(type, converter);
        }
        var families = new Dictionary<GraphNodeKind, PolymorphicFamily>();
        var variantsUsed = new HashSet<GraphNodeKind>();
        var hidden = new HashSet<GraphNodeKind>();
        foreach (var entry in file.Families)
        {
            if (entry is null || entry.Variants is null || entry.DomainInputs is null || entry.BroadcastInputs is null)
                throw new InvalidDataException("Incomplete polymorphic family.");
            var baseDefinition = Find(entry.BaseKind);
            var domain = entry.DomainInputs.ToHashSet(StringComparer.Ordinal);
            var broadcast = entry.BroadcastInputs.ToHashSet(StringComparer.Ordinal);
            var fallback = entry.FallbackType ?? file.ScalarType;
            if (entry.DefaultMigration is not (null or "resizeVector") ||
                domain.Count == 0 || domain.Count != entry.DomainInputs.Count ||
                broadcast.Count != entry.BroadcastInputs.Count || !broadcast.IsSubsetOf(domain))
                throw new InvalidDataException($"Invalid polymorphic family '{entry.BaseKind}'.");
            var variants = new Dictionary<string, GraphNodeDefinition>(StringComparer.Ordinal);
            foreach (var (type, kind) in entry.Variants)
            {
                var variant = Find(kind);
                if (!variantsUsed.Add(variant.Kind) || variant.PortType != (entry.OutputType ?? type) ||
                    variant.PortCategory != baseDefinition.PortCategory ||
                    !domain.All(port => variant.GetInputPortType(port) == type) ||
                    !(baseDefinition.InputPorts ?? []).Select(port => port.Id)
                        .SequenceEqual((variant.InputPorts ?? []).Select(port => port.Id)) ||
                    !(baseDefinition.OutputPorts ?? []).Select(port => port.Id)
                        .SequenceEqual((variant.OutputPorts ?? []).Select(port => port.Id)) ||
                    (type != file.ScalarType && broadcast.Count != 0 && !converters.ContainsKey(type)))
                    throw new InvalidDataException($"Invalid variant '{kind}' in family '{entry.BaseKind}'.");
                foreach (var port in baseDefinition.InputPorts ?? [])
                    if (variant.GetInputPortCategory(port.Id) != baseDefinition.GetInputPortCategory(port.Id) ||
                        !domain.Contains(port.Id) && variant.GetInputPortType(port.Id) != baseDefinition.GetInputPortType(port.Id))
                        throw new InvalidDataException($"Invalid input port '{port.Id}'.");
                foreach (var port in variant.OutputPorts ?? [])
                    if (variant.GetOutputPortType(port.Id) != variant.PortType ||
                        variant.GetOutputPortCategory(port.Id) != baseDefinition.GetOutputPortCategory(port.Id))
                        throw new InvalidDataException($"Invalid output port '{port.Id}'.");
                variants.Add(type, variant);
                if (variant.Kind != baseDefinition.Kind) hidden.Add(variant.Kind);
            }
            if (!variants.TryGetValue(fallback, out var fallbackVariant) || fallbackVariant.Kind != baseDefinition.Kind)
                throw new InvalidDataException($"Family '{entry.BaseKind}' has no matching fallback variant.");
            families.Add(baseDefinition.Kind, new(baseDefinition.Kind, variants, domain, broadcast, fallback,
                entry.DisplayNameKey, entry.DefaultMigration));
        }
        return new(file.ScalarType, converters, families, hidden);
    }

    public static bool CanBroadcast(GraphNodeSnapshot target, string? port, string? sourceType, string? targetType) =>
        target.IsPolymorphic && port is not null && sourceType == ScalarType && targetType is not null &&
        TryFamily(target.Kind, out var family) && family.BroadcastInputs.Contains(port) &&
        family.Variants.ContainsKey(targetType) && Data.Value.Converters.ContainsKey(targetType);

    public static GraphNodeDefinition BroadcastConverter(string targetType) => Data.Value.Converters[targetType];

    public static IReadOnlyDictionary<Guid, GraphNodeDefinition> Resolve(GraphSnapshot graph)
    {
        var all = GraphNodeDefinition.ByKind;
        var nodes = graph.Nodes.ToDictionary(item => item.Id);
        var result = graph.Nodes.ToDictionary(item => item.Id,
            item => GroupBoundarySchema.Effective(SpawnableGroupSchema.Effective(
                DynamicPinSchema.Effective(SequenceSchema.Effective(GameIoSchema.Effective(
                    RenderNodeSchema.Effective(all[item.Kind], item.Parameters), item.Parameters), item.Parameters),
                    item.Parameters),
                item.ChildGraph), item.ChildGraph));
        var incoming = graph.Connections.ToLookup(edge => edge.TargetNodeId);
        var outgoing = graph.Connections.ToLookup(edge => edge.SourceNodeId);
        // An unconnected reroute inherits the first concrete endpoint in its chain.
        // Upstream takes precedence so mismatched downstream ports remain diagnosable.
        (GraphPortCategory Category, string Type)? RerouteEndpoint(Guid id, bool upstream)
        {
            var visited = new HashSet<Guid> { id };
            var pending = new Queue<Guid>();
            pending.Enqueue(id);
            while (pending.TryDequeue(out var current))
            {
                foreach (var edge in upstream ? incoming[current] : outgoing[current])
                {
                    var other = upstream ? edge.SourceNodeId : edge.TargetNodeId;
                    if (!visited.Add(other))
                    {
                        continue;
                    }
                    if (nodes[other].Kind.Value == "Reroute")
                    {
                        pending.Enqueue(other);
                        continue;
                    }
                    var definition = result[other];
                    var type = upstream ? definition.GetOutputPortType(edge.SourcePortId) :
                        definition.GetInputPortType(edge.TargetPortId);
                    if (type is not null)
                    {
                        var category = upstream ? definition.GetOutputPortCategory(edge.SourcePortId) :
                            definition.GetInputPortCategory(edge.TargetPortId);
                        return (category, type);
                    }
                }
            }
            return null;
        }
        var degree = graph.Nodes.ToDictionary(node => node.Id, node => incoming[node.Id].Count());
        var queue = new Queue<GraphNodeSnapshot>(graph.Nodes.Where(node => degree[node.Id] == 0));
        while (queue.TryDequeue(out var node))
        {
            if (node.Kind.Value == "Reroute" &&
                (RerouteEndpoint(node.Id, true) ?? RerouteEndpoint(node.Id, false)) is { } endpoint)
            {
                var basis = result[node.Id];
                result[node.Id] = basis with
                {
                    PortType = endpoint.Type,
                    PortCategory = endpoint.Category,
                    InputPorts = basis.InputPorts?.Select(port => port with
                        { PortType = endpoint.Type, Category = endpoint.Category }).ToArray(),
                    OutputPorts = basis.OutputPorts?.Select(port => port with
                        { PortType = endpoint.Type, Category = endpoint.Category }).ToArray()
                };
            }
            if (node.IsPolymorphic && TryFamily(node.Kind, out var family))
            {
                var shaped = incoming[node.Id]
                    .Where(edge => edge.TargetPortId is not null && family.DomainInputs.Contains(edge.TargetPortId))
                    .Select(edge => result[edge.SourceNodeId].GetOutputPortType(edge.SourcePortId))
                    .FirstOrDefault(type => type is not null && type != ScalarType);
                var type = shaped is not null && family.Variants.ContainsKey(shaped) ? shaped : family.FallbackType;
                var variant = family.Variants[type];
                var basis = result[node.Id];
                result[node.Id] = basis with
                {
                    PortType = variant.PortType,
                    InputPorts = variant.InputPorts,
                    OutputPorts = variant.OutputPorts
                };
            }
            foreach (var edge in outgoing[node.Id])
                if (--degree[edge.TargetNodeId] == 0) queue.Enqueue(nodes[edge.TargetNodeId]);
        }
        return result;
    }

    public static GraphSnapshot Lower(GraphSnapshot graph) => LowerWithOutputs(graph).Graph;

    public static MatrixLoweringResult LowerWithOutputs(GraphSnapshot graph)
    {
        var ids = new HashSet<Guid>();
        void Collect(GraphSnapshot current)
        {
            foreach (var node in current.Nodes)
            {
                ids.Add(node.Id);
                if (node.ChildGraph is not null) Collect(node.ChildGraph);
            }
        }
        Collect(graph);
        GraphSnapshot Rewrite(GraphSnapshot current)
        {
            var resolved = Resolve(current);
            var original = current.Nodes.ToDictionary(item => item.Id);
            var lowered = new List<GraphNodeSnapshot>();
            foreach (var node in current.Nodes)
            {
                var child = node.ChildGraph is null ? null : Rewrite(node.ChildGraph);
                if (!node.IsPolymorphic)
                {
                    lowered.Add(node with { ChildGraph = child });
                    continue;
                }
                var family = Data.Value.Families[node.Kind];
                var variantType = resolved[node.Id].GetInputPortType(family.DomainInputs.First())!;
                var variant = family.Variants[variantType];
                var basis = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
                var parameters = new List<NodeParameterSnapshot>();
                foreach (var parameter in variant.Parameters)
                {
                    var old = basis.Parameters.FirstOrDefault(item => item.Id == parameter.Id);
                    var saved = node.Parameters?.FirstOrDefault(item => item.Id == parameter.Id);
                    var value = saved?.Value ?? old?.DefaultValue ?? parameter.DefaultValue;
                    var oldType = saved?.Type ?? old?.Type ?? parameter.Type;
                    if (oldType == parameter.Type)
                        parameters.Add(new(parameter.Id, parameter.Type, value.Clone()));
                    else if (oldType == "float" && family.BroadcastInputs.Contains(parameter.Id) &&
                             parameter.Type is "vector2" or "vector3" or "vector4")
                    {
                        var width = parameter.Type[^1] - '0';
                        parameters.Add(new(parameter.Id, parameter.Type,
                            JsonSerializer.SerializeToElement(Enumerable.Repeat(value.GetDouble(), width).ToArray())));
                    }
                    else if (family.DefaultMigration == "resizeVector" && oldType is "vector2" or "vector3" or "vector4" &&
                             parameter.Type is "vector2" or "vector3" or "vector4")
                    {
                        var width = parameter.Type[^1] - '0';
                        var oldValues = value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
                        var defaults = parameter.DefaultValue.EnumerateArray().Select(item => item.GetDouble()).ToArray();
                        parameters.Add(new(parameter.Id, parameter.Type, JsonSerializer.SerializeToElement(
                            Enumerable.Range(0, width).Select(i => i < oldValues.Length ? oldValues[i] : defaults[i]).ToArray())));
                    }
                    else throw new InvalidDataException($"Cannot convert default '{parameter.Id}' on node {node.Id}.");
                }
                lowered.Add(node with { Kind = variant.Kind, PortType = variant.PortType,
                    PortCategory = variant.PortCategory, Parameters = parameters, ChildGraph = child, IsPolymorphic = false });
            }
            var edges = new List<GraphConnectionSnapshot>();
            foreach (var edge in current.Connections)
            {
                var target = original[edge.TargetNodeId];
                var sourceType = resolved[edge.SourceNodeId].GetOutputPortType(edge.SourcePortId);
                var targetType = resolved[edge.TargetNodeId].GetInputPortType(edge.TargetPortId);
                if (!CanBroadcast(target, edge.TargetPortId, sourceType, targetType))
                {
                    edges.Add(edge);
                    continue;
                }
                var converter = BroadcastConverter(targetType!);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"LiberaFx.broadcast:{target.Id:D}:{edge.TargetPortId}"));
                var id = new Guid(hash.AsSpan(0, 16));
                if (id == Guid.Empty || !ids.Add(id))
                    throw new InvalidDataException($"Implicit conversion ID conflicts at node {target.Id}.");
                lowered.Add(new(id, converter.Kind, converter.Name, target.X, target.Y,
                    converter.PortType, converter.PortCategory));
                edges.Add(new(edge.SourceNodeId, id, "a", edge.SourcePortId));
                edges.Add(new(id, edge.TargetNodeId, edge.TargetPortId));
            }
            return new(lowered, edges);
        }
        return GraphMatrixLowering.Lower(Rewrite(graph));
    }
}
