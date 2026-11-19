using System.Text.Json;

namespace LiberaFx.Models.Graph;

public sealed record GraphEnumValue(int Value, string Name)
{
    public string? DisplayNameKey { get; init; }
}

public sealed record GraphEnumDefinition(Guid TypeId, string Namespace, string Name, IReadOnlyList<GraphEnumValue> Values)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly List<string> Warnings = [];
    public static IReadOnlyDictionary<Guid, GraphEnumDefinition> All { get; } = LoadAll();
    public static IReadOnlyList<string> LoadWarnings => Warnings;
    public string QualifiedName => $"{Namespace}.{Name}";

    public static bool TryGetTypeId(string type, out Guid id)
    {
        id = Guid.Empty;
        return type.StartsWith("enum:", StringComparison.Ordinal) && Guid.TryParse(type[5..], out id);
    }

    private static IReadOnlyDictionary<Guid, GraphEnumDefinition> LoadAll()
    {
        var result = new Dictionary<Guid, GraphEnumDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Enums");
        var builtIns = new Dictionary<Guid, string>
        {
            [Guid.Parse("c0bb0566-0892-4c0f-b8eb-ce5a38d3f0b2")] = "Interpolation",
            [Guid.Parse("c1c90dae-c39c-46c9-bc67-c0b6fd7cf06b")] = "CommentFormat",
            [Guid.Parse("1e309707-1fa0-4d38-aed9-42d000000001")] = "PropertyValueType",
            [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000201")] = "TransformOrder",
            [Guid.Parse("b17b0000-0000-4000-8000-000000000101")] = "BillboardMode",
            [Guid.Parse("4a94e882-39e8-4efb-92c7-180000000030")] = "LifecyclePhase",
            [Guid.Parse("4a94e882-39e8-4efb-92c7-180000000040")] = "HostQualityTier",
            [Guid.Parse("4a94e882-39e8-4efb-92c7-180000000041")] = "HostQualityComparison"
        };
        var builtInPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in builtIns)
        {
            var path = Path.Combine(directory, name + ".json");
            builtInPaths.Add(path);
            try
            {
                using var stream = File.OpenRead(path);
                Add(stream, result, names, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Warnings.Add($"{name}.json: {ex.Message} (using built-in definition)");
                using var stream = BuiltInAssets.Open("Enums", name);
                Add(stream, result, names, id);
            }
        }
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                if (builtInPaths.Contains(path)) continue;
                try
                {
                    using var stream = File.OpenRead(path);
                    Add(stream, result, names);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        return result;
    }

    private static void Add(Stream stream, Dictionary<Guid, GraphEnumDefinition> result, HashSet<string> names, Guid? expectedId = null)
    {
        var file = JsonSerializer.Deserialize<EnumFile>(stream, Options);
        if (file is null || file.SchemaVersion != 1 || file.TypeId == Guid.Empty ||
            (expectedId.HasValue && file.TypeId != expectedId) ||
            string.IsNullOrWhiteSpace(file.Namespace) || string.IsNullOrWhiteSpace(file.Name) ||
            file.Values is not { Count: > 0 } ||
            file.Values.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name)) ||
            file.Values.Select(item => item!.Value).Distinct().Count() != file.Values.Count ||
            file.Values.Select(item => item!.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Values.Count ||
            result.ContainsKey(file.TypeId) || !names.Add($"{file.Namespace}.{file.Name}"))
            throw new InvalidDataException("Invalid or duplicate enum definition.");
        result.Add(file.TypeId, new GraphEnumDefinition(file.TypeId, file.Namespace, file.Name,
            file.Values.Select(item => new GraphEnumValue(item!.Value, item.Name!)
                { DisplayNameKey = item.DisplayNameKey }).ToArray()));
    }

    private sealed class EnumFile
    {
        public int SchemaVersion { get; set; }
        public Guid TypeId { get; set; }
        public string? Namespace { get; set; }
        public string? Name { get; set; }
        public List<EnumValueFile?>? Values { get; set; }
    }

    private sealed class EnumValueFile
    {
        public int Value { get; set; }
        public string? Name { get; set; }
        public string? DisplayNameKey { get; set; }
    }
}
