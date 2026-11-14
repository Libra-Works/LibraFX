using System.Collections.ObjectModel;
using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class NodeCatalogViewModel : Tool
{
    private IReadOnlyList<NodeCatalogEntryViewModel> _entries;
    private readonly GroupAssetCatalog? _groups;
    private string _searchText = string.Empty;

    public ObservableCollection<NodeCatalogGroupViewModel> Groups { get; } = [];
    public IRelayCommand RefreshCommand { get; }
    public string LoadWarnings => string.Join(Environment.NewLine,
        GraphNodeDefinition.LoadWarnings.Concat(string.IsNullOrEmpty(_groups?.Warnings) ? [] : [_groups.Warnings]));

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RefreshGroups();
            }
        }
    }

    public NodeCatalogViewModel(ActiveGraphProvider activeGraph, GroupAssetCatalog? groups = null)
    {
        _groups = groups;
        _entries = [];
        RefreshCommand = new RelayCommand(() => _groups?.Refresh());
        if (_groups is not null) _groups.Changed += (_, _) => RefreshEntries();
        RefreshEntries();
    }

    private void RefreshEntries()
    {
        _entries = GraphNodeDefinition.All.Select(definition => new NodeCatalogEntryViewModel(definition))
            .Concat((_groups?.Assets ?? []).Select(asset => new NodeCatalogEntryViewModel(asset)))
            .ToArray();
        OnPropertyChanged(nameof(LoadWarnings));
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        var search = SearchText.Trim();
        var groups = _entries
            .Where(entry => entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.KindName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.QualifiedName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.Group.Contains(search, StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Group)
            .Select(group => new NodeCatalogGroupViewModel(group.Key, group.ToArray()))
            .ToArray();
        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(group);
        }
    }
}

public sealed class NodeCatalogGroupViewModel(string name, IReadOnlyList<NodeCatalogEntryViewModel> nodes)
{
    public string Name { get; } = name;

    public IReadOnlyList<NodeCatalogEntryViewModel> Nodes { get; } = nodes;
}

public sealed class NodeCatalogEntryViewModel
{
    public const string GroupPrefix = "liberafx-group:";
    public string Name { get; }

    public string KindName { get; }

    public string Group { get; }
    public string Namespace { get; }
    public string QualifiedName { get; }
    public string DragIdentifier { get; }
    public Guid? GroupAssetId { get; }

    public NodeCatalogEntryViewModel(GraphNodeDefinition definition)
    {
        Name = definition.Name;
        KindName = definition.Kind.ToString();
        Namespace = definition.Namespace;
        QualifiedName = definition.QualifiedName;
        Group = $"{definition.Namespace} / {definition.Group}";
        DragIdentifier = definition.Kind.ToString();
    }

    public NodeCatalogEntryViewModel(GroupAsset asset)
    {
        Name = asset.Name;
        KindName = "GraphGroup";
        Namespace = "Project";
        QualifiedName = $"Project.{asset.Name}";
        Group = "Project / Groups";
        GroupAssetId = asset.Id;
        DragIdentifier = GroupPrefix + asset.Id.ToString("D");
    }
}
