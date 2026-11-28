using System.Text.Json;
using Avalonia.Media;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Settings;

public sealed class ColorPaletteService
{
    private const int MaxFavorites = 32;
    private const int MaxRecent = 16;
    private readonly string _path = Path.Combine(AppDataDirectory.Root, "LiberaFx", "color-palette.json");
    private readonly List<RgbaColor> _favorites = [];
    private readonly List<RgbaColor> _recent = [];

    public IReadOnlyList<RgbaColor> Favorites => _favorites;
    public IReadOnlyList<RgbaColor> Recent => _recent;
    public string Status { get; private set; } = "";

    public ColorPaletteService()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Invalid color palette.");
            if (document.RootElement.TryGetProperty("Favorites", out var favorites))
                LoadColors(favorites, _favorites, MaxFavorites);
            if (document.RootElement.TryGetProperty("Recent", out var recent))
                LoadColors(recent, _recent, MaxRecent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Status = $"Could not load colors: {ex.Message}";
        }
    }

    public void AddFavorite(RgbaColor color)
    {
        if (!color.IsValid) { Status = "Invalid color."; return; }
        if (_favorites.Contains(color)) return;
        if (_favorites.Count >= MaxFavorites) { Status = "Favorites are full."; return; }
        _favorites.Add(color);
        Save();
    }

    public void RemoveFavorite(RgbaColor color)
    {
        if (_favorites.Remove(color)) Save();
    }

    public void AddRecent(RgbaColor color)
    {
        if (!color.IsValid) { Status = "Invalid color."; return; }
        _recent.Remove(color);
        _recent.Insert(0, color);
        if (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
        Save();
    }

    private static void LoadColors(JsonElement source, List<RgbaColor> target, int limit)
    {
        if (source.ValueKind != JsonValueKind.Array) return;
        foreach (var value in source.EnumerateArray())
        {
            RgbaColor? parsed = null;
            if (value.ValueKind == JsonValueKind.String && Color.TryParse(value.GetString(), out var color))
                parsed = RgbaColor.FromColor(color);
            else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 4 &&
                     value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number &&
                         item.TryGetDouble(out var number) && double.IsFinite(number)))
                parsed = new RgbaColor(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble(), value[3].GetDouble());
            if (parsed is { IsValid: true } rgba && !target.Contains(rgba)) target.Add(rgba);
            if (target.Count == limit) break;
        }
    }

    private void Save()
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var file = new PaletteFile
            {
                Favorites = _favorites.Select(color => color.Components).ToArray(),
                Recent = _recent.Select(color => color.Components).ToArray()
            };
            File.WriteAllText(temporary, JsonSerializer.Serialize(file));
            File.Move(temporary, _path, overwrite: true);
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save colors: {ex.Message}";
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class PaletteFile
    {
        public double[][]? Favorites { get; set; }
        public double[][]? Recent { get; set; }
    }
}
