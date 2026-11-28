using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Models.Commands;

namespace LiberaFx.Services;

public sealed class UndoHistorySettingsService : ObservableObject
{
    public static UndoHistorySettingsService Shared { get; } = new();
    private readonly string _path = Path.Combine(AppDataDirectory.Root, "LiberaFx", "undo-history.json");
    private int _maximum;
    private string _status = "";
    public bool Unlimited
    {
        get => _maximum == 0;
        set => Apply(value ? 0 : Math.Max(200, _maximum));
    }
    public string MaximumText
    {
        get => (_maximum == 0 ? 200 : _maximum).ToString(CultureInfo.CurrentCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) && count is >= 1 and <= 1000000)
                Apply(count);
            else { _status = LocalizationService.Shared.Get("History.InvalidLimit"); OnPropertyChanged(nameof(Status)); }
        }
    }
    public string Status => _status;
    private UndoHistorySettingsService()
    {
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= 4096)
            {
                var maximum = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path))?.MaximumSavedEntries ?? 0;
                if (maximum is >= 0 and <= 1000000) _maximum = maximum;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        UndoHistoryPreferences.MaximumSavedEntries = _maximum;
    }
    private void Apply(int maximum)
    {
        if (maximum == _maximum) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(maximum)));
            File.Move(temporary, _path, overwrite: true);
            _maximum = maximum; UndoHistoryPreferences.MaximumSavedEntries = maximum;
            _status = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _status = error.Message; }
        OnPropertyChanged(nameof(Unlimited)); OnPropertyChanged(nameof(MaximumText)); OnPropertyChanged(nameof(Status));
    }
    private sealed record Settings(int MaximumSavedEntries);
}
