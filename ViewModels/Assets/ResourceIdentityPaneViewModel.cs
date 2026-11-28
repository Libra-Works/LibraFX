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
public sealed partial class ResourceIdentityPaneViewModel : ObservableObject, IDisposable
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
            if (definition.Parameters.Any(p => p.Id == "assetPath"))
                Locations.Add(new("assetPath", "assetPath", StringParameter(node, "assetPath")));
            foreach (var material in RenderNodeSchema.ReadMaterials(definition, node.Parameters).OrderBy(slot => slot.Slot))
                Locations.Add(new($"material:{material.Slot}", $"material:{material.Slot}", material.Path));
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

}
