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
    private async void OnCopyClick(object? sender, RoutedEventArgs e) => await CopySelectionAsync(false);

    private async void OnCutClick(object? sender, RoutedEventArgs e) => await CopySelectionAsync(true);

    private async void OnPasteClick(object? sender, RoutedEventArgs e) => await PasteAsync();
    private void PopulateAddNodeMenu(GraphWorkspaceViewModel graph)
    {
        AddNodeMenu.Items.Clear();
        var entries = NodeCatalogViewModel.BuildEntries(graph, graph.GroupAssets, graph.NodeDefinitions);
        foreach (var group in NodeCatalogViewModel.GroupEntries(entries))
        {
            var categoryItem = new MenuItem { Header = group.Name };
            foreach (var entry in group.Nodes)
            {
                var nodeItem = new MenuItem
                {
                    Header = entry.Name,
                    IsEnabled = entry.CanCreate
                };
                if (!string.IsNullOrEmpty(entry.ToolTipText))
                {
                    ToolTip.SetTip(nodeItem, entry.ToolTipText);
                }
                var captured = entry;
                nodeItem.Click += (_, _) => AddCatalogEntryAt(graph, captured, _pastePosition);
                categoryItem.Items.Add(nodeItem);
            }
            AddNodeMenu.Items.Add(categoryItem);
        }
    }

    private void AddCatalogEntryAt(GraphWorkspaceViewModel graph, NodeCatalogEntryViewModel entry, Point position)
    {
        try
        {
            if (entry.GroupAssetId is { } id) graph.CreateGroupAssetNode(id, position.X, position.Y);
            else if (GraphNodeDefinition.TryFind(entry.KindName, out var definition, graph.AvailableNodeDefinitions))
                graph.CreateNode(definition!.Kind, position.X, position.Y, entry.IsPolymorphic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or ArgumentException)
        {
            graph.ReportStatus(ex.Message);
        }
        Focus();
    }

    private void OnAddRerouteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        var connection = _contextMenuConnection ?? GraphSceneLayer.HitTestConnection(graph, _pastePosition, WireHitTolerance());
        _contextMenuConnection = null;
        if (graph.SelectedConnections.Count > 0 && (connection is null || graph.SelectedConnections.Contains(connection)))
        {
            graph.RunAuthoringAction(() => graph.InsertReroutesOnConnections(graph.SelectedConnections.ToArray()));
        }
        else if (connection is not null)
        {
            graph.RunAuthoringAction(() => graph.InsertRerouteOnConnection(connection, _pastePosition.X, _pastePosition.Y));
        }
        Focus();
    }
    private void OnAddCommentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        graph.RunAuthoringAction(() =>
        {
            var node = graph.CreateCommentAroundSelection(_pastePosition.X, _pastePosition.Y);
            graph.SelectNode(node);
        });
    }

    private void OnAddGraphGroupClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        try { graph.CreateNode(GraphNodeKind.GraphGroup, _pastePosition.X, _pastePosition.Y); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { graph.ReportStatus(ex.Message); }
    }

    private void OnAddSpawnableGroupClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        try { graph.CreateNode(GraphNodeKind.SpawnableGroup, _pastePosition.X, _pastePosition.Y); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { graph.ReportStatus(ex.Message); }
    }
    private void OnAddCpuParticleClick(object? sender, RoutedEventArgs e) => AddParticle("CpuParticle");
    private void OnAddGpuParticleClick(object? sender, RoutedEventArgs e) => AddParticle("GpuParticle");
    private void AddParticle(string kind)
    {
        if (DataContext is GraphWorkspaceViewModel graph)
            graph.RunAuthoringAction(() => graph.CreateNode(new(kind), _pastePosition.X, _pastePosition.Y));
    }

    private void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GraphBreadcrumb breadcrumb } || DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }
        if (breadcrumb.IsKernel)
        {
            return;
        }
        if (breadcrumb.IsSpecialized)
        {
            _gpuWorkspace?.Workspace?.Back();
            return;
        }
        CloseSpecializedWorkspace();
        graph.NavigateToGraph(breadcrumb.Depth);
        graph.RecordGraphNavigation();
        graph.Document.ActivateEditingWorkspace();
    }

    private void OnNavigateBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphWorkspaceViewModel graph)
        {
            graph.NavigateBack();
        }
    }

    private void OnNavigateForwardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphWorkspaceViewModel graph)
        {
            graph.NavigateForward();
        }
    }

}
