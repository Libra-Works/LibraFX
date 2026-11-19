using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed record StandardGroupParameterMetadata(Guid ItemId, string NameKey, string DescriptionKey);

public sealed record StandardGroupModuleDefinition(GroupAsset Asset, StandardGroupMetadata Metadata);

public sealed record StandardGroupMetadata(
    Guid Id,
    string NameKey,
    string DescriptionKey,
    string CategoryKey,
    string PhaseKey,
    string UnitsKey,
    string InputsKey,
    string OutputsKey,
    string StateKey,
    string BackendKey,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<StandardGroupParameterMetadata> Parameters)
{
    public bool Legacy { get; init; }
}

/// <summary>
/// Loads read-only shipped graph groups from the standard manifest and registered providers.
/// </summary>
public sealed class StandardGroupAssetLibrary
{
    public const string ManifestFileName = "manifest.json";
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Modules");

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly GroupAssetLibrary _assets;
    private readonly Dictionary<Guid, StandardGroupModuleDefinition> _registeredModules = [];
    private IReadOnlyDictionary<Guid, StandardGroupMetadata>? _metadata;

    public StandardGroupAssetLibrary(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? DefaultDirectory);
        _assets = new GroupAssetLibrary(_directory);
    }

    public IReadOnlyList<GroupAsset> LoadAll(out IReadOnlyList<string> warnings)
    {
        var assets = new List<GroupAsset>();
        var errors = new List<string>();
        IReadOnlyDictionary<Guid, StandardGroupMetadata> metadata;
        try { metadata = ReadMetadata(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            warnings = [$"{ManifestFileName}: {ex.Message}"];
            return [];
        }

        foreach (var module in metadata.Values.Concat(_registeredModules.Values.Select(item => item.Metadata))
                     .OrderBy(item => item.Id))
        {
            try { assets.Add(Load(module.Id)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
                errors.Add($"{module.Id:N}: {ex.Message}");
            }
        }

        var registeredFiles = metadata.Keys.Select(id => id.ToString("N") + GroupAssetLibrary.FileExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_directory))
            foreach (var path in Directory.EnumerateFiles(_directory, "*" + GroupAssetLibrary.FileExtension)
                         .Where(path => !registeredFiles.Contains(Path.GetFileName(path))))
                errors.Add($"{Path.GetFileName(path)}: The standard asset is not registered in {ManifestFileName}.");

        warnings = errors.AsReadOnly();
        return assets.AsReadOnly();
    }

    public GroupAsset Load(Guid id)
    {
        if (_registeredModules.TryGetValue(id, out var registered))
            return registered.Asset with { Origin = GroupAssetOrigin.Standard };
        if (!ReadMetadata().TryGetValue(id, out var metadata))
        {
            throw new FileNotFoundException($"Standard group asset {id} is not registered.");
        }
        if (id == BillboardGraphLibrary.AssetId)
        {
            var graph = BillboardGraphLibrary.Create("Billboard");
            var generated = new GroupAsset(5, id, "Billboard", 1, true, graph) { Origin = GroupAssetOrigin.Standard };
            ValidateDefinition(new(generated, metadata));
            return generated;
        }
        var asset = _assets.Load(id);
        if (!asset.Locked)
            throw new InvalidDataException($"Standard group asset {id} must be shipped locked.");
        return asset with { Origin = GroupAssetOrigin.Standard };
    }

    public GroupAsset LoadRevision(Guid id, long revision)
    {
        if (_registeredModules.TryGetValue(id, out var registered))
        {
            if (registered.Asset.Revision != revision)
                throw new FileNotFoundException($"Standard group asset {id} revision {revision} is not registered.");
            return registered.Asset with { Origin = GroupAssetOrigin.Standard };
        }
        if (!ReadMetadata().ContainsKey(id)) throw new FileNotFoundException($"Standard group asset {id} is not registered.");
        if (id == BillboardGraphLibrary.AssetId && revision == 1)
        {
            return Load(id);
        }
        var asset = _assets.LoadRevision(id, revision);
        if (!asset.Locked)
            throw new InvalidDataException($"Standard group asset {id} revision {revision} must be shipped locked.");
        return asset with { Origin = GroupAssetOrigin.Standard };
    }

    public StandardGroupMetadata? Metadata(Guid id) =>
        _registeredModules.GetValueOrDefault(id)?.Metadata ?? ReadMetadata().GetValueOrDefault(id);

    public void Register(StandardGroupModuleDefinition definition)
    {
        Register([definition]);
    }

    public void Register(IEnumerable<StandardGroupModuleDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var pending = definitions.ToArray();
        if (pending.Any(item => item is null || item.Asset is null || item.Metadata is null) ||
            pending.Select(item => item.Asset.Id).Distinct().Count() != pending.Length)
            throw new InvalidDataException("Standard module registration contains duplicate IDs.");
        var existingIds = ReadMetadata().Keys.Concat(_registeredModules.Keys).ToHashSet();
        foreach (var definition in pending)
        {
            ValidateDefinition(definition);
            if (!existingIds.Add(definition.Asset.Id))
                throw new InvalidOperationException($"Standard group ID {definition.Asset.Id} is already registered.");
        }
        foreach (var definition in pending)
        {
            GroupAssetLibrary.RegisterStandardAssetId(definition.Asset.Id);
            _registeredModules.Add(definition.Asset.Id,
                definition with { Asset = definition.Asset with { Origin = GroupAssetOrigin.Standard } });
        }
    }

    public static bool IsRegisteredId(Guid id)
    {
        if (id == Guid.Empty) return false;
        if (GroupAssetLibrary.IsRegisteredStandardAssetId(id)) return true;
        var manifestPath = Path.Combine(DefaultDirectory, ManifestFileName);
        if (!File.Exists(manifestPath)) return false;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            var modules = JsonSerializer.Deserialize<List<StandardGroupMetadata>>(stream, ManifestOptions)
                ?? throw new InvalidDataException("Standard module manifest is empty.");
            return GroupAssetLibrary.IsRegisteredStandardAssetId(id) || modules.Any(module => module.Id == id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The standard group manifest cannot be read; refusing to write a possibly standard ID.", ex);
        }
    }

    private IReadOnlyDictionary<Guid, StandardGroupMetadata> ReadMetadata()
    {
        if (_metadata is not null) return _metadata;
        var path = Path.Combine(_directory, ManifestFileName);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Standard module manifest is too large.");
        var modules = JsonSerializer.Deserialize<List<StandardGroupMetadata>>(stream, ManifestOptions)
            ?? throw new InvalidDataException("Standard module manifest is empty.");
        var indexed = new Dictionary<Guid, StandardGroupMetadata>();
        foreach (var module in modules)
        {
            try { ValidateMetadata(module); }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("Standard module manifest contains invalid or duplicate metadata.");
            }
            if (!indexed.TryAdd(module.Id, module))
                throw new InvalidDataException("Standard module manifest contains invalid or duplicate metadata.");
        }
        _metadata = indexed;
        return indexed;
    }

    private static void ValidateMetadata(StandardGroupMetadata module)
    {
        if (module is null || module.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(module.NameKey) || string.IsNullOrWhiteSpace(module.DescriptionKey) ||
            string.IsNullOrWhiteSpace(module.CategoryKey) || string.IsNullOrWhiteSpace(module.PhaseKey) ||
            string.IsNullOrWhiteSpace(module.UnitsKey) || string.IsNullOrWhiteSpace(module.InputsKey) ||
            string.IsNullOrWhiteSpace(module.OutputsKey) || string.IsNullOrWhiteSpace(module.StateKey) ||
            string.IsNullOrWhiteSpace(module.BackendKey) ||
            module.Keywords is null || module.Keywords.Count is < 1 or > 64 ||
            module.Keywords.Any(string.IsNullOrWhiteSpace) ||
            module.Parameters is null || module.Parameters.Count > 64 ||
            module.Parameters.Any(parameter => parameter is null || parameter.ItemId == Guid.Empty ||
                string.IsNullOrWhiteSpace(parameter.NameKey) || string.IsNullOrWhiteSpace(parameter.DescriptionKey)) ||
            module.Parameters.Select(parameter => parameter.ItemId).Distinct().Count() != module.Parameters.Count)
            throw new InvalidDataException("Standard module metadata is invalid.");
    }

    private static void ValidateDefinition(StandardGroupModuleDefinition definition)
    {
        ValidateMetadata(definition.Metadata);
        var asset = definition.Asset;
        if (asset.Id != definition.Metadata.Id || asset.Id == Guid.Empty || asset.Revision < 1 ||
            asset.FormatVersion is < 1 or > 5 || !asset.Locked || string.IsNullOrWhiteSpace(asset.Name))
            throw new InvalidDataException("A registered standard group must have matching metadata, a stable ID/revision, and a locked asset.");
        GraphValidator.Validate(asset.Graph);
        var exposed = (asset.Graph.ExposedItems ?? []).Select(item => item.Id).ToHashSet();
        if (definition.Metadata.Parameters.Any(parameter => !exposed.Contains(parameter.ItemId)))
            throw new InvalidDataException("Standard module parameter metadata does not match exposed group parameters.");
    }
}
