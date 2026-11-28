using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Views;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public sealed class AssetSelectionRequest
{
    internal AssetSelectionRequest(GraphDocumentViewModel document, GraphWorkspaceViewModel graph,
        GraphNodeViewModel node, ResourceAssignmentTarget? target, string? parameterId,
        string? assetClass, Func<bool>? isCurrent, JsonElement? parameterValue = null,
        bool revealOnly = false, Func<AssetBrowserAssetReference, bool>? selectionReceiver = null)
    {
        Document = document;
        Graph = graph;
        Node = node;
        Target = target;
        ParameterId = parameterId;
        AssetClass = assetClass;
        IsCurrent = isCurrent;
        ParameterValue = parameterValue?.Clone();
        RevealOnly = revealOnly;
        SelectionReceiver = selectionReceiver;
    }

    public GraphDocumentViewModel Document { get; }
    public GraphWorkspaceViewModel Graph { get; }
    public GraphNodeViewModel Node { get; }
    public ResourceAssignmentTarget? Target { get; }
    public string? ParameterId { get; }
    public string? AssetClass { get; }
    public bool RevealOnly { get; }
    public bool IsExternalSelection => SelectionReceiver is not null;
    public Guid? PeerId { get; internal set; }
    public bool IsStale { get; internal set; }
    internal Func<bool>? IsCurrent { get; }
    internal JsonElement? ParameterValue { get; }
    internal Func<AssetBrowserAssetReference, bool>? SelectionReceiver { get; }
}

public sealed class AssetBrowserCoordinator : IDisposable
{
    private readonly MainViewModel _main;
    private GraphWorkspaceViewModel? _observedGraph;
    private bool _disposed;

    public EngineAssetsWindowViewModel ViewModel { get; }
    public AssetSelectionRequest? Request { get; private set; }

