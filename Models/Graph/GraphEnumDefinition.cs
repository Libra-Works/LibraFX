using System.Text.Json;
using Avalonia.Platform;

namespace LiberaFx.Models.Graph;

public sealed record GraphEnumValue(int Value, string Name);

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
        var builtIn = Path.Combine(directory, "Interpolation.json");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
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
        if (!File.Exists(builtIn))
        {
            using var stream = AssetLoader.Open(new Uri("avares://LiberaFx/Assets/Enums/Interpolation.json"));
            try { Add(stream, result, names); }
            catch (InvalidDataException ex) { Warnings.Add($"Interpolation.json: {ex.Message}"); }
        }
        return result;
    }

    private static void Add(Stream stream, Dictionary<Guid, GraphEnumDefinition> result, HashSet<string> names)
    {
        var file = JsonSerializer.Deserialize<EnumFile>(stream, Options);
        if (file is null || file.SchemaVersion != 1 || file.TypeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(file.Namespace) || string.IsNullOrWhiteSpace(file.Name) ||
            file.Values is not { Count: > 0 } ||
            file.Values.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name)) ||
            file.Values.Select(item => item!.Value).Distinct().Count() != file.Values.Count ||
            file.Values.Select(item => item!.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Values.Count ||
            result.ContainsKey(file.TypeId) || !names.Add($"{file.Namespace}.{file.Name}"))
            throw new InvalidDataException("Invalid or duplicate enum definition.");
        result.Add(file.TypeId, new GraphEnumDefinition(file.TypeId, file.Namespace, file.Name,
            file.Values.Select(item => new GraphEnumValue(item!.Value, item.Name!)).ToArray()));
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
    }
}
