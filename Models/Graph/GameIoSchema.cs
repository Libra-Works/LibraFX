using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiberaFx.Models.Graph;

public enum GameIoPortKind : byte
{
    InputProperty,
    InputSignal,
    OutputProperty,
    OutputSignal,
    OwnerMatrix
}

public sealed record GameIoPort(Guid NodeId, string Name, GameIoPortKind Kind, int Width, bool IsColor,
    IReadOnlyList<Guid>? RuntimeNodeIds = null);

public static class GameIoSchema
{
    public const string CurrentCameraProperty = "__LFX_CAMERA_V1";
    private static readonly GraphNodeKind CurrentCameraKind = new("CurrentCamera");
    private const string TypeParameterId = "valueType";
    private const string NameParameterId = "name";
    private const string PropertyTypeEnum = "enum:1e309707-1fa0-4d38-aed9-42d000000001";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsInstanceNode(GraphNodeKind kind) => kind.Value is
        "InstanceInputProperty" or "InstanceInputSignal" or "InstanceOutputProperty" or "InstanceOutputSignal";

    public static GraphNodeKind SharedKind(GraphNodeKind kind) => IsInstanceNode(kind)
        ? new GraphNodeKind(kind.Value["Instance".Length..]) : kind;

    public static bool IsNode(GraphNodeKind kind) => IsInstanceNode(kind) || kind.Value is
        "InputProperty" or "InputSignal" or "OutputProperty" or "OutputSignal" or "OwnerMatrix" or "CurrentCamera";

    public static bool IsInternalOutputSignal(GraphNodeKind kind) =>
        kind.Value == "GameIoRuntimeOutputSignal";

    public static bool HasCurrentCamera(GraphSnapshot graph) =>
        graph.Nodes.Any(node => node.Kind == CurrentCameraKind);

    public static GraphNodeDefinition Effective(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        var kind = SharedKind(definition.Kind);
        if (kind != GraphNodeKind.InputProperty && kind != GraphNodeKind.OutputProperty)
            return definition;

        var type = PropertyType(definition, nodeParameters);
        var port = new GraphInputPortDefinition("value", "Value", type, GraphPortCategory.Parameter);
        return kind == GraphNodeKind.InputProperty
            ? definition with
            {
                PortType = type,
                OutputPorts = [new GraphOutputPortDefinition("value", "Value", type, GraphPortCategory.Parameter)]
            }
            : definition with { PortType = type, InputPorts = [port] };
    }

    public static JsonElement? ValueTypeValue(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        var kind = SharedKind(definition.Kind);
        if (kind != GraphNodeKind.InputProperty && kind != GraphNodeKind.OutputProperty)
            return null;
        var parameter = definition.Parameters.Single(item => item.Id == TypeParameterId);
        return nodeParameters?.FirstOrDefault(item =>
            item.Id == parameter.Id && item.Type == parameter.Type)?.Value ?? parameter.DefaultValue;
    }

    public static void ValidateNodeParameters(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        if (definition.Kind.Value is "OwnerMatrix" or "CurrentCamera") return;
        if (!IsNode(definition.Kind)) return;
        var nameDefinition = definition.Parameters.Single(item => item.Id == NameParameterId);
        var name = nodeParameters?.FirstOrDefault(item => item.Id == NameParameterId)?.Value
            ?? nameDefinition.DefaultValue;
        if (name.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("A Game I/O port name must be a string.");
        var nameText = name.GetString()!;
        if (nameText.Length != 0) ValidateName(nameText);

        var kind = SharedKind(definition.Kind);
        if (kind == GraphNodeKind.InputProperty || kind == GraphNodeKind.OutputProperty)
            _ = PropertyType(definition, nodeParameters);
    }

    public static (GraphSnapshot Graph, IReadOnlyList<GameIoPort> Ports) Lower(GraphSnapshot graph)
    {
        ValidateLocations(graph, true);
        var ports = CreatePorts(graph);
        if (ports.Count == 0) return (graph, ports);

        var definitions = GraphNodeDefinition.All.ToDictionary(item => item.Kind);
        var camera = graph.Nodes.SingleOrDefault(node => node.Kind == CurrentCameraKind);
        var position = camera is null ? null : CameraOutput(camera, "position", "Vector4ToVector3");
        var valid = camera is null ? null : CameraOutput(camera, "valid", "BreakVector4");
        GraphNodeSnapshot CameraOutput(GraphNodeSnapshot source, string name, string kind)
        {
            var definition = definitions[new GraphNodeKind(kind)];
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.camera.v1:{source.Id:D}:{name}"));
            return new(new Guid(hash.AsSpan(0, 16)), definition.Kind, $"{source.Name}: {name}",
                source.X, source.Y, definition.PortType, definition.PortCategory);
        }

        var loweredNodes = graph.Nodes.Select(node =>
        {
            if (!IsNode(node.Kind)) return node;
            if (node.Kind.Value == "OwnerMatrix") return node;
            if (node.Kind == CurrentCameraKind)
            {
                var input = definitions[new GraphNodeKind("FloatX4")];
                return node with { Kind = input.Kind, PortType = input.PortType, PortCategory = input.PortCategory };
            }
            var sharedKind = SharedKind(node.Kind);
            if (sharedKind == GraphNodeKind.InputSignal)
                return node with { Kind = GraphNodeKind.Signal, PortType = "Signal", PortCategory = GraphPortCategory.Signal };
            if (sharedKind == GraphNodeKind.OutputSignal)
                return node with
                {
                    Kind = new GraphNodeKind("GameIoRuntimeOutputSignal"),
                    PortType = "Signal",
                    PortCategory = GraphPortCategory.Signal
                };

            var valueType = PropertyType(definitions[node.Kind], node.Parameters);
            if (valueType == "Matrix") return node with { Kind = sharedKind };
            var typeName = valueType switch
            {
                "Float" => "Float",
                "Bool" => "Bool",
                "FloatX2" => "FloatX2",
                "FloatX3" => "FloatX3",
                "FloatX4" => "FloatX4",
                "Color" => "Color",
                _ => throw new InvalidDataException("Unsupported Game I/O property type.")
            };
            var valueDefinition = definitions.Values.Single(item =>
                item.Namespace == "LiberaFx" && item.TypeName == typeName);
            var parameters = valueDefinition.Parameters.Select(item =>
                new NodeParameterSnapshot(item.Id, item.Type, item.DefaultValue.Clone())).ToArray();
            return node with
            {
                Kind = valueDefinition.Kind,
                PortType = valueDefinition.PortType,
                PortCategory = valueDefinition.PortCategory,
                Parameters = parameters
            };
        }).ToList();
        var originals = graph.Nodes.ToDictionary(item => item.Id);
        var connections = graph.Connections.Select(edge =>
        {
            var sourceKind = SharedKind(originals[edge.SourceNodeId].Kind);
            var targetKind = SharedKind(originals[edge.TargetNodeId].Kind);
            var sourcePort = sourceKind == GraphNodeKind.InputSignal ||
                sourceKind == GraphNodeKind.InputProperty &&
                PropertyType(definitions[sourceKind], originals[edge.SourceNodeId].Parameters) != "Matrix" ||
                sourceKind == GraphNodeKind.OutputProperty ? null : edge.SourcePortId;
            var targetPort = targetKind == GraphNodeKind.OutputProperty &&
                PropertyType(definitions[targetKind], originals[edge.TargetNodeId].Parameters) != "Matrix" ? null :
                targetKind == GraphNodeKind.OutputSignal ? "signal" : edge.TargetPortId;
            if (sourceKind == CurrentCameraKind)
            {
                return sourcePort switch
                {
                    "position" => edge with { SourceNodeId = position!.Id, SourcePortId = null, TargetPortId = targetPort },
                    "valid" => edge with { SourceNodeId = valid!.Id, SourcePortId = "w", TargetPortId = targetPort },
                    _ => throw new InvalidDataException("Unknown Current Camera output.")
                };
            }
            return edge with { SourcePortId = sourcePort, TargetPortId = targetPort };
        }).ToList();
        if (camera is not null)
        {
            loweredNodes.Add(position!);
            loweredNodes.Add(valid!);
            connections.Add(new(camera.Id, position!.Id, "a"));
            connections.Add(new(camera.Id, valid!.Id, "a"));
        }
        return (new GraphSnapshot(loweredNodes, connections)
        {
            AutoDestroyWhenIdle = graph.AutoDestroyWhenIdle
        }, ports);
    }

    private static IReadOnlyList<GameIoPort> CreatePorts(GraphSnapshot graph)
    {
        var ports = new List<GameIoPort>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ownerMatrices = graph.Nodes.Count(node => node.Kind.Value == "OwnerMatrix");
        if (ownerMatrices > 1)
            throw new InvalidDataException("An effect can contain only one Owner Matrix node.");
        if (ownerMatrices == 1) names.Add("OwnerMatrix");
        if (graph.Nodes.Count(node => node.Kind == CurrentCameraKind) > 1)
        {
            throw new InvalidDataException("An effect can contain only one Current Camera node.");
        }
        foreach (var node in graph.Nodes.Where(node => IsNode(node.Kind)))
        {
            if (node.Kind.Value == "OwnerMatrix")
            {
                ports.Add(new(node.Id, "OwnerMatrix", GameIoPortKind.OwnerMatrix, 16, false));
                continue;
            }
            if (node.Kind == CurrentCameraKind)
            {
                ports.Add(new(node.Id, CurrentCameraProperty, GameIoPortKind.InputProperty, 4, false));
                continue;
            }
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
            ValidateNodeParameters(definition, node.Parameters);
            var nameParameter = definition.Parameters.Single(item => item.Id == NameParameterId);
            var name = node.Parameters?.FirstOrDefault(item => item.Id == nameParameter.Id)?.Value
                ?? nameParameter.DefaultValue;
            var text = name.GetString()!;
            ValidateName(text);
            if (!names.Add(text))
                throw new InvalidDataException($"Game I/O port name '{text}' must be unique.");
            var kind = SharedKind(node.Kind).Value switch
            {
                "InputProperty" => GameIoPortKind.InputProperty,
                "InputSignal" => GameIoPortKind.InputSignal,
                "OutputProperty" => GameIoPortKind.OutputProperty,
                "OutputSignal" => GameIoPortKind.OutputSignal,
                _ => throw new InvalidDataException("Unknown Game I/O node.")
            };
            var type = kind is GameIoPortKind.InputProperty or GameIoPortKind.OutputProperty
                ? PropertyType(definition, node.Parameters) : "Signal";
            var width = type switch
            {
                "Float" or "Bool" => 1,
                "FloatX2" => 2,
                "FloatX3" => 3,
                "FloatX4" or "Color" => 4,
                "Matrix" => 16,
                _ => 0
            };
            ports.Add(new(node.Id, text, kind, width, type == "Color"));
        }
        if (ports.Count > 256)
            throw new InvalidDataException("An effect supports at most 256 Game I/O ports.");
        return ports.AsReadOnly();
    }

    private static string PropertyType(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        var value = ValueTypeValue(definition, nodeParameters)
            ?? throw new InvalidDataException("A Property node is missing its value type.");
        if (!value.TryGetInt32(out var selected))
            throw new InvalidDataException("A Property node has an invalid value type.");
        return selected switch
        {
            0 => "Float",
            1 => "FloatX2",
            2 => "FloatX3",
            3 => "FloatX4",
            4 => "Color",
            5 => "Matrix",
            6 => "Bool",
            _ => throw new InvalidDataException("A Property node has an unsupported value type.")
        };
    }

    private static void ValidateLocations(GraphSnapshot graph, bool isRoot)
    {
        foreach (var node in graph.Nodes)
        {
            if (IsNode(node.Kind) && !IsInstanceNode(node.Kind) && !isRoot)
                throw new InvalidDataException("Game I/O nodes must be directly in the root graph.");
            if (node.ChildGraph is { } child) ValidateLocations(child, false);
        }
    }

    private static void ValidateName(string name)
    {
        int byteCount;
        try { byteCount = StrictUtf8.GetByteCount(name); }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException("A Game I/O port name is not valid UTF-8.", ex);
        }
        if (name == CurrentCameraProperty)
        {
            throw new InvalidDataException("The Game I/O name is reserved for Current Camera.");
        }
        if (string.IsNullOrWhiteSpace(name) || byteCount > 128 || name[0] == ' ' || name[^1] == ' ' ||
            name.Any(char.IsControl))
            throw new InvalidDataException("A Game I/O port name must be 1..128 UTF-8 bytes without control characters or edge spaces.");
    }
}
