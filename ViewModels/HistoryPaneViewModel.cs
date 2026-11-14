using System.Collections.ObjectModel;
using Avalonia;
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

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

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
        Refresh();
    }

    private void Refresh()
    {
        CurrentBranch = _history.CurrentBranch;
        CurrentDescription = _history.CurrentDescription;
        OnPropertyChanged(nameof(CanPinCurrent));
        PinCurrentCommand.NotifyCanExecuteChanged();
        Entries.Clear();
        var snapshot = _history.CreateSnapshot();
        var children = snapshot.Entries.Where(entry => entry.ParentId is not null)
            .GroupBy(entry => entry.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Id).ToArray());
        var indents = new Dictionary<Guid, int> { [snapshot.RootId] = 0 };
        foreach (var entry in snapshot.Entries.Where(entry => entry.ParentId is not null))
        {
            var parentId = entry.ParentId!.Value;
            var siblings = children[parentId];
            indents[entry.Id] = indents[parentId] + (siblings[0] == entry.Id ? 0 : 1);
        }
        foreach (var item in _history.GetItems())
        {
            Entries.Add(new HistoryEntryViewModel(_history, item, indents[item.Id]));
        }
    }
}

public sealed class HistoryEntryViewModel : ObservableObject
{
    private readonly UndoRedoManager _history;

    public Guid Id { get; }

    public string Description { get; }

    public string BranchName { get; }

    public int Depth { get; }

    public bool IsCurrent { get; }

    public bool IsPinned { get; }

    public Thickness Indent { get; }
    public string PinButtonText => IsPinned ? "◆" : "◇";
    public string PinToolTip => LocalizationService.Shared.Get(IsPinned ? "Messages.UnpinState" : "Messages.PinState");

    public IRelayCommand NavigateCommand { get; }

    public IRelayCommand TogglePinCommand { get; }

    public HistoryEntryViewModel(UndoRedoManager history, UndoRedoHistoryItem item, int indent)
    {
        _history = history;
        Id = item.Id;
        Description = item.Description;
        BranchName = item.BranchName;
        Depth = item.Depth;
        IsCurrent = item.IsCurrent;
        IsPinned = item.IsPinned;
        Indent = new Thickness(Math.Min(indent, 12) * 14, 0, 0, 0);
        NavigateCommand = new RelayCommand(() => _history.NavigateTo(Id));
        TogglePinCommand = new RelayCommand(() => _history.SetPinned(Id, !IsPinned));
        MessageLocalization.Observe(this, static entry => entry.OnPropertyChanged(nameof(PinToolTip)));
    }
}
