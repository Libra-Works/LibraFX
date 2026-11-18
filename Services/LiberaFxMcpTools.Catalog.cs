using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

public sealed partial class LiberaFxMcpTools
{
    [McpServerTool(Name = "libera_graph_list_node_types", ReadOnly = true, OpenWorld = false)]
    [Description("Browse the active project's authoring types. Returns a sorted summary page by default. Set summaryOnly=false for descriptions, ports, parameters and explicit conversions. Port arrays are empty when there are no pins; a pin with id=null is the default pin. Use libera_graph_describe_node_ports for effective instance pins. Sequence outputs then_0, then_1, etc. execute in order. Use the returned kind or qualifiedName to create nodes.")]
    public static Task<string> ListNodeTypes(ActiveGraphProvider activeGraph,
        [Description("Optional substring of kind, type name, display name, qualified name, group or enum name.")] string? query = null,
        [Description("Optional exact catalog group (original or localized name).")] string? group = null,
        [Description("Return brief entries only (default). Set false for full details.")] bool summaryOnly = true,
        [Description("Optional exact list of kind IDs returned by this tool.")] string[]? kinds = null,
        [Description("Number of matching rows to skip, starting at zero.")] int offset = 0,
        [Description("Maximum rows per page, 1–100. A shorter page means there are no more matches.")] int limit = 50) =>
        ChatMcpExecution.InvokeAsync(() =>
        {
            if (offset < 0 || limit is < 1 or > 100)
            {
                throw new McpException("offset must be nonnegative and limit must be between 1 and 100.");
            }

            var requested = kinds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = activeGraph.Graph.AvailableNodeDefinitions
                .Where(definition => requested is null || requested.Contains(definition.Kind.Value))
                .Select(definition => (Definition: definition, Entry: new NodeCatalogEntryViewModel(definition)))
                .Where(row => MatchesGroup(row.Definition, group) &&
                    (string.IsNullOrWhiteSpace(query) || new[] { row.Definition.Kind.Value, row.Definition.TypeName,
                        row.Definition.Name, row.Entry.Name, row.Definition.QualifiedName, row.Entry.SearchTerms,
                        row.Entry.Group }.Any(text => text.Contains(query, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(row => row.Definition.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .Skip(offset).Take(limit)
                .Select(row => EncodeDefinition(row.Definition, row.Entry.Name, summaryOnly));
            return JsonSerializer.Serialize(rows, JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_describe_node_ports", ReadOnly = true, OpenWorld = false)]
    [Description("Read the effective input/output pins of one existing node using its instance settings and current types. An empty array means no pins; a null id denotes the default pin.")]
    public static Task<string> DescribeNodePorts(ActiveGraphProvider activeGraph,
        [Description("GUID of the node in the current graph.")] string nodeId) =>
        ChatMcpExecution.InvokeAsync(() =>
        {
            var node = FindNode(activeGraph.Graph, nodeId);
            var result = new JsonObject();
            AddPorts(result, node.Definition, node.HasPorts);
            return result.ToJsonString(JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_list_enums", ReadOnly = true, OpenWorld = false)]
    [Description("List enum names, stable enum:<GUID> port/parameter types, and integer values. Use the numeric value when editing an enum parameter.")]
    public static Task<string> ListEnums(
        [Description("Optional enum:<GUID>, GUID, enum name or qualified enum name.")] string? query = null) =>
        ChatMcpExecution.InvokeAsync(() => JsonSerializer.Serialize(GraphEnumDefinition.All.Values
            .Where(item => string.IsNullOrWhiteSpace(query) ||
                string.Equals(query, item.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, item.QualifiedName, StringComparison.OrdinalIgnoreCase) ||
                (Guid.TryParse(query, out var guid) || GraphEnumDefinition.TryGetTypeId(query, out guid)) && guid == item.TypeId)
            .OrderBy(item => item.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new
            {
                item.TypeId, item.Name, item.QualifiedName, type = $"enum:{item.TypeId:D}",
                options = item.Values.Select(option => new
                {
                    option.Value, option.Name,
                    displayName = LocalizationService.Shared.GetOrDefault(option.DisplayNameKey, option.Name)
                })
            }), JsonOptions));

    private static bool MatchesGroup(GraphNodeDefinition definition, string? group) =>
        string.IsNullOrWhiteSpace(group) || string.Equals(definition.Group, group, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(LocalizationService.Shared.GetOrDefault(definition.GroupNameKey, definition.Group), group,
            StringComparison.OrdinalIgnoreCase);

    private static JsonObject EncodeDefinition(GraphNodeDefinition definition, string displayName, bool summaryOnly)
    {
        var result = summaryOnly
            ? JsonSerializer.SerializeToNode(new
            {
                definition.Kind, definition.TypeId, definition.TypeName, definition.QualifiedName,
                definition.Group, definition.PortType, definition.KindDomain
            }, JsonOptions)!.AsObject()
            : JsonSerializer.SerializeToNode(definition, JsonOptions)!.AsObject();
        result["name"] = displayName;
        if (GraphEnumBranchSchema.TryGetEnumDefinition(definition.Kind, out var enumeration))
        {
            result["enumType"] = $"enum:{enumeration.TypeId:D}";
            result["enumName"] = enumeration.QualifiedName;
        }

        if (summaryOnly)
        {
            return result;
        }

        result["description"] = LocalizationService.Shared.GetOrDefault(definition.DescriptionKey, "");
        foreach (var (parameter, index) in definition.Parameters.Select((parameter, index) => (parameter, index)))
        {
            if (EnumName(parameter.Type) is { } enumName)
            {
                result["parameters"]![index]!["enumName"] = enumName;
            }
        }

        var ports = GraphNodeViewModel.EffectiveDefinition(definition, null, null);
        var hasPorts = definition.Kind != GraphNodeKind.Comment && definition.Kind != GraphNodeKind.GraphGroup &&
                       !GpuParticleEditorService.IsGpu(definition.Kind);
        var family = GraphPolymorphism.TryFamily(definition.Kind, out var polymorphic) ? polymorphic : null;
        if (family is not null)
        {
            result["polymorphism"] = JsonSerializer.SerializeToNode(new
            {
                types = family.Variants.Keys.Order(StringComparer.Ordinal),
                domainInputIds = family.DomainInputs.Order(StringComparer.Ordinal),
                scalarBroadcastInputIds = family.BroadcastInputs.Order(StringComparer.Ordinal),
                family.FallbackType
            }, JsonOptions);
        }

        AddPorts(result, ports, hasPorts);
        return result;
    }

    private static void AddPorts(JsonObject result, GraphNodeDefinition definition, bool hasPorts)
    {
        result["inputPorts"] = EncodePorts(hasPorts, definition.InputPorts?.Select(port => (port.Id, port.Name, port.DefaultValue)),
            definition.GetInputPortType, definition.GetInputPortCategory);
        result["outputPorts"] = EncodePorts(hasPorts, definition.OutputPorts?.Select(port => (port.Id, port.Name, (JsonElement?)null)),
            definition.GetOutputPortType, definition.GetOutputPortCategory);
    }

    private static JsonNode? EncodePorts(bool hasPorts, IEnumerable<(string Id, string Name, JsonElement? DefaultValue)>? namedPorts,
        Func<string?, string?> getType, Func<string?, GraphPortCategory> getCategory)
    {
        IEnumerable<(string? Id, string Name, JsonElement? DefaultValue)> ports = !hasPorts ? [] :
            namedPorts is null ? [(null, "Default", null)] :
            namedPorts.Select(port => ((string?)port.Id, port.Name, port.DefaultValue));
        return JsonSerializer.SerializeToNode(ports.Select(port =>
        {
            var type = getType(port.Id);
            return new
            {
                port.Id, port.Name, port.DefaultValue, portType = type, category = getCategory(port.Id),
                enumName = EnumName(type)
            };
        }), JsonOptions);
    }

    private static string? EnumName(string? type) =>
        type is not null && GraphEnumDefinition.TryGetTypeId(type, out var enumId)
            ? GraphEnumDefinition.All.GetValueOrDefault(enumId)?.QualifiedName : null;

    private static JsonObject EncodeGraph(GraphSnapshot snapshot)
    {
        var graph = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        AddTypes(snapshot, graph["nodes"]!.AsArray());
        return graph;
    }

    private static JsonObject EncodeNode(GraphNodeSnapshot snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        AddType(snapshot, node);
        return node;
    }

    private static void AddTypes(GraphSnapshot graph, JsonArray nodes)
    {
        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            AddType(graph.Nodes[index], nodes[index]!.AsObject());
        }
    }

    private static void AddType(GraphNodeSnapshot snapshot, JsonObject node)
    {
        node["typeName"] = GraphNodeDefinition.TryFind(snapshot.Kind.Value, out var definition)
            ? definition!.TypeName : snapshot.Kind.Value;
        if (snapshot.ChildGraph is { } child)
        {
            AddTypes(child, node["childGraph"]!["nodes"]!.AsArray());
        }
    }
}
