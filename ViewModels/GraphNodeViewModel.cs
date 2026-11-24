using Avalonia;
using Avalonia.Media;
using System.Globalization;
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
    private IReadOnlyList<SignalTimelineKey>? _signalKeys;
    private IReadOnlyList<NodeParameterSnapshot>? _parameters;
    private string _diagnosticText = string.Empty;

    public Guid Id { get; }

    internal GraphWorkspaceViewModel Workspace => _workspace;

    public GraphNodeDefinition Definition => _definition;
    internal GraphNodeDefinition BaseDefinition => _baseDefinition;
    public bool IsPolymorphic { get; }

    public FCurveSnapshot? Curve => _curve;
    public IReadOnlyList<SignalTimelineKey>? SignalKeys => _signalKeys;
    public IReadOnlyList<NodeParameterSnapshot>? Parameters => _parameters;
    public GraphSnapshot? ChildGraph { get; private set; }
    public IReadOnlyList<GroupInstanceValue>? GroupOverrides { get; private set; }
    public GroupAssetReference? GroupAsset { get; private set; }
    public bool IsGraphGroup => ChildGraph is not null;
    public bool IsSpawnableGroup => Definition.Kind == GraphNodeKind.SpawnableGroup;
    public bool IsSequence => SequenceSchema.IsSequence(Definition.Kind);
    public bool IsPreviewDebugDraw => Definition.Kind.Value is "DebugText" or "DebugModel";
    public string PreviewDebugDrawStatus => !IsPreviewDebugDraw ? string.Empty :
        LocalizationService.Shared.Get(Parameters?.FirstOrDefault(item => item.Id == "enabled")?.Value.GetBoolean() == false
            ? "DebugDraw.Disabled" : "DebugDraw.Active");
    public bool IsPreviewSignal => SignalTimeline.Supports(BaseDefinition.Kind);
    public bool CanSendPreviewSignal => _workspace.CanSendPreviewSignal(this);
    public string PreviewSignalSendHelp => LocalizationService.Shared.Get(CanSendPreviewSignal
        ? "Editor.SignalSend.Help" : "Editor.SignalSend.InstanceTargetRequired");
    public bool CanAddSequenceOutput => IsSequence && OutputPorts.Count < SequenceSchema.MaximumOutputs;
    public bool CanRemoveSequenceOutput => IsSequence && OutputPorts.Count > 1;
    private DynamicPinGroupDefinition? DynamicInputGroup => BaseDefinition.DynamicPins?.FirstOrDefault(group => !group.IsOutput);
    private DynamicPinGroupDefinition? DynamicOutputGroup => BaseDefinition.DynamicPins?.FirstOrDefault(group => group.IsOutput);
    public bool HasDynamicInputs => DynamicInputGroup is not null;
    public bool HasDynamicOutputs => IsSequence || DynamicOutputGroup is not null;
    public bool HasDynamicPins => HasDynamicInputs || HasDynamicOutputs;
    public bool CanAddDynamicInput => DynamicInputGroup is { } group && DynamicPinSchema.Count(group, Parameters) < group.MaxCount;
    public bool CanRemoveDynamicInput => DynamicInputGroup is { } group && DynamicPinSchema.Count(group, Parameters) > group.MinCount;
    public bool CanAddDynamicOutput => IsSequence ? CanAddSequenceOutput :
        DynamicOutputGroup is { } group && DynamicPinSchema.Count(group, Parameters) < group.MaxCount;
    public bool CanRemoveDynamicOutput => IsSequence ? CanRemoveSequenceOutput :
        DynamicOutputGroup is { } group && DynamicPinSchema.Count(group, Parameters) > group.MinCount;
    public bool IsLifecycleNode => IsSpawnableGroup ||
        Definition.Kind.Value is "LifeTime" or "Delay";
    public string LifecycleRoleLabel => !IsLifecycleNode ? string.Empty :
        LocalizationService.Shared.Get(IsSpawnableGroup ? "Editor.Lifecycle.OwnerRole" :
            Definition.Kind.Value == "LifeTime" ? "Editor.Lifecycle.InstanceLifeRole" : "Editor.Lifecycle.DelayRole");
    public string LifecycleSummary => !IsLifecycleNode ? string.Empty :
        IsSpawnableGroup ? SpawnableLifeTimeSummary() :
        Definition.Kind.Value == "LifeTime" ? ParameterSecondsSummary("LifeTime") : ParameterSecondsSummary("Delay");
    public string LifecycleSummaryHelp => !IsLifecycleNode ? string.Empty :
        LocalizationService.Shared.Get(IsSpawnableGroup ? "Editor.Lifecycle.OwnerHelp" :
            Definition.Kind.Value == "LifeTime" ? "Editor.Lifecycle.InstanceLifeHelp" : "Editor.Lifecycle.DelayHelp");
    public IBrush LifecycleAccentBrush => ThemeSettingsService.EditorBrush(
        IsSpawnableGroup || Definition.Kind.Value == "LifeTime" ? "GraphParameter" : "GraphSignal");
    public bool ShowGroupHint => IsGraphGroup && !IsSpawnableGroup && !HasCustomInputs && !HasCustomOutputs;
    public bool IsGpuParticle => GpuParticleEditorService.IsGpu(Definition.Kind);
    public bool IsProcedural => ProceduralResourceSchema.IsMetadata(Definition.Kind);
    public bool HasInlineWorkspace => IsGraphGroup || IsGpuParticle || IsProcedural;
    public bool IsComment => Definition.Kind == GraphNodeKind.Comment;
    public bool IsReroute => BaseDefinition.Kind.Value == "Reroute";
    public bool HasPorts => !IsComment && (!IsGraphGroup ||
        IsSpawnableGroup || Definition.InputPorts is { Count: > 0 } || Definition.OutputPorts is { Count: > 0 });
    public bool ShowPortType => HasPorts && !IsReroute && Definition.Kind != GraphNodeKind.GraphGroup;
    public string OriginalNodeName => LocalizationService.Shared.GetNodeLabel(BaseDefinition.DisplayNameKey, BaseDefinition.Name);
    public int InputColumnSpan => Definition.OutputPorts is { Count: 0 } ? 3 : 1;
    // These are authored sources. Keep old relay connections readable in saved projects,
    // but do not offer an input connector on new constant/event nodes.
    public bool HasLegacyInput => HasPorts && Definition.InputPorts is null &&
        BaseDefinition.Kind.Value is not ("Float" or "FloatX2" or "FloatX3" or "FloatX4" or "Int32" or "Bool" or "Signal");
    public bool HasCustomInputs => HasPorts && Definition.InputPorts is { Count: > 0 };
    public IReadOnlyList<GraphInputPortDefinition> InputPorts
    {
        get
        {
            var ports = Definition.InputPorts ?? [];
            if (IsPreviewDebugDraw) return ports.Select(port => port with { Name = LocalizationService.Shared.Get(
                port.Id == "matrix" ? "DebugDraw.Matrix" : "DebugDraw.Visible") }).ToArray();
            if (!ProceduralResourceSchema.IsMetadata(Definition.Kind)) return ports;
            var model = ProceduralResourceSchema.Name(Definition.Kind) == "ProceduralModel";
            var names = model ? ProceduralResourceSchema.ModifierNames : ProceduralResourceSchema.PixelNames;
            return ports.Select(port =>
            {
                var label = names.FirstOrDefault(name => port.Name.Contains(". " + name + " · ", StringComparison.Ordinal));
                return label is null ? port : port with { Name = port.Name.Replace(". " + label + " · ", ". " +
                    LocalizationService.Shared.Get("Procedural." + (model ? "Modifier." : "Pixel.") + label) + " · ", StringComparison.Ordinal) };
            }).ToArray();
        }
    }
    public bool HasLegacyOutput => HasPorts && Definition.OutputPorts is null;
    public bool HasCustomOutputs => HasPorts && Definition.OutputPorts is { Count: > 0 };
    public IReadOnlyList<GraphOutputPortDefinition> OutputPorts => BaseDefinition.Kind.Value is "EffectAge" or "InstanceAge"
        ? (Definition.OutputPorts ?? []).Select(port => port with { Name = LocalizationService.Shared.Get(
            port.Id == "real" ? "Editor.Time.Real" : "Editor.Time.Scaled") }).ToArray()
        : Definition.OutputPorts ?? [];
    public double OutputPortOffset(string? portId)
    {
        for (var index = 0; index < OutputPorts.Count; index++)
            if (OutputPorts[index].Id == portId) return 5 + index * 24;
        return 0;
    }
    internal const double MaxCommentSize = 1_000_000;
    public double CommentWidth => _previewWidth ?? CommentNumber("width", 360, 180, MaxCommentSize);
    public double CommentHeight => _previewHeight ?? CommentNumber("height", 200, 80, MaxCommentSize);
    internal const string InlineExpandedId = "editor.inline.expanded";
    internal const string InlineWidthId = "editor.inline.width";
    internal const string InlineHeightId = "editor.inline.height";
    internal const string NodeLockedId = "editor.locked";
    internal const string ParameterLockedPrefix = "editor.locked.";
    internal static string ParameterLockId(string parameterId) => ParameterLockedPrefix + parameterId;
    public bool IsLocked => Parameters?.FirstOrDefault(p =>
        p.Id == NodeLockedId && p.Type == "bool")?.Value.ValueKind == JsonValueKind.True;
    public bool IsParameterLocked(string parameterId) => Parameters?.FirstOrDefault(p =>
        p.Id == ParameterLockId(parameterId) && p.Type == "bool")?.Value.ValueKind == JsonValueKind.True;
    public bool IsInlineExpanded => HasInlineWorkspace && Parameters?.FirstOrDefault(p =>
        p.Id == InlineExpandedId && p.Type == "bool")?.Value.ValueKind == JsonValueKind.True;
    public double InlineWidth => _previewWidth ?? InlineNumber(InlineWidthId, 640, 420, 2400);
    public double InlineHeight => _previewHeight ?? InlineNumber(InlineHeightId, 380, 260, 1600);
    public double RenderedWidth => IsComment ? CommentWidth : IsReroute ? 86 : IsInlineExpanded ? InlineWidth : CalculatedNodeWidth;

    private double CalculatedNodeWidth
    {
        get
        {
            var maxInput = HasCustomInputs
                ? InputPorts.Select(p => p.Name.Length).DefaultIfEmpty(0).Max()
                : (HasLegacyInput ? 6 : 0);
            var maxOutput = HasCustomOutputs
                ? OutputPorts.Select(p => p.Name.Length).DefaultIfEmpty(0).Max()
                : (HasLegacyOutput ? 6 : 0);
            var left = maxInput > 0 ? maxInput * 7.2 + 26 : 0;
            var right = maxOutput > 0 ? maxOutput * 7.2 + 26 : 0;
            return Math.Clamp(Math.Ceiling(left + right + 36), NodeWidth, 520);
        }
    }

    private double InlineNumber(string id, double fallback, double min, double max)
    {
        var parameter = Parameters?.FirstOrDefault(p => p.Id == id && p.Type == "float");
        return parameter?.Value.ValueKind == JsonValueKind.Number &&
            parameter.Value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Clamp(number, min, max) : fallback;
    }

    internal void SetChildGraphValue(GraphSnapshot? graph)
    {
        if (ReferenceEquals(ChildGraph, graph)) return;
        ChildGraph = graph;
        RefreshEffectiveDefinition();
        OnPropertyChanged(nameof(ChildGraph));
        OnPropertyChanged(nameof(ShowGroupHint));
        OnPropertyChanged(nameof(HasPorts));
        RefreshLifecyclePresentation();
    }

    internal void PreviewInlineSize(double width, double height)
    {
        _previewWidth = Math.Clamp(width, 420, 2400);
        _previewHeight = Math.Clamp(height, 260, 1600);
        RefreshInlinePresentation();
    }

    internal void CancelInlineSize()
    {
        _previewWidth = null;
        _previewHeight = null;
        RefreshInlinePresentation();
    }

    private void RefreshInlinePresentation()
    {
        OnPropertyChanged(nameof(IsInlineExpanded));
        OnPropertyChanged(nameof(InlineWidth));
        OnPropertyChanged(nameof(InlineHeight));
        OnPropertyChanged(nameof(RenderedWidth));
    }
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
    public bool HasConstantEditor => BaseDefinition.Kind.Value is
        "Float" or "FloatX2" or "FloatX3" or "FloatX4" or "Int32" or "Bool";
    public bool HasExpandableParameters => HasParameters && !HasConstantEditor;
    public IReadOnlyList<NodeParameterEditorViewModel> InlineParameters { get; private set; }
    internal double RenderedHeight { get; set; } = 80;
    private Dictionary<string, Point> _inputAnchors = [];
    private Dictionary<string, Point> _outputAnchors = [];
    internal Point InputAnchor(string? id) => _inputAnchors.GetValueOrDefault(id ?? "",
        new Point(PortHorizontalOffset, PortCenterOffset + InputPortOffset(id)));
    internal Point OutputAnchor(string? id) => _outputAnchors.GetValueOrDefault(id ?? "",
        new Point(RenderedWidth - PortHorizontalOffset, PortCenterOffset + OutputPortOffset(id)));
    private double InputPortOffset(string? id)
    {
        for (var index = 0; index < InputPorts.Count; index++)
            if (InputPorts[index].Id == id) return 5 + index * 24;
        return 0;
    }

    internal void SetPortAnchors(Dictionary<string, Point> inputs, Dictionary<string, Point> outputs)
    {
        if (inputs.Count == _inputAnchors.Count && outputs.Count == _outputAnchors.Count &&
            inputs.All(pair => _inputAnchors.TryGetValue(pair.Key, out var point) && point == pair.Value) &&
            outputs.All(pair => _outputAnchors.TryGetValue(pair.Key, out var point) && point == pair.Value)) return;
        _inputAnchors = inputs;
        _outputAnchors = outputs;
        OnPropertyChanged("PortGeometry");
    }

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

    public string DisplayName
    {
        get
        {
            var text = LocalizationService.Shared;
            if (GroupAsset is { } reference &&
                _workspace.GroupAssets is { } catalog &&
                catalog.Metadata(reference.Id) is { } metadata &&
                catalog.Assets.FirstOrDefault(asset => asset.Id == reference.Id) is { } standard &&
                (Name == standard.Name || Name == text.GetEnglishOrDefault(metadata.NameKey, standard.Name)))
            {
                return text.GetNodeLabel(metadata.NameKey, Name);
            }
            if (IsPolymorphic && GraphPolymorphism.TryFamily(BaseDefinition.Kind, out var family))
            {
                var key = family.DisplayNameKey ?? BaseDefinition.DisplayNameKey;
                if (Name == BaseDefinition.Name || Name == text.GetEnglishOrDefault(key, BaseDefinition.Name))
                {
                    return text.GetNodeLabel(key, Name);
                }
            }
            return Name == BaseDefinition.Name ? text.GetNodeLabel(BaseDefinition.DisplayNameKey, Name) : Name;
        }
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
    public string? DiagnosticToolTip => string.IsNullOrEmpty(_diagnosticText) ? null : _diagnosticText;
    public string Description => LocalizationService.Shared.GetOrDefault(Definition.DescriptionKey, string.Empty);
    public string HeaderHelp => string.Join(Environment.NewLine, new[] { Description, DiagnosticText }
        .Where(text => !string.IsNullOrEmpty(text)));
    public string? HeaderHelpToolTip => string.IsNullOrEmpty(HeaderHelp) ? null : HeaderHelp;
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(OriginalNodeName));
        if (BaseDefinition.Kind.Value is "EffectAge" or "InstanceAge") OnPropertyChanged(nameof(OutputPorts));
        OnPropertyChanged(nameof(DisplayName));
        if (IsPreviewDebugDraw || ProceduralResourceSchema.IsMetadata(Definition.Kind)) OnPropertyChanged(nameof(InputPorts));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HeaderHelp));
        OnPropertyChanged(nameof(HeaderHelpToolTip));
        OnPropertyChanged(nameof(LifecycleRoleLabel));
        OnPropertyChanged(nameof(LifecycleSummary));
        OnPropertyChanged(nameof(LifecycleSummaryHelp));
        OnPropertyChanged(nameof(PreviewSignalSendHelp));
        OnPropertyChanged(nameof(PreviewDebugDrawStatus));
    }
    internal void RefreshLifecyclePresentation()
    {
        OnPropertyChanged(nameof(PreviewDebugDrawStatus));
        OnPropertyChanged(nameof(LifecycleSummary));
        OnPropertyChanged(nameof(LifecycleSummaryHelp));
    }
    public IBrush BorderBrush => HasDiagnostics ? Brushes.Red :
        ThemeSettingsService.EditorBrush(IsSelected ? "NodeSelectedBorder" : "NodeBorder");

    internal void SetDiagnostics(string text)
    {
        if (!SetProperty(ref _diagnosticText, text, nameof(DiagnosticText))) return;
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(DiagnosticToolTip));
        OnPropertyChanged(nameof(BorderBrush));
        OnPropertyChanged(nameof(HeaderHelp));
        OnPropertyChanged(nameof(HeaderHelpToolTip));
    }

    public string PortType => Definition.PortType;
    public string DisplayPortType => GraphNodeDefinition.DisplayPortType(PortType);

    public IBrush PortBrush =>
        ThemeSettingsService.EditorBrush(Definition.PortCategory == GraphPortCategory.Signal ? "GraphSignal" : "GraphParameter");

    public IRelayCommand SelectCommand { get; }

    public IRelayCommand SelectInputPortCommand { get; }

    public IRelayCommand SelectOutputPortCommand { get; }
    public IRelayCommand AddSequenceOutputCommand { get; }
    public IRelayCommand RemoveSequenceOutputCommand { get; }
    public IRelayCommand AddDynamicInputCommand { get; }
    public IRelayCommand RemoveDynamicInputCommand { get; }
    public IRelayCommand AddDynamicOutputCommand { get; }
    public IRelayCommand RemoveDynamicOutputCommand { get; }
    public IRelayCommand SendPreviewSignalCommand { get; }

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
        IReadOnlyList<GroupInstanceValue>? groupOverrides = null,
        IReadOnlyList<SignalTimelineKey>? signalKeys = null)
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
        _signalKeys = signalKeys;
        ChildGraph = definition.Kind == GraphNodeKind.GraphGroup || definition.Kind == GraphNodeKind.SpawnableGroup ||
            CpuParticleSchema.HasChild(definition.Kind)
            ? childGraph ?? (CpuParticleSchema.HasChild(definition.Kind) ? CpuParticleSchema.CreateChild(definition.Kind) :
                definition.Kind == GraphNodeKind.SpawnableGroup ? GraphDefaults.CreateEffect() : new GraphSnapshot([], [])) : null;
        _definition = EffectiveDefinition(definition, parameters, ChildGraph);
        GroupAsset = ChildGraph is not null ? groupAsset : null;
        GroupOverrides = ChildGraph is not null ? groupOverrides : null;
        _parameters = parameters;
        InlineParameters = VisibleParameters(_definition)
            .Select(parameter => new NodeParameterEditorViewModel(workspace, this, parameter)).ToArray();
        SelectCommand = new RelayCommand(() => _workspace.SelectNode(this));
        SelectInputPortCommand = new RelayCommand(() => _workspace.CompleteConnection(this));
        SelectOutputPortCommand = new RelayCommand(() => _workspace.BeginConnection(this));
        AddSequenceOutputCommand = new RelayCommand(() => _workspace.SetParameter(this,
            SequenceSchema.OutputCountParameter, JsonSerializer.SerializeToElement(OutputPorts.Count + 1)),
            () => CanAddSequenceOutput);
        RemoveSequenceOutputCommand = new RelayCommand(() => _workspace.SetParameter(this,
            SequenceSchema.OutputCountParameter, JsonSerializer.SerializeToElement(OutputPorts.Count - 1)),
            () => CanRemoveSequenceOutput);
        AddDynamicInputCommand = new RelayCommand(() =>
        {
            if (DynamicInputGroup is { } group)
                _workspace.SetParameter(this, group.ParameterId,
                    JsonSerializer.SerializeToElement(DynamicPinSchema.Count(group, Parameters) + 1));
        }, () => CanAddDynamicInput);
        RemoveDynamicInputCommand = new RelayCommand(() =>
        {
            if (DynamicInputGroup is { } group)
                _workspace.SetParameter(this, group.ParameterId,
                    JsonSerializer.SerializeToElement(DynamicPinSchema.Count(group, Parameters) - 1));
        }, () => CanRemoveDynamicInput);
        AddDynamicOutputCommand = new RelayCommand(() =>
        {
            if (IsSequence) AddSequenceOutputCommand.Execute(null);
            else if (DynamicOutputGroup is { } group)
                _workspace.SetParameter(this, group.ParameterId,
                    JsonSerializer.SerializeToElement(DynamicPinSchema.Count(group, Parameters) + 1));
        }, () => CanAddDynamicOutput);
        RemoveDynamicOutputCommand = new RelayCommand(() =>
        {
            if (IsSequence) RemoveSequenceOutputCommand.Execute(null);
            else if (DynamicOutputGroup is { } group)
                _workspace.SetParameter(this, group.ParameterId,
                    JsonSerializer.SerializeToElement(DynamicPinSchema.Count(group, Parameters) - 1));
        }, () => CanRemoveDynamicOutput);
        SendPreviewSignalCommand = new RelayCommand(() => _workspace.RequestPreviewSignal(this),
            () => CanSendPreviewSignal);
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
        {
            OnPropertyChanged(nameof(DisplayName));
            _workspace.RefreshTypeDiagnostics();
        }
    }

    internal void SetCurveValue(FCurveSnapshot value)
    {
        SetProperty(ref _curve, value, nameof(Curve));
    }

    internal void SetSignalKeysValue(IReadOnlyList<SignalTimelineKey>? value) =>
        SetProperty(ref _signalKeys, value, nameof(SignalKeys));

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
            OnPropertyChanged(nameof(IsLocked));
            RefreshInlinePresentation();
            RefreshLifecyclePresentation();
            _workspace.RefreshTypeDiagnostics();
        }
    }

    private (JsonElement? Render, JsonElement? GameIo, int SequenceOutputs, string DynamicPins) SchemaValue(
        IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        var schema = _baseDefinition.Parameters.FirstOrDefault(parameter => parameter.Id == "materialSchema");
        JsonElement? render = schema is null ? null :
            parameters?.FirstOrDefault(parameter => parameter.Id == schema.Id && parameter.Type == schema.Type)?.Value
            ?? schema.DefaultValue;
        return (render, GameIoSchema.ValueTypeValue(_baseDefinition, parameters),
            IsSequence ? SequenceSchema.OutputCount(parameters) : 0,
            DynamicPinSchema.SchemaKey(_baseDefinition, parameters));
    }

    private static bool SchemaValuesEqual(
        (JsonElement? Render, JsonElement? GameIo, int SequenceOutputs, string DynamicPins) left,
        (JsonElement? Render, JsonElement? GameIo, int SequenceOutputs, string DynamicPins) right) =>
        JsonValuesEqual(left.Render, right.Render) && JsonValuesEqual(left.GameIo, right.GameIo) &&
        left.SequenceOutputs == right.SequenceOutputs && left.DynamicPins == right.DynamicPins;

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
        OnPropertyChanged(nameof(OutputPorts));
        OnPropertyChanged(nameof(RenderedWidth));
        OnPropertyChanged(nameof(HasCustomOutputs));
        OnPropertyChanged(nameof(HasLegacyOutput));
        OnPropertyChanged(nameof(CanAddSequenceOutput));
        OnPropertyChanged(nameof(CanRemoveSequenceOutput));
        OnPropertyChanged(nameof(CanAddDynamicInput));
        OnPropertyChanged(nameof(CanRemoveDynamicInput));
        OnPropertyChanged(nameof(CanAddDynamicOutput));
        OnPropertyChanged(nameof(CanRemoveDynamicOutput));
        AddSequenceOutputCommand.NotifyCanExecuteChanged();
        RemoveSequenceOutputCommand.NotifyCanExecuteChanged();
        AddDynamicInputCommand.NotifyCanExecuteChanged();
        RemoveDynamicInputCommand.NotifyCanExecuteChanged();
        AddDynamicOutputCommand.NotifyCanExecuteChanged();
        RemoveDynamicOutputCommand.NotifyCanExecuteChanged();
    }

    internal void ApplyResolvedDefinition(GraphNodeDefinition definition)
    {
        if (definition == _definition) return;
        if (definition with { InputPorts = null, OutputPorts = null } == _definition with { InputPorts = null, OutputPorts = null } &&
            (definition.InputPorts is null && _definition.InputPorts is null ||
             definition.InputPorts is not null && _definition.InputPorts is not null && definition.InputPorts.SequenceEqual(_definition.InputPorts)) &&
            (definition.OutputPorts is null && _definition.OutputPorts is null ||
             definition.OutputPorts is not null && _definition.OutputPorts is not null && definition.OutputPorts.SequenceEqual(_definition.OutputPorts))) return;
        _definition = definition;
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(PortType));
        OnPropertyChanged(nameof(DisplayPortType));
        OnPropertyChanged(nameof(InputPorts));
        OnPropertyChanged(nameof(OutputPorts));
        OnPropertyChanged(nameof(RenderedWidth));
        OnPropertyChanged(nameof(HasCustomInputs));
        OnPropertyChanged(nameof(HasCustomOutputs));
        OnPropertyChanged(nameof(HasLegacyInput));
        OnPropertyChanged(nameof(HasLegacyOutput));
        RefreshLocalizedText();
    }

    internal static GraphNodeDefinition EffectiveDefinition(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters, GraphSnapshot? childGraph) =>
        GroupBoundarySchema.Effective(SpawnableGroupSchema.Effective(
            DynamicPinSchema.Effective(SequenceSchema.Effective(GameIoSchema.Effective(
                RenderNodeSchema.Effective(definition, parameters), parameters), parameters), parameters),
            childGraph), childGraph);

    private IReadOnlyList<NodeParameterDefinition> VisibleParameters(GraphNodeDefinition definition) =>
        definition.Parameters.Where(parameter =>
            !parameter.Hidden &&
            !IsGpuParticle &&
            (!IsSequence || parameter.Id != SequenceSchema.OutputCountParameter) &&
            (!IsComment || parameter.Id != "image") &&
            (!RenderNodeSchema.IsRenderNode(definition) ||
                 parameter.Id is not ("materialSchema" or "spriteData" or "compositionData") &&
                 !parameter.Id.StartsWith("material_", StringComparison.Ordinal))).ToArray();

    internal void SetGroupAssetValue(GroupAssetReference? value)
    {
        GroupAsset = value;
        OnPropertyChanged(nameof(GroupAsset));
        OnPropertyChanged(nameof(DisplayName));
    }

    internal void SetGroupOverridesValue(IReadOnlyList<GroupInstanceValue>? value)
    {
        GroupOverrides = value;
        OnPropertyChanged(nameof(GroupOverrides));
        RefreshLifecyclePresentation();
    }

    internal void PreviewCommentSize(double width, double height)
    {
        SetProperty(ref _previewWidth, Math.Clamp(width, 180, MaxCommentSize), nameof(CommentWidth));
        SetProperty(ref _previewHeight, Math.Clamp(height, 80, MaxCommentSize), nameof(CommentHeight));
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

    private string ParameterSecondsSummary(string label)
    {
        if (IsSecondsInputDriven(null, Id)) return SecondsState(label, "Editor.Lifecycle.InputDependent");
        return TryGetSeconds(Definition, Parameters, out var seconds)
            ? SecondsSummary(label, SecondsText(seconds))
            : SecondsState(label, "Editor.Lifecycle.Invalid");
    }

    private string SpawnableLifeTimeSummary()
    {
        if (ChildGraph is not { } child)
            return SecondsState("LifeTime", "Editor.Lifecycle.Invalid");
        var lifetimeNodes = child.Nodes.Where(node => node.Kind.Value == "LifeTime").ToArray();
        if (lifetimeNodes.Length == 0)
            return SecondsState("LifeTime", "Editor.Lifecycle.Unbounded");
        if (lifetimeNodes.Length != 1)
            return SecondsState("LifeTime", "Editor.Lifecycle.Invalid");

        var lifetime = lifetimeNodes[0];
        if (IsSecondsInputDriven(child, lifetime.Id))
            return SecondsState("LifeTime", "Editor.Lifecycle.InputDependent");

        var definition = GraphNodeDefinition.All.FirstOrDefault(item => item.Kind == lifetime.Kind);
        if (definition is null || !TryGetSeconds(definition, lifetime.Parameters, out var seconds, out var value))
            return SecondsState("LifeTime", "Editor.Lifecycle.Invalid");

        var exposed = child.ExposedItems?.FirstOrDefault(item => item.Editor == "parameter" &&
            item.Targets.Any(target => target.NodeId == lifetime.Id && target.ParameterId == "seconds"));
        if (exposed is not null && GroupOverrides?.FirstOrDefault(item => item.ItemId == exposed.Id) is { } instanceValue)
        {
            var componentIndex = exposed.Targets.ToList().FindIndex(target =>
                target.NodeId == lifetime.Id && target.ParameterId == "seconds");
            if (componentIndex < 0 || componentIndex >= instanceValue.Components.Count ||
                instanceValue.Components[componentIndex].Value is not { } overrideValue ||
                !TryReadSeconds(overrideValue, out seconds))
                return SecondsState("LifeTime", "Editor.Lifecycle.Invalid");
            value = overrideValue;
            return SecondsSummary("LifeTime",
                LocalizationService.Shared.Get("Editor.Lifecycle.InstanceOverride", FormatSeconds(value)));
        }

        return SecondsSummary("LifeTime", FormatSeconds(value));
    }

    private bool IsSecondsInputDriven(GraphSnapshot? childGraph, Guid nodeId)
    {
        return childGraph is not null
            ? childGraph.Connections.Any(connection => connection.TargetNodeId == nodeId &&
                (connection.TargetPortId is null or "seconds"))
            : _workspace.Connections.Any(connection => connection.Target.Id == nodeId &&
                (connection.TargetPortId is null or "seconds"));
    }

    private static bool TryGetSeconds(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters, out double seconds) =>
        TryGetSeconds(definition, parameters, out seconds, out _);

    private static bool TryGetSeconds(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters, out double seconds, out JsonElement value)
    {
        var parameter = definition.Parameters.FirstOrDefault(item => item.Id == "seconds" && item.Type == "float");
        value = parameter is null ? default :
            parameters?.FirstOrDefault(item => item.Id == parameter.Id && item.Type == parameter.Type)?.Value
            ?? parameter.DefaultValue;
        if (parameter is null || !TryReadSeconds(value, out seconds))
        {
            seconds = 0;
            return false;
        }
        return true;
    }

    private static bool TryReadSeconds(JsonElement value, out double seconds)
    {
        seconds = 0;
        return NodeParameter.IsValidValue("float", value) &&
            (seconds = value.GetDouble()) >= 0;
    }

    private static string FormatSeconds(JsonElement value) =>
        LocalizationService.Shared.Get("Editor.Lifecycle.Seconds",
            value.GetDouble().ToString("G", CultureInfo.CurrentCulture));

    private static string SecondsText(double seconds) =>
        LocalizationService.Shared.Get("Editor.Lifecycle.Seconds",
            seconds.ToString("G", CultureInfo.CurrentCulture));

    private static string SecondsSummary(string label, string value) =>
        LocalizationService.Shared.Get(label == "Delay"
            ? "Editor.Lifecycle.DelaySummary" : "Editor.Lifecycle.LifeTimeSummary", value);

    private static string SecondsState(string label, string key) =>
        SecondsSummary(label, LocalizationService.Shared.Get(key));

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
