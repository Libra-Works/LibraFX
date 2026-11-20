using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Commands;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

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
        var snapshot = _history.CreateSnapshot();
        var itemsById = _history.GetItems().ToDictionary(item => item.Id);
        var children = snapshot.Entries.Where(entry => entry.ParentId is not null)
            .GroupBy(entry => entry.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Id).ToArray());

        var widths = new Dictionary<Guid, double>();
        double MeasureWidth(Guid id)
        {
            if (widths.TryGetValue(id, out var cached)) return cached;
            if (!children.TryGetValue(id, out var childIds) || childIds.Length == 0)
                return widths[id] = NodeWidth;
            var total = childIds.Sum(MeasureWidth) + HorizontalGap * (childIds.Length - 1);
            return widths[id] = Math.Max(NodeWidth, total);
        }

        var rootWidth = MeasureWidth(snapshot.RootId);
        var maxDepth = 0;
        var positions = new Dictionary<Guid, (double X, double Y)>();
        void Layout(Guid id, double left, int depth)
        {
            maxDepth = Math.Max(maxDepth, depth);
            var subtreeWidth = widths[id];
            var x = left + (subtreeWidth - NodeWidth) * 0.5;
            var y = CanvasMargin + depth * (NodeHeight + VerticalGap);
            positions[id] = (x, y);
            if (!children.TryGetValue(id, out var childIds) || childIds.Length == 0) return;
            var totalChildrenWidth = childIds.Sum(child => widths[child]) + HorizontalGap * (childIds.Length - 1);
            var childLeft = left + Math.Max(0, (subtreeWidth - totalChildrenWidth) * 0.5);
            foreach (var childId in childIds)
            {
                Layout(childId, childLeft, depth + 1);
                var childPos = positions[childId];
                Edges.Add(new HistoryEdgeViewModel(
                    new Point(x + NodeWidth * 0.5, y + NodeHeight),
                    new Point(childPos.X + NodeWidth * 0.5, childPos.Y),
                    itemsById.TryGetValue(childId, out var childItem) && childItem.IsCurrent));
                childLeft += widths[childId] + HorizontalGap;
            }
        }

        Layout(snapshot.RootId, CanvasMargin, 0);
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

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public string ToolTipText => $"{Description}\n{BranchName}";
    public string PinButtonText => IsPinned ? "◆" : "◇";
    public string PinToolTip => LocalizationService.Shared.Get(IsPinned ? "Messages.UnpinState" : "Messages.PinState");

    public IRelayCommand NavigateCommand { get; }

    public IRelayCommand TogglePinCommand { get; }

    public HistoryEntryViewModel(UndoRedoManager history, UndoRedoHistoryItem item,
        double x, double y, double width, double height)
    {
        _history = history;
        Id = item.Id;
        Description = item.Description;
        BranchName = item.BranchName;
        Depth = item.Depth;
        IsCurrent = item.IsCurrent;
        IsPinned = item.IsPinned;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        NavigateCommand = new RelayCommand(() => _history.NavigateTo(Id));
        TogglePinCommand = new RelayCommand(() => _history.SetPinned(Id, !IsPinned));
        MessageLocalization.Observe(this, static entry => entry.OnPropertyChanged(nameof(PinToolTip)));
    }
}
