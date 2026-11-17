using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiberaFx.Models.Graph;

/// <summary>
/// Stable graph definitions for enum values, enum group boundaries and lazy enum
/// selection. Pins are keyed by the enum's numeric value, never by its display name.
/// </summary>
public static class GraphEnumBranchSchema
{
    private static readonly IReadOnlyDictionary<Guid, EnumGraphDefinitions> Definitions = Build();

    public static GraphNodeDefinition BranchTime { get; } =
        new(new GraphNodeKind("EnumBranchTime"), StableId("EnumBranchTime/v1"), "LiberaFx",
            "Enum Branch Time", "Float", GraphPortCategory.Parameter, "Enum")
        {
            TypeName = "EnumBranchTime",
            DisplayNameKey = "EnumBranches.BranchTime",
            GroupNameKey = "EnumBranches.Group"
        };

    public static IReadOnlyList<GraphNodeDefinition> NodeDefinitions { get; } =
        Array.AsReadOnly(Definitions.Values.SelectMany(item =>
            new[] { item.Value, item.GroupInput, item.GroupOutput }
                .Concat(item.Selectors.Values)).Append(BranchTime).ToArray());

    public static string EnumPortType(Guid typeId) => $"enum:{typeId:D}";
    public static bool IsEnumPort(string? type) => GraphEnumDefinition.TryGetTypeId(type ?? "", out _);

    public static string ValueKind(Guid typeId) => Definitions[typeId].Value.Kind.Value;
    public static string GroupInputKind(Guid typeId) => Definitions[typeId].GroupInput.Kind.Value;
    public static string GroupOutputKind(Guid typeId) => Definitions[typeId].GroupOutput.Kind.Value;
    public static bool TryGetEnumDefinition(GraphNodeKind kind, out GraphEnumDefinition enumeration)
    {
        foreach (var item in Definitions.Values)
        {
            if (item.Value.Kind == kind || item.GroupInput.Kind == kind ||
                item.GroupOutput.Kind == kind || item.Selectors.Values.Any(selector => selector.Kind == kind))
            {
                enumeration = item.Enum;
                return true;
            }
        }
        enumeration = null!;
        return false;
    }

    public static bool TryGetValueDefinition(GraphNodeKind kind, out GraphNodeDefinition definition)
    {
        definition = NodeDefinitions.FirstOrDefault(item => item.Kind == kind &&
            Definitions.Values.Any(entry => entry.Value.Kind == kind))!;
        return definition is not null;
    }

    public static bool TryGetBoundaryDefinition(GraphNodeKind kind, out GraphNodeDefinition definition,
        out bool isInput)
    {
        foreach (var item in Definitions.Values)
        {
            if (item.GroupInput.Kind == kind)
            {
                definition = item.GroupInput;
                isInput = true;
                return true;
            }
            if (item.GroupOutput.Kind == kind)
            {
                definition = item.GroupOutput;
                isInput = false;
                return true;
            }
        }
        definition = null!;
        isInput = false;
        return false;
    }

    public static bool TryGetSelector(GraphNodeKind kind, out Guid enumTypeId,
        out string valueType, out IReadOnlyList<GraphEnumValue> values)
    {
        foreach (var pair in Definitions)
        foreach (var selector in pair.Value.Selectors.Values)
        {
            if (selector.Kind != kind) continue;
            enumTypeId = pair.Key;
            valueType = selector.PortType;
            values = pair.Value.Enum.Values;
            return true;
        }
        enumTypeId = Guid.Empty;
        valueType = string.Empty;
        values = [];
        return false;
    }

    public static string CasePortId(int value) => "case_" +
        value.ToString(System.Globalization.CultureInfo.InvariantCulture).Replace("-", "m", StringComparison.Ordinal);

    public static JsonElement DefaultValue(string type) => type switch
    {
        "Float" => JsonSerializer.SerializeToElement(0d),
        "FloatX2" => JsonSerializer.SerializeToElement(new[] { 0d, 0d }),
        "FloatX3" => JsonSerializer.SerializeToElement(new[] { 0d, 0d, 0d }),
        "FloatX4" => JsonSerializer.SerializeToElement(new[] { 0d, 0d, 0d, 0d }),
        "Color" => JsonSerializer.SerializeToElement(new[] { 0d, 0d, 0d, 1d }),
        _ => throw new InvalidDataException($"Unsupported enum selection output type '{type}'.")
    };

    private static IReadOnlyDictionary<Guid, EnumGraphDefinitions> Build()
    {
        var result = new Dictionary<Guid, EnumGraphDefinitions>();
        foreach (var enumeration in GraphEnumDefinition.All.Values)
        {
            var enumType = EnumPortType(enumeration.TypeId);
            var valueKind = Kind("EnumValue", enumeration.TypeId);
            var groupInputKind = Kind("GroupInputEnum", enumeration.TypeId);
            var groupOutputKind = Kind("GroupOutputEnum", enumeration.TypeId);
            var value = Definition(valueKind, enumeration.TypeId, "Enum value", enumType,
                "Enum", [new NodeParameterDefinition("value", "Value", enumType,
                    JsonSerializer.SerializeToElement(enumeration.Values[0].Value))
                    { DisplayNameKey = "EnumBranches.ValueParameter" }],
                [new("value", "Value", enumType, GraphPortCategory.Parameter)],
                "EnumBranches.Value")
            with
            {
                InputPorts = [new("value", "Enum", enumType, GraphPortCategory.Parameter)]
            };
            var groupInput = Definition(groupInputKind, enumeration.TypeId, "Group Input (Enum)", enumType,
                "Group boundary", [new NodeParameterDefinition("value", "Default", enumType,
                    JsonSerializer.SerializeToElement(enumeration.Values[0].Value))
                    { DisplayNameKey = "EnumBranches.Default" }],
                displayNameKey: "EnumBranches.GroupInput");
            var groupOutput = Definition(groupOutputKind, enumeration.TypeId, "Group Output (Enum)", enumType,
                "Group boundary", [], displayNameKey: "EnumBranches.GroupOutput");
            var selectors = new Dictionary<string, GraphNodeDefinition>(StringComparer.Ordinal);
            foreach (var outputType in new[] { "Float", "FloatX2", "FloatX3", "FloatX4", "Color" })
            {
                var kind = Kind("EnumSelect" + outputType, enumeration.TypeId);
                var ports = new List<GraphInputPortDefinition>
                {
                    new("selector", "Enum", enumType, GraphPortCategory.Parameter)
                };
                ports.AddRange(enumeration.Values.Select(item => new GraphInputPortDefinition(
                    CasePortId(item.Value), item.Name, outputType, GraphPortCategory.Parameter,
                    DefaultValue(outputType))));
                ports.Add(new("default", "Default", outputType, GraphPortCategory.Parameter,
                    DefaultValue(outputType)));
                var fallbackType = outputType switch
                {
                    "Float" => "float", "FloatX2" => "vector2", "FloatX3" => "vector3",
                    "FloatX4" => "vector4", _ => "color"
                };
                var fallback = DefaultValue(outputType);
                var selector = Definition(kind, enumeration.TypeId, $"Enum Select ({outputType})",
                    outputType, "Enum", [new NodeParameterDefinition("fallback", "Fallback", fallbackType, fallback)
                        { DisplayNameKey = "EnumBranches.Fallback" }], ports,
                    $"EnumBranches.Select{outputType}");
                selectors.Add(outputType, selector);
            }
            result.Add(enumeration.TypeId, new(enumeration, value, groupInput, groupOutput, selectors));
        }
        return result;
    }

    private static GraphNodeDefinition Definition(GraphNodeKind kind, Guid enumTypeId, string name,
        string portType, string group, IReadOnlyList<NodeParameterDefinition> parameters,
        IReadOnlyList<GraphInputPortDefinition>? inputs = null, string? displayNameKey = null) =>
        new(kind, StableId($"{kind.Value}:{enumTypeId:D}"), "LiberaFx", name, portType,
            GraphPortCategory.Parameter, group)
        {
            TypeName = kind.Value,
            DisplayNameKey = displayNameKey,
            GroupNameKey = "EnumBranches.Group",
            Parameters = parameters,
            InputPorts = inputs
        };

    private static GraphNodeKind Kind(string name, Guid enumTypeId) =>
        new($"{name}_{enumTypeId:N}");

    private static Guid StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("LibraFX.GraphEnumBranch/v1/" + value));
        Span<byte> id = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(id);
        id[6] = (byte)((id[6] & 0x0f) | 0x50);
        id[8] = (byte)((id[8] & 0x3f) | 0x80);
        return new Guid(id, bigEndian: true);
    }

    private sealed record EnumGraphDefinitions(GraphEnumDefinition Enum, GraphNodeDefinition Value,
        GraphNodeDefinition GroupInput, GraphNodeDefinition GroupOutput,
        IReadOnlyDictionary<string, GraphNodeDefinition> Selectors);
}
