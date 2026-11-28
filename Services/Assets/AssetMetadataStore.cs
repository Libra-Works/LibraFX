using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed record AssetMetadata(
    string Engine,
    string Path,
    string Description = "",
    IReadOnlyList<string>? Tags = null,
    string SemanticUsage = "",
    int AtlasColumns = 1,
    int AtlasRows = 1,
    double FramesPerSecond = 12)
{
    [JsonIgnore]
    public IReadOnlyList<string> NormalizedTags => Tags ?? [];
}

public sealed class AssetMetadataStore
{
    private const int MaximumEntries = 10000;
    private const int MaximumTags = 64;
    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly Dictionary<string, AssetMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public AssetMetadataStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppDataDirectory.Root, "LiberaFx", "asset-metadata.json");
    }

    public AssetMetadata Get(string engine, string path)
    {
        ValidateIdentity(engine, path);
        lock (_sync)
        {
            EnsureLoaded();
            return _metadata.GetValueOrDefault(Key(engine, path)) ?? new AssetMetadata(engine, path);
        }
    }

    public void Set(string engine, string path, AssetMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateIdentity(engine, path);
        var normalized = Validate(metadata with { Engine = engine.Trim(), Path = path.Trim() });
        lock (_sync)
        {
            EnsureLoaded();
            var key = Key(engine, path);
            if (!_metadata.ContainsKey(key) && _metadata.Count >= MaximumEntries)
                throw new InvalidOperationException($"Asset metadata is limited to {MaximumEntries} entries.");
            var hadPrevious = _metadata.TryGetValue(key, out var previous);
            _metadata[key] = normalized;
            try { SaveAtomically(); }
            catch
            {
                if (hadPrevious) _metadata[key] = previous!;
                else _metadata.Remove(key);
                throw;
            }
        }
    }

    public IReadOnlyList<AssetMetadata> All(string? engine = null)
    {
        if (engine is not null) ValidateEngine(engine);
        lock (_sync)
        {
            EnsureLoaded();
            return _metadata.Values
                .Where(metadata => engine is null || string.Equals(metadata.Engine, engine, StringComparison.OrdinalIgnoreCase))
                .OrderBy(metadata => metadata.Engine, StringComparer.OrdinalIgnoreCase)
                .ThenBy(metadata => metadata.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public IReadOnlyList<AssetMetadata> Search(string query, string? engine = null, int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (query is { Length: > 512 }) throw new ArgumentException("The metadata search query is too long.", nameof(query));
        if (engine is not null) ValidateEngine(engine);
        var terms = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        lock (_sync)
        {
            EnsureLoaded();
            return _metadata.Values
                .Where(metadata => engine is null || string.Equals(metadata.Engine, engine, StringComparison.OrdinalIgnoreCase))
                .Where(metadata => terms.All(term =>
                    metadata.Path.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    metadata.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    metadata.SemanticUsage.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    metadata.NormalizedTags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(metadata => metadata.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        if (File.Exists(_filePath))
        {
            var file = new FileInfo(_filePath);
            if (file.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("The local asset metadata file is too large.");
            List<AssetMetadata?> entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<AssetMetadata?>>(File.ReadAllText(_filePath))
                    ?? throw new InvalidDataException("The local asset metadata file is invalid.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The local asset metadata file is invalid.", exception);
            }
            if (entries.Count > MaximumEntries) throw new InvalidDataException("The local asset metadata file has too many entries.");
            foreach (var item in entries)
            {
                if (item is null) throw new InvalidDataException("The local asset metadata file contains an empty entry.");
                AssetMetadata normalized;
                try { normalized = Validate(item); }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The local asset metadata file contains invalid values.", exception);
                }
                if (!_metadata.TryAdd(Key(item.Engine, item.Path), normalized))
                    throw new InvalidDataException("The local asset metadata file contains duplicate assets.");
            }
        }
        _loaded = true;
    }

    private void SaveAtomically()
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The metadata file has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".asset-metadata-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_metadata.Values));
            File.Move(temporary, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static AssetMetadata Validate(AssetMetadata metadata)
    {
        ValidateIdentity(metadata.Engine, metadata.Path);
        var description = (metadata.Description ?? string.Empty).Trim();
        var usage = (metadata.SemanticUsage ?? string.Empty).Trim();
        if (description.Length > 4096 || usage.Length > 256 ||
            description.Any(char.IsControl) || usage.Any(char.IsControl) ||
            metadata.AtlasColumns is < 1 or > 4096 || metadata.AtlasRows is < 1 or > 4096 ||
            !double.IsFinite(metadata.FramesPerSecond) || metadata.FramesPerSecond is < 0 or > 10000)
            throw new ArgumentException("Asset metadata contains invalid values.", nameof(metadata));
        var tags = (metadata.Tags ?? []).Select(tag => (tag ?? string.Empty).Trim())
            .Where(tag => tag.Length != 0).ToArray();
        if (tags.Length > MaximumTags || tags.Any(tag => tag.Length > 64 || tag.Any(char.IsControl)) ||
            tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tags.Length)
            throw new ArgumentException("Asset metadata tags are invalid.", nameof(metadata));
        return metadata with
        {
            Engine = metadata.Engine.Trim(),
            Path = metadata.Path.Trim(),
            Description = description,
            SemanticUsage = usage,
            Tags = Array.AsReadOnly(tags)
        };
    }

    private static void ValidateIdentity(string engine, string path)
    {
        ValidateEngine(engine);
        if (!RenderNodeSchema.IsSafeAssetPath(path))
            throw new ArgumentException("The engine asset path is invalid.", nameof(path));
    }

    private static void ValidateEngine(string engine)
    {
        if (string.IsNullOrWhiteSpace(engine) || engine.Length > 64 ||
            engine.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("The engine identifier is invalid.", nameof(engine));
    }

    private static string Key(string engine, string path) => $"{engine.Trim()}\u001f{path.Trim()}";
}
