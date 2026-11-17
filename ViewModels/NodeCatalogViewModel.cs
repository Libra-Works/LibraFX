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
    private readonly NodeDefinitionCatalog? _definitions;
    private string _searchText = string.Empty;

    public ObservableCollection<NodeCatalogGroupViewModel> Groups { get; } = [];
    public IRelayCommand RefreshCommand { get; }
    public string ProjectName => _definitions?.ProjectName ?? string.Empty;
    public string LoadWarnings => string.Join(Environment.NewLine,
        GraphNodeDefinition.LoadWarnings
            .Concat(string.IsNullOrEmpty(_definitions?.Warnings) ? [] : [_definitions.Warnings])
            .Concat(string.IsNullOrEmpty(_groups?.Warnings) ? [] : [_groups.Warnings]));

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
        _definitions = activeGraph.Graph.NodeDefinitions;
        _entries = [];
        RefreshCommand = new RelayCommand(() =>
        {
            _definitions?.Refresh();
            _groups?.Refresh();
        });
        if (_groups is not null) _groups.Changed += (_, _) => RefreshEntries();
        if (_definitions is not null) _definitions.Changed += (_, _) => RefreshEntries();
        RefreshEntries();
        MessageLocalization.Observe(this, static catalog => catalog.RefreshEntries());
    }

    private void RefreshEntries()
    {
        _entries = (_definitions?.AvailableDefinitions ?? GraphNodeDefinition.AuthoringCore)
            .Where(definition => !GraphPolymorphism.IsHiddenVariant(definition.Kind))
            .Select(definition => new NodeCatalogEntryViewModel(definition, ProjectName))
            .Concat((_groups?.Assets ?? []).Select(asset => new NodeCatalogEntryViewModel(asset)))
            .ToArray();
        OnPropertyChanged(nameof(LoadWarnings));
        OnPropertyChanged(nameof(ProjectName));
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        var search = SearchText.Trim();
        var groups = _entries
            .Where(entry => entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.OriginalName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.KindName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.QualifiedName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.Group.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.ConversionDescription.Contains(search, StringComparison.OrdinalIgnoreCase))
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
    public const string PolymorphicPrefix = "liberafx-polymorphic:";
    public string Name { get; }
    public string OriginalName { get; }

    public string KindName { get; }

    public string Group { get; }
    public string Namespace { get; }
    public string QualifiedName { get; }
    public string DragIdentifier { get; }
    public Guid? GroupAssetId { get; }
    public bool IsPolymorphic { get; }
    public string ConversionDescription { get; } = string.Empty;
    public bool HasConversions => ConversionDescription.Length != 0;

    public NodeCatalogEntryViewModel(GraphNodeDefinition definition, string? projectName = null)
    {
        IsPolymorphic = GraphPolymorphism.TryFamily(definition.Kind, out var family);
        Name = LocalizationService.Shared.GetOrDefault(family?.DisplayNameKey ?? definition.DisplayNameKey, definition.Name);
        OriginalName = definition.Name;
        KindName = definition.Kind.ToString();
        Namespace = definition.Namespace;
        QualifiedName = definition.QualifiedName;
        ConversionDescription = GraphConversionPresentation.Describe(definition);
        var source = GraphNodeDefinition.Core.Contains(definition)
            ? LocalizationService.Shared.Get("Editor.CoreNodes") : projectName ?? definition.Namespace;
        Group = $"{source} / {LocalizationService.Shared.GetOrDefault(definition.GroupNameKey, definition.Group)}";
        DragIdentifier = (IsPolymorphic ? PolymorphicPrefix : "") + definition.Kind;
    }

    public NodeCatalogEntryViewModel(GroupAsset asset)
    {
        Name = asset.Name;
        OriginalName = asset.Name;
        KindName = "GraphGroup";
        Namespace = "Project";
        QualifiedName = $"Project.{asset.Name}";
        Group = LocalizationService.Shared.Get("Messages.ProjectGroups");
        GroupAssetId = asset.Id;
        DragIdentifier = GroupPrefix + asset.Id.ToString("D");
    }
}
