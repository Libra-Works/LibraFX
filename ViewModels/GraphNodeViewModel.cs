using Avalonia.Media;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class GraphNodeViewModel : ObservableObject
{
    public const double NodeWidth = 240;
    public const double PortCenterOffset = 53;
    public const double PortHorizontalOffset = 19;

    private readonly GraphWorkspaceViewModel _workspace;
    private readonly GraphNodeDefinition _baseDefinition;
    private GraphNodeDefinition _definition;
    private string _name;
    private double _x;
    private double _y;
    private double? _previewX;
    private double? _previewY;
    private double? _previewWidth;
    private double? _previewHeight;
    private bool _isSelected;
    private bool _isDetailsExpanded;
    private FCurveSnapshot? _curve;
    private IReadOnlyList<NodeParameterSnapshot>? _parameters;
    private string _diagnosticText = string.Empty;

    public Guid Id { get; }

    internal GraphWorkspaceViewModel Workspace => _workspace;

    public GraphNodeDefinition Definition => _definition;
    internal GraphNodeDefinition BaseDefinition => _baseDefinition;
    public bool IsPolymorphic { get; }

    public FCurveSnapshot? Curve => _curve;
    public IReadOnlyList<NodeParameterSnapshot>? Parameters => _parameters;
    public GraphSnapshot? ChildGraph { get; }
    public IReadOnlyList<GroupInstanceValue>? GroupOverrides { get; private set; }
    public GroupAssetReference? GroupAsset { get; private set; }
    public bool IsGraphGroup => ChildGraph is not null;
    public bool IsSpawnableGroup => Definition.Kind == GraphNodeKind.SpawnableGroup;
    public bool ShowGroupHint => IsGraphGroup && !IsSpawnableGroup && !HasCustomInputs && !HasCustomOutputs;
    public bool IsGpuParticle => GpuParticleEditorService.IsGpu(Definition.Kind);
    public bool IsComment => Definition.Kind == GraphNodeKind.Comment;
    public bool HasPorts => !IsGpuParticle && !IsComment && (!IsGraphGroup ||
        IsSpawnableGroup || Definition.InputPorts is { Count: > 0 } || Definition.OutputPorts is { Count: > 0 });
    public bool ShowPortType => Definition.Kind != GraphNodeKind.GraphGroup;
    public bool HasLegacyInput => HasPorts && Definition.InputPorts is null;
    public bool HasCustomInputs => HasPorts && Definition.InputPorts is { Count: > 0 };
    public IReadOnlyList<GraphInputPortDefinition> InputPorts => Definition.InputPorts ?? [];
    public bool HasLegacyOutput => HasPorts && Definition.OutputPorts is null;
    public bool HasCustomOutputs => HasPorts && Definition.OutputPorts is { Count: > 0 };
    public IReadOnlyList<GraphOutputPortDefinition> OutputPorts => Definition.OutputPorts ?? [];
    public double OutputPortOffset(string? portId)
    {
        for (var index = 0; index < OutputPorts.Count; index++)
            if (OutputPorts[index].Id == portId) return 5 + index * 24;
        return 0;
    }
    public double CommentWidth => _previewWidth ?? CommentNumber("width", 360, 180, 4000);
    public double CommentHeight => _previewHeight ?? CommentNumber("height", 200, 80, 4000);
    public double RenderedWidth => IsComment ? CommentWidth : NodeWidth;
    public string CommentText
    {
        get => IsComment && Definition.Parameters.FirstOrDefault(item => item.Id == "text") is { } definition
            ? GetParameterValue(definition).GetString() ?? "" : "";
        set { if (IsComment) _workspace.SetParameter(this, "text", JsonSerializer.SerializeToElement(value ?? "")); }
    }
    public CommentFormat CommentStyle => IsComment && Definition.Parameters.FirstOrDefault(item => item.Id == "format") is { } format
        && GetParameterValue(format).TryGetInt32(out var value) && Enum.IsDefined((CommentFormat)value)
        ? (CommentFormat)value : CommentFormat.Text;
    public bool HasFormattedComment => IsComment && CommentStyle != CommentFormat.Text;
    public bool HasParameters => InlineParameters.Count > 0;
    public IReadOnlyList<NodeParameterEditorViewModel> InlineParameters { get; private set; }
    internal double RenderedHeight { get; set; } = 80;

    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set => SetProperty(ref _isDetailsExpanded, value);
    }

    public string Name
    {
        get => _name;
        set => _workspace.RenameNode(this, value);
    }

    public double X => _x;

    public double Y => _y;

    public double DisplayX => _previewX ?? _x;

    public double DisplayY => _previewY ?? _y;

    public bool IsSelected
    {
        get => _isSelected;
        private set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(BorderBrush));
            }
        }
    }

    public bool HasDiagnostics => _diagnosticText.Length != 0;
    public string DiagnosticText => _diagnosticText;
    public string Description => LocalizationService.Shared.GetOrDefault(Definition.DescriptionKey, string.Empty);
    public string HeaderHelp => string.Join(Environment.NewLine, new[] { Description, DiagnosticText }
        .Where(text => !string.IsNullOrEmpty(text)));
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HeaderHelp));
    }
    public IBrush BorderBrush => HasDiagnostics ? Brushes.Red :
        ThemeSettingsService.EditorBrush(IsSelected ? "NodeSelectedBorder" : "NodeBorder");

    internal void SetDiagnostics(string text)
    {
        if (!SetProperty(ref _diagnosticText, text, nameof(DiagnosticText))) return;
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(HeaderHelp));
    }

    public string PortType => Definition.PortType;
    public string DisplayPortType => GraphNodeDefinition.DisplayPortType(PortType);

    public IBrush PortBrush =>
        ThemeSettingsService.EditorBrush(Definition.PortCategory == GraphPortCategory.Signal ? "GraphSignal" : "GraphParameter");

    public IRelayCommand SelectCommand { get; }

    public IRelayCommand SelectInputPortCommand { get; }

    public IRelayCommand SelectOutputPortCommand { get; }

    public GraphNodeViewModel(
        GraphWorkspaceViewModel workspace,
        GraphNodeDefinition definition,
        string name,
        double x,
        double y,
        Guid? id = null,
        FCurveSnapshot? curve = null,
        GraphSnapshot? childGraph = null,
        IReadOnlyList<NodeParameterSnapshot>? parameters = null,
        GroupAssetReference? groupAsset = null,
        bool isPolymorphic = false,
        IReadOnlyList<GroupInstanceValue>? groupOverrides = null)
    {
        _workspace = workspace;
        _baseDefinition = definition;
        IsPolymorphic = isPolymorphic;
        _definition = EffectiveDefinition(definition, parameters, childGraph);
        Id = id ?? Guid.NewGuid();
        _name = name;
        _x = x;
        _y = y;
        _curve = definition.Kind == GraphNodeKind.FloatFCurve ? curve ?? FCurve.CreateDefault() : null;
        ChildGraph = definition.Kind == GraphNodeKind.GraphGroup || definition.Kind == GraphNodeKind.SpawnableGroup
            ? childGraph ?? (definition.Kind == GraphNodeKind.SpawnableGroup
                ? GraphDefaults.CreateEffect() : new GraphSnapshot([], [])) : null;
        GroupAsset = ChildGraph is not null ? groupAsset : null;
        GroupOverrides = ChildGraph is not null ? groupOverrides : null;
        _parameters = parameters;
        InlineParameters = VisibleParameters(_definition)
            .Select(parameter => new NodeParameterEditorViewModel(workspace, this, parameter)).ToArray();
        SelectCommand = new RelayCommand(() => _workspace.SelectNode(this));
        SelectInputPortCommand = new RelayCommand(() => _workspace.CompleteConnection(this));
        SelectOutputPortCommand = new RelayCommand(() => _workspace.BeginConnection(this));
    }

    public void PreviewMoveTo(double x, double y)
    {
        SetProperty(ref _previewX, x, nameof(DisplayX));
        SetProperty(ref _previewY, y, nameof(DisplayY));
    }

    public void MoveTo(double x, double y)
    {
        try
        {
            _workspace.MoveNode(this, x, y);
        }
        finally
        {
            ClearPreviewPosition();
        }
    }

    public void CancelPreviewMove()
    {
        ClearPreviewPosition();
    }

    internal void SetNameValue(string value)
    {
        if (SetProperty(ref _name, value, nameof(Name)))
            _workspace.RefreshTypeDiagnostics();
    }

    internal void SetCurveValue(FCurveSnapshot value)
    {
        SetProperty(ref _curve, value, nameof(Curve));
    }

    public JsonElement GetParameterValue(NodeParameterDefinition definition) =>
        Parameters?.FirstOrDefault(item => item.Id == definition.Id && item.Type == definition.Type)?.Value
        ?? definition.DefaultValue;

    internal void SetParametersValue(IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        var oldSchema = SchemaValue(_parameters);
        if (SetProperty(ref _parameters, parameters, nameof(Parameters)))
        {
            if (!SchemaValuesEqual(oldSchema, SchemaValue(_parameters)))
                RefreshEffectiveDefinition();
            foreach (var parameter in InlineParameters) parameter.Refresh();
            OnPropertyChanged(nameof(CommentText));
            OnPropertyChanged(nameof(CommentStyle));
            OnPropertyChanged(nameof(HasFormattedComment));
            OnPropertyChanged(nameof(CommentWidth));
            OnPropertyChanged(nameof(CommentHeight));
            OnPropertyChanged(nameof(RenderedWidth));
            _workspace.RefreshTypeDiagnostics();
        }
    }

    private (JsonElement? Render, JsonElement? GameIo) SchemaValue(IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        var schema = _baseDefinition.Parameters.FirstOrDefault(parameter => parameter.Id == "materialSchema");
        JsonElement? render = schema is null ? null :
            parameters?.FirstOrDefault(parameter => parameter.Id == schema.Id && parameter.Type == schema.Type)?.Value
            ?? schema.DefaultValue;
        return (render, GameIoSchema.ValueTypeValue(_baseDefinition, parameters));
    }

    private static bool SchemaValuesEqual(
        (JsonElement? Render, JsonElement? GameIo) left,
        (JsonElement? Render, JsonElement? GameIo) right) =>
        JsonValuesEqual(left.Render, right.Render) && JsonValuesEqual(left.GameIo, right.GameIo);

    private static bool JsonValuesEqual(JsonElement? left, JsonElement? right) =>
        left is null && right is null ||
        left is { } a && right is { } b && JsonElement.DeepEquals(a, b);

    private void RefreshEffectiveDefinition()
    {
        var effective = EffectiveDefinition(_baseDefinition, _parameters, ChildGraph);
        if (ReferenceEquals(effective, _definition)) return;
        if (!SetProperty(ref _definition, effective, nameof(Definition))) return;
        InlineParameters = VisibleParameters(effective)
            .Select(parameter => new NodeParameterEditorViewModel(_workspace, this, parameter)).ToArray();
        OnPropertyChanged(nameof(InlineParameters));
        OnPropertyChanged(nameof(InputPorts));
        OnPropertyChanged(nameof(HasCustomInputs));
        OnPropertyChanged(nameof(HasLegacyInput));
        OnPropertyChanged(nameof(HasParameters));
    }

    internal void ApplyResolvedDefinition(GraphNodeDefinition definition)
    {
        if (definition == _definition) return;
        _definition = definition;
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(PortType));
        OnPropertyChanged(nameof(DisplayPortType));
        OnPropertyChanged(nameof(InputPorts));
        OnPropertyChanged(nameof(OutputPorts));
        OnPropertyChanged(nameof(HasCustomInputs));
        OnPropertyChanged(nameof(HasCustomOutputs));
        OnPropertyChanged(nameof(HasLegacyInput));
        OnPropertyChanged(nameof(HasLegacyOutput));
        RefreshLocalizedText();
    }

    private static GraphNodeDefinition EffectiveDefinition(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters, GraphSnapshot? childGraph) =>
        GroupBoundarySchema.Effective(SpawnableGroupSchema.Effective(
            GameIoSchema.Effective(RenderNodeSchema.Effective(definition, parameters), parameters), childGraph), childGraph);

    private IReadOnlyList<NodeParameterDefinition> VisibleParameters(GraphNodeDefinition definition) =>
        definition.Parameters.Where(parameter =>
            !IsGpuParticle &&
            (!IsComment || parameter.Id != "image") &&
            (!RenderNodeSchema.IsRenderNode(definition) ||
             parameter.Id != "materialSchema" && !parameter.Id.StartsWith("material_", StringComparison.Ordinal))).ToArray();

    internal void SetGroupAssetValue(GroupAssetReference? value)
    {
        GroupAsset = value;
        OnPropertyChanged(nameof(GroupAsset));
    }

    internal void SetGroupOverridesValue(IReadOnlyList<GroupInstanceValue>? value)
    {
        GroupOverrides = value;
        OnPropertyChanged(nameof(GroupOverrides));
    }

    internal void PreviewCommentSize(double width, double height)
    {
        SetProperty(ref _previewWidth, Math.Clamp(width, 180, 4000), nameof(CommentWidth));
        SetProperty(ref _previewHeight, Math.Clamp(height, 80, 4000), nameof(CommentHeight));
        OnPropertyChanged(nameof(RenderedWidth));
    }

    internal void CancelCommentSize()
    {
        _previewWidth = null;
        _previewHeight = null;
        OnPropertyChanged(nameof(CommentWidth));
        OnPropertyChanged(nameof(CommentHeight));
        OnPropertyChanged(nameof(RenderedWidth));
    }

    private double CommentNumber(string id, double fallback, double min, double max)
    {
        if (!IsComment) return fallback;
        var definition = Definition.Parameters.FirstOrDefault(item => item.Id == id);
        return definition is null ? fallback : Math.Clamp(GetParameterValue(definition).GetDouble(), min, max);
    }

    internal void SetPosition(double x, double y)
    {
        if (SetProperty(ref _x, x, nameof(X)))
        {
            OnPropertyChanged(nameof(DisplayX));
        }

        if (SetProperty(ref _y, y, nameof(Y)))
        {
            OnPropertyChanged(nameof(DisplayY));
        }
    }

    internal void SetSelected(bool value)
    {
        IsSelected = value;
    }

    private void ClearPreviewPosition()
    {
        if (_previewX is not null)
        {
            _previewX = null;
            OnPropertyChanged(nameof(DisplayX));
        }

        if (_previewY is not null)
        {
            _previewY = null;
            OnPropertyChanged(nameof(DisplayY));
        }
    }
}
