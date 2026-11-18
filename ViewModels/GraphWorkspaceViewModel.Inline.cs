using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    private readonly GraphWorkspaceViewModel _document;
    // One editing state per absolute graph path. Controls never own a competing child snapshot/history.
    private readonly Dictionary<string, WeakReference<GraphWorkspaceViewModel>> _inlineWorkspaces = [];
    private bool _inlinePathExists = true;
    private GraphWorkspaceViewModel? _activeEditingWorkspace;
    public bool IsInlineWorkspace => !ReferenceEquals(_document, this);
    public GraphWorkspaceViewModel Document => _document;
    public GraphWorkspaceViewModel ActiveEditingWorkspace => _document._activeEditingWorkspace ?? _document;
    public IReadOnlyList<Guid> GraphPath => _graphPath.ToArray();
    public event EventHandler<GraphWorkspaceViewModel>? EditingWorkspaceActivated;

    public void ActivateEditingWorkspace()
    {
        Dispatcher.UIThread.VerifyAccess();
        _document._activeEditingWorkspace = this;
        _document.EditingWorkspaceActivated?.Invoke(_document, this);
    }

    internal void RunAuthoringAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            ReportStatus(ex.Message);
        }
    }

    public bool IsReadOnly
    {
        get
        {
            if (!_inlinePathExists)
            {
                return true;
            }

            try { EnsurePathEditable(); return false; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException) { return true; }
        }
    }

    private void EnsurePathEditable(bool containingInstanceValue = false)
    {
        if (!_inlinePathExists)
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Inline.MissingGraph"));
        }

        var graph = _document._rootSnapshot;
        for (var index = 0; index < _graphPath.Count; index++)
        {
            var owner = graph.Nodes.FirstOrDefault(node => node.Id == _graphPath[index]);
            if (owner?.ChildGraph is null)
            {
                throw new InvalidOperationException(LocalizationService.Shared.Get("Inline.MissingGraph"));
            }

            if (!containingInstanceValue || index != _graphPath.Count - 1)
            {
                EnsureAssetUnlocked(owner.GroupAsset);
            }

            graph = owner.ChildGraph;
        }
    }

    // Every existing editing command goes through this gate, including MCP/non-UI commands.
    // History captures the full document only after the edited level has been merged.
    private void ExecuteEdit(IUndoableCommand command, bool containingInstanceValue = false,
        ResourceIdentityDocument? updatedResourceIdentities = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _document.CreateSnapshot();
        EnsurePathEditable(containingInstanceValue);
        _history.Execute(new InlineGraphEditCommand(command, () =>
        {
            var before = _document._rootSnapshot;
            try
            {
                command.Execute();
                var after = _document._rootSnapshot;
                if (command is not ReplaceGraphSnapshotCommand)
                {
                    var current = CreateCurrentSnapshot() with
                    {
                        AutoDestroyWhenIdle = GetCurrentGraph().AutoDestroyWhenIdle
                    };
                    after = ReplaceGraph(after, _graphPath, 0, current);
                }
                if (updatedResourceIdentities is not null)
                {
                    after = after with { ResourceIdentities = updatedResourceIdentities };
                }
                ValidateSnapshot(after);
                _document.AcceptInlineDocument(after, this);
            }
            catch
            {
                _document.RestoreFromHistory(before);
                throw;
            }
        }));
    }

    public GraphWorkspaceViewModel OpenInlineGraph(GraphNodeViewModel node)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!Nodes.Contains(node) || node.ChildGraph is null || _graphPath.Count >= MaxGraphDepth)
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Inline.MissingGraph"));
        }

        var snapshot = _document.CreateSnapshot();
        var path = _graphPath.Append(node.Id).ToArray();
        var key = string.Join("/", path);
        if (_document._inlineWorkspaces.TryGetValue(key, out var weak) && weak.TryGetTarget(out var existing))
        {
            existing.ReceiveInlineDocument(snapshot);
            return existing;
        }
        var workspace = new GraphWorkspaceViewModel(_history, GroupAssets, NodeDefinitions, _document, path);
        workspace.ReceiveInlineDocument(snapshot);
        _document._inlineWorkspaces[key] = new(workspace);
        return workspace;
    }

    public bool MaximizeInlineGraph(GraphNodeViewModel node)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!Nodes.Contains(node) || node.ChildGraph is null)
        {
            return false;
        }

        var path = _graphPath.ToArray();
        _document.NavigateToGraph(0);
        foreach (var id in path.Append(node.Id))
        {
            var target = _document.Nodes.FirstOrDefault(candidate => candidate.Id == id);
            if (target is null || !_document.EnterGraphCore(target, inspectLocked: true))
            {
                return false;
            }
        }
        _document.ActivateEditingWorkspace();
        return true;
    }

    private void AcceptInlineDocument(GraphSnapshot snapshot, GraphWorkspaceViewModel source)
    {
        _rootSnapshot = snapshot;
        if (!ReferenceEquals(source, this))
        {
            ReceiveInlineDocument(snapshot);
        }

        SynchronizeInlineWorkspaces(snapshot, source);
    }

    private void SynchronizeInlineWorkspaces(GraphSnapshot snapshot, GraphWorkspaceViewModel? source = null)
    {
        foreach (var (key, weak) in _inlineWorkspaces.ToArray())
        {
            if (!weak.TryGetTarget(out var workspace)) { _inlineWorkspaces.Remove(key); continue; }
            if (ReferenceEquals(workspace, source))
            {
                workspace._rootSnapshot = snapshot;
                workspace.RefreshContainingSpawnable();
            }
            else
            {
                workspace.ReceiveInlineDocument(snapshot);
            }
        }
    }

    private void ReceiveInlineDocument(GraphSnapshot snapshot)
    {
        var previous = _inlinePathExists ? CreateCurrentSnapshot() : new GraphSnapshot([], []);
        _rootSnapshot = snapshot;
        var graph = snapshot;
        _inlinePathExists = true;
        foreach (var id in _graphPath)
        {
            if (graph.Nodes.FirstOrDefault(node => node.Id == id)?.ChildGraph is not { } child)
            {
                _inlinePathExists = false;
                break;
            }
            graph = child;
        }
        OnPropertyChanged(nameof(IsReadOnly));
        if (!_inlinePathExists)
        {
            // Detached controls must never write their old graph back, or remain the inspector/MCP target.
            _containingSpawnable = null;
            OnPropertyChanged(nameof(ContainingSpawnable));
            SelectNode(null);
            OnPropertyChanged(nameof(InspectorNode));
            CancelConnection();
            if (ReferenceEquals(_document.ActiveEditingWorkspace, this))
            {
                _document.ActivateEditingWorkspace();
            }

            return;
        }
        if (!GraphSnapshot.ContentEquals(previous, graph))
        {
            // Descendant edits must not recreate ancestor controls (and destroy pointer capture/viewports).
            var descendantOnly = previous.Nodes.Count == graph.Nodes.Count &&
                previous.Nodes.Zip(graph.Nodes).All(pair => pair.First with { ChildGraph = null } ==
                    pair.Second with { ChildGraph = null }) &&
                graph.Connections.SequenceEqual(previous.Connections.Where(edge => graph.Connections.Contains(edge)));
            if (descendantOnly)
            {
                foreach (var specification in graph.Nodes)
                {
                    GetNode(specification.Id).SetChildGraphValue(specification.ChildGraph);
                }

                var valid = graph.Connections.ToHashSet();
                foreach (var edge in Connections.Where(edge => !valid.Contains(new GraphConnectionSnapshot(
                    edge.Source.Id, edge.Target.Id, edge.TargetPortId, edge.SourcePortId))).ToArray())
                {
                    RemoveConnection(edge);
                }

                RefreshTypeDiagnostics();
            }
            else
            {
                RestoreCurrentGraph(graph);
            }
        }
        RefreshBreadcrumbs();
    }

    private void RefreshInlineReadOnlyState()
    {
        OnPropertyChanged(nameof(IsReadOnly));
        foreach (var weak in _inlineWorkspaces.Values.ToArray())
        {
            if (weak.TryGetTarget(out var workspace))
            {
                workspace.OnPropertyChanged(nameof(IsReadOnly));
            }
        }
    }

    public void SetInlinePresentation(GraphNodeViewModel node, bool expanded, double width, double height)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!Nodes.Contains(node) || !node.IsGraphGroup)
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Inline.MissingGraph"));
        }

        if (!double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException("Invalid inline size.");
        }

        width = Math.Clamp(width, 420, 2400);
        height = Math.Clamp(height, 260, 1600);
        if (node.IsInlineExpanded == expanded && node.InlineWidth == width && node.InlineHeight == height)
        {
            return;
        }

        var values = node.Parameters?.Where(value => value.Id is not
            (GraphNodeViewModel.InlineExpandedId or GraphNodeViewModel.InlineWidthId or GraphNodeViewModel.InlineHeightId))
            .ToList() ?? [];
        values.Add(new(GraphNodeViewModel.InlineExpandedId, "bool", JsonSerializer.SerializeToElement(expanded)));
        values.Add(new(GraphNodeViewModel.InlineWidthId, "float", JsonSerializer.SerializeToElement(width)));
        values.Add(new(GraphNodeViewModel.InlineHeightId, "float", JsonSerializer.SerializeToElement(height)));
        ExecuteEdit(new SetNodeParametersCommand(node, node.Parameters, values.AsReadOnly(),
            LocalizationService.Shared.Get("Inline.History.Presentation")));
    }
}
