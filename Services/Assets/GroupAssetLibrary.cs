using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Assets;

public enum GroupAssetOrigin
{
    Project,
    Standard
}

public sealed record GroupAsset(int FormatVersion, Guid Id, string Name, long Revision, bool Locked, GraphSnapshot Graph)
{
    [JsonIgnore]
    public GroupAssetOrigin Origin { get; init; } = GroupAssetOrigin.Project;
}

public sealed class GroupAssetLibrary
{
    public const string FileExtension = ".lfxgroup";
    private const int CurrentFormatVersion = 5;
    private const long MaxFileBytes = 16 * 1024 * 1024;
    private static readonly ConcurrentDictionary<Guid, byte> StandardAssetIds = new();
    private readonly string _directory;
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    public GroupAssetLibrary(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("An absolute group library directory is required.", nameof(directory));
        _directory = Path.GetFullPath(directory);
    }

    public IReadOnlyList<GroupAsset> LoadAll(out IReadOnlyList<string> warnings)
    {
        var assets = new List<GroupAsset>();
        var errors = new List<string>();
        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*" + FileExtension,
                         new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var asset = Read(path);
                    if (!Path.GetFileName(path).Equals(asset.Id.ToString("N") + FileExtension, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Filename does not match the group ID.");
                    assets.Add(asset);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        warnings = errors.AsReadOnly();
        return assets.AsReadOnly();
    }

    public GroupAsset Load(Guid id)
    {
        var asset = Read(PathFor(id));
        if (asset.Id != id) throw new InvalidDataException("Group ID does not match the filename.");
        return asset;
    }

    public GroupAsset LoadRevision(Guid id, long revision)
    {
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        var current = Load(id);
        if (current.Revision == revision) return current;
        var archived = Read(Path.Combine(_directory, ".versions", id.ToString("N"), $"{revision}.lfxgroup"));
        if (archived.Id != id || archived.Revision != revision)
            throw new InvalidDataException("Archived group revision does not match the requested ID.");
        return archived;
    }

    public GroupAsset Create(string name, GraphSnapshot graph)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(graph);
        var id = Guid.NewGuid();
        EnsureProjectOwnedId(id);
        var asset = new GroupAsset(CurrentFormatVersion, id, name.Trim(), 1, false, graph);
        Validate(asset);
        Write(asset, false);
        return asset;
    }

    public static byte[] SerializeAsset(GroupAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Validate(asset);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(asset, Options);
        if (bytes.LongLength > MaxFileBytes) throw new InvalidDataException("Group asset is too large.");
        return bytes;
    }

    public GroupAsset Save(Guid id, long expectedRevision, string name, GraphSnapshot graph)
    {
        EnsureProjectOwnedId(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(graph);
        using var guard = Lock(id);
        var current = Load(id);
        if (current.Locked) throw new InvalidOperationException("The group asset is locked.");
        if (current.Revision != expectedRevision) throw new InvalidOperationException("The group asset changed; reload it first.");
        var updated = current with { FormatVersion = CurrentFormatVersion, Name = name.Trim(), Revision = checked(current.Revision + 1), Graph = graph };
        Validate(updated);
        Archive(current);
        Write(updated, true);
        return updated;
    }

    public GroupAsset SetLocked(Guid id, bool locked)
    {
        EnsureProjectOwnedId(id);
        using var guard = Lock(id);
        var current = Load(id);
        if (current.Locked == locked) return current;
        var updated = current with { FormatVersion = CurrentFormatVersion, Locked = locked };
        Write(updated, true);
        return updated;
    }

    private GroupAsset Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadAsset(stream);
    }

    internal static GroupAsset ReadAsset(Stream stream)
    {
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Group asset is too large.");
        GroupAsset asset;
        try
        {
            asset = JsonSerializer.Deserialize<GroupAsset>(stream, Options)
                ?? throw new InvalidDataException("Group asset is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Group asset contains invalid JSON.", ex);
        }
        Validate(asset);
        return asset;
    }

    private static void Validate(GroupAsset asset)
    {
        if (asset.FormatVersion is < 1 or > CurrentFormatVersion || asset.Id == Guid.Empty || asset.Revision < 1 ||
            string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 200 || asset.Graph is null)
            throw new InvalidDataException("Invalid group asset metadata.");
        try { GraphValidator.Validate(asset.Graph); }
        catch (ArgumentNullException ex) { throw new InvalidDataException("Incomplete group graph.", ex); }
        if (asset.FormatVersion < 4 && GroupExposedItems.ContainsMetadata(asset.Graph))
            throw new InvalidDataException("This group uses exposed items with an older format version.");
        if (asset.FormatVersion < 5 && SignalTimeline.ContainsKeys(asset.Graph))
            throw new InvalidDataException("Signal timeline keys require group asset format 5 or later.");
    }

    private void Write(GroupAsset asset, bool overwrite)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(asset.Id);
        var temporary = Path.Combine(_directory, $".group-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = SerializeAsset(asset);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private FileStream Lock(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A group ID is required.", nameof(id));
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, $".{id:N}.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private void Archive(GroupAsset asset)
    {
        var directory = Path.Combine(_directory, ".versions", asset.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{asset.Revision}.lfxgroup");
        if (File.Exists(path))
        {
            var existing = Read(path);
            if (existing.Id != asset.Id || existing.Revision != asset.Revision ||
                existing.Name != asset.Name || !GraphSnapshot.ContentEquals(existing.Graph, asset.Graph))
                throw new InvalidDataException("The saved group revision conflicts with the current asset.");
            return;
        }
        var bytes = SerializeAsset(asset);
        var temporary = Path.Combine(directory, $".group-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A group ID is required.", nameof(id));
        return Path.Combine(_directory, id.ToString("N") + FileExtension);
    }

    private static void EnsureProjectOwnedId(Guid id)
    {
        if (StandardGroupAssetLibrary.IsRegisteredId(id))
            throw new InvalidOperationException("Standard group assets are read-only; clone the asset into a project library first.");
    }

    internal static void RegisterStandardAssetId(Guid id) => StandardAssetIds.TryAdd(id, 0);

    internal static bool IsRegisteredStandardAssetId(Guid id) => StandardAssetIds.ContainsKey(id);
}
