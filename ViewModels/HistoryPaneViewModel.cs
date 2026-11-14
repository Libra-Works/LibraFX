using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Commands;

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
        foreach (var item in _history.GetItems())
        {
            Entries.Add(new HistoryEntryViewModel(_history, item));
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

    public string DisplayDescription =>
        $"{(IsCurrent ? "> " : string.Empty)}{(IsPinned ? "[Pinned] " : string.Empty)}{Description}";

    public string PinButtonText => IsPinned ? "Unpin" : "Pin";

    public IRelayCommand NavigateCommand { get; }

    public IRelayCommand TogglePinCommand { get; }

    public HistoryEntryViewModel(UndoRedoManager history, UndoRedoHistoryItem item)
    {
        _history = history;
        Id = item.Id;
        Description = item.Description;
        BranchName = item.BranchName;
        Depth = item.Depth;
        IsCurrent = item.IsCurrent;
        IsPinned = item.IsPinned;
        NavigateCommand = new RelayCommand(() => _history.NavigateTo(Id));
        TogglePinCommand = new RelayCommand(() => _history.SetPinned(Id, !IsPinned));
    }
}
