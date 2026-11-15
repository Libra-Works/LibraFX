using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class LogCategoryOption(string value) : ObservableObject
{
    public string Value { get; } = value;
    public string Label => Value.Length == 0 ? LocalizationService.Shared.Get("Logs.AllCategories") : Value;
    internal void Refresh() => OnPropertyChanged(nameof(Label));
}

public sealed class LogViewerViewModel : ObservableObject, IDisposable
{
    private readonly EditorLog _log;
    private readonly LogCategoryOption _all = new("");
    private LogCategoryOption _selectedCategory;
    private string _filterText = "";
    private string _searchText = "";
    private string _visibleText = "";
    private readonly List<int> _matches = [];
    private int _matchIndex = -1;
    private int _visibleCount;
    private bool _wrapLines;
    private bool _followTail = true;
    private string? _statusKey;
    private object?[] _statusArgs = [];

    public ObservableCollection<LogCategoryOption> Categories { get; } = [];
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand ResetFiltersCommand { get; }
    public IRelayCommand NextMatchCommand { get; }
    public IRelayCommand PreviousMatchCommand { get; }
    public string VisibleText { get => _visibleText; private set => SetProperty(ref _visibleText, value); }
    public string AllText => Format(_log.Entries);
    public string Counts => LocalizationService.Shared.Get("Logs.EntryCounts", _visibleCount, _log.Entries.Count);
    public string SearchStatus => SearchText.Length == 0 ? "" :
        LocalizationService.Shared.Get("Logs.MatchCounts", _matchIndex + 1, _matches.Count);
    public string Status => _statusKey is null ? "" : LocalizationService.Shared.Get(_statusKey, _statusArgs);
    public int MatchStart => _matchIndex < 0 ? -1 : _matches[_matchIndex];
    public int MatchLength => _matchIndex < 0 ? 0 : SearchText.Length;
    public bool WrapLines { get => _wrapLines; set => SetProperty(ref _wrapLines, value); }
    public bool FollowTail { get => _followTail; set => SetProperty(ref _followTail, value); }
    public LogCategoryOption SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value is not null && Categories.Contains(value) ? value : _all))
                Refresh();
        }
    }
    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value ?? "")) Refresh(); }
    }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value ?? "")) FindMatches(); }
    }

    public LogViewerViewModel(EditorLog log)
    {
        _log = log;
        _selectedCategory = _all;
        Categories.Add(_all);
        ClearCommand = new RelayCommand(_log.Clear);
        ResetFiltersCommand = new RelayCommand(() =>
        {
            _filterText = "";
            _selectedCategory = _all;
            OnPropertyChanged(nameof(FilterText));
            OnPropertyChanged(nameof(SelectedCategory));
            Refresh();
        });
        NextMatchCommand = new RelayCommand(() => MoveMatch(1), () => _matches.Count != 0);
        PreviousMatchCommand = new RelayCommand(() => MoveMatch(-1), () => _matches.Count != 0);
        _log.Entries.CollectionChanged += OnEntriesChanged;
        Refresh();
        MessageLocalization.Observe(this, static viewer =>
        {
            viewer._all.Refresh();
            viewer.OnPropertyChanged(nameof(Counts));
            viewer.OnPropertyChanged(nameof(SearchStatus));
            viewer.OnPropertyChanged(nameof(Status));
        });
    }

    public void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        OnPropertyChanged(nameof(Status));
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var categories = _log.Entries.Select(entry => entry.Category).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (_selectedCategory != _all && !categories.Contains(_selectedCategory.Value))
        {
            _selectedCategory = _all;
            OnPropertyChanged(nameof(SelectedCategory));
        }
        foreach (var category in Categories.Where(option => option != _all && !categories.Contains(option.Value)).ToArray())
            Categories.Remove(category);
        foreach (var category in categories.Order(StringComparer.Ordinal))
            if (!Categories.Any(option => option.Value == category)) Categories.Add(new LogCategoryOption(category));
        var entries = _log.Entries.Where(entry =>
            (_selectedCategory == _all || entry.Category == _selectedCategory.Value) &&
            (FilterText.Length == 0 || entry.Time.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
             entry.Category.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
             entry.Message.Contains(FilterText, StringComparison.OrdinalIgnoreCase))).ToArray();
        _visibleCount = entries.Length;
        VisibleText = Format(entries);
        OnPropertyChanged(nameof(Counts));
        FindMatches();
    }

    private void FindMatches()
    {
        _matches.Clear();
        if (SearchText.Length != 0)
        {
            var start = 0;
            while (start <= VisibleText.Length - SearchText.Length)
            {
                var match = VisibleText.IndexOf(SearchText, start, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                _matches.Add(match);
                start = match + SearchText.Length;
            }
        }
        _matchIndex = _matches.Count == 0 ? -1 : 0;
        RefreshMatch();
        NextMatchCommand.NotifyCanExecuteChanged();
        PreviousMatchCommand.NotifyCanExecuteChanged();
    }

    private void MoveMatch(int direction)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + direction + _matches.Count) % _matches.Count;
        RefreshMatch();
    }

    private void RefreshMatch()
    {
        OnPropertyChanged(nameof(SearchStatus));
        OnPropertyChanged(nameof(MatchStart));
        OnPropertyChanged(nameof(MatchLength));
    }

    private static string Format(IEnumerable<EditorLogEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(entry => $"{entry.Time} [{entry.Category}] {entry.Message}"));

    public void Dispose() => _log.Entries.CollectionChanged -= OnEntriesChanged;
}
