using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class NodeCatalogViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private GraphWorkspaceViewModel? _observedGraph;
    private IReadOnlyList<NodeCatalogEntryViewModel> _entries;
    private GroupAssetCatalog? _groups;
    private NodeDefinitionCatalog? _definitions;
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
        _activeGraph = activeGraph;
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
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        ObserveGraph(_activeGraph.Graph);
        RefreshEntries();
        MessageLocalization.Observe(this, static catalog => catalog.RefreshEntries());
        var weakCatalog = new WeakReference<NodeCatalogViewModel>(this);
        EventHandler? favoritesChanged = null;
        favoritesChanged = (_, _) =>
        {
            if (weakCatalog.TryGetTarget(out var catalog)) catalog.RefreshGroups();
            else NodeFavoritesService.Shared.Changed -= favoritesChanged;
        };
        NodeFavoritesService.Shared.Changed += favoritesChanged;
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        ObserveGraph(_activeGraph.Graph);
        RefreshEntries();
    }

    private void ObserveGraph(GraphWorkspaceViewModel graph)
    {
        if (_observedGraph is not null)
        {
            _observedGraph.PropertyChanged -= OnObservedGraphPropertyChanged;
            _observedGraph.Nodes.CollectionChanged -= OnObservedGraphCollectionChanged;
            _observedGraph.Breadcrumbs.CollectionChanged -= OnObservedGraphCollectionChanged;
        }
        _observedGraph = graph;
        _definitions = graph.NodeDefinitions;
        _observedGraph.PropertyChanged += OnObservedGraphPropertyChanged;
        _observedGraph.Nodes.CollectionChanged += OnObservedGraphCollectionChanged;
        _observedGraph.Breadcrumbs.CollectionChanged += OnObservedGraphCollectionChanged;
    }

    private void OnObservedGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.IsReadOnly))
            RefreshEntries();
    }

    private void OnObservedGraphCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _observedGraph?.Nodes) && _entries.Count > 0 && !CreatabilityChanged(_activeGraph.Graph))
            return;
        RefreshEntries();
    }

    private bool CreatabilityChanged(GraphWorkspaceViewModel graph)
    {
        foreach (var entry in _entries)
        {
            var kind = entry.GroupAssetId is not null
                ? GraphNodeKind.GraphGroup
                : new GraphNodeKind(entry.KindName);
            if (graph.CanCreateNode(kind, out _) != entry.CanCreate)
                return true;
        }
        return false;
    }

    private void RefreshEntries()
    {
        var graph = _activeGraph.Graph;
        _entries = (_definitions?.AvailableDefinitions ?? GraphNodeDefinition.AuthoringCore)
            .Where(definition => !GraphPolymorphism.IsHiddenVariant(definition.Kind))
            .Select(definition =>
            {
                var canCreate = graph.CanCreateNode(definition.Kind, out var reason);
                return new NodeCatalogEntryViewModel(definition, ProjectName, canCreate, reason);
            })
            .Concat((_groups?.CreatableAssets ?? []).Select(asset =>
            {
                var canCreate = graph.CanCreateNode(GraphNodeKind.GraphGroup, out var reason);
                return new NodeCatalogEntryViewModel(asset, _groups, canCreate, reason);
            }))
            .ToArray();
        OnPropertyChanged(nameof(LoadWarnings));
        OnPropertyChanged(nameof(ProjectName));
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        var search = SearchText.Trim();
        var groups = _entries.OrderByDescending(entry => entry.IsFavorite)
            .Where(entry => entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.OriginalName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.KindName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.QualifiedName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.Group.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.SearchTerms.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            entry.ConversionDescription.Contains(search, StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.IsFavorite ? LocalizationService.Shared.Get("Editor.Favorites") : entry.Group)
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

public sealed class NodeCatalogEntryViewModel : ObservableObject
{
    public bool IsFavorite => NodeFavoritesService.Shared.Contains(DragIdentifier);
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    private string? _favoriteError;
    public string FavoriteHelp => _favoriteError ?? LocalizationService.Shared.Get(IsFavorite ? "Editor.RemoveFavorite" : "Editor.AddFavorite");
    private IRelayCommand? _toggleFavorite;
    public IRelayCommand ToggleFavoriteCommand => _toggleFavorite ??= new RelayCommand(() =>
    {
        try { NodeFavoritesService.Shared.Toggle(DragIdentifier); _favoriteError = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _favoriteError = error.Message; }
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteHelp));
    });
    public const string GroupPrefix = "liberafx-group:";
    public const string PolymorphicPrefix = "liberafx-polymorphic:";
    public string Name { get; }
    public string OriginalName { get; }

    public string KindName { get; }

    public string Group { get; }
    public string Namespace { get; }
    public string QualifiedName { get; }
    public string DragIdentifier { get; }
    public string SearchTerms { get; } = string.Empty;
    public Guid? GroupAssetId { get; }
    public bool IsPolymorphic { get; }
    public bool CanCreate { get; }
    public double DisplayOpacity => CanCreate ? 1.0 : 0.4;
    public string ToolTipText { get; }
    public string ConversionDescription { get; } = string.Empty;
    public bool HasConversions => ConversionDescription.Length != 0;

    public NodeCatalogEntryViewModel(GraphNodeDefinition definition, string? projectName = null,
        bool canCreate = true, string? unavailableReason = null)
    {
        CanCreate = canCreate;
        ToolTipText = canCreate || string.IsNullOrWhiteSpace(unavailableReason)
            ? LocalizationService.Shared.Get("Editor.DragNode")
            : unavailableReason;
        IsPolymorphic = GraphPolymorphism.TryFamily(definition.Kind, out var family);
        var text = LocalizationService.Shared;
        var labelKey = family?.DisplayNameKey ?? definition.DisplayNameKey;
        Name = text.GetNodeLabel(labelKey, definition.Name);
        SearchTerms = text.GetNodeSearchTerms(labelKey, definition.Name);
        OriginalName = definition.Name;
        KindName = definition.Kind.ToString();
        Namespace = definition.Namespace;
        QualifiedName = definition.QualifiedName;
        ConversionDescription = GraphConversionPresentation.Describe(definition);
        var source = GraphNodeDefinition.Core.Contains(definition)
            ? LocalizationService.Shared.Get("Editor.CoreNodes") : projectName ?? definition.Namespace;
        Group = $"{source} / {LocalizationService.Shared.GetOrDefault(definition.GroupNameKey, definition.Group)}";
        if (GraphEnumBranchSchema.TryGetEnumDefinition(definition.Kind, out var enumeration))
        {
            var fallback = GraphEnumDefinition.All.Values.Count(item =>
                string.Equals(item.Name, enumeration.Name, StringComparison.OrdinalIgnoreCase)) > 1
                ? enumeration.QualifiedName : enumeration.Name;
            var typeKey = $"EnumBranches.Type.{enumeration.TypeId:D}";
            var typeName = text.GetNodeLabel(typeKey, fallback);
            Name = $"{Name} — {typeName}";
            var localizedTypeName = text.GetOrDefault(typeKey, fallback);
            Group = $"{Group} / {localizedTypeName}";
            SearchTerms = $"{SearchTerms} {text.GetNodeSearchTerms(typeKey, fallback)} {enumeration.Name} {enumeration.QualifiedName}";
        }
        DragIdentifier = (IsPolymorphic ? PolymorphicPrefix : "") + definition.Kind;
    }

    public NodeCatalogEntryViewModel(GroupAsset asset, GroupAssetCatalog? catalog = null,
        bool canCreate = true, string? unavailableReason = null)
    {
        CanCreate = canCreate;
        ToolTipText = canCreate || string.IsNullOrWhiteSpace(unavailableReason)
            ? LocalizationService.Shared.Get("Editor.DragNode")
            : unavailableReason;
        var metadata = asset.Origin == GroupAssetOrigin.Standard ? catalog?.Metadata(asset.Id) : null;
        Name = LocalizationService.Shared.GetNodeLabel(metadata?.NameKey, asset.Name);
        OriginalName = asset.Name;
        KindName = "GraphGroup";
        Namespace = asset.Origin == GroupAssetOrigin.Standard ? "Standard" : "Project";
        QualifiedName = $"{Namespace}.{asset.Name}";
        Group = asset.Origin == GroupAssetOrigin.Standard
            ? $"{LocalizationService.Shared.Get("Modules.StandardLibrary")} / " +
              LocalizationService.Shared.GetOrDefault(metadata?.CategoryKey, "Modules")
            : LocalizationService.Shared.Get("Messages.ProjectGroups");
        SearchTerms = $"{LocalizationService.Shared.GetNodeSearchTerms(metadata?.NameKey, asset.Name)} {string.Join(" ", metadata?.Keywords ?? [])}";
        GroupAssetId = asset.Id;
        DragIdentifier = GroupPrefix + asset.Id.ToString("D");
    }
}
