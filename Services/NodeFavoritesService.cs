using System.Text.Json;

namespace LiberaFx.Services;

public sealed class NodeFavoritesService
{
    public static NodeFavoritesService Shared { get; } = new(Path.Combine(AppDataDirectory.Root, "LiberaFx", "node-favorites.json"));
    private readonly string _path;
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
    public event EventHandler? Changed;
    public NodeFavoritesService(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 512 * 1024)
                _favorites.UnionWith((JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 512).Take(4096));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public bool Contains(string id) => _favorites.Contains(id);
    public void Toggle(string id)
    {
        var updated = _favorites.ToHashSet(StringComparer.Ordinal);
        if (!updated.Remove(id)) updated.Add(id);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated.Order(StringComparer.Ordinal)));
        File.Move(temporary, _path, true);
        _favorites.Clear();
        _favorites.UnionWith(updated);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
