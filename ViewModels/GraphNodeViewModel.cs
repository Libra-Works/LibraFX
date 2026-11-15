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

    public Guid Id { get; }

    internal GraphWorkspaceViewModel Workspace => _workspace;

    public GraphNodeDefinition Definition { get; }

    public FCurveSnapshot? Curve => _curve;
    public IReadOnlyList<NodeParameterSnapshot>? Parameters => _parameters;
    public GraphSnapshot? ChildGraph { get; }
    public GroupAssetReference? GroupAsset { get; private set; }
    public bool IsGraphGroup => ChildGraph is not null;
    public bool IsComment => Definition.Kind == GraphNodeKind.Comment;
    public bool HasPorts => !IsGraphGroup && !IsComment;
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
    public IReadOnlyList<NodeParameterEditorViewModel> InlineParameters { get; }
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

    public IBrush BorderBrush => ThemeSettingsService.EditorBrush(IsSelected ? "NodeSelectedBorder" : "NodeBorder");

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
        GroupAssetReference? groupAsset = null)
    {
        _workspace = workspace;
        Definition = definition;
        Id = id ?? Guid.NewGuid();
        _name = name;
        _x = x;
        _y = y;
        _curve = definition.Kind == GraphNodeKind.FloatFCurve ? curve ?? FCurve.CreateDefault() : null;
        ChildGraph = definition.Kind == GraphNodeKind.GraphGroup ? childGraph ?? new GraphSnapshot([], []) : null;
        GroupAsset = definition.Kind == GraphNodeKind.GraphGroup ? groupAsset : null;
        _parameters = parameters;
        InlineParameters = definition.Parameters.Where(parameter => !IsComment || parameter.Id != "image")
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
        SetProperty(ref _name, value, nameof(Name));
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
        if (SetProperty(ref _parameters, parameters, nameof(Parameters)))
        {
            foreach (var parameter in InlineParameters) parameter.Refresh();
            OnPropertyChanged(nameof(CommentText));
            OnPropertyChanged(nameof(CommentStyle));
            OnPropertyChanged(nameof(HasFormattedComment));
            OnPropertyChanged(nameof(CommentWidth));
            OnPropertyChanged(nameof(CommentHeight));
            OnPropertyChanged(nameof(RenderedWidth));
        }
    }

    internal void SetGroupAssetValue(GroupAssetReference? value)
    {
        GroupAsset = value;
        OnPropertyChanged(nameof(GroupAsset));
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
