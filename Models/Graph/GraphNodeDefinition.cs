using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;

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
        [Guid.Parse("149e6e41-9976-4b69-a74c-3f6e6b254e5a")] = "GraphGroup"
    };

    private static readonly List<string> Warnings = [];
    public static IReadOnlyList<GraphNodeDefinition> All { get; } = LoadAll();
    public static IReadOnlyList<string> LoadWarnings => Warnings;
    public string QualifiedName => $"{Namespace}.{TypeName}";
    public string TypeName { get; init; } = string.Empty;

    public static bool TryFind(string? value, out GraphNodeDefinition? definition)
    {
        definition = All.FirstOrDefault(item =>
            string.Equals(item.Kind.Value, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.QualifiedName, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.TypeId.ToString("D"), value, StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }

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
                using var stream = AssetLoader.Open(new Uri($"avares://LiberaFx/Assets/Nodes/{name}.json"));
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
        ids.Add(node.TypeId);
        names.Add(qualifiedName);
        var kind = new GraphNodeKind(BuiltInIds.GetValueOrDefault(node.TypeId) ?? node.TypeId.ToString("D"));
        definitions.Add(new GraphNodeDefinition(kind, node.TypeId, node.Namespace, node.Name, node.PortType, category, node.Group)
        {
            TypeName = node.Kind!
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
    }
}
