using System.Collections.ObjectModel;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Graph;

namespace LiberaFx.ViewModels;

public sealed class NodeCatalogViewModel : Tool
{
    private readonly IReadOnlyList<NodeCatalogEntryViewModel> _entries;
    private string _searchText = string.Empty;

    public ObservableCollection<NodeCatalogGroupViewModel> Groups { get; } = [];
    public string LoadWarnings => string.Join(Environment.NewLine, GraphNodeDefinition.LoadWarnings);

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

    public NodeCatalogViewModel(ActiveGraphProvider activeGraph)
    {
        _entries = GraphNodeDefinition.All
            .Select(definition => new NodeCatalogEntryViewModel(definition))
            .ToArray();
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        var search = SearchText.Trim();
        var groups = _entries
            .Where(entry => entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.KindName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
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
    public string Name { get; }

    public string KindName { get; }

    public string Group { get; }
    public string Namespace { get; }

    public NodeCatalogEntryViewModel(GraphNodeDefinition definition)
    {
        Name = definition.Name;
        KindName = definition.Kind.ToString();
        Namespace = definition.Namespace;
        Group = $"{definition.Namespace} / {definition.Group}";
    }
}