    public AssetBrowserCoordinator(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        ViewModel = new EngineAssetsWindowViewModel(main, this);
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    public AssetSelectionRequest? BeginForParameter(GraphDocumentViewModel document,
        GraphWorkspaceViewModel graph, GraphNodeViewModel node, string parameterId,
        ResourceAssignmentTarget target, string assetClass, Func<bool> isCurrent, bool revealOnly = false)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (!ReferenceEquals(_main.ActiveDocument, document) || !IsActiveGraph(document, graph) ||
            !graph.IsCurrentInspectorTarget(node) || graph.SelectedNode != node)
            return null;
        var parameterDefinition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId);
        if (parameterDefinition is null) return null;
        var request = new AssetSelectionRequest(document, graph, node, target, parameterId, assetClass, isCurrent,
            node.GetParameterValue(parameterDefinition), revealOnly)
        {
            PeerId = ViewModel.SelectedPeer?.ClientId
        };
        ViewModel.AssetClass = assetClass;
        ViewModel.Query = string.Empty;
        ViewModel.SelectedTab = 1;
        Begin(request);
        return request;
    }

    public AssetSelectionRequest? BeginForCurrentSelection()
    {
        var document = _main.ActiveDocument;
        var graph = _main.ActiveGraphProvider.Graph;
        var node = document is not null ? graph.SelectedNode : null;
        if (document is null || node is null)
        {
            if (Request is { } previous) previous.IsStale = true;
            Request = null;
            DetachGraph();
            ViewModel.OnCallerChanged();
            return null;
        }
        var request = new AssetSelectionRequest(document, graph, node, null, null, null, null)
        {
            PeerId = ViewModel.SelectedPeer?.ClientId
        };
        Begin(request);
        return request;
    }

    public AssetSelectionRequest? BeginForOtherWindow(GraphDocumentViewModel document,
        GraphWorkspaceViewModel graph, GraphNodeViewModel node, Func<bool> isCurrent,
        Func<AssetBrowserAssetReference, bool> receiveSelection, string? assetClass = null)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(receiveSelection);
        if (!ReferenceEquals(_main.ActiveDocument, document) || !IsActiveGraph(document, graph) ||
            !graph.IsCurrentInspectorTarget(node) || graph.SelectedNode != node)
            return null;
        if (assetClass is not (null or "model" or "texture" or "material"))
            throw new ArgumentException("Unsupported asset class.", nameof(assetClass));
        var request = new AssetSelectionRequest(document, graph, node, null, null, assetClass, isCurrent,
            selectionReceiver: receiveSelection)
        {
            PeerId = ViewModel.SelectedPeer?.ClientId
        };
        if (assetClass is not null) ViewModel.AssetClass = assetClass;
        ViewModel.SelectedTab = 1;
        Begin(request);
        return request;
    }

    public bool OpenForOtherWindow(GraphDocumentViewModel document, GraphWorkspaceViewModel graph,
        GraphNodeViewModel node, Func<bool> isCurrent,
        Func<AssetBrowserAssetReference, bool> receiveSelection, string? assetClass = null)
    {
        if (BeginForOtherWindow(document, graph, node, isCurrent, receiveSelection, assetClass) is null)
        {
            return false;
        }
        Open();
        return true;
    }

    public void OpenForCurrentSelection()
    {
        _ = BeginForCurrentSelection();
        Open();
    }

    public GraphDocumentViewModel? MainDocumentFor(GraphWorkspaceViewModel graph) =>
        _main.ActiveDocument is { } document && IsActiveGraph(document, graph) ? document : null;

    private bool IsActiveGraph(GraphDocumentViewModel document, GraphWorkspaceViewModel graph) =>
        ReferenceEquals(document.Workspace, graph.Document) &&
        ReferenceEquals(_main.ActiveGraphProvider.Graph, graph);

    internal void OnPeerSelectionChanged(Guid? selectedPeer)
    {
        var request = Request;
        if (request is null || request.IsStale) return;
        if (request.PeerId is null && selectedPeer is not null)
            request.PeerId = selectedPeer;
        else if (request.PeerId != selectedPeer)
            request.IsStale = true;
        ViewModel.OnCallerChanged();
    }

    public async Task RevealAsync(AssetSelectionRequest request, string path)
    {
        try
        {
            EngineAssetPath.Validate(path);
            if (!ReferenceEquals(Request, request) || request.RevealOnly is false)
                throw new InvalidOperationException("The reveal request is no longer current.");
            var peer = request.PeerId ?? ViewModel.SelectedPeer?.ClientId;
            if (peer is null || !IsCurrent(request, peer))
                throw new InvalidOperationException("Select a connected engine editor before revealing this asset.");
            await ViewModel.RevealExistingAsync(request, path, peer.Value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           InvalidDataException or IOException or TimeoutException)
        {
            ViewModel.SetCallerError(exception.Message);
        }
    }

    public bool IsCurrent(AssetSelectionRequest? request, Guid? selectedPeer, bool requiresPeer = true)
    {
        if (_disposed || request is null || !ReferenceEquals(Request, request) || request.IsStale ||
            !ReferenceEquals(_main.ActiveDocument, request.Document) ||
            !IsActiveGraph(request.Document, request.Graph) ||
            !request.Graph.Nodes.Contains(request.Node) ||
            !ReferenceEquals(request.Graph.SelectedNode, request.Node) ||
            request.IsCurrent?.Invoke() == false)
        {
            if (ReferenceEquals(Request, request) && request is not null) request.IsStale = true;
            return false;
        }
        if (request.PeerId != selectedPeer && (request.PeerId.HasValue || selectedPeer.HasValue))
        {
            request.IsStale = true;
            return false;
        }
        if (requiresPeer && (request.PeerId is not { } peer || !_main.PreviewAssets.Supports(peer)))
        {
            request.IsStale = true;
            return false;
        }
        if (request.ParameterId is { } parameterId)
        {
            var definition = request.Node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId);
            if (definition is null || request.ParameterValue is not { } expected ||
                !NodeParameter.ValuesEqual(request.Node.GetParameterValue(definition), expected))
            {
                request.IsStale = true;
                return false;
            }
        }
        return true;
    }

    public bool TryAssign(EngineAsset asset, Guid? selectedPeer, ResourceAssignmentTarget? target,
        out string error)
        => TrySelect(asset, new AssetBrowserAssetReference(AssetBrowserRootKind.VirtualEngine,
            asset.Engine, asset.Path, selectedPeer), selectedPeer, target, out error);

    public bool TrySelect(EngineAsset? asset, AssetBrowserAssetReference? reference,
        Guid? selectedPeer, ResourceAssignmentTarget? target, out string error)
    {
        error = LocalizationService.Shared.Get("Messages.AssetSelectionStale");
        var request = Request;
        if (request is null || request.RevealOnly || reference is null ||
            !IsCurrent(request, selectedPeer, requiresPeer: reference.Root == AssetBrowserRootKind.VirtualEngine))
            return false;
        if (request.AssetClass is { } assetClass &&
            (asset is null || asset.AssetClass != assetClass))
        {
            error = LocalizationService.Shared.Get("Resources.AssetClassMismatch");
            return false;
        }
        if (reference.Root == AssetBrowserRootKind.PhysicalProject &&
            !string.Equals(reference.ProjectRoot,
                ProjectAssetRoot.FromProjectFileKey(request.Document.FileKey),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            request.IsStale = true;
            error = LocalizationService.Shared.Get("Resources.ProjectRootChanged");
            return false;
        }
        if (request.SelectionReceiver is { } receiver)
        {
            if (reference.Root == AssetBrowserRootKind.VirtualEngine &&
                (reference.SessionPeerId != request.PeerId || asset is null ||
                 asset.Engine != reference.Type || asset.Path != reference.Identity))
            {
                error = LocalizationService.Shared.Get("Resources.SelectionChanged");
                return false;
            }
            bool accepted;
            try { accepted = receiver(reference); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                               InvalidDataException or IOException or UnauthorizedAccessException)
            {
                error = MessageLocalization.Error(exception.Message);
                return false;
            }
            if (!accepted)
            {
                error = LocalizationService.Shared.Get("Resources.CallerRejected");
                return false;
            }
            Request = null;
            ViewModel.OnCallerCompleted();
            return true;
        }
        if (asset is null || reference.Root != AssetBrowserRootKind.VirtualEngine)
        {
            error = LocalizationService.Shared.Get("Resources.RequiresEngineAsset");
            return false;
        }
        if (reference.Type != asset.Engine || reference.Identity != asset.Path ||
            reference.SessionPeerId != selectedPeer)
        {
            error = LocalizationService.Shared.Get("Resources.SelectionChanged");
            return false;
        }
        var assignmentTarget = request.Target ?? target;
        if (assignmentTarget is null ||
            !ResourceAssignment.Targets(request.Node, asset).Any(current =>
                current.NodeId == assignmentTarget.NodeId &&
                current.Kind == assignmentTarget.Kind && current.Id == assignmentTarget.Id))
        {
            error = LocalizationService.Shared.Get("Resources.ParameterUnavailable");
            return false;
        }
        try
        {
            ResourceAssignment.Assign(request.Graph, request.Node, asset, assignmentTarget);
            Request = null;
            ViewModel.OnCallerCompleted();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           InvalidDataException)
        {
            error = MessageLocalization.Error(exception.Message);
            return false;
        }
    }

    public void Open()
    {
        if (_disposed)
        {
            return;
        }
        if (Request is null)
        {
            _ = BeginForCurrentSelection();
        }
        _main.ShowDockPanel("Content");
        if (Request?.RevealOnly != true && ViewModel.SelectedPeer is not null &&
            ViewModel.SearchCommand.CanExecute(null))
        {
            _ = ViewModel.SearchCommand.ExecuteAsync(null);
        }
    }

    private void Begin(AssetSelectionRequest request)
    {
        if (Request is not null) Request.IsStale = true;
        DetachGraph();
        Request = request;
        _observedGraph = request.Graph;
        _observedGraph.PropertyChanged += OnGraphPropertyChanged;
        ViewModel.OnCallerChanged();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.ActiveDocument) or nameof(MainViewModel.Graph))) return;
        if (Request is { } request && (!ReferenceEquals(_main.ActiveDocument, request.Document) ||
            !IsActiveGraph(request.Document, request.Graph)))
            request.IsStale = true;
        if (e.PropertyName == nameof(MainViewModel.ActiveDocument)) ViewModel.RefreshPhysicalRoot();
        ViewModel.OnCallerChanged();
    }

    private void OnGraphPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode) &&
            Request is { } request && !ReferenceEquals(request.Graph.SelectedNode, request.Node))
            request.IsStale = true;
        ViewModel.OnCallerChanged();
    }

    private void DetachGraph()
    {
        if (_observedGraph is null) return;
        _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
        _observedGraph = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= OnMainPropertyChanged;
        DetachGraph();
        ViewModel.Dispose();
    }
}
