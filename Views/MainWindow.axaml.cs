using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using LiberaFx.ViewModels;
using LiberaFx.Services;
using LiberaFx.Models;

namespace LiberaFx.Views;

public partial class MainWindow : Window
{
    private LogWindow? _logWindow;
    private McpWindow? _mcpWindow;
    private PreviewProfilerWindow? _profilerWindow;
    private PreviewConnectionsWindow? _connectionsWindow;
    private EngineAssetsWindow? _contentWindow;
    private ProblemsWindow? _problemsWindow;
    private WindowLayoutsWindow? _windowLayoutsWindow;
    private LayoutWindowBounds? _normalLayoutBounds;
    private MainViewModel? _groupModel;
    private bool _checkingGroupUpdates;
    private bool _pendingGroupCheck;
    private readonly HashSet<(string Document, Guid Node, long Revision)> _deferredGroupUpdates = [];
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnGpuDataContextChanged;
        Closed += (_, _) => DetachGpuWorkspace();
        DataContextChanged += OnGroupDataContextChanged;
        Opened += (_, _) => QueueGroupUpdateCheck();
        Opened += (_, _) => RestoreWindowWorkspace();
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel model) model.WindowLayouts.SaveWorkspace();
        };
        Closed += (_, _) => DetachGroupEvents();
        PositionChanged += (_, _) => RememberNormalWindowBounds();
        SizeChanged += (_, _) => RememberNormalWindowBounds();
    }

    private void RememberNormalWindowBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible || Bounds.Width < 100 || Bounds.Height < 100) return;
        _normalLayoutBounds = new()
        {
            X = Position.X, Y = Position.Y, Width = Bounds.Width, Height = Bounds.Height
        };
    }

    private LayoutWindowBounds CaptureWindowBounds()
    {
        var bounds = _normalLayoutBounds;
        return new()
        {
            X = bounds?.X ?? Position.X,
            Y = bounds?.Y ?? Position.Y,
            Width = bounds?.Width ?? Math.Max(400, Bounds.Width),
            Height = bounds?.Height ?? Math.Max(300, Bounds.Height),
            Maximized = WindowState == WindowState.Maximized
        };
    }

    private LayoutWindowBounds ClampLayoutBounds(LayoutWindowBounds bounds)
    {
        var point = new PixelPoint((int)bounds.X, (int)bounds.Y);
        var scaling = Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(point))?.Scaling ??
            Screens.Primary?.Scaling ?? RenderScaling;
        return WindowLayoutGeometry.Clamp(bounds, Screens.All.Select(s => s.WorkingArea), scaling);
    }

    private void RestoreWindowWorkspace()
    {
        if (DataContext is not MainViewModel model) return;
        model.EnableDockWindows();
        RememberNormalWindowBounds();
        model.WindowLayouts.CaptureMainWindow = CaptureWindowBounds;
        model.WindowLayouts.ClampFloatingWindow = ClampLayoutBounds;
        model.WindowLayouts.RestoreMainWindow = bounds =>
        {
            bounds = ClampLayoutBounds(bounds);
            WindowState = WindowState.Normal;
            Width = bounds.Width;
            Height = bounds.Height;
            Position = new PixelPoint((int)bounds.X, (int)bounds.Y);
            if (bounds.Maximized) WindowState = WindowState.Maximized;
        };
        model.WindowLayouts.RestoreWorkspace();
    }

    private void OnDockPanelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is MenuItem { Tag: string id }) model.ShowDockPanel(id);
    }

    private void OnProblemsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (_problemsWindow is not null) { _problemsWindow.Activate(); return; }
        _problemsWindow = new ProblemsWindow(model);
        _problemsWindow.Closed += (_, _) => _problemsWindow = null;
        _problemsWindow.Show(this);
    }

    private void OnWindowLayoutsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (_windowLayoutsWindow is not null) { _windowLayoutsWindow.Activate(); return; }
        _windowLayoutsWindow = new WindowLayoutsWindow(model.WindowLayouts);
        _windowLayoutsWindow.Closed += (_, _) => _windowLayoutsWindow = null;
        _windowLayoutsWindow.Show(this);
    }

    private void OnRestoreDefaultLayoutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.WindowLayouts.Apply("Default");
    }

    private void OnMainKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (model.Shortcuts.Matches("undo", e) && model.UndoCommand.CanExecute(null))
        {
            model.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if (model.Shortcuts.Matches("redo", e) && model.RedoCommand.CanExecute(null))
        {
            model.RedoCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var settings = new SettingsWindow
        {
            DataContext = DataContext
        };
        await settings.ShowDialog(this);
        QueueGroupUpdateCheck();
    }

    private async void OnHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await new HelpWindow().ShowDialog(this);

    private async void OnAboutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await new AboutWindow().ShowDialog(this);

    private void OnLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_logWindow is not null)
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow { DataContext = DataContext };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show(this);
    }

    private void OnMcpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mcpWindow is not null)
        {
            _mcpWindow.Activate();
            return;
        }

        _mcpWindow = new McpWindow { DataContext = DataContext };
        _mcpWindow.Closed += (_, _) => _mcpWindow = null;
        _mcpWindow.Show(this);
    }

    private void OnProfilerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_profilerWindow is not null) { _profilerWindow.Activate(); return; }
        _profilerWindow = new PreviewProfilerWindow { DataContext = DataContext };
        _profilerWindow.Closed += (_, _) => _profilerWindow = null;
        _profilerWindow.Show(this);
    }

    private void OnConnectionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_connectionsWindow is not null) { _connectionsWindow.Activate(); return; }
        _connectionsWindow = new PreviewConnectionsWindow { DataContext = DataContext };
        _connectionsWindow.Closed += (_, _) => _connectionsWindow = null;
        _connectionsWindow.Show(this);
    }

    private void OnContentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (_contentWindow is not null) { _contentWindow.Activate(); return; }
        _contentWindow = new EngineAssetsWindow(model);
        _contentWindow.Closed += (_, _) => _contentWindow = null;
        _contentWindow.Show(this);
    }

    private async void OnCloseDocumentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.ActiveDocument is not { } document)
        {
            return;
        }

        if (!document.IsDirty)
        {
            viewModel.CloseActiveDocument(discardChanges: false);
            return;
        }

        var dialog = new UnsavedChangesWindow
        {
            DataContext = viewModel
        };
        var choice = await dialog.ShowDialog<UnsavedChangesChoice>(this);
        if (choice == UnsavedChangesChoice.Discard)
        {
            viewModel.CloseActiveDocument(discardChanges: true);
        }
        else if (choice == UnsavedChangesChoice.Save &&
                 await viewModel.SaveAllDocumentsAsync())
        {
            viewModel.CloseActiveDocument(discardChanges: false);
        }
    }

    private void OnGroupDataContextChanged(object? sender, EventArgs e)
    {
        DetachGroupEvents();
        if (DataContext is not MainViewModel model) return;
        _groupModel = model;
        model.GroupAssets.Changed += OnGroupCatalogChanged;
        model.ActiveGraphProvider.PropertyChanged += OnActiveGraphChanged;
        if (IsVisible) QueueGroupUpdateCheck();
    }

    private void DetachGroupEvents()
    {
        if (_groupModel is null) return;
        _groupModel.GroupAssets.Changed -= OnGroupCatalogChanged;
        _groupModel.ActiveGraphProvider.PropertyChanged -= OnActiveGraphChanged;
        _groupModel = null;
    }

    private void OnGroupCatalogChanged(object? sender, EventArgs e) => QueueGroupUpdateCheck();

    private void OnActiveGraphChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveGraphProvider.Graph)) QueueGroupUpdateCheck();
    }

    private void QueueGroupUpdateCheck()
    {
        _pendingGroupCheck = true;
        Dispatcher.UIThread.Post(async () => await CheckGroupUpdatesAsync());
    }

    private async Task CheckGroupUpdatesAsync()
    {
        if (_checkingGroupUpdates || !IsVisible || _groupModel is not { } model) return;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.Windows.OfType<SettingsWindow>().Any(window => window.IsVisible)) return;
        _pendingGroupCheck = false;
        _checkingGroupUpdates = true;
        try
        {
            var batches = model.Documents.Select(document => new
            {
                Document = document,
                Candidates = document.Workspace.FindGroupUpdates()
                    .Where(item => !_deferredGroupUpdates.Contains((document.Id, item.NodeId, item.LatestRevision)))
                    .ToArray()
            }).Where(batch => batch.Candidates.Length != 0).ToArray();
            if (batches.Length == 0) return;
            var candidates = batches.SelectMany(batch => batch.Candidates).ToArray();
            var update = await new GroupUpdatesWindow(candidates).ShowDialog<bool>(this);
            var prepared = update
                ? batches.Where(batch => model.Documents.Contains(batch.Document))
                    .Select(batch => (batch.Document, Updates: batch.Document.Workspace.PrepareGroupUpdates(batch.Candidates)))
                    .ToArray()
                : [];
            foreach (var item in prepared)
                if (item.Updates is not null) item.Document.Workspace.CommitGroupUpdates(item.Updates);
            foreach (var batch in batches)
            {
                if (!model.Documents.Contains(batch.Document)) continue;
                foreach (var item in batch.Candidates.Where(item => !update || item.HasLocalChanges))
                    _deferredGroupUpdates.Add((batch.Document.Id, item.NodeId, item.LatestRevision));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or ArgumentException)
        {
            var detail = MessageLocalization.Error(ex.Message);
            var message = LocalizationService.Shared.Get("Editor.GroupUpdateError", detail);
            model.Log.Add("Groups", message);
            model.ActiveGraphProvider.Graph.SetConnectionStatus("Editor.GroupUpdateError", detail);
        }
        finally
        {
            _checkingGroupUpdates = false;
            if (_pendingGroupCheck) QueueGroupUpdateCheck();
        }
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (DataContext is not MainViewModel viewModel ||
            viewModel.Documents.All(document => !document.IsDirty))
        {
            return true;
        }

        var dialog = new UnsavedChangesWindow
        {
            DataContext = viewModel
        };
        var choice = await dialog.ShowDialog<UnsavedChangesChoice>(this);
        return choice switch
        {
            UnsavedChangesChoice.Save => await viewModel.SaveAllDocumentsAsync(),
            UnsavedChangesChoice.Discard => true,
            _ => false
        };
    }
}