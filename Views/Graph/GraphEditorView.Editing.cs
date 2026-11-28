using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Services;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls.Templates;
using Avalonia.Threading;

namespace LiberaFx.Views;

public partial class GraphEditorView : UserControl
{
    private async void OnGraphKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !OwnsInput(e.Source)) return;
        if (DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        var shortcuts = ShortcutService.Shared;
        if (graph.IsCpuParticleOverview &&
            !shortcuts.Matches("undo", e) && !shortcuts.Matches("redo", e))
        {
            return;
        }
        if (shortcuts.Matches("graph.search", e) && e.Source is Visual source &&
            source.GetSelfAndVisualAncestors().OfType<TextBox>().Any())
        {
            return;
        }
        if (graph.IsReadOnly && new[] { "graph.cut", "graph.paste", "graph.delete", "graph.group", "graph.search" }
            .Any(id => shortcuts.Matches(id, e)))
        {
            graph.SetConnectionStatus("Inline.Locked");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F9 && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel f9Main)
        {
            f9Main.ExecutionDebugger.BreakpointCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (shortcuts.Matches("undo", e))
        {
            graph.History.Undo();
            e.Handled = true;
        }
        else if (shortcuts.Matches("redo", e))
        {
            graph.History.Redo();
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.copy", e))
        {
            e.Handled = true;
            await CopySelectionAsync(false);
        }
        else if (shortcuts.Matches("graph.cut", e))
        {
            e.Handled = true;
            await CopySelectionAsync(true);
        }
        else if (shortcuts.Matches("graph.paste", e))
        {
            e.Handled = true;
            await PasteAsync();
        }
        else if (shortcuts.Matches("graph.all", e))
        {
            graph.SetSelection(graph.Nodes);
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.delete", e))
        {
            graph.RunAuthoringAction(graph.DeleteSelection);
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.search", e))
        {
            var center = new Point(
                (GraphScrollViewer.Offset.X + GraphScrollViewer.Viewport.Width / 2) / _zoom - _graphOrigin.X,
                (GraphScrollViewer.Offset.Y + GraphScrollViewer.Viewport.Height / 2) / _zoom - _graphOrigin.Y);
            ShowNodeSearch(_lastGraphPointer ?? center);
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.frame", e))
        {
            FrameSelection();
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.group", e))
        {
            graph.RunAuthoringAction(() => graph.GroupSelection());
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.zoomIn", e) || shortcuts.Matches("graph.zoomOut", e))
        {
            ZoomBy(shortcuts.Matches("graph.zoomIn", e) ? 1.25 : 1 / 1.25);
            e.Handled = true;
        }
    }

    private NodeCatalogEntryViewModel[]? _searchEntries;

    private void ShowNodeSearch(Point position)
    {
        if (DataContext is GraphWorkspaceViewModel { IsReadOnly: true }) return;
        _nodeSearchPosition = position;
        _searchEntries = BuildNodeSearchEntries();
        if (string.IsNullOrEmpty(NodeSearchBox.Text))
            RefreshNodeSearch();
        else
            NodeSearchBox.Text = string.Empty;
        NodeSearchPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => NodeSearchBox.Focus());
    }

    private NodeCatalogEntryViewModel[] BuildNodeSearchEntries()
    {
        var workspace = DataContext as GraphWorkspaceViewModel;
        var projectName = workspace?.NodeDefinitions?.ProjectName;
        var allowedKinds = workspace?.CreatableNodeKinds();
        var definitions = (workspace?.AvailableNodeDefinitions ?? GraphNodeDefinition.Core)
            .Where(definition => !GraphPolymorphism.IsHiddenVariant(definition.Kind) &&
                                 (allowedKinds is null || allowedKinds.Contains(definition.Kind)))
            .Select(definition => new NodeCatalogEntryViewModel(definition, projectName));
        var groups = (workspace is null || workspace.CanCreateNode(GraphNodeKind.GraphGroup, out _)
            ? workspace?.GroupAssets?.Assets
                .Select(asset => new NodeCatalogEntryViewModel(asset, workspace?.GroupAssets))
            : null) ?? [];
        return [.. definitions, .. groups];
    }

    private void OnNodeSearchTextChanged(object? sender, TextChangedEventArgs e) => RefreshNodeSearch();

    private void RefreshNodeSearch()
    {
        if (NodeSearchResults is null || NodeSearchBox is null) return;
        var allEntries = _searchEntries ??= BuildNodeSearchEntries();
        var search = NodeSearchBox.Text?.Trim() ?? string.Empty;
        var entries = search.Length == 0
            ? allEntries
            : allEntries.Where(entry =>
                entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.OriginalName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.KindName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.QualifiedName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Group.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.SearchTerms.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        entries = entries.OrderByDescending(entry => entry.IsFavorite)
            .ThenByDescending(entry => string.Equals(entry.Name, search, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        NodeSearchResults.ItemsSource = entries;
        NodeSearchResults.SelectedIndex = entries.Length > 0 ? 0 : -1;
    }

    private void OnNodeSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NodeSearchPopup.IsOpen = false;
            Focus();
        }
        else if (e.Key == Key.Enter) AddSearchSelection();
        else if (e.Key is Key.Down or Key.Up && NodeSearchResults.ItemCount > 0)
        {
            var delta = e.Key == Key.Down ? 1 : -1;
            NodeSearchResults.SelectedIndex = Math.Clamp(NodeSearchResults.SelectedIndex + delta, 0, NodeSearchResults.ItemCount - 1);
            NodeSearchResults.ScrollIntoView(NodeSearchResults.SelectedIndex);
        }
        else return;
        e.Handled = true;
    }

    private void OnNodeSearchDoubleTapped(object? sender, TappedEventArgs e) => AddSearchSelection();

    private void AddSearchSelection()
    {
        if (DataContext is not GraphWorkspaceViewModel graph ||
            NodeSearchResults.SelectedItem is not NodeCatalogEntryViewModel entry) return;
        AddCatalogEntryAt(graph, entry, _nodeSearchPosition);
        NodeSearchPopup.IsOpen = false;
    }

    private void OnToggleBreakpointClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
            main.ExecutionDebugger.BreakpointCommand.Execute(null);
    }

    private async Task CopySelectionAsync(bool cut)
    {
        if (DataContext is not GraphWorkspaceViewModel graph || graph.SelectionCount == 0)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException(LocalizationService.Shared.Get("Editor.ClipboardUnavailable"));
            var nodes = graph.SelectedNodes.ToArray();
            var revision = graph.History.CurrentNodeId;
            var text = ClipboardPrefix + JsonSerializer.Serialize(graph.CreateSelectionSnapshot());
            await clipboard.SetTextAsync(text);
            if (cut)
            {
                if (graph.History.CurrentNodeId != revision || nodes.Any(node => !graph.Nodes.Contains(node)))
                {
                    graph.SetConnectionStatus("Editor.CutCancelled");
                    return;
                }

                graph.DeleteNodes(nodes);
            }
            graph.SetConnectionStatus(cut ? "Editor.NodesCut" : "Editor.NodesCopied");
        }
        catch (Exception ex)
        {
            graph.SetConnectionStatus("Editor.ClipboardFailed", MessageLocalization.Error(ex.Message));
        }
    }

    private async Task PasteAsync()
    {
        if (DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException(LocalizationService.Shared.Get("Editor.ClipboardUnavailable"));
            var text = await clipboard.TryGetTextAsync();
            if (text is null || text.Length > 1_000_000 ||
                !text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            {
                graph.SetConnectionStatus("Editor.ClipboardNotNodes");
                return;
            }

            var snapshot = JsonSerializer.Deserialize<GraphSnapshot>(text[ClipboardPrefix.Length..])
                ?? throw new InvalidDataException(LocalizationService.Shared.Get("Editor.ClipboardEmpty"));
            graph.PasteSnapshot(snapshot, _pastePosition.X, _pastePosition.Y);
            _pastePosition = new Point(_pastePosition.X + 24, _pastePosition.Y + 24);
        }
        catch (Exception ex)
        {
            graph.SetConnectionStatus("Editor.PasteFailed", MessageLocalization.Error(ex.Message));
        }
    }

}
