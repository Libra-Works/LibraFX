using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Inspector.Abstractions;
using LiberaFx.Services.Settings;

namespace LiberaFx.Services.Extensions;

public sealed class InspectorPluginEntry : ObservableObject
{
    private bool _isTrusted;
    private bool _isEnabled;
    private bool _identityConflict;
    private string _status = string.Empty;

    internal InspectorPluginEntry(InspectorPluginManifest manifest, string directory, string fingerprint)
    {
        Manifest = manifest;
        Directory = directory;
        Fingerprint = fingerprint;
    }

    internal InspectorPluginManifest Manifest { get; }
    internal string Directory { get; }
    internal string Fingerprint { get; }
    public string Id => Manifest.PluginId;
    public string Name => Manifest.DisplayName;
    public string Version => Manifest.Version;
    public string Inspectors => string.Join(", ", Manifest.Inspectors.Select(item =>
        string.IsNullOrWhiteSpace(item.Description) ? item.DisplayName : $"{item.DisplayName} — {item.Description}"));
    public string Types => string.Join(", ", Manifest.Inspectors.SelectMany(item => item.Types).Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal));
    public bool IsCompatible => Manifest.CompatibilityError is null && !_identityConflict;
    public bool IsTrusted
    {
        get => _isTrusted;
        internal set
        {
            if (SetProperty(ref _isTrusted, value))
            {
                OnPropertyChanged(nameof(ActionLabel));
                OnPropertyChanged(nameof(TrustHelp));
            }
        }
    }
    public bool IsEnabled
    {
        get => _isEnabled;
        internal set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(ActionLabel));
                OnPropertyChanged(nameof(TrustHelp));
            }
        }
    }
    public string Status => StatusKey(_status) is { } key ? LocalizationService.Shared.Get(key) : _status;
    public string ActionLabel => LocalizationService.Shared.Get(
        IsEnabled ? "Editor.InspectorDisable" : IsTrusted ? "Editor.InspectorEnable" : "Editor.InspectorTrustEnable");
    public string TrustHelp => LocalizationService.Shared.Get("Editor.InspectorTrustHelp");
    internal void MarkIdentityConflict()
    {
        _identityConflict = true;
        OnPropertyChanged(nameof(IsCompatible));
    }
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(TrustHelp));
        OnPropertyChanged(nameof(Status));
    }

    internal void SetStatus(string status)
    {
        _status = status;
        OnPropertyChanged(nameof(Status));
    }

    private static string? StatusKey(string status) => status switch
    {
        "Enabled for next restart. DLL code runs with the editor user's permissions; no sandbox is provided." =>
            "Editor.InspectorStatusEnabledRestart",
        "Enabled for next restart; this new DLL has not been executed." => "Editor.InspectorStatusEnabledNew",
        "Trusted, disabled. DLL not executed." => "Editor.InspectorStatusTrustedDisabled",
        "Not trusted or enabled. DLL not executed." => "Editor.InspectorStatusUntrusted",
        "Enabled preference ignored because this DLL has not been explicitly trusted." =>
            "Editor.InspectorStatusTrustRequired",
        "Disabled for next restart." => "Editor.InspectorStatusDisabledRestart",
        "Trusted and loaded. DLL code runs with the editor user's permissions; no sandbox is provided." =>
            "Editor.InspectorStatusLoaded",
        _ => null
    };
}

public sealed class InspectorDefaultChoice : ObservableObject
{
    private InspectorDefaultOption _selected;

    internal InspectorDefaultChoice(string type, IEnumerable<InspectorDefaultOption> options,
        string selectedId, bool canEdit, Func<string, string, bool> save)
    {
        Type = type;
        Options = new ObservableCollection<InspectorDefaultOption>(options);
        _selected = Options.FirstOrDefault(option => option.Id == selectedId)
            ?? new InspectorDefaultOption(selectedId, $"Unavailable: {selectedId}");
        if (!Options.Contains(_selected)) Options.Add(_selected);
        CanEdit = canEdit;
        _save = save;
    }

    private readonly Func<string, string, bool> _save;
    public string Type { get; }
    public bool CanEdit { get; }
    public ObservableCollection<InspectorDefaultOption> Options { get; }
    public InspectorDefaultOption Selected
    {
        get => _selected;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selected.Id == value.Id || !_save(Type, value.Id)) return;
            SetProperty(ref _selected, value);
        }
    }
}

public sealed record InspectorDefaultOption(string Id, string Name);
