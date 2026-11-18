namespace LiberaFx.Services;

internal sealed class StartupSetupState
{
    private readonly string _completionPath;

    public StartupSetupState()
    {
        var root = AppDataDirectory.Root;
        var directory = Path.Combine(root, "LiberaFx");
        _completionPath = Path.Combine(directory, "startup-setup.completed");
        IsPending = !File.Exists(_completionPath) &&
            !File.Exists(Path.Combine(directory, "settings.json")) &&
            !File.Exists(Path.Combine(root, "EffectTool", "settings.json")) &&
            !File.Exists(new WindowLayoutPresetStore().Path);
    }

    public bool IsPending { get; }

    public void Complete()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_completionPath)!);
        using var marker = new FileStream(_completionPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
    }
}
