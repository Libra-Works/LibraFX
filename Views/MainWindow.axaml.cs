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
    private AnythingSearchWindow? _anythingSearchWindow;
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
        Closed += (_, _) => DisposeSharedAssetBrowser();
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
        if (DataContext is MainViewModel model && sender is Control { Tag: string id }) model.ShowDockPanel(id);
    }

    private void OnRestoreDefaultLayoutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.WindowLayouts.Apply("Default");
    }

    private void OnMainKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (model.Shortcuts.Matches("search.anything", e))
        {
            OnAnythingSearchClick(sender, e);
            e.Handled = true;
        }
        else if (model.Shortcuts.Matches("undo", e) && model.UndoCommand.CanExecute(null))
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

    private async void OnAnythingSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (_anythingSearchWindow is not null) { _anythingSearchWindow.Activate(); return; }
        var additional = new List<AnythingSearchItem>
        {
            AnythingSearchWindow.Item("window/assets", "Search.Panel", LocalizationService.Shared.Get("Asset.Title"),
                "", () => OnContentClick(null, new Avalonia.Interactivity.RoutedEventArgs()), "assets"),
            AnythingSearchWindow.Item("window/sprite", "Search.Panel", LocalizationService.Shared.Get("Sprite.Title"),
                "", () => model.ShowDockPanel("SpriteEditor"), "sprite"),
            AnythingSearchWindow.Item("window/log", "Search.Panel", LocalizationService.Shared.Get("Editor.Log"),
                "", () => model.ShowDockPanel("Log"), "log")
        };
        if (SharedAssetBrowserViewModel is { } assets)
        {
            var peerId = assets.SelectedPeer?.ClientId;
            var peerLabel = assets.SelectedPeer?.Label ?? "";
            foreach (var asset in assets.Assets.ToArray())
                additional.Add(AnythingSearchWindow.Item("engine-asset/" + peerId + "/" + asset.Path, "Search.Asset",
                    asset.Name, peerLabel + " / " + asset.Path, () =>
                    {
                        OnContentClick(null, new Avalonia.Interactivity.RoutedEventArgs());
                        if (SharedAssetBrowserViewModel is { } current &&
                            current.SelectedPeer?.ClientId == peerId && current.Assets.Contains(asset))
                            current.SelectedAsset = asset;
                    }));
        }
        var window = new AnythingSearchWindow(model, additional);
        _anythingSearchWindow = window;
        try { await window.ShowDialog(this); }
        finally { _anythingSearchWindow = null; }
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

    private void OnContentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenSharedAssetBrowser();
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