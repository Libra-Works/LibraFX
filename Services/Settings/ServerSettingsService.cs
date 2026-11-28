using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Preview;

namespace LiberaFx.Services.Settings;

public sealed class ServerSettingsService : ObservableObject
{
    private readonly string _path;
    private string _mcpPort;
    private string _previewPort;
    private string _statusKey = "Messages.ServerRestartRequired";
    private object?[] _statusArgs = [];

    public int StartupMcpPort { get; }
    public int StartupPreviewPort { get; }
    public string McpPort { get => _mcpPort; set => SetProperty(ref _mcpPort, value); }
    public string PreviewPort { get => _previewPort; set => SetProperty(ref _previewPort, value); }
    public string Status => LocalizationService.Shared.Get(_statusKey, _statusArgs);

    public ServerSettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataDirectory.Root, "LiberaFx", "server-settings.json");
        MessageLocalization.Observe(this, static service => service.OnPropertyChanged(nameof(Status)));
        var settings = new ServerSettings(McpHost.Port, PreviewHost.Port);
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(_path));
                if (loaded is null || !Valid(loaded))
                    throw new InvalidDataException("Ports must be different and between 1024 and 65535.");
                settings = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SetStatus("Messages.LoadServerSettingsFailed", ex.Message);
        }
        StartupMcpPort = settings.McpPort;
        StartupPreviewPort = settings.PreviewPort;
        _mcpPort = settings.McpPort.ToString();
        _previewPort = settings.PreviewPort.ToString();
    }

    public void Save()
    {
        if (!int.TryParse(McpPort, out var mcp) || !int.TryParse(PreviewPort, out var preview) ||
            !Valid(new ServerSettings(mcp, preview)))
        {
            SetStatus("Messages.EnterServerPorts");
            return;
        }
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".server-settings-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ServerSettings(mcp, preview),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
            temporary = null;
            SetStatus("Messages.ServerSettingsSaved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Messages.SaveServerSettingsFailed", ex.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        OnPropertyChanged(nameof(Status));
    }

    private static bool Valid(ServerSettings settings) =>
        settings.McpPort is >= 1024 and <= 65535 &&
        settings.PreviewPort is >= 1024 and <= 65535 && settings.McpPort != settings.PreviewPort;

    private sealed record ServerSettings(int McpPort, int PreviewPort);
}
