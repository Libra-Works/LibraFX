using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class AssetPeerOption(Guid clientId, string label)
{
    public Guid ClientId { get; } = clientId;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public sealed class EngineAssetsWindowViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly AssetBrowserCoordinator? _coordinator;
    private CancellationTokenSource? _requests;
    private bool _disposed;
    private Bitmap? _thumbnail;
    private EngineAsset? _selectedAsset;
    private string _query = string.Empty;
    private string _assetClass = string.Empty;
    private string _status = string.Empty;
    private string _description = string.Empty;
    private string _semanticUsage = string.Empty;
    private string _tags = string.Empty;
    private string _columns = "1";
    private string _rows = "1";
    private string _fps = "12";
    private int _offset;
    private int _total;
    private AssetPeerOption? _selectedPeer;
    private Guid? _explicitPeerId;
    private GraphWorkspaceViewModel? _observedGraph;
    private GraphDocumentViewModel? _observedDocument;
    private ResourceAssignmentTarget? _selectedAssignmentTarget;
    private bool _refreshingTargets;
    private bool _refreshingPeers;
    private bool _updatingAssetSelection;
    private bool _detailsReady;
    private Guid? _selectedAssetPeerId;
    private string? _physicalRoot;
    private string _physicalFolder = string.Empty;
    private string _physicalFolderName = string.Empty;
    private string _physicalRootStatus = string.Empty;
    private AssetBrowserRootEntry? _selectedRootEntry;

    public ObservableCollection<AssetPeerOption> Peers { get; } = [];
    internal MainViewModel Main => _main;
    public ObservableCollection<EngineAsset> Assets { get; } = [];
    public ObservableCollection<AssetBrowserRootEntry> VirtualRootEntries { get; } = [];
    public ObservableCollection<AssetBrowserRootEntry> PhysicalRootEntries { get; } = [];
    public ObservableCollection<ResourceAssignmentTarget> AssignmentTargets { get; } = [];
    public ProjectResourcesViewModel ProjectResources { get; }
    public AssetReferencesViewModel References { get; }
    public string PhysicalRootLabel => _physicalRoot ?? LocalizationService.Shared.Get("Resources.ProjectRootUnavailable");
    public string PhysicalFolderLabel => _physicalRoot is null ? string.Empty :
        Path.Combine(_physicalRoot, _physicalFolder);
    public bool HasPhysicalRoot => _physicalRoot is not null;
    public bool CanNavigatePhysicalUp => _physicalRoot is not null && _physicalFolder.Length > 0;
    public bool CanOpenSelectedPhysicalFolder =>
        SelectedRootEntry is { Root: AssetBrowserRootKind.PhysicalProject, IsDirectory: true };
    public string PhysicalFolderName
    {
        get => _physicalFolderName;
        set
        {
            if (SetProperty(ref _physicalFolderName, value))
                CreatePhysicalFolderCommand?.NotifyCanExecuteChanged();
        }
    }
    public string PhysicalRootStatus { get => _physicalRootStatus; private set => SetProperty(ref _physicalRootStatus, value); }
    public AssetBrowserRootEntry? SelectedRootEntry
    {
        get => _selectedRootEntry;
        set
        {
            if (!SetProperty(ref _selectedRootEntry, value)) return;
            OnPropertyChanged(nameof(SelectedVirtualRootEntry));
            OnPropertyChanged(nameof(SelectedPhysicalRootEntry));
            OnPropertyChanged(nameof(SelectedAssetReference));
            OnPropertyChanged(nameof(HasSelectedAssetReference));
            OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
            OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
            if (value is null) return;
            if (value.Root == AssetBrowserRootKind.VirtualEngine && value.EngineAsset is { } asset &&
                value.PeerId == SelectedPeer?.ClientId)
                SelectedAsset = Assets.FirstOrDefault(candidate => candidate.Path == asset.Path) ?? asset;
            else if (value.Root == AssetBrowserRootKind.PhysicalProject)
            {
                SelectedAsset = null;
                Status = LocalizationService.Shared.Get(value.IsDirectory
                    ? "Resources.PhysicalFolderSelected" : "Resources.PhysicalFileSelected");
            }
        }
    }
    public AssetBrowserRootEntry? SelectedVirtualRootEntry
    {
        get => _selectedRootEntry?.Root == AssetBrowserRootKind.VirtualEngine ? _selectedRootEntry : null;
        set
        {
            if (value is not null) SelectedRootEntry = value;
            else if (_selectedRootEntry?.Root == AssetBrowserRootKind.VirtualEngine) SelectedRootEntry = null;
        }
    }
    public AssetBrowserRootEntry? SelectedPhysicalRootEntry
    {
        get => _selectedRootEntry?.Root == AssetBrowserRootKind.PhysicalProject ? _selectedRootEntry : null;
        set
        {
            if (value is not null) SelectedRootEntry = value;
            else if (_selectedRootEntry?.Root == AssetBrowserRootKind.PhysicalProject) SelectedRootEntry = null;
        }
    }
    public AssetBrowserAssetReference? SelectedAssetReference =>
        SelectedRootEntry?.Reference ??
        (SelectedAsset is { } asset && _selectedAssetPeerId is { } peer
            ? new(AssetBrowserRootKind.VirtualEngine, asset.Engine, asset.Path, peer)
            : null);
    public bool HasSelectedAssetReference => SelectedAssetReference is not null;
    public bool CanSelectAssignmentTarget =>
        _coordinator?.Request is { IsExternalSelection: false, Target: null } &&
        AssignmentTargets.Count > 1;
    private int _selectedTab;
    public int SelectedTab { get => _selectedTab; set => SetProperty(ref _selectedTab, value); }
    public IRelayCommand InspectReferencesCommand { get; }
    public ResourceAssignmentTarget? SelectedAssignmentTarget
    {
        get => _selectedAssignmentTarget;
        set
        {
            if (_refreshingTargets) return;
            SetProperty(ref _selectedAssignmentTarget, value);
            OnPropertyChanged(nameof(SelectedAssignmentTarget));
            OnPropertyChanged(nameof(CanAssign));
            OnPropertyChanged(nameof(AssignmentHint));
            AssignCommand?.NotifyCanExecuteChanged();
        }
    }
    public string AssignmentHint => LocalizationService.Shared.Get(
        _coordinator?.Request is { IsStale: true } ? "Messages.AssetSelectionStale" :
        _coordinator?.Request is { RevealOnly: true } ? "Resources.RevealOnly" :
        _coordinator?.Request is { IsExternalSelection: true } ? "Resources.ExternalCallerReady" :
        SelectedAsset is not null && !_detailsReady ? "Resources.LoadDetails" :
        AssignmentTargets.Count > 1 && SelectedAssignmentTarget is null
            ? "Resources.SelectTarget" : AssignmentTargets.Count == 0 ? "Resources.NoTarget" : "Resources.TargetReady");
    public string CallerHint => _coordinator?.Request is { } request
        ? LocalizationService.Shared.Get(request.IsStale ? "Messages.AssetSelectionStale" :
            request.RevealOnly ? "Resources.RevealOnly" : "Resources.CallerBound")
        : string.Empty;
    public IReadOnlyList<string> AssetClasses { get; } = ["", "model", "texture", "material"];

    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string AssetClass { get => _assetClass; set => SetProperty(ref _assetClass, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string SemanticUsage { get => _semanticUsage; set => SetProperty(ref _semanticUsage, value); }
    public string Tags { get => _tags; set => SetProperty(ref _tags, value); }
    public string Columns { get => _columns; set => SetProperty(ref _columns, value); }
    public string Rows { get => _rows; set => SetProperty(ref _rows, value); }
    public string Fps { get => _fps; set => SetProperty(ref _fps, value); }
    public int Offset { get => _offset; private set => SetProperty(ref _offset, value); }
    public int Total { get => _total; private set => SetProperty(ref _total, value); }
    public EngineAsset? SelectedAsset
    {
        get => _selectedAsset;
        set { if (!_updatingAssetSelection) SetSelectedAsset(value, loadDetails: true); }
    }

    public AssetPeerOption? SelectedPeer
    {
        get => _selectedPeer;
        set { if (!_refreshingPeers) SetSelectedPeer(value, userChanged: true); }
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail?.Dispose();
            SetProperty(ref _thumbnail, value);
        }
    }

    public bool HasSelectedAsset => SelectedAsset is not null;
    public string AssetDetails => SelectedAsset is not { } asset
        ? string.Empty
        : $"{LocalizationService.Shared.Get("Asset.Class")}: {asset.AssetClass}" +
          $"{Environment.NewLine}{LocalizationService.Shared.Get("Asset.EngineName")}: {asset.Engine}" +
          (asset.Width > 0 && asset.Height > 0
              ? $"{Environment.NewLine}{LocalizationService.Shared.Get("Asset.Size")}: {asset.Width} × {asset.Height}"
              : "") +
          FormatMaterials(asset.Materials);
    public bool CanAssign
    {
        get
        {
            if (_disposed || _coordinator?.Request is { RevealOnly: true }) return false;
            if (_coordinator?.Request is { IsExternalSelection: true } externalRequest)
            {
                var reference = SelectedAssetReference;
                if (reference is null || externalRequest.AssetClass is { } expected &&
                    (SelectedAsset is null || SelectedAsset.AssetClass != expected)) return false;
                if (reference.Root == AssetBrowserRootKind.VirtualEngine &&
                    (!_detailsReady || SelectedAsset is not { } remoteAsset ||
                     _selectedAssetPeerId != SelectedPeer?.ClientId ||
                     reference.Type != remoteAsset.Engine || reference.Identity != remoteAsset.Path ||
                     reference.SessionPeerId != SelectedPeer?.ClientId)) return false;
                return _coordinator.IsCurrent(externalRequest, SelectedPeer?.ClientId,
                    requiresPeer: reference.Root == AssetBrowserRootKind.VirtualEngine);
            }
            if (!_detailsReady || SelectedAsset is not { } asset || SelectedPeer is not { } peer ||
                _selectedAssetPeerId != peer.ClientId ||
                _coordinator?.Request is { } request && !_coordinator.IsCurrent(request, peer.ClientId)) return false;
            var node = _coordinator?.Request?.Node ?? _observedGraph?.SelectedNode;
            if (node is null || _observedGraph?.Nodes.Contains(node) != true) return false;
            var target = _coordinator?.Request?.Target ?? SelectedAssignmentTarget;
            return target is not null && target.NodeId == node.Id &&
                   ResourceAssignment.Targets(node, asset).Any(current =>
                       current.NodeId == target.NodeId && current.Kind == target.Kind && current.Id == target.Id) &&
                   AssignmentTargets.Contains(target);
        }
    }
    public bool CanGoPrevious => Offset > 0;
    public bool CanGoNext => Offset + Assets.Count < Total;
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand AddModelCommand { get; }
    public IRelayCommand AddSpriteCommand { get; }
    public IRelayCommand AssignCommand { get; }
    public IRelayCommand SaveMetadataCommand { get; }
    public IRelayCommand CreatePhysicalFolderCommand { get; }
    public IRelayCommand RefreshPhysicalRootCommand { get; }
    public IRelayCommand OpenPhysicalFolderCommand { get; }
    public IRelayCommand NavigatePhysicalUpCommand { get; }

    public EngineAssetsWindowViewModel(MainViewModel main, AssetBrowserCoordinator? coordinator = null)
    {
        _main = main;
        _coordinator = coordinator;
        ProjectResources = new ProjectResourcesViewModel(main);
        References = new AssetReferencesViewModel(main);
        InspectReferencesCommand = new RelayCommand(() =>
        {
            if (SelectedAsset is not { } asset) return;
            References.InspectBinding(asset.Path);
            SelectedTab = 2;
        }, () => SelectedAsset is not null);
        SearchCommand = new AsyncRelayCommand(() => LoadPageAsync(0));
        PreviousCommand = new AsyncRelayCommand(() => LoadPageAsync(Math.Max(0, Offset - 32)), () => CanGoPrevious);
        NextCommand = new AsyncRelayCommand(() => LoadPageAsync(Offset + 32), () => CanGoNext);
        AddModelCommand = new RelayCommand(AddModel,
            () => !_disposed && _detailsReady && SelectedAsset?.AssetClass == "model" && _main.ActiveDocument is not null);
        AddSpriteCommand = new RelayCommand(AddSprite,
            () => !_disposed && _detailsReady && SelectedAsset?.AssetClass == "texture" && _main.ActiveDocument is not null);
        AssignCommand = new RelayCommand(Assign, () => CanAssign);
        SaveMetadataCommand = new RelayCommand(SaveMetadata, () => SelectedAsset is not null);
        CreatePhysicalFolderCommand = new RelayCommand(CreatePhysicalFolder,
            () => !_disposed && HasPhysicalRoot && !string.IsNullOrWhiteSpace(PhysicalFolderName));
        RefreshPhysicalRootCommand = new RelayCommand(RefreshPhysicalRoot, () => !_disposed && HasPhysicalRoot);
        OpenPhysicalFolderCommand = new RelayCommand(OpenPhysicalFolder, () => !_disposed && CanOpenSelectedPhysicalFolder);
        NavigatePhysicalUpCommand = new RelayCommand(NavigatePhysicalUp, () => !_disposed && CanNavigatePhysicalUp);
        _main.PropertyChanged += OnMainPropertyChanged;
        AttachGraph();
        RefreshPhysicalRoot();
        RefreshPeers();
        MessageLocalization.Observe(this, static model => { if (!model._disposed) model.RefreshLocalizedText(); });
    }

    private void SetSelectedAsset(EngineAsset? value, bool loadDetails)
    {
        if (!SetProperty(ref _selectedAsset, value)) return;
        _selectedAssetPeerId = value is not null && Assets.Contains(value) ? SelectedPeer?.ClientId : null;
        if (loadDetails)
        {
            _detailsReady = false;
            ClearThumbnail();
            if (value is null) CancelRequest();
        }
        OnPropertyChanged(nameof(HasSelectedAsset));
        OnPropertyChanged(nameof(CanAssign));
        OnPropertyChanged(nameof(AssetDetails));
        OnPropertyChanged(nameof(SelectedAssetReference));
        OnPropertyChanged(nameof(HasSelectedAssetReference));
        OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelectAssignmentTarget));
        SaveMetadataCommand.NotifyCanExecuteChanged();
        InspectReferencesCommand.NotifyCanExecuteChanged();
        AssignCommand.NotifyCanExecuteChanged();
        AddModelCommand.NotifyCanExecuteChanged();
        AddSpriteCommand.NotifyCanExecuteChanged();
        SyncVirtualRootSelection();
        RefreshAssignmentState();
        if (loadDetails && value is not null) _ = LoadDetailsAsync(value);
    }

    private void SetSelectedPeer(AssetPeerOption? value, bool userChanged)
    {
        var changed = _selectedPeer?.ClientId != value?.ClientId;
        SetProperty(ref _selectedPeer, value);
        if (userChanged) _explicitPeerId = value?.ClientId;
        if (changed) _coordinator?.OnPeerSelectionChanged(value?.ClientId);
        if (!changed) return;
        CancelRequest();
        Assets.Clear();
        SelectedAsset = null;
        ClearThumbnail();
        _detailsReady = false;
        _selectedAssetPeerId = null;
        VirtualRootEntries.Clear();
        if (_selectedRootEntry?.Root == AssetBrowserRootKind.VirtualEngine)
        {
            _selectedRootEntry = null;
            OnPropertyChanged(nameof(SelectedRootEntry));
            OnPropertyChanged(nameof(SelectedVirtualRootEntry));
            OnPropertyChanged(nameof(SelectedAssetReference));
            OnPropertyChanged(nameof(HasSelectedAssetReference));
            OnPropertyChanged(nameof(SelectedVirtualRootEntry));
        }
        OnPropertyChanged(nameof(SelectedAssetReference));
        OnPropertyChanged(nameof(HasSelectedAssetReference));
        Offset = Total = 0;
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        PreviousCommand?.NotifyCanExecuteChanged();
        NextCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAssign));
        OnPropertyChanged(nameof(AssignmentHint));
        OnPropertyChanged(nameof(CallerHint));
        RefreshAssignmentState();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewConnectionSummary))
        {
            RefreshPeers();
            return;
        }
        if (e.PropertyName is not (nameof(MainViewModel.ActiveDocument) or nameof(MainViewModel.Graph))) return;
        AttachGraph();
        RefreshPhysicalRoot();
    }

    private void AttachGraph()
    {
        if (_observedDocument is not null) _observedDocument.PropertyChanged -= OnDocumentPropertyChanged;
        _observedDocument = _main.ActiveDocument;
        if (_observedDocument is not null) _observedDocument.PropertyChanged += OnDocumentPropertyChanged;
        if (_observedGraph is not null)
        {
            _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
            _observedGraph.History.StateChanged -= OnGraphHistoryChanged;
        }
        SelectedAssignmentTarget = null;
        _observedGraph = _main.ActiveDocument is not null ? _main.Graph : null;
        if (_observedGraph is not null)
        {
            _observedGraph.PropertyChanged += OnGraphPropertyChanged;
            _observedGraph.History.StateChanged += OnGraphHistoryChanged;
        }
        RefreshAssignmentState();
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphDocumentViewModel.FileKey)) RefreshPhysicalRoot();
    }

    private void OnGraphPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode)) RefreshAssignmentState();
    }

    private void RefreshAssignmentState()
    {
        if (_disposed) return;
        var previous = SelectedAssignmentTarget;
        _refreshingTargets = true;
        try
        {
            AssignmentTargets.Clear();
            var request = _coordinator?.Request;
            var node = request?.Node ?? _observedGraph?.SelectedNode;
            if (SelectedAsset is { } asset && node is not null)
                foreach (var target in ResourceAssignment.Targets(node, asset)) AssignmentTargets.Add(target);
        }
        finally { _refreshingTargets = false; }
        var bound = _coordinator?.Request?.Target;
        SelectedAssignmentTarget = AssignmentTargets.FirstOrDefault(target => bound is not null &&
                target.NodeId == bound.NodeId && target.Kind == bound.Kind && target.Id == bound.Id)
            ?? AssignmentTargets.FirstOrDefault(target =>
                previous is not null && target.NodeId == previous.NodeId && target.Kind == previous.Kind && target.Id == previous.Id)
            ?? (AssignmentTargets.Count == 1 ? AssignmentTargets[0] : null);
        OnPropertyChanged(nameof(CanAssign));
        OnPropertyChanged(nameof(CanSelectAssignmentTarget));
        OnPropertyChanged(nameof(AssignmentHint));
        OnPropertyChanged(nameof(CallerHint));
        AssignCommand?.NotifyCanExecuteChanged();
        AddModelCommand?.NotifyCanExecuteChanged();
        AddSpriteCommand?.NotifyCanExecuteChanged();
    }

    private void OnGraphHistoryChanged(object? sender, EventArgs e) => RefreshAssignmentState();

    public void RefreshPeers()
    {
        var clients = _main.PreviewTelemetry.Clients()
            .Where(peer => _main.PreviewAssets.Supports(peer.ClientId))
            .Select(peer => new AssetPeerOption(peer.ClientId,
                $"{peer.Engine ?? "?"} · {peer.ViewerName ?? ""} · {peer.ClientId:D}"))
            .ToArray();
        _refreshingPeers = true;
        try
        {
            Peers.Clear();
            foreach (var client in clients) Peers.Add(client);
            if (_explicitPeerId is { } explicitId)
                SetSelectedPeer(Peers.FirstOrDefault(peer => peer.ClientId == explicitId), userChanged: false);
            else
                SetSelectedPeer(Peers.Count == 1 ? Peers[0] : null, userChanged: false);
        }
        finally { _refreshingPeers = false; }
    }

    public void OnCallerChanged()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(CallerHint));
        RefreshAssignmentState();
    }

    public void OnCallerCompleted()
    {
        if (_disposed) return;
        Status = LocalizationService.Shared.Get("Asset.Assigned");
        OnCallerChanged();
    }

    public void SetCallerError(string message)
    {
        if (!_disposed) Status = MessageLocalization.Error(message);
    }

    public void RefreshPhysicalRoot()
    {
        if (_disposed) return;
        var root = ProjectAssetRoot.FromProjectFileKey(_main.ActiveDocument?.FileKey);
        var oldRoot = _physicalRoot;
        _physicalRoot = root;
        var sameRoot = oldRoot is not null && root is not null &&
            string.Equals(oldRoot, root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!sameRoot) _physicalFolder = string.Empty;
        var physicalSelection = _selectedRootEntry?.Root == AssetBrowserRootKind.PhysicalProject;
        var previousPath = sameRoot && physicalSelection
            ? _selectedRootEntry?.Path : null;
        PhysicalRootEntries.Clear();
        OnPropertyChanged(nameof(PhysicalRootLabel));
        OnPropertyChanged(nameof(PhysicalFolderLabel));
        OnPropertyChanged(nameof(HasPhysicalRoot));
        OnPropertyChanged(nameof(CanNavigatePhysicalUp));
        OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
        CreatePhysicalFolderCommand?.NotifyCanExecuteChanged();
        RefreshPhysicalRootCommand?.NotifyCanExecuteChanged();
        OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
        NavigatePhysicalUpCommand?.NotifyCanExecuteChanged();
        if (root is null)
        {
            if (_selectedRootEntry?.Root == AssetBrowserRootKind.PhysicalProject)
            {
                _selectedRootEntry = null;
                OnPropertyChanged(nameof(SelectedRootEntry));
                OnPropertyChanged(nameof(SelectedPhysicalRootEntry));
                OnPropertyChanged(nameof(SelectedAssetReference));
                OnPropertyChanged(nameof(HasSelectedAssetReference));
                OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
                OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
            }
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.ProjectRootUnavailable");
            return;
        }
        try
        {
            foreach (var entry in ProjectAssetRoot.List(root, _physicalFolder))
                PhysicalRootEntries.Add(new(AssetBrowserRootKind.PhysicalProject, entry.Name,
                    entry.RelativePath, entry.IsDirectory, Length: entry.Length, ProjectRoot: root));
            if (physicalSelection)
            {
                _selectedRootEntry = previousPath is null ? null :
                    PhysicalRootEntries.FirstOrDefault(entry => entry.Path == previousPath);
                OnPropertyChanged(nameof(SelectedRootEntry));
                OnPropertyChanged(nameof(SelectedPhysicalRootEntry));
                OnPropertyChanged(nameof(SelectedAssetReference));
                OnPropertyChanged(nameof(HasSelectedAssetReference));
                OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
                OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
            }
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalItemCount",
                PhysicalRootEntries.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalRootReadFailed",
                exception.Message);
        }
    }

    private void CreatePhysicalFolder()
    {
        if (_physicalRoot is not { } root) return;
        try
        {
            _ = ProjectAssetRoot.CreateFolder(root, AssetBrowserRootKind.PhysicalProject, PhysicalFolderName,
                _physicalFolder);
            PhysicalFolderName = string.Empty;
            RefreshPhysicalRoot();
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderCreated");
        }
        catch (ProjectFolderExistsException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderExists");
        }
        catch (UnauthorizedAccessException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderAccessDenied");
        }
        catch (ArgumentException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.InvalidFolderName");
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or
                                           System.Security.SecurityException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderFailed",
                exception.Message);
        }
    }

    private void OpenPhysicalFolder()
    {
        if (SelectedRootEntry is not { Root: AssetBrowserRootKind.PhysicalProject, IsDirectory: true } entry)
            return;
        _physicalFolder = entry.Path;
        SelectedRootEntry = null;
        OnPropertyChanged(nameof(PhysicalFolderLabel));
        RefreshPhysicalRoot();
    }

    private void NavigatePhysicalUp()
    {
        if (!CanNavigatePhysicalUp) return;
        _physicalFolder = Path.GetDirectoryName(_physicalFolder) ?? string.Empty;
        SelectedRootEntry = null;
        OnPropertyChanged(nameof(PhysicalFolderLabel));
        RefreshPhysicalRoot();
    }

    private void SyncVirtualRootSelection()
    {
        var current = SelectedAsset is { } asset
            ? VirtualRootEntries.FirstOrDefault(entry => entry.EngineAsset?.Path == asset.Path &&
                entry.PeerId == _selectedAssetPeerId)
            : _selectedRootEntry?.Root == AssetBrowserRootKind.VirtualEngine ? null : _selectedRootEntry;
        if (!ReferenceEquals(_selectedRootEntry, current))
        {
            _selectedRootEntry = current;
            OnPropertyChanged(nameof(SelectedRootEntry));
            OnPropertyChanged(nameof(SelectedVirtualRootEntry));
            OnPropertyChanged(nameof(SelectedPhysicalRootEntry));
            OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
            OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(SelectedAssetReference));
        OnPropertyChanged(nameof(HasSelectedAssetReference));
    }

    private void RefreshVirtualRoot()
    {
        VirtualRootEntries.Clear();
        if (SelectedPeer is { } peer)
            foreach (var asset in Assets)
                VirtualRootEntries.Add(new(AssetBrowserRootKind.VirtualEngine, asset.Name, asset.Path,
                    false, asset, peer.ClientId));
        SyncVirtualRootSelection();
    }

    public async Task RevealExistingAsync(AssetSelectionRequest request, string path, Guid peerId)
    {
        if (_disposed || _coordinator is null || !_coordinator.IsCurrent(request, peerId)) return;
        var definition = request.Node.Definition.Parameters.FirstOrDefault(parameter =>
            parameter.Id == request.ParameterId);
        if (definition is null || request.Node.GetParameterValue(definition).ValueKind !=
            System.Text.Json.JsonValueKind.String ||
            request.Node.GetParameterValue(definition).GetString() != path)
        {
            request.IsStale = true;
            OnCallerChanged();
            return;
        }

        try
        {
            var detail = await _main.PreviewAssets.DetailsAsync(path, peerId);
            if (!_coordinator.IsCurrent(request, peerId) || !ReferenceEquals(_coordinator.Request, request) ||
                request.Node.GetParameterValue(definition).GetString() != path) return;
            if (request.AssetClass is { } assetClass && detail.AssetClass != assetClass)
                throw new InvalidDataException(LocalizationService.Shared.Get("Resources.AssetClassMismatch"));

            var existing = Assets.FirstOrDefault(asset => asset.Path == detail.Path);
            if (existing is null) Assets.Insert(0, detail);
            else Assets[Assets.IndexOf(existing)] = detail;
            _updatingAssetSelection = true;
            try
            {
                _detailsReady = true;
                SetSelectedAsset(detail, loadDetails: false);
            }
            finally { _updatingAssetSelection = false; }
            RefreshVirtualRoot();
            RefreshAssignmentState();
            var thumbnail = await _main.PreviewAssets.ThumbnailAsync(path, 256, peerId);
            if (!_coordinator.IsCurrent(request, peerId) || SelectedAsset?.Path != path) return;
            using var stream = new MemoryStream(thumbnail.Image, writable: false);
            Thumbnail = new Bitmap(stream);
            Status = LocalizationService.Shared.Get("Resources.AssetRevealed");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           InvalidDataException or IOException or TimeoutException or FormatException)
        {
            if (!_disposed && _coordinator.IsCurrent(request, peerId))
                Status = LocalizationService.Shared.Get("Resources.AssetRevealFailed", exception.Message);
        }
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(AssetDetails));
        OnPropertyChanged(nameof(PhysicalRootLabel));
        OnPropertyChanged(nameof(CallerHint));
        OnPropertyChanged(nameof(AssignmentHint));
        RefreshPhysicalRoot();
        RefreshVirtualRoot();
        RefreshAssignmentState();
    }

    private async Task LoadPageAsync(int offset)
    {
        if (_disposed) return;
        RefreshPeers();
        if (SelectedPeer is null)
        {
            Status = _explicitPeerId.HasValue
                ? LocalizationService.Shared.Get("Asset.PeerDisconnected")
                : Peers.Count > 1
                ? LocalizationService.Shared.Get("Asset.SelectPeer")
                : LocalizationService.Shared.Get("Asset.NoPeer");
            return;
        }
        CancellationTokenSource? request = null;
        var peerId = SelectedPeer.ClientId;
        try
        {
            request = BeginRequest();
            var cancellation = request.Token;
            var page = await _main.PreviewAssets.CatalogAsync(Query.Trim(), AssetClass,
                offset, 32, peerId).WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (SelectedPeer?.ClientId != peerId) return;
            SetSelectedAsset(null, loadDetails: false);
            _detailsReady = false;
            Assets.Clear();
            foreach (var asset in page.Assets) Assets.Add(asset);
            RefreshVirtualRoot();
            Offset = page.Offset;
            Total = page.Total;
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
            PreviousCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            SelectedAsset = Assets.FirstOrDefault();
            Status = LocalizationService.Shared.Get("Asset.Results", Total, Offset + 1,
                Math.Min(Offset + Assets.Count, Total));
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestTimedOut");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           IOException or InvalidDataException or TimeoutException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestFailed", exception.Message);
        }
    }

    private async Task LoadDetailsAsync(EngineAsset selected)
    {
        if (_disposed) return;
        ClearThumbnail();
        LoadMetadata(selected);
        if (SelectedPeer is null) return;
        var peerId = SelectedPeer.ClientId;
        CancellationTokenSource? request = null;
        try
        {
            request = BeginRequest();
            var cancellation = request.Token;
            var detail = await _main.PreviewAssets.DetailsAsync(selected.Path, peerId)
                .WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (SelectedAsset?.Path != selected.Path || SelectedPeer?.ClientId != peerId) return;
            _updatingAssetSelection = true;
            try
            {
                var index = Assets.IndexOf(selected);
                if (index >= 0) Assets[index] = detail;
                _detailsReady = true;
                SetSelectedAsset(detail, loadDetails: false);
                RefreshAssignmentState();
            }
            finally { _updatingAssetSelection = false; }
            OnPropertyChanged(nameof(SelectedAsset));
            var thumbnail = await _main.PreviewAssets.ThumbnailAsync(selected.Path, 256, peerId)
                .WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (SelectedAsset?.Path != selected.Path || SelectedPeer?.ClientId != peerId) return;
            using var stream = new MemoryStream(thumbnail.Image, writable: false);
            Thumbnail = new Bitmap(stream);
            Status = detail.Materials?.Count > 0
                ? LocalizationService.Shared.Get("Asset.MaterialCount", detail.Materials.Count)
                : LocalizationService.Shared.Get("Asset.Ready");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestTimedOut");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           IOException or InvalidDataException or TimeoutException or
                                           FormatException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestFailed", exception.Message);
        }
    }

    private void LoadMetadata(EngineAsset asset)
    {
        try
        {
            var metadata = _main.AssetMetadata.Get(asset.Engine, asset.Path);
            Description = metadata.Description;
            SemanticUsage = metadata.SemanticUsage;
            Tags = string.Join(", ", metadata.NormalizedTags);
            Columns = metadata.AtlasColumns.ToString(CultureInfo.InvariantCulture);
            Rows = metadata.AtlasRows.ToString(CultureInfo.InvariantCulture);
            Fps = metadata.FramesPerSecond.ToString("G", CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = LocalizationService.Shared.Get("Asset.MetadataReadFailed", exception.Message);
        }
    }

    private void SaveMetadata()
    {
        if (SelectedAsset is not { } asset) return;
        if (!int.TryParse(Columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var columns) ||
            !int.TryParse(Rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) ||
            !double.TryParse(Fps, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            Status = LocalizationService.Shared.Get("Asset.InvalidAtlas");
            return;
        }
        try
        {
            _main.AssetMetadata.Set(asset.Engine, asset.Path, new AssetMetadata(asset.Engine, asset.Path,
                Description, Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                SemanticUsage, columns, rows, fps));
            Status = LocalizationService.Shared.Get("Asset.MetadataSaved");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Status = LocalizationService.Shared.Get("Asset.MetadataSaveFailed", exception.Message);
        }
    }

    private void AddModel()
    {
        if (SelectedAsset is not { } asset) return;
        try { RenderNodeAuthoring.CreateModel(_main.Graph, asset); Status = LocalizationService.Shared.Get("Asset.ModelAdded"); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        { Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message); }
    }

    private void AddSprite()
    {
        if (SelectedAsset is not { } asset) return;
        try
        {
            if (!TryAtlasValues(out var columns, out var rows, out var fps))
            {
                Status = LocalizationService.Shared.Get("Asset.InvalidAtlas");
                return;
            }
            RenderNodeAuthoring.CreateSprite(_main.Graph, asset, columns, rows, fps);
            Status = LocalizationService.Shared.Get("Asset.SpriteAdded");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           InvalidDataException or IOException)
        { Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message); }
    }

    private bool TryAtlasValues(out int columns, out int rows, out double fps)
    {
        columns = 0;
        rows = 0;
        fps = 0;
        return int.TryParse(Columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out columns) &&
               int.TryParse(Rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out rows) &&
               double.TryParse(Fps, NumberStyles.Float, CultureInfo.InvariantCulture, out fps) &&
               columns is >= 1 and <= 4096 && rows is >= 1 and <= 4096 &&
               double.IsFinite(fps) && fps is >= 0 and <= 10000;
    }

    private void Assign()
    {
        if (!CanAssign) return;
        try
        {
            if (_coordinator?.Request is not null)
            {
                if (!_coordinator.TrySelect(SelectedAsset, SelectedAssetReference, SelectedPeer?.ClientId,
                        SelectedAssignmentTarget, out var error))
                {
                    Status = error;
                    return;
                }
            }
            else if (SelectedAsset is { } asset && SelectedAssignmentTarget is { } target &&
                     _observedGraph?.SelectedNode is { } node)
                ResourceAssignment.Assign(_observedGraph, node, asset, target);
            else return;
            Status = LocalizationService.Shared.Get("Asset.Assigned");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        { Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message); }
    }

    private CancellationTokenSource BeginRequest()
    {
        CancelRequest();
        _requests = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        return _requests;
    }

    private void CancelRequest()
    {
        var request = _requests;
        _requests = null;
        request?.Cancel();
        request?.Dispose();
    }

    private void ClearThumbnail() => Thumbnail = null;

    private static string FormatMaterials(IReadOnlyList<EngineMaterial>? materials)
    {
        if (materials is not { Count: > 0 })
            return $"{Environment.NewLine}{Environment.NewLine}{LocalizationService.Shared.Get("Asset.NoMaterials")}";
        var lines = new List<string>();
        foreach (var material in materials.Take(32))
        {
            lines.Add($"{Environment.NewLine}{Environment.NewLine}{LocalizationService.Shared.Get("Asset.Slot", material.Slot)}: {material.Name} — {material.Path}");
            foreach (var parameter in material.Parameters.Take(24))
                lines.Add($"  {parameter.Name} [{parameter.Kind}] = {parameter.Value.GetRawText()}");
            if (material.Parameters.Count > 24) lines.Add("  …");
        }
        if (materials.Count > 32) lines.Add("…");
        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelRequest();
        _main.PropertyChanged -= OnMainPropertyChanged;
        if (_observedDocument is not null) _observedDocument.PropertyChanged -= OnDocumentPropertyChanged;
        if (_observedGraph is not null)
        {
            _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
            _observedGraph.History.StateChanged -= OnGraphHistoryChanged;
        }
        ProjectResources.Dispose();
        References.Dispose();
        ClearThumbnail();
    }
}
