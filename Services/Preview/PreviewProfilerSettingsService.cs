using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Services.Settings;

namespace LiberaFx.Services.Preview;

public sealed record PreviewProfilerSettings(bool Nodes, bool Accessors, int IntervalMs)
{
    public static PreviewProfilerSettings Default { get; } = new(true, false, 500);
}

public sealed class PreviewProfilerSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    public PreviewProfilerSettings Current { get; private set; } = PreviewProfilerSettings.Default;
    public string? LoadError { get; private set; }

    public PreviewProfilerSettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataDirectory.Root, "LiberaFx", "preview-profiler.json");
        Load();
    }

    public void Save(PreviewProfilerSettings settings)
    {
        Validate(settings);
        if (LoadError is not null)
        {
            throw new InvalidDataException("Invalid saved profiler settings must be resolved before saving.", new InvalidDataException(LoadError));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))
            ?? throw new InvalidOperationException("The profiler settings directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".preview-profiler-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, true);
            Current = settings;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(property => property.Name).ToArray()
                : [];
            if (properties.Length != 3 || properties.Distinct(StringComparer.Ordinal).Count() != 3 ||
                !properties.ToHashSet(StringComparer.Ordinal).SetEquals(["nodes", "accessors", "intervalMs"]))
            {
                throw new InvalidDataException("Saved profiler settings have an unsupported shape.");
            }

            var nodes = root.GetProperty("nodes");
            var accessors = root.GetProperty("accessors");
            var interval = root.GetProperty("intervalMs");
            if (nodes.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                accessors.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !interval.TryGetInt32(out var intervalMs))
            {
                throw new InvalidDataException("Saved profiler settings have invalid values.");
            }

            var settings = new PreviewProfilerSettings(nodes.GetBoolean(), accessors.GetBoolean(), intervalMs);
            Validate(settings);
            Current = settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LoadError = ex.Message;
        }
    }

    private static void Validate(PreviewProfilerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.IntervalMs is < 100 or > 5000)
        {
            throw new InvalidDataException("Profiler interval must be between 100 and 5000 ms.");
        }
    }
}
