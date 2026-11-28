using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Commands;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.ViewModels.Panels;

public sealed class HistoryPaneViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private UndoRedoManager _history;
    private string _currentBranch = "Main";
    private string _currentDescription = "Start";

    private const double NodeWidth = 152;
    private const double NodeHeight = 46;
    private const double HorizontalGap = 18;
    private const double VerticalGap = 28;
    private const double CanvasMargin = 20;
    private double _canvasWidth = 320;
    private double _canvasHeight = 200;
    private bool _refreshPending;

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];
    public ObservableCollection<HistoryEdgeViewModel> Edges { get; } = [];

    public double CanvasWidth
    {
        get => _canvasWidth;
        private set => SetProperty(ref _canvasWidth, value);
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set => SetProperty(ref _canvasHeight, value);
    }

    public string CurrentBranch
    {
        get => _currentBranch;
        private set => SetProperty(ref _currentBranch, value);
    }

    public string CurrentDescription
    {
        get => _currentDescription;
        private set => SetProperty(ref _currentDescription, value);
    }

    public bool CanPinCurrent => !_history.IsCurrentPinned;

    public Guid CurrentNodeId => _history.CurrentNodeId;

    public bool DeleteHistoryEntry(Guid nodeId) => _history.DeleteNode(nodeId);

    public IRelayCommand PinCurrentCommand { get; }

    public HistoryPaneViewModel(ActiveGraphProvider activeGraph)
    {
        _activeGraph = activeGraph;
        _history = activeGraph.Graph.History;
        PinCurrentCommand = new RelayCommand(
            () => _history.SetPinned(_history.CurrentNodeId, true),
            () => CanPinCurrent);
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        _history.StateChanged += OnHistoryStateChanged;
        Refresh();
    }

    private void OnActiveGraphChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph))
        {
            return;
        }

        _history.StateChanged -= OnHistoryStateChanged;
        _history = _activeGraph.Graph.History;
        _history.StateChanged += OnHistoryStateChanged;
        Refresh();
    }

    private void OnHistoryStateChanged(object? sender, EventArgs e)
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        CurrentBranch = _history.CurrentBranch;
        CurrentDescription = _history.CurrentDescription;
        OnPropertyChanged(nameof(CanPinCurrent));
        PinCurrentCommand.NotifyCanExecuteChanged();
        Entries.Clear();
        Edges.Clear();
        var items = _history.GetDisplayItems();
        var rootId = items.Single(item => item.ParentId is null).Id;
        var itemsById = items.ToDictionary(item => item.Id);
        var children = items.Where(entry => entry.ParentId is not null)
            .GroupBy(entry => entry.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Id).ToArray());

        var widths = new Dictionary<Guid, double>();
        var measure = new Stack<(Guid Id, bool Visited)>();
        measure.Push((rootId, false));
        while (measure.TryPop(out var item))
        {
            if (!children.TryGetValue(item.Id, out var childIds) || childIds.Length == 0)
            { widths[item.Id] = NodeWidth; continue; }
            if (!item.Visited)
            {
                measure.Push((item.Id, true));
                foreach (var child in childIds) measure.Push((child, false));
            }
            else widths[item.Id] = Math.Max(NodeWidth,
                childIds.Sum(child => widths[child]) + HorizontalGap * (childIds.Length - 1));
        }

        var rootWidth = widths[rootId];
        var maxDepth = 0;
        var positions = new Dictionary<Guid, (double X, double Y)>();
        var layout = new Stack<(Guid Id, double Left, int Depth)>();
        layout.Push((rootId, CanvasMargin, 0));
        while (layout.TryPop(out var next))
        {
            var (id, left, depth) = next;
            maxDepth = Math.Max(maxDepth, depth);
            var subtreeWidth = widths[id];
            var x = left + (subtreeWidth - NodeWidth) * 0.5;
            var y = CanvasMargin + depth * (NodeHeight + VerticalGap);
            positions[id] = (x, y);
            if (!children.TryGetValue(id, out var childIds) || childIds.Length == 0) continue;
            var totalChildrenWidth = childIds.Sum(child => widths[child]) + HorizontalGap * (childIds.Length - 1);
            var childLeft = left + Math.Max(0, (subtreeWidth - totalChildrenWidth) * 0.5);
            foreach (var childId in childIds)
            {
                layout.Push((childId, childLeft, depth + 1));
                childLeft += widths[childId] + HorizontalGap;
            }
        }

        foreach (var item in items.Where(item => item.ParentId is not null))
        {
            var parent = positions[item.ParentId!.Value];
            var child = positions[item.Id];
            Edges.Add(new HistoryEdgeViewModel(new Point(parent.X + NodeWidth * .5, parent.Y + NodeHeight),
                new Point(child.X + NodeWidth * .5, child.Y), item.IsCurrent));
        }
        CanvasWidth = Math.Max(320, rootWidth + CanvasMargin * 2);
        CanvasHeight = Math.Max(180, CanvasMargin * 2 + (maxDepth + 1) * NodeHeight + maxDepth * VerticalGap);

        foreach (var item in itemsById.Values)
        {
            var pos = positions.TryGetValue(item.Id, out var p) ? p : (X: CanvasMargin, Y: CanvasMargin);
            Entries.Add(new HistoryEntryViewModel(_history, item, pos.X, pos.Y, NodeWidth, NodeHeight));
        }
    }
}

public sealed record HistoryEdgeViewModel(Point Start, Point End, bool IsCurrent);

public sealed class HistoryEntryViewModel : ObservableObject
{
    private readonly UndoRedoManager _history;

    public Guid Id { get; }

    public string Description { get; }

    public string BranchName { get; }

    public int Depth { get; }

    public bool IsCurrent { get; }

    public bool IsPinned { get; }
    public bool IsArchive { get; }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public string ToolTipText => IsArchive ? LocalizationService.Shared.Get("History.ArchiveHelp") : $"{Description}\n{BranchName}";
    public string PinButtonText => IsPinned ? "◆" : "◇";
    public string PinToolTip => LocalizationService.Shared.Get(IsPinned ? "Messages.UnpinState" : "Messages.PinState");

    public IRelayCommand NavigateCommand { get; }

    public IRelayCommand TogglePinCommand { get; }

    public HistoryEntryViewModel(UndoRedoManager history, UndoRedoHistoryItem item,
        double x, double y, double width, double height)
    {
        _history = history;
        Id = item.Id;
        Description = item.IsArchive ? LocalizationService.Shared.Get("History.Archive", item.ArchiveCount) : item.Description;
        BranchName = item.BranchName;
        Depth = item.Depth;
        IsCurrent = item.IsCurrent;
        IsPinned = item.IsPinned;
        IsArchive = item.IsArchive;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        NavigateCommand = new RelayCommand(() => { if (IsArchive) _history.ToggleArchive(Id); else _history.NavigateTo(Id); });
        TogglePinCommand = new RelayCommand(() => _history.SetPinned(Id, !IsPinned), () => !IsArchive);
        MessageLocalization.Observe(this, static entry => entry.OnPropertyChanged(nameof(PinToolTip)));
    }
}
