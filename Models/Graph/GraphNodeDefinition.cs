using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Models.Graph;

[JsonConverter(typeof(GraphNodeKindConverter))]
public readonly record struct GraphNodeKind(string Value)
{
    public static GraphNodeKind Float => new("Float");
    public static GraphNodeKind FloatX2 => new("FloatX2");
    public static GraphNodeKind FloatX3 => new("FloatX3");
    public static GraphNodeKind FloatX4 => new("FloatX4");
    public static GraphNodeKind Int32 => new("Int32");
    public static GraphNodeKind Signal => new("Signal");
    public static GraphNodeKind FloatFCurve => new("FloatFCurve");
    public static GraphNodeKind GraphGroup => new("GraphGroup");
    public static GraphNodeKind Comment => new("Comment");
    public static GraphNodeKind Time => new("Time");
    public static GraphNodeKind Add => new("Add");
    public static GraphNodeKind Subtract => new("Subtract");
    public static GraphNodeKind Multiply => new("Multiply");
    public static GraphNodeKind Divide => new("Divide");
    public override string ToString() => Value ?? string.Empty;
}

public sealed class GraphNodeKindConverter : JsonConverter<GraphNodeKind>
{
    private static readonly string[] LegacyKinds = ["Float", "FloatX2", "FloatX3", "FloatX4", "Int32", "Signal", "FloatFCurve"];

    public override GraphNodeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } value)
        {
            var legacy = LegacyKinds.FirstOrDefault(kind => string.Equals(kind, value, StringComparison.OrdinalIgnoreCase));
            return new GraphNodeKind(legacy ?? (Guid.TryParse(value, out var id) ? id.ToString("D") : value));
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var index) && index >= 0 && index < LegacyKinds.Length)
            return new GraphNodeKind(LegacyKinds[index]);
        throw new JsonException("Invalid graph node type.");
    }

    public override void Write(Utf8JsonWriter writer, GraphNodeKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public enum GraphPortCategory
{
    Parameter,
    Signal
}

public sealed record GraphNodeDefinition(
    GraphNodeKind Kind,
    Guid TypeId,
    string Namespace,
    string Name,
    string PortType,
    GraphPortCategory PortCategory,
    string Group)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<Guid, string> BuiltInIds = new()
    {
        [Guid.Parse("19aa71e5-b3c7-453d-860e-baad3b9d0fc4")] = "Float",
        [Guid.Parse("d98eb6f5-ee2e-432a-9450-09e44dcc42e3")] = "FloatX2",
        [Guid.Parse("7ef61949-53c4-41fb-aabd-840b0a4680f7")] = "FloatX3",
        [Guid.Parse("0a613e91-1039-478e-9ca7-b8555a67f988")] = "FloatX4",
        [Guid.Parse("c7410281-9b6e-46f0-9bb7-334cc3b40767")] = "Int32",
        [Guid.Parse("a40e6f94-c2c1-48ed-9a7b-2087c422ad5a")] = "Signal",
        [Guid.Parse("f54171ee-4aa3-498b-b2ff-8c9983fc1501")] = "FloatFCurve",
        [Guid.Parse("149e6e41-9976-4b69-a74c-3f6e6b254e5a")] = "GraphGroup",
        [Guid.Parse("8f4bc759-b775-41ab-a7da-2ac5c17e29ed")] = "Comment",
        [Guid.Parse("e97a5832-31c5-461c-b7bb-c176c804c115")] = "Time",
        [Guid.Parse("c6de2a2a-a44b-41cb-8173-3c417a92bca5")] = "Add",
        [Guid.Parse("a49f3240-e62f-4d86-b3ab-312246e72db2")] = "Subtract",
        [Guid.Parse("3fec134e-1d88-438c-bbf2-4c8ead69bdaa")] = "Multiply",
        [Guid.Parse("3983558b-d822-44be-81c7-274fd62d62be")] = "Divide"
    };

    private static readonly List<string> Warnings = [];
    public static IReadOnlyList<GraphNodeDefinition> All { get; } = LoadAll();
    public static IReadOnlyList<string> LoadWarnings => Warnings;
    public string QualifiedName => $"{Namespace}.{TypeName}";
    public string TypeName { get; init; } = string.Empty;
    public IReadOnlyList<NodeParameterDefinition> Parameters { get; init; } = [];
    public IReadOnlyList<GraphInputPortDefinition>? InputPorts { get; init; }

    public string? GetInputPortType(string? portId)
    {
        if (InputPorts is null) return portId is null ? PortType : null;
        var port = InputPorts.FirstOrDefault(port => port.Id == portId);
        return port is null ? null : port.PortType ?? PortType;
    }

    public static bool TryFind(string? value, out GraphNodeDefinition? definition)
    {
        definition = All.FirstOrDefault(item =>
            string.Equals(item.Kind.Value, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.QualifiedName, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.TypeId.ToString("D"), value, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            var matches = All.Where(item => string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 1) definition = matches[0];
        }
        return definition is not null;
    }

    public static string DisplayPortType(string portType) => portType switch
    {
        "Float" => "Scalar",
        "FloatX2" => "Vector2",
        "FloatX3" => "Vector3",
        "FloatX4" => "Vector4",
        "Int32" => "Int",
        _ when GraphEnumDefinition.TryGetTypeId(portType, out var id) &&
               GraphEnumDefinition.All.TryGetValue(id, out var definition) => definition.Name,
        _ => portType
    };

    private static IReadOnlyList<GraphNodeDefinition> LoadAll()
    {
        var definitions = new List<GraphNodeDefinition>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Nodes");
        var builtInPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in BuiltInIds)
        {
            var path = Path.Combine(directory, name + ".json");
            builtInPaths.Add(path);
            try
            {
                using var stream = File.OpenRead(path);
                AddDefinition(stream, definitions, ids, names, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Trace.TraceWarning("Using embedded node definition for {0}: {1}", path, ex.Message);
                Warnings.Add($"{Path.GetFileName(path)}: {ex.Message} (using built-in definition)");
                using var stream = BuiltInAssets.Open("Nodes", name);
                AddDefinition(stream, definitions, ids, names, id);
            }
        }
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                if (builtInPaths.Contains(path) || Path.GetFileName(path).Equals("nodes.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var stream = File.OpenRead(path);
                    AddDefinition(stream, definitions, ids, names);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    Trace.TraceWarning("Ignoring invalid node definition {0}: {1}", path, ex.Message);
                    Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }

        return definitions.AsReadOnly();
    }

    private static void AddDefinition(Stream stream, List<GraphNodeDefinition> definitions, HashSet<Guid> ids, HashSet<string> names, Guid? expectedId = null)
    {
        var node = JsonSerializer.Deserialize<NodeFile>(stream, SerializerOptions);
        if (node is null || node.SchemaVersion != 1 || node.TypeId == Guid.Empty ||
            (expectedId.HasValue && node.TypeId != expectedId) ||
            !IsIdentifier(node.Kind) || string.IsNullOrWhiteSpace(node.Namespace) ||
            !node.Namespace.Split('.').All(IsIdentifier) ||
            string.IsNullOrWhiteSpace(node.Name) || string.IsNullOrWhiteSpace(node.Group) ||
            string.IsNullOrWhiteSpace(node.PortType) ||
            !Enum.TryParse(node.PortCategory, out GraphPortCategory category) || !Enum.IsDefined(category))
            throw new InvalidDataException("Invalid node definition.");
        var qualifiedName = $"{node.Namespace}.{node.Kind}";
        if (node.Namespace == "LiberaFx" && BuiltInIds.Any(item => item.Key != node.TypeId && item.Value == node.Kind))
            throw new InvalidDataException("A built-in qualified name belongs to another UUID.");
        if (ids.Contains(node.TypeId) || names.Contains(qualifiedName))
            throw new InvalidDataException("Duplicate node type UUID or qualified name.");
        var parameters = new List<NodeParameterDefinition>();
        List<GraphInputPortDefinition>? inputs = null;
        if (node.InputPorts is not null)
        {
            inputs = [];
            var inputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in node.InputPorts)
            {
                if (port is null || !IsIdentifier(port.Id) || !inputIds.Add(port.Id!) ||
                    string.IsNullOrWhiteSpace(port.Name) ||
                    (port.PortType is not null && (string.IsNullOrWhiteSpace(port.PortType) || port.PortType.Length > 128)) ||
                    inputs.Count >= 32)
                    throw new InvalidDataException("Invalid input port definition.");
                inputs.Add(new GraphInputPortDefinition(port.Id!, port.Name, port.PortType));
            }
        }
        var parameterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in node.Parameters ?? [])
        {
            if (parameter is null || !IsIdentifier(parameter.Id) || !parameterIds.Add(parameter.Id!) ||
                string.IsNullOrWhiteSpace(parameter.Name) || string.IsNullOrWhiteSpace(parameter.Type) ||
                parameter.Type.Length > 128 || !NodeParameter.IsValidValue(parameter.Type, parameter.DefaultValue))
                throw new InvalidDataException("Invalid node parameter definition.");
            if (parameter.Type.StartsWith("enum:", StringComparison.Ordinal) &&
                (!GraphEnumDefinition.TryGetTypeId(parameter.Type, out var enumId) ||
                 !GraphEnumDefinition.All.TryGetValue(enumId, out var enumDefinition) ||
                 !enumDefinition.Values.Any(item => item.Value == parameter.DefaultValue.GetInt32())))
                throw new InvalidDataException("Unknown enum or invalid default enum value.");
            NodeParameterEditorDefinition? editor = null;
            if (parameter.Editor is { } config)
            {
                if (string.IsNullOrWhiteSpace(config.Kind) || config.Kind.Length > 128 ||
                    (config.Step is { } step && (!double.IsFinite(step) || step <= 0)) ||
                    (config.Minimum is { } minimum && !double.IsFinite(minimum)) ||
                    (config.Maximum is { } maximum && !double.IsFinite(maximum)))
                    throw new InvalidDataException("Invalid parameter editor definition.");
                if (config.Kind == "radio" && parameter.Type != "bool")
                    throw new InvalidDataException("Radio editor requires a bool parameter.");
                if (config.Kind == "slider" &&
                    (parameter.Type is not ("float" or "int") ||
                     config.Minimum is not { } min || config.Maximum is not { } max || min >= max ||
                     (parameter.Type == "int" && (min < int.MinValue || max > int.MaxValue ||
                         Math.Truncate(min) != min || Math.Truncate(max) != max ||
                         (config.Step is { } intStep && Math.Truncate(intStep) != intStep)))))
                    throw new InvalidDataException("Invalid slider editor definition.");
                editor = new NodeParameterEditorDefinition(config.Kind, config.Minimum, config.Maximum, config.Step);
            }
            if (parameter.EnabledWhen is { Expected.ValueKind: JsonValueKind.Undefined })
                throw new InvalidDataException("Parameter enable condition requires a value.");
            var condition = parameter.EnabledWhen is { } when
                ? new NodeParameterCondition(when.Parameter ?? "", when.Expected.Clone()) : null;
            parameters.Add(new NodeParameterDefinition(parameter.Id!, parameter.Name, parameter.Type,
                parameter.DefaultValue.Clone()) { Editor = editor, EnabledWhen = condition });
        }
        foreach (var parameter in parameters)
        {
            if (parameter.EnabledWhen is not { } condition) continue;
            var dependency = parameters.FirstOrDefault(item => item.Id == condition.Parameter);
            if (dependency is null || dependency.Id == parameter.Id ||
                !NodeParameter.IsValidValue(dependency.Type, condition.Expected))
                throw new InvalidDataException("Invalid parameter enable condition.");
        }
        if (node.TypeId == Guid.Parse("8f4bc759-b775-41ab-a7da-2ac5c17e29ed") &&
            (node.Kind != "Comment" ||
             !parameters.Any(item => item.Id == "text" && item.Type == "string") ||
             !parameters.Any(item => item.Id == "width" && item.Type == "float") ||
             !parameters.Any(item => item.Id == "height" && item.Type == "float") ||
             !parameters.Any(item => item.Id == "image" && item.Type == "commentImage")))
            throw new InvalidDataException("Invalid built-in comment definition.");
        ids.Add(node.TypeId);
        names.Add(qualifiedName);
        var kind = new GraphNodeKind(BuiltInIds.GetValueOrDefault(node.TypeId) ?? node.TypeId.ToString("D"));
        definitions.Add(new GraphNodeDefinition(kind, node.TypeId, node.Namespace, node.Name, node.PortType, category, node.Group)
        {
            TypeName = node.Kind!,
            Parameters = parameters.AsReadOnly(),
            InputPorts = inputs?.AsReadOnly()
        });
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private sealed class NodeFile
    {
        public int SchemaVersion { get; set; }
        public Guid TypeId { get; set; }
        public string? Namespace { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Group { get; set; }
        public string? PortType { get; set; }
        public string? PortCategory { get; set; }
        public List<NodeParameterFile>? Parameters { get; set; }
        public List<InputPortFile>? InputPorts { get; set; }
    }

    private sealed class InputPortFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? PortType { get; set; }
    }

    private sealed class NodeParameterFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public JsonElement DefaultValue { get; set; }
        public ParameterEditorFile? Editor { get; set; }
        public ParameterConditionFile? EnabledWhen { get; set; }
    }

    private sealed class ParameterConditionFile
    {
        public string? Parameter { get; set; }
        [JsonPropertyName("equals")]
        public JsonElement Expected { get; set; }
    }

    private sealed class ParameterEditorFile
    {
        public string? Kind { get; set; }
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }
        public double? Step { get; set; }
    }
}

public sealed record GraphInputPortDefinition(string Id, string Name, string? PortType = null);
