using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed record WindowLayoutChoice(string Name, string DisplayName);

public sealed class WindowLayoutsViewModel : ObservableObject
{
    private readonly DockFactory _factory;
    private readonly MainViewModel _main;
    private readonly WindowLayoutPresetStore _store;
    private WindowLayoutSettings _settings = new();
    private bool _autoSave = true;
    private bool _restored;
    private LayoutWindowBounds? _defaultWindowBounds;
    private string _status = "";
    private string _name = "";
    private WindowLayoutChoice? _selected;

    public ObservableCollection<WindowLayoutChoice> Choices { get; } = [];
    public string ActivePreset => _settings.Selected;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string NewName { get => _name; set => SetProperty(ref _name, value); }
    public WindowLayoutChoice? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                SaveSelectedCommand.NotifyCanExecuteChanged();
                DeleteCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public Func<LayoutWindowBounds?>? CaptureMainWindow { get; set; }
    public Action<LayoutWindowBounds>? RestoreMainWindow { get; set; }
    public Func<LayoutWindowBounds, LayoutWindowBounds>? ClampFloatingWindow { get; set; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand SaveSelectedCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand RestoreDefaultCommand { get; }

    public WindowLayoutsViewModel(DockFactory factory, MainViewModel main, WindowLayoutPresetStore? store = null)
    {
        _factory = factory;
        _main = main;
        _store = store ?? new();
        CreateCommand = new(() => Create(NewName));
        ApplyCommand = new(() => Apply(Selected?.Name ?? "Default"));
        SaveSelectedCommand = new(() => SaveCurrent(Selected!.Name), () => Selected is { Name: not "Default" });
        DeleteCommand = new(() => Delete(Selected!.Name), () => Selected is { Name: not "Default" });
        RestoreDefaultCommand = new(() => Apply("Default"));
        try { _settings = _store.Load(); }
        catch (Exception ex) when (IsLayoutError(ex))
        {
            _autoSave = false;
            Failure("Layouts.LoadFailed", ex);
        }
        RefreshChoices();
        MessageLocalization.Observe(this, static vm => vm.RefreshChoices());
    }

    private void RefreshChoices()
    {
        var selected = Selected?.Name ?? _settings.Selected;
        Choices.Clear();
        Choices.Add(new("Default", LocalizationService.Shared.Get("Layouts.Default")));
        foreach (var preset in _settings.Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Choices.Add(new(preset.Name, preset.Name));
        Selected = Choices.FirstOrDefault(c => c.Name == selected) ?? Choices[0];
        OnPropertyChanged(nameof(ActivePreset));
    }

    public void RestoreWorkspace()
    {
        if (_restored) return;
        _restored = true;
        _defaultWindowBounds = CaptureMainWindow?.Invoke();
        var snapshot = _settings.Workspace ??
            _settings.Presets.FirstOrDefault(p => p.Name == _settings.Selected)?.Layout;
        if (snapshot is null) return;
        try { Restore(snapshot); }
        catch (Exception ex) when (IsLayoutError(ex))
        {
            _autoSave = false;
            _factory.ApplyLayout(_factory.DefaultLayout, ClampFloatingWindow);
            Failure("Layouts.LoadFailed", ex);
        }
    }

    private WindowLayoutSnapshot Capture()
    {
        var snapshot = _factory.CaptureLayout();
        snapshot.MainWindow = CaptureMainWindow?.Invoke();
        return snapshot;
    }

    private void Restore(WindowLayoutSnapshot snapshot)
    {
        _factory.ApplyLayout(snapshot, ClampFloatingWindow);
        if (snapshot.MainWindow is { } bounds) RestoreMainWindow?.Invoke(bounds);
        else if (ReferenceEquals(snapshot, _factory.DefaultLayout) && _defaultWindowBounds is { } defaults)
            RestoreMainWindow?.Invoke(defaults);
    }

    public bool Create(string name)
    {
        try
        {
            name = WindowLayoutPresetStore.ValidateName(name);
            if (_settings.Presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(LocalizationService.Shared.Get("Layouts.NameExists"));
            var next = Copy();
            next.Presets.Add(new() { Name = name, Layout = Capture() });
            next.Selected = name;
            next.Workspace = Capture();
            if (!Commit(next)) return false;
            Selected = Choices.Single(c => c.Name == name);
            NewName = "";
            Success("Layouts.Saved", name);
            return true;
        }
        catch (Exception ex) when (IsLayoutError(ex)) { Failure("Layouts.SaveFailed", ex); return false; }
    }

    public bool SaveCurrent(string name)
    {
        try
        {
            var next = Copy();
            var preset = next.Presets.SingleOrDefault(p => p.Name == name)
                ?? throw new InvalidDataException("Default is protected; create a named preset first.");
            preset.Layout = Capture();
            next.Selected = name;
            next.Workspace = preset.Layout;
            if (!Commit(next)) return false;
            Success("Layouts.Saved", name);
            return true;
        }
        catch (Exception ex) when (IsLayoutError(ex)) { Failure("Layouts.SaveFailed", ex); return false; }
    }

    public bool Apply(string name)
    {
        try
        {
            var snapshot = name == "Default" ? _factory.DefaultLayout :
                _settings.Presets.SingleOrDefault(p => p.Name == name)?.Layout
                ?? throw new InvalidDataException("Window layout preset does not exist.");
            var next = Copy();
            next.Selected = name;
            next.Workspace = snapshot;
            // Check disk before changing the current UI or selected preset.
            if (!Commit(next)) return false;
            Restore(snapshot);
            Selected = Choices.Single(c => c.Name == name);
            Success("Layouts.Applied", name == "Default" ? LocalizationService.Shared.Get("Layouts.Default") : name);
            return true;
        }
        catch (Exception ex) when (IsLayoutError(ex)) { Failure("Layouts.ApplyFailed", ex); return false; }
    }

    public bool Delete(string name)
    {
        try
        {
            var next = Copy();
            var preset = next.Presets.SingleOrDefault(p => p.Name == name)
                ?? throw new InvalidDataException("Default cannot be deleted.");
            next.Presets.Remove(preset);
            if (next.Selected == name) next.Selected = "Default";
            next.Workspace = Capture();
            if (!Commit(next)) return false;
            Selected = Choices[0];
            Success("Layouts.Deleted", name);
            return true;
        }
        catch (Exception ex) when (IsLayoutError(ex)) { Failure("Layouts.SaveFailed", ex); return false; }
    }

    public bool SaveWorkspace()
    {
        if (!_autoSave) return false;
        try
        {
            var next = Copy();
            next.Workspace = Capture();
            return Commit(next);
        }
        catch (Exception ex) when (IsLayoutError(ex)) { Failure("Layouts.SaveFailed", ex); return false; }
    }

    private WindowLayoutSettings Copy() => new()
    {
        Selected = _settings.Selected,
        Workspace = _settings.Workspace,
        Presets = _settings.Presets.Select(p => new WindowLayoutPreset { Name = p.Name, Layout = p.Layout }).ToList()
    };

    private bool Commit(WindowLayoutSettings next)
    {
        try { _store.Save(next); }
        catch (Exception ex) when (IsLayoutError(ex)) { Failure("Layouts.SaveFailed", ex); return false; }
        _settings = next;
        _autoSave = true;
        RefreshChoices();
        return true;
    }

    private static bool IsLayoutError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException;

    private void Failure(string key, Exception ex)
    {
        Status = LocalizationService.Shared.Get(key, _store.Path, ex.Message);
        _main.Log.Add("Layouts", Status);
    }

    private void Success(string key, string name)
    {
        Status = LocalizationService.Shared.Get(key, name);
        _main.Log.Add("Layouts", Status);
    }
}
