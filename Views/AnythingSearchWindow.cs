using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class AnythingSearchWindow : Window
{
    private readonly TextBox _query;
    private readonly ListBox _results;
    private readonly TextBlock _status;
    private readonly MainViewModel _main;
    private readonly Func<IEnumerable<AnythingSearchItem>> _additional;
    private IReadOnlyList<AnythingSearchItem> _items = [];
    private bool _executing;

    public AnythingSearchWindow(MainViewModel main, IEnumerable<AnythingSearchItem>? additional = null,
        Func<IEnumerable<AnythingSearchItem>>? additionalFactory = null)
    {
        var text = LocalizationService.Shared;
        _main = main;
        _additional = additionalFactory ?? (() => additional ?? []);
        Title = text.Get("Search.Title");
        Width = 760;
        Height = 500;
        MinWidth = 440;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _query = new TextBox { PlaceholderText = text.Get("Search.Hint"), Margin = new Thickness(12), MaxLength = 256 };
        Avalonia.Automation.AutomationProperties.SetName(_query, Title);
        _results = new ListBox
        {
            Margin = new Thickness(12, 0),
            ItemTemplate = new FuncDataTemplate<AnythingSearchItem>((item, _) =>
            {
                if (item is null) return new TextBlock();
                return new StackPanel
                {
                    Margin = new Thickness(4), Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = (item.IsFavorite ? "★  " : "") + item.Category + "  " + item.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = item.Detail, TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                };
            })
        };
        _status = new TextBlock { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(_results, 1);
        Grid.SetRow(_status, 2);
        layout.Children.Add(_query);
        layout.Children.Add(_results);
        layout.Children.Add(_status);
        Content = layout;
        RebuildItems();
        _query.TextChanged += (_, _) => Refresh();
        _results.DoubleTapped += async (_, _) => await ExecuteAsync();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
            _query.Focus();
        };
        Closed += (_, _) => LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Title = LocalizationService.Shared.Get("Search.Title");
        _query.PlaceholderText = LocalizationService.Shared.Get("Search.Hint");
        Avalonia.Automation.AutomationProperties.SetName(_query, Title);
        RebuildItems();
        Refresh();
    }

    private void RebuildItems()
    {
        _items = CreateItems(_main).Concat(_additional()).Take(50000).ToArray();
    }

    private void Refresh()
    {
        var matches = AnythingSearch.Find(_items, _query.Text);
        _results.ItemsSource = matches;
        _results.SelectedIndex = matches.Count == 0 ? -1 : 0;
        _status.Text = LocalizationService.Shared.Get(matches.Count == 0 ? "Search.Empty" : "Search.Keys");
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key is Key.Down or Key.Up)
        {
            var count = _results.ItemCount;
            if (count > 0)
            {
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
                _results.ScrollIntoView(_results.SelectedItem!);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter) { e.Handled = true; await ExecuteAsync(); }
    }

    private async Task ExecuteAsync()
    {
        if (_executing || _results.SelectedItem is not AnythingSearchItem selected) return;
        _executing = true;
        try { await selected.ExecuteAsync(); Close(); }
        catch (Exception ex) { _status.Text = ex.GetBaseException().Message; }
        finally { _executing = false; }
    }

    private static IEnumerable<AnythingSearchItem> CreateItems(MainViewModel main)
    {
        var text = LocalizationService.Shared;
        foreach (var document in main.Documents.ToArray())
        {
            yield return Item("document/" + document.Id, "Search.Graph", document.FileName, document.FileKey ?? "",
                () => main.ActivateDocument(document));
            foreach (var item in Nodes(document.Workspace.CreateSnapshot(), []))
            {
                var (node, parent, detail) = item;
                var aliases = node.Kind.Value;
                if (GraphNodeDefinition.TryFind(node.Kind.Value, out var definition))
                {
                    var entry = new NodeCatalogEntryViewModel(definition!);
                    aliases = $"{entry.SearchTerms} {entry.OriginalName} {entry.KindName} {entry.QualifiedName}";
                }
                yield return Item("node/" + document.Id + "/" + node.Id, "Search.Node", node.Name,
                    document.FileName + " / " + detail,
                    () => { main.ActivateDocument(document); document.Workspace.NavigateToNode(parent, node.Id); },
                    aliases);
            }
        }
        foreach (var group in main.GroupAssets.CreatableAssets)
        {
            var entry = new NodeCatalogEntryViewModel(group, main.GroupAssets);
            yield return Item("asset/" + group.Id, "Search.Group", entry.Name,
                text.Get("Search.GroupDetail", group.Revision),
                () => main.Graph.CreateGroupAssetNode(group.Id, 100, 100),
                $"{entry.SearchTerms} {entry.OriginalName} {entry.QualifiedName} {group.Id:D}") with
                { IsFavorite = NodeFavoritesService.Shared.Contains(NodeCatalogEntryViewModel.GroupPrefix + group.Id.ToString("D")) };
        }
        var editing = main.Graph.ActiveEditingWorkspace;
        foreach (var definition in editing.AvailableNodeDefinitions.Where(definition =>
                     !GraphPolymorphism.IsHiddenVariant(definition.Kind) && editing.CanCreateNode(definition.Kind, out _)))
        {
            var entry = new NodeCatalogEntryViewModel(definition);
            yield return Item("create/" + entry.DragIdentifier, "Search.CreateNode", entry.Name, entry.Group,
                () =>
                {
                    editing.CreateNode(definition.Kind, 100, 100, entry.IsPolymorphic);
                }, $"{entry.SearchTerms} {entry.OriginalName} {entry.KindName} {entry.QualifiedName} {entry.Namespace}") with { IsFavorite = entry.IsFavorite };
        }
        foreach (var command in main.Extensions.Commands.ToArray())
            yield return new("extension/" + command.Id, text.Get("Search.Command"), command.Name,
                command.Definition.Description, command.Id + " " + text.GetEnglishOrDefault("Search.Command", "Command"),
                () => main.Extensions.ExecuteAsync(command.Id));
        yield return Item("command/new", "Search.Command", text.Get("Editor.New"), "",
            () => main.NewDocumentCommand.Execute(null), "new");
        yield return Item("command/open", "Search.Command", text.Get("Editor.Open"), "",
            () => main.OpenProjectCommand.Execute(null), "open");
        yield return new("command/save", text.Get("Search.Command"), text.Get("Editor.Save"), "",
            "save " + text.GetEnglishOrDefault("Search.Command", "Command"),
            () => main.SaveProjectCommand.ExecuteAsync(null));
        foreach (var (id, label) in new[]
                 {
                     ("Outliner", "Outliner"), ("NodeList", "NodeList"), ("Resources", "Resources"),
                     ("Properties", "Properties"), ("Properties2", "Properties2"),
                     ("Properties3", "Properties3"), ("Properties4", "Properties4"),
                     ("EngineConnection", "EngineConnection"),
                     ("History", "History"), ("Timeline", "Timeline"), ("Curves", "Curves")
                 })
            yield return Item("panel/" + id, "Search.Panel", text.Get("Messages.Tool." + label), "",
                () => main.ShowDockPanel(id), id + " " + text.GetEnglishOrDefault("Messages.Tool." + label, id));
    }

    internal static AnythingSearchItem Item(string id, string category, string name, string detail,
        Action action, string aliases = "") => new(id, LocalizationService.Shared.Get(category), name, detail,
        aliases + " " + LocalizationService.Shared.GetEnglishOrDefault(category, category),
        () => { action(); return Task.CompletedTask; });

    private static IEnumerable<(GraphNodeSnapshot Node, Guid[] Parent, string Detail)> Nodes(
        GraphSnapshot graph, Guid[] parent, string prefix = "")
    {
        foreach (var node in graph.Nodes)
        {
            yield return (node, parent, prefix + node.Name);
            if (node.ChildGraph is null) continue;
            foreach (var nested in Nodes(node.ChildGraph, [.. parent, node.Id], prefix + node.Name + " / "))
                yield return nested;
        }
    }
}
