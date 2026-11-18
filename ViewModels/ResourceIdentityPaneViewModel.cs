using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed record ResourceIdentityTargetNode(Guid Id, string Label, GraphNodeKind Kind);
public sealed record ResourceIdentityTargetLocation(string Id, string Label, string LegacyValue);
public sealed record ResourceIdentityGraphParameter(string Id, string Name, string Kind);
public sealed record RegisteredResourceOption(Guid Id, string Name, string Kind, bool HasProjectBinding);

/// <summary>
/// Explicit project/resource registration and legacy-use migration. This view model
/// never changes the render node's legacy path; only the identity document is edited.
/// </summary>
public sealed class ResourceIdentityPaneViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private GraphWorkspaceViewModel? _observedGraph;
    private AssetPeerOption? _selectedPeer;
    private EngineProjectConnectionState? _projectState;
    private string _status = "";
    private string _resourceName = "";
    private string _resourceKind = "model";
    private string _bindingValue = "";
    private string _parameterName = "";
    private string _parameterKind = "scalar";
    private string _parameterDefaultJson = "0";
    private string _parameterMinimumJson = "";
    private string _parameterMaximumJson = "";
    private string _parameterBindingName = "";
    private string _parameterAssociation = "0";
    private string _parameterIndex = "-1";
    private bool _replaceExistingUse;
    private bool _confirmFork;
    private bool _disposed;
    private ResourceIdentityTargetNode? _selectedTargetNode;
    private ResourceIdentityTargetLocation? _selectedLocation;
    private RegisteredResourceOption? _selectedResource;
    private EngineMaterialParameterMetadata? _selectedEngineParameter;
    private ResourceIdentityGraphParameter? _selectedGraphParameter;
    private string _bindingFormat = "";

    public ObservableCollection<AssetPeerOption> Peers { get; } = [];
    public ObservableCollection<ResourceIdentityTargetNode> TargetNodes { get; } = [];
    public ObservableCollection<ResourceIdentityTargetLocation> Locations { get; } = [];
    public ObservableCollection<ResourceIdentityGraphParameter> GraphParameters { get; } = [];
    public ObservableCollection<EngineMaterialParameterMetadata> EngineParameters { get; } = [];
    public ObservableCollection<RegisteredResourceOption> RegisteredResources { get; } = [];
    public IReadOnlyList<string> ResourceKinds { get; } = ["model", "texture", "material"];
    public IReadOnlyList<string> ParameterKinds { get; } = ["scalar", "vector", "texture"];
    public IReadOnlyList<string> ParameterAssociations { get; } = ["0", "1", "2"];

    public IRelayCommand RefreshPeersCommand { get; }
    public IAsyncRelayCommand RefreshProjectCommand { get; }
    public IAsyncRelayCommand InitializeProjectCommand { get; }
    public IAsyncRelayCommand KeepProjectCommand { get; }
    public IAsyncRelayCommand ForkProjectCommand { get; }
    public IAsyncRelayCommand ProbeCommand { get; }
    public IAsyncRelayCommand RegisterAndAssignCommand { get; }
    public IAsyncRelayCommand AssignExistingCommand { get; }
    public IAsyncRelayCommand RegisterParameterCommand { get; }
    public IAsyncRelayCommand ReadMaterialSchemaCommand { get; }
    public IAsyncRelayCommand DeliverUnityBindingsCommand { get; }

    public AssetPeerOption? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (!SetProperty(ref _selectedPeer, value)) return;
            ProjectState = null;
            OnPropertyChanged(nameof(Engine));
            OnPropertyChanged(nameof(ProjectIdText));
            OnPropertyChanged(nameof(CanRegister));
            OnPropertyChanged(nameof(CanDeliverUnityBindings));
            RefreshBindingFormat();
            RefreshProjectCommand.NotifyCanExecuteChanged();
            InitializeProjectCommand.NotifyCanExecuteChanged();
            KeepProjectCommand.NotifyCanExecuteChanged();
            ForkProjectCommand.NotifyCanExecuteChanged();
            ProbeCommand.NotifyCanExecuteChanged();
            RegisterAndAssignCommand.NotifyCanExecuteChanged();
            DeliverUnityBindingsCommand.NotifyCanExecuteChanged();
            if (value is not null) _ = RefreshProjectAsync();
        }
    }

    public string Engine => SelectedPeer is { } peer
        ? _main.PreviewTelemetry.Clients(peer.ClientId).FirstOrDefault()?.Engine ?? "" : "";
    public string BindingFormat
    {
        get => _bindingFormat;
        private set
        {
            if (!SetProperty(ref _bindingFormat, value)) return;
            OnPropertyChanged(nameof(IsUnity));
        }
    }
    public bool IsUnity => Engine == "Unity";
    public EngineProjectConnectionState? ProjectState
    {
        get => _projectState;
        private set
        {
            if (!SetProperty(ref _projectState, value)) return;
            OnPropertyChanged(nameof(ProjectIdText));
            OnPropertyChanged(nameof(ProjectStatus));
            OnPropertyChanged(nameof(CanRegister));
            OnPropertyChanged(nameof(CanDeliverUnityBindings));
            RefreshRegisteredResources();
            ProbeCommand.NotifyCanExecuteChanged();
            RegisterAndAssignCommand.NotifyCanExecuteChanged();
            AssignExistingCommand.NotifyCanExecuteChanged();
            RefreshGraphParameters();
            RegisterParameterCommand.NotifyCanExecuteChanged();
            ReadMaterialSchemaCommand.NotifyCanExecuteChanged();
            DeliverUnityBindingsCommand.NotifyCanExecuteChanged();
        }
    }
    public string ProjectIdText => ProjectState?.Project is { } project
        ? $"{project.Engine} · {project.ProjectId:D}" : ProjectState?.ProjectState ?? "";
    public string ProjectStatus => ProjectState is { } state
        ? $"{state.Status} · {state.Detail ?? state.ProjectState}" : LocalizationService.Shared.Get("Resources.IdentityProjectUnavailable");
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ResourceName
    {
        get => _resourceName;
        set
        {
            if (!SetProperty(ref _resourceName, value)) return;
            OnPropertyChanged(nameof(CanRegister));
            RegisterAndAssignCommand.NotifyCanExecuteChanged();
        }
    }
    public string ResourceKind
    {
        get => _resourceKind;
        set
        {
            if (!SetProperty(ref _resourceKind, value)) return;
            OnPropertyChanged(nameof(CanRegister));
            ProbeCommand.NotifyCanExecuteChanged();
            RegisterAndAssignCommand.NotifyCanExecuteChanged();
        }
    }
    public string BindingValue
    {
        get => _bindingValue;
        set
        {
            if (!SetProperty(ref _bindingValue, value)) return;
            OnPropertyChanged(nameof(CanRegister));
            ProbeCommand.NotifyCanExecuteChanged();
            RegisterAndAssignCommand.NotifyCanExecuteChanged();
        }
    }
    public bool ReplaceExistingUse
    {
        get => _replaceExistingUse;
        set { if (SetProperty(ref _replaceExistingUse, value)) RegisterAndAssignCommand.NotifyCanExecuteChanged(); }
    }
    public bool ConfirmFork
    {
        get => _confirmFork;
        set { if (SetProperty(ref _confirmFork, value)) ForkProjectCommand.NotifyCanExecuteChanged(); }
    }
    public ResourceIdentityTargetNode? SelectedTargetNode
    {
        get => _selectedTargetNode;
        set
        {
            if (!SetProperty(ref _selectedTargetNode, value)) return;
            RefreshLocations();
            OnPropertyChanged(nameof(LegacyValue));
            OnPropertyChanged(nameof(ExpectedResourceKind));
            OnPropertyChanged(nameof(CanRegister));
            RefreshRegisteredResources();
            RegisterAndAssignCommand.NotifyCanExecuteChanged();
            AssignExistingCommand.NotifyCanExecuteChanged();
            RegisterParameterCommand.NotifyCanExecuteChanged();
            RefreshGraphParameters();
        }
    }
    public ResourceIdentityTargetLocation? SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (!SetProperty(ref _selectedLocation, value)) return;
            OnPropertyChanged(nameof(LegacyValue));
            OnPropertyChanged(nameof(ExpectedResourceKind));
            OnPropertyChanged(nameof(CanRegister));
            RegisterAndAssignCommand.NotifyCanExecuteChanged();
            AssignExistingCommand.NotifyCanExecuteChanged();
        }
    }
    public string LegacyValue => SelectedLocation?.LegacyValue ?? "";
    public string ExpectedResourceKind => SelectedLocation is null ? "" : KindForLocation(SelectedLocation.Id,
        SelectedTargetNode?.Kind);
    public RegisteredResourceOption? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (!SetProperty(ref _selectedResource, value)) return;
            RegisterParameterCommand.NotifyCanExecuteChanged();
            AssignExistingCommand.NotifyCanExecuteChanged();
        }
    }
    public EngineMaterialParameterMetadata? SelectedEngineParameter
    {
        get => _selectedEngineParameter;
        set
        {
            if (!SetProperty(ref _selectedEngineParameter, value) || value is null) return;
            ParameterName = value.Name;
            ParameterKind = value.Kind;
            ParameterBindingName = value.Name;
            ParameterAssociation = value.Association.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ParameterIndex = value.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ParameterDefaultJson = value.Default.GetRawText();
            ParameterMinimumJson = value.Minimum?.GetRawText() ?? "";
            ParameterMaximumJson = value.Maximum?.GetRawText() ?? "";
            RegisterParameterCommand.NotifyCanExecuteChanged();
        }
    }
    public ResourceIdentityGraphParameter? SelectedGraphParameter
    {
        get => _selectedGraphParameter;
        set
        {
            if (!SetProperty(ref _selectedGraphParameter, value)) return;
            RegisterParameterCommand.NotifyCanExecuteChanged();
        }
    }
    public string ParameterName { get => _parameterName; set => SetProperty(ref _parameterName, value); }
    public string ParameterKind { get => _parameterKind; set => SetProperty(ref _parameterKind, value); }
    public string ParameterDefaultJson { get => _parameterDefaultJson; set => SetProperty(ref _parameterDefaultJson, value); }
    public string ParameterMinimumJson { get => _parameterMinimumJson; set => SetProperty(ref _parameterMinimumJson, value); }
    public string ParameterMaximumJson { get => _parameterMaximumJson; set => SetProperty(ref _parameterMaximumJson, value); }
    public string ParameterBindingName { get => _parameterBindingName; set => SetProperty(ref _parameterBindingName, value); }
    public string ParameterAssociation { get => _parameterAssociation; set => SetProperty(ref _parameterAssociation, value); }
    public string ParameterIndex { get => _parameterIndex; set => SetProperty(ref _parameterIndex, value); }
    public bool CanRegister => !_disposed && ProjectState?.Status == "Ready" && SelectedPeer is not null &&
        !string.IsNullOrWhiteSpace(ResourceName) && !string.IsNullOrWhiteSpace(BindingValue) &&
        (SelectedLocation is null || ExpectedResourceKind == ResourceKind);
    public bool CanDeliverUnityBindings => !_disposed && IsUnity && ProjectState?.Status == "Ready" &&
        SelectedPeer is not null && ProjectState.Project is { Engine: "Unity" } &&
        CurrentDocument?.ResourceBindings.Any(binding => binding.Project == ProjectState.Project) == true;

    private GraphWorkspaceViewModel Graph => _main.Graph;
    private ResourceIdentityDocument? CurrentDocument => _main.ActiveDocument?.Workspace.ResourceIdentities;

    public ResourceIdentityPaneViewModel(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        RefreshPeersCommand = new RelayCommand(RefreshPeers);
        RefreshProjectCommand = new AsyncRelayCommand(RefreshProjectAsync, () => SelectedPeer is not null);
        InitializeProjectCommand = new AsyncRelayCommand(() => ProjectOperationAsync("initialize"),
            () => SelectedPeer is not null && ProjectState?.Status == "Unregistered");
        KeepProjectCommand = new AsyncRelayCommand(() => ProjectOperationAsync("keep"),
            () => SelectedPeer is not null && ProjectState?.ProjectState == "CloneDecisionRequired");
        ForkProjectCommand = new AsyncRelayCommand(() => ProjectOperationAsync("fork"),
            () => SelectedPeer is not null && ConfirmFork && ProjectState?.ProjectState == "CloneDecisionRequired");
        ProbeCommand = new AsyncRelayCommand(ProbeAsync,
            () => SelectedPeer is not null && ProjectState?.Status == "Ready" &&
                !string.IsNullOrWhiteSpace(BindingValue) && !string.IsNullOrWhiteSpace(ResourceKind));
        RegisterAndAssignCommand = new AsyncRelayCommand(RegisterAndAssignAsync, () => CanRegister);
        AssignExistingCommand = new AsyncRelayCommand(AssignExistingAsync,
            () => SelectedResource is { HasProjectBinding: true } && SelectedLocation is not null &&
                ProjectState?.Status == "Ready");
        ReadMaterialSchemaCommand = new AsyncRelayCommand(ReadMaterialSchemaAsync,
            () => SelectedResource is { Kind: "material", HasProjectBinding: true } &&
                ProjectState?.Status == "Ready" && SelectedPeer is not null);
        RegisterParameterCommand = new AsyncRelayCommand(RegisterParameterAsync,
            () => SelectedResource is { Kind: "material", HasProjectBinding: true } &&
                SelectedTargetNode is not null && SelectedGraphParameter is not null &&
                SelectedEngineParameter is not null && ProjectState?.Status == "Ready");
        DeliverUnityBindingsCommand = new AsyncRelayCommand(DeliverUnityBindingsAsync, () => CanDeliverUnityBindings);
        _main.PropertyChanged += OnMainChanged;
        _main.ActiveGraphProvider.PropertyChanged += OnActiveGraphChanged;
        RefreshPeers();
        AttachGraph();
    }

    public void RefreshPeers()
    {
        if (_disposed) return;
        var old = SelectedPeer?.ClientId;
        Peers.Clear();
        foreach (var peer in _main.PreviewTelemetry.Clients()
                     .Where(peer => _main.PreviewAssets.Supports(peer.ClientId, "resourceIdentity")))
            Peers.Add(new(peer.ClientId, $"{peer.Engine ?? "?"} · {peer.ViewerName ?? peer.ClientId.ToString("D")[..8]}"));
        SelectedPeer = Peers.FirstOrDefault(peer => peer.ClientId == old) ??
            (Peers.Count == 1 ? Peers[0] : null);
    }

    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ActiveDocument)) return;
        AttachGraph();
        RefreshTargetNodes();
        RefreshRegisteredResources();
    }

    private void OnActiveGraphChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        AttachGraph();
        RefreshTargetNodes();
    }

    private void AttachGraph()
    {
        var graph = _main.ActiveDocument is null ? null : _main.ActiveGraphProvider.Graph;
        if (ReferenceEquals(graph, _observedGraph)) return;
        if (_observedGraph is not null)
        {
            _observedGraph.History.StateChanged -= OnGraphStateChanged;
            _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
        }
        _observedGraph = graph;
        if (graph is not null)
        {
            graph.History.StateChanged += OnGraphStateChanged;
            graph.PropertyChanged += OnGraphPropertyChanged;
        }
        RefreshTargetNodes();
    }

    private void OnGraphStateChanged(object? sender, EventArgs e)
    {
        RefreshTargetNodes();
        RefreshRegisteredResources();
    }

    private void OnGraphPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphWorkspaceViewModel.SelectedNode) or nameof(GraphWorkspaceViewModel.Nodes))
            RefreshTargetNodes();
    }

    private void RefreshTargetNodes()
    {
        if (_disposed) return;
        var previousId = SelectedTargetNode?.Id;
        TargetNodes.Clear();
        var root = _observedGraph?.CreateSnapshot();
        if (root is not null)
        {
            void Visit(GraphSnapshot graph)
            {
                foreach (var node in graph.Nodes)
                {
                    if (GraphNodeDefinition.All.FirstOrDefault(definition => definition.Kind == node.Kind) is { } definition &&
                        RenderNodeSchema.IsRenderNode(definition))
                        TargetNodes.Add(new(node.Id, $"{node.Name} · {node.Kind.Value} · {node.Id.ToString("D")[..8]}", node.Kind));
                    if (node.ChildGraph is { } child) Visit(child);
                }
            }
            Visit(root);
        }
        SelectedTargetNode = TargetNodes.FirstOrDefault(node => node.Id == previousId) ??
            TargetNodes.FirstOrDefault(node => node.Id == _observedGraph?.SelectedNode?.Id);
    }

    private void RefreshLocations()
    {
        var previousId = SelectedLocation?.Id;
        Locations.Clear();
        if (SelectedTargetNode is { } target && FindNode(Graph.CreateSnapshot(), target.Id, out _, out var node))
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
            Locations.Add(new("assetPath", "assetPath", StringParameter(node, "assetPath")));
            foreach (var material in RenderNodeSchema.Read(definition, node.Parameters)
                         .GroupBy(parameter => parameter.Slot).OrderBy(group => group.Key))
                Locations.Add(new($"material:{material.Key}", $"material:{material.Key}", material.First().MaterialPath));
            foreach (var parameter in RenderNodeSchema.Read(definition, node.Parameters).Where(item => item.Kind == "texture"))
                Locations.Add(new($"texture:{parameter.Id}", $"texture:{parameter.Name}", parameter.Value.GetString() ?? ""));
        }
        SelectedLocation = Locations.FirstOrDefault(location => location.Id == previousId) ?? Locations.FirstOrDefault();
        RefreshGraphParameters();
    }

    private void RefreshGraphParameters()
    {
        var previousId = SelectedGraphParameter?.Id;
        GraphParameters.Clear();
        EngineParameters.Clear();
        SelectedEngineParameter = null;
        if (SelectedTargetNode is { } target && SelectedLocation?.Id is { } location &&
            location.StartsWith("material:", StringComparison.Ordinal) &&
            FindNode(Graph.CreateSnapshot(), target.Id, out _, out var node))
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
            var slot = location["material:".Length..];
            foreach (var parameter in RenderNodeSchema.Read(definition, node.Parameters)
                         .Where(item => item.Slot.ToString() == slot && item.Kind is "scalar" or "vector"))
                GraphParameters.Add(new(parameter.Id, parameter.Name, parameter.Kind));
        }
        SelectedGraphParameter = GraphParameters.FirstOrDefault(item => item.Id == previousId) ??
            GraphParameters.FirstOrDefault();
        ReadMaterialSchemaCommand?.NotifyCanExecuteChanged();
    }

    private void RefreshRegisteredResources()
    {
        if (_disposed) return;
        var previous = SelectedResource?.Id;
        RegisteredResources.Clear();
        var project = ProjectState?.Project;
        var document = CurrentDocument;
        foreach (var resource in document?.Resources ?? [])
        {
            var bound = project is not null && document is not null && document.ResourceBindings.Any(binding =>
                binding.Project == project && binding.ResourceId == resource.Id);
            RegisteredResources.Add(new(resource.Id, resource.Name, resource.Kind, bound));
        }
        SelectedResource = RegisteredResources.FirstOrDefault(resource => resource.Id == previous);
        ReadMaterialSchemaCommand?.NotifyCanExecuteChanged();
        RegisterParameterCommand?.NotifyCanExecuteChanged();
    }

    private async Task RefreshProjectAsync()
    {
        if (_disposed || SelectedPeer is not { } peer) return;
        try
        {
            var state = await _main.PreviewAssets.EngineProjectAsync(peer.ClientId);
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            ProjectState = state;
            Status = state.Project is null
                ? LocalizationService.Shared.Get("Resources.IdentityNoProject", state.Status, state.Detail ?? "")
                : LocalizationService.Shared.Get("Resources.IdentityProjectReady", state.Project.Engine,
                    state.Project.ProjectId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            ProjectState = null;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
        }
    }

    private async Task ProjectOperationAsync(string operation)
    {
        if (_disposed || SelectedPeer is not { } peer) return;
        if (operation == "fork" && !ConfirmFork) return;
        try
        {
            var state = await _main.PreviewAssets.EngineProjectAsync(peer.ClientId, operation);
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            ProjectState = state;
            ConfirmFork = false;
            Status = state.Detail ?? LocalizationService.Shared.Get("Resources.IdentityProjectState",
                state.Status, state.ProjectState);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
        }
    }

    private async Task ProbeAsync()
    {
        if (SelectedPeer is not { } peer || ProjectState?.Project is not { } project) return;
        var bindingFormat = BindingFormat;
        var bindingValue = BindingValue.Trim();
        try
        {
            var result = await _main.PreviewAssets.ProbeResourceAsync(peer.ClientId, project,
                new(bindingFormat, bindingValue));
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId || ProjectState?.Project != project ||
                BindingFormat != bindingFormat || BindingValue.Trim() != bindingValue)
                return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult",
                result.Status, result.ActualKind ?? "", result.Detail ?? "");
            if (result.Status == ResourceResolutionStatus.Resolved && result.ActualKind != ResourceKind)
                Status = LocalizationService.Shared.Get("Resources.IdentityKindMismatch", ResourceKind,
                    result.ActualKind ?? "");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException or JsonException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId || ProjectState?.Project != project) return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
        }
    }

    private async Task RegisterAndAssignAsync()
    {
        if (!CanRegister || SelectedPeer is not { } peer || ProjectState?.Project is not { } project) return;
        var target = SelectedTargetNode;
        var location = SelectedLocation;
        var workspace = _observedGraph;
        var capturedKind = ResourceKind;
        var capturedName = ResourceName.Trim();
        var capturedBindingValue = BindingValue.Trim();
        var capturedFormat = BindingFormat;
        if (target is not null && (location is null ||
            !FindNode(workspace!.CreateSnapshot(), target.Id, out _, out _)))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var capturedScope = target is null ? Array.Empty<Guid>() : GetScope(workspace!.CreateSnapshot(), target.Id);
        var binding = new OpaqueBinding(capturedFormat, capturedBindingValue);
        var result = await _main.PreviewAssets.ProbeResourceAsync(peer.ClientId, project, binding);
        if (result.Status != ResourceResolutionStatus.Resolved)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult", result.Status,
                result.ActualKind ?? "", result.Detail ?? "");
            return;
        }
        if (result.ActualKind != capturedKind || location is not null &&
            capturedKind != KindForLocation(location.Id, target?.Kind))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityKindMismatch", capturedKind,
                result.ActualKind ?? "");
            return;
        }
        if (_disposed || !ReferenceEquals(workspace, _main.ActiveGraphProvider.Graph) ||
            peer.ClientId != SelectedPeer?.ClientId || project != ProjectState?.Project ||
            capturedKind != ResourceKind || capturedName != ResourceName.Trim() ||
            capturedBindingValue != BindingValue.Trim() || capturedFormat != BindingFormat ||
            target is not null && (target.Id != SelectedTargetNode?.Id ||
                location?.Id != SelectedLocation?.Id ||
                !FindNode(workspace!.CreateSnapshot(), target.Id, out var currentScope, out _) ||
                !ResourceIdentityScope.Equal(capturedScope, currentScope)))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var resourceId = Guid.NewGuid();
        var current = workspace?.ResourceIdentities;
        var useExists = target is not null && location is not null && current?.Uses.Any(use =>
            use.NodeId == target.Id && use.Location == location.Id &&
            ResourceIdentityScope.Equal(use.InstancePath, capturedScope)) == true;
        if (useExists && !ReplaceExistingUse)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityReplaceRequired");
            return;
        }
        var definition = new ResourceDefinition(resourceId, capturedKind, capturedName);
        if (workspace is null)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityDocumentRequired");
            return;
        }
        workspace.EditResourceIdentityDocument(document =>
        {
            document ??= EmptyDocument();
            if (document.Resources.Any(resource => resource.Id == resourceId))
                throw new InvalidDataException("Generated duplicate resource identity.");
            var uses = document.Uses.Where(use => target is null || location is null ||
                use.NodeId != target.Id || use.Location != location.Id ||
                !ResourceIdentityScope.Equal(use.InstancePath, capturedScope)).ToList();
            if (target is not null && location is not null)
                uses.Add(new(target.Id, location.Id, resourceId, capturedScope));
            return document with
            {
                Resources = document.Resources.Append(definition).ToArray(),
                ResourceBindings = document.ResourceBindings.Append(
                    new ProjectResourceBinding(project, resourceId, capturedKind, binding)).ToArray(),
                Uses = uses.ToArray()
            };
        }, LocalizationService.Shared.Get(useExists
            ? "Resources.IdentityHistoryReplace" : "Resources.IdentityHistoryRegister"));
        ReplaceExistingUse = false;
        RefreshRegisteredResources();
        SelectedResource = RegisteredResources.FirstOrDefault(item => item.Id == resourceId);
        Status = LocalizationService.Shared.Get("Resources.IdentityRegistered", capturedName, resourceId);
    }

    private async Task AssignExistingAsync()
    {
        if (_observedGraph is not { } workspace || SelectedPeer is not { } peer ||
            ProjectState?.Project is not { } project || SelectedTargetNode is not { } target ||
            SelectedLocation is not { } location || SelectedResource is not { } selected)
            return;
        var initialGraph = workspace.CreateSnapshot();
        if (!FindNode(initialGraph, target.Id, out var capturedScope, out _)) return;
        var document = workspace.ResourceIdentities;
        if (document is null) return;
        var resource = document.Resources.SingleOrDefault(item => item.Id == selected.Id);
        var binding = document.ResourceBindings.SingleOrDefault(item =>
            item.Project == project && item.ResourceId == selected.Id);
        if (resource is null || binding is null) return;
        if (resource.Kind != KindForLocation(location.Id, target.Kind))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityKindMismatch",
                KindForLocation(location.Id, target.Kind), resource.Kind);
            return;
        }
        var probe = await _main.PreviewAssets.ProbeResourceAsync(peer.ClientId, project, binding.Binding);
        if (probe.Status != ResourceResolutionStatus.Resolved || probe.ActualKind != resource.Kind)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult",
                probe.Status, probe.ActualKind ?? "", probe.Detail ?? "");
            return;
        }
        if (_disposed || !ReferenceEquals(workspace, _main.ActiveGraphProvider.Graph) ||
            SelectedTargetNode?.Id != target.Id || SelectedLocation?.Id != location.Id ||
            SelectedResource?.Id != selected.Id || ProjectState?.Project != project ||
            !ReferenceEquals(document, workspace.ResourceIdentities))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var root = workspace.CreateSnapshot();
        if (!FindNode(root, target.Id, out var scope, out _)) return;
        if (!ResourceIdentityScope.Equal(capturedScope, scope))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var replaces = document.Uses.Any(use => use.NodeId == target.Id && use.Location == location.Id &&
            ResourceIdentityScope.Equal(use.InstancePath, scope));
        if (replaces && !ReplaceExistingUse)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityReplaceRequired");
            return;
        }
        workspace.EditResourceIdentityDocument(current =>
        {
            current ??= EmptyDocument();
            return current with
            {
                Uses = current.Uses.Where(use => use.NodeId != target.Id || use.Location != location.Id ||
                    !ResourceIdentityScope.Equal(use.InstancePath, scope)).Append(
                    new ResourceUse(target.Id, location.Id, selected.Id, scope)).ToArray()
            };
        }, LocalizationService.Shared.Get(replaces
            ? "Resources.IdentityHistoryReplace" : "Resources.IdentityHistoryAssign"));
        ReplaceExistingUse = false;
        Status = LocalizationService.Shared.Get("Resources.IdentityAssigned", selected.Name, location.Id);
    }

    private async Task ReadMaterialSchemaAsync()
    {
        if (SelectedPeer is not { } peer || ProjectState?.Project is not { } project ||
            SelectedResource is not { Kind: "material" } selected ||
            CurrentDocument is not { } document ||
            document.ResourceBindings.SingleOrDefault(binding =>
                binding.Project == project && binding.ResourceId == selected.Id) is not { } materialBinding)
            return;
        try
        {
            var schema = await _main.PreviewAssets.MaterialSchemaAsync(peer.ClientId, project, materialBinding.Binding);
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId ||
                SelectedResource?.Id != selected.Id || ProjectState?.Project != project ||
                !ReferenceEquals(document, CurrentDocument))
            {
                Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
                return;
            }
            EngineParameters.Clear();
            foreach (var item in schema) EngineParameters.Add(item);
            SelectedEngineParameter = EngineParameters.FirstOrDefault();
            Status = LocalizationService.Shared.Get("Resources.IdentitySchemaLoaded", schema.Count);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException or JsonException)
        { Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message); }
    }

    private async Task RegisterParameterAsync()
    {
        if (_observedGraph is not { } workspace || SelectedPeer is not { } peer ||
            ProjectState?.Project is not { } project || SelectedTargetNode is not { } target ||
            SelectedLocation is not { Id: var locationId } || !locationId.StartsWith("material:", StringComparison.Ordinal) ||
            SelectedResource is not { Kind: "material" } selected ||
            SelectedGraphParameter is not { } graphParameter || SelectedEngineParameter is not { } engineParameter)
            return;
        var initialGraph = workspace.CreateSnapshot();
        if (!FindNode(initialGraph, target.Id, out var capturedScope, out _)) return;
        var document = workspace.ResourceIdentities;
        if (document is null) return;
        var material = document.Resources.SingleOrDefault(item => item.Id == selected.Id);
        var materialBinding = document.ResourceBindings.SingleOrDefault(item =>
            item.Project == project && item.ResourceId == selected.Id);
        if (material is null || materialBinding is null) return;
        var capturedName = ParameterName.Trim();
        var capturedKind = ParameterKind;
        var capturedBindingName = ParameterBindingName.Trim();
        var capturedBindingFormat = BindingFormat;
        var capturedAssociationText = ParameterAssociation;
        var capturedIndexText = ParameterIndex;
        var capturedDefaultJson = ParameterDefaultJson;
        var capturedMinimumJson = ParameterMinimumJson;
        var capturedMaximumJson = ParameterMaximumJson;
        if (!int.TryParse(capturedAssociationText, out var association) || association is < 0 or > 2 ||
            !int.TryParse(capturedIndexText, out var index) || index is < -1 or > 65535 ||
            string.IsNullOrWhiteSpace(capturedName) || string.IsNullOrWhiteSpace(capturedBindingName))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityParameterInvalid");
            return;
        }
        JsonElement defaultValue;
        JsonElement? minimum = null;
        JsonElement? maximum = null;
        try
        {
            using var defaultDocument = JsonDocument.Parse(capturedDefaultJson);
            defaultValue = defaultDocument.RootElement.Clone();
            if (!string.IsNullOrWhiteSpace(capturedMinimumJson))
            {
                using var minimumDocument = JsonDocument.Parse(capturedMinimumJson);
                minimum = minimumDocument.RootElement.Clone();
            }
            if (!string.IsNullOrWhiteSpace(capturedMaximumJson))
            {
                using var maximumDocument = JsonDocument.Parse(capturedMaximumJson);
                maximum = maximumDocument.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityParameterInvalid");
            return;
        }
        var parameterBinding = new OpaqueBinding(capturedBindingFormat == "unreal.object-path"
            ? "unreal.material-parameter.v1" : "unity.shader-property.v1",
            JsonSerializer.Serialize(new { name = capturedBindingName, association, index }));
        ResourceProbeResult probe;
        try
        {
            probe = await _main.PreviewAssets.ProbeParameterAsync(peer.ClientId, project,
                materialBinding.Binding, parameterBinding);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException or JsonException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId || ProjectState?.Project != project) return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
            return;
        }
        if (probe.Status != ResourceResolutionStatus.Resolved || probe.ActualKind != capturedKind)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult",
                probe.Status, probe.ActualKind ?? "", probe.Detail ?? "");
            return;
        }
        if (_disposed || !ReferenceEquals(workspace, _main.ActiveGraphProvider.Graph) ||
            SelectedTargetNode?.Id != target.Id || SelectedLocation?.Id != locationId ||
            SelectedResource?.Id != selected.Id || SelectedGraphParameter?.Id != graphParameter.Id ||
            SelectedEngineParameter != engineParameter || ParameterKind != capturedKind ||
            ParameterName.Trim() != capturedName || ParameterBindingName.Trim() != capturedBindingName ||
            BindingFormat != capturedBindingFormat || ParameterAssociation != capturedAssociationText ||
            ParameterIndex != capturedIndexText || ParameterDefaultJson != capturedDefaultJson ||
            ParameterMinimumJson != capturedMinimumJson || ParameterMaximumJson != capturedMaximumJson ||
            ProjectState?.Project != project || !ReferenceEquals(document, workspace.ResourceIdentities))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var root = workspace.CreateSnapshot();
        if (!FindNode(root, target.Id, out var scope, out var node)) return;
        if (!ResourceIdentityScope.Equal(capturedScope, scope))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        if (!document.Uses.Any(use => use.NodeId == target.Id && use.Location == locationId &&
                use.ResourceId == selected.Id && ResourceIdentityScope.Equal(use.InstancePath, scope)))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityMaterialRequired");
            return;
        }
        var pin = RenderNodeSchema.Read(GraphNodeDefinition.All.Single(item => item.Kind == node.Kind), node.Parameters)
            .FirstOrDefault(item => item.Id == graphParameter.Id && item.Slot.ToString() == locationId["material:".Length..]);
        if (pin is null || pin.Kind != engineParameter.Kind || capturedKind != engineParameter.Kind)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityParameterPinMissing", graphParameter.Name);
            return;
        }
        var parameterId = Guid.NewGuid();
        var currentDocument = workspace.ResourceIdentities ?? EmptyDocument();
        var existingUse = currentDocument.ParameterUses.Any(use => use.NodeId == target.Id &&
            use.PinId == graphParameter.Id && ResourceIdentityScope.Equal(use.InstancePath, scope));
        if (existingUse && !ReplaceExistingUse)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityReplaceRequired");
            return;
        }
        workspace.EditResourceIdentityDocument(current =>
        {
            current ??= EmptyDocument();
            var resources = current.Resources.Select(resource => resource.Id == selected.Id
                ? resource with
                {
                    Parameters = (resource.Parameters ?? []).Append(new MaterialParameterDefinition(
                        parameterId, capturedKind, capturedName, defaultValue, minimum, maximum)).ToArray()
                } : resource).ToArray();
            return current with
            {
                Resources = resources,
                ParameterBindings = current.ParameterBindings.Append(new ProjectParameterBinding(
                    project, selected.Id, parameterId, capturedKind, parameterBinding)).ToArray(),
                ParameterUses = current.ParameterUses.Where(use => use.NodeId != target.Id || use.PinId != graphParameter.Id ||
                    !ResourceIdentityScope.Equal(use.InstancePath, scope)).Append(
                    new ParameterUse(target.Id, graphParameter.Id, selected.Id, parameterId, scope)).ToArray()
            };
        }, LocalizationService.Shared.Get("Resources.IdentityHistoryParameter"));
        ReplaceExistingUse = false;
        Status = LocalizationService.Shared.Get("Resources.IdentityParameterRegistered", capturedName);
    }

    private async Task DeliverUnityBindingsAsync()
    {
        if (SelectedPeer is not { } peer || ProjectState?.Project is not { Engine: "Unity" } project ||
            CurrentDocument is not { } document) return;
        var resources = document.Resources.Select(resource =>
        {
            var binding = document.ResourceBindings.SingleOrDefault(item =>
                item.Project == project && item.ResourceId == resource.Id);
            if (binding is null) return null;
            var parameters = (resource.Parameters ?? []).Select(parameter =>
            {
                var parameterBinding = document.ParameterBindings.SingleOrDefault(item =>
                    item.Project == project && item.ResourceId == resource.Id && item.ParameterId == parameter.Id);
                return parameterBinding is null ? null : new UnityRuntimeParameterBindingDelivery(
                    parameter.Id, parameter.Name, parameter.Kind, parameterBinding.Binding);
            }).Where(item => item is not null).Cast<UnityRuntimeParameterBindingDelivery>().ToArray();
            return new UnityRuntimeBindingDelivery(resource.Id, resource.Kind, binding.Binding, parameters);
        }).Where(item => item is not null).Cast<UnityRuntimeBindingDelivery>().ToArray();
        try
        {
            var path = await _main.PreviewAssets.DeliverUnityBindingsAsync(peer.ClientId, project, resources);
            Status = LocalizationService.Shared.Get("Resources.IdentityUnityDelivered", path ?? "Assets/LiberaFx/LiberaFxBindings.asset");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException)
        { Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message); }
    }

    private void RefreshBindingFormat()
    {
        BindingFormat = Engine switch
        {
            "Unity" => "unity.asset-guid-local-id.v1",
            "Unreal" => "unreal.object-path",
            _ => ""
        };
        if (BindingFormat == "unreal.object-path" && string.IsNullOrEmpty(BindingValue)) BindingValue = "/Game/";
    }

    private static ResourceIdentityDocument EmptyDocument() => new(1, [], [], [], [], []);

    private static string StringParameter(GraphNodeSnapshot node, string id) =>
        node.Parameters?.FirstOrDefault(parameter => parameter.Id == id)?.Value.GetString() ?? "";

    private static string KindForLocation(string location, GraphNodeKind? nodeKind)
    {
        if (location.StartsWith("material:", StringComparison.Ordinal)) return "material";
        if (location.StartsWith("texture:", StringComparison.Ordinal)) return "texture";
        return nodeKind?.Value is "SpriteDraw" or "SpriteAnimation" or "SpriteComposition" ? "texture" : "model";
    }

    private static IReadOnlyList<Guid> GetScope(GraphSnapshot graph, Guid nodeId) =>
        FindNode(graph, nodeId, out var scope, out _) ? scope : Array.Empty<Guid>();

    private static bool FindNode(GraphSnapshot graph, Guid nodeId, out IReadOnlyList<Guid> scope,
        out GraphNodeSnapshot node)
    {
        var found = false;
        IReadOnlyList<Guid> foundScope = Array.Empty<Guid>();
        GraphNodeSnapshot? foundNode = null;
        void Visit(GraphSnapshot current, IReadOnlyList<Guid> path)
        {
            foreach (var candidate in current.Nodes)
            {
                if (candidate.Id == nodeId)
                {
                    found = true;
                    foundScope = path.ToArray();
                    foundNode = candidate;
                    return;
                }
                if (candidate.ChildGraph is { } child)
                {
                    Visit(child, ResourceIdentityScope.Child(path, candidate.Id));
                    if (found) return;
                }
            }
        }
        Visit(graph, Array.Empty<Guid>());
        scope = foundScope;
        node = foundNode!;
        return found;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= OnMainChanged;
        _main.ActiveGraphProvider.PropertyChanged -= OnActiveGraphChanged;
        if (_observedGraph is not null)
        {
            _observedGraph.History.StateChanged -= OnGraphStateChanged;
            _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
        }
    }
}
