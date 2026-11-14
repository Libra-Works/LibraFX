using Avalonia.Media;
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
    private bool _isSelected;
    private FCurveSnapshot? _curve;

    public Guid Id { get; }

    internal GraphWorkspaceViewModel Workspace => _workspace;

    public GraphNodeDefinition Definition { get; }

    public FCurveSnapshot? Curve => _curve;
    public GraphSnapshot? ChildGraph { get; }
    public bool IsGraphGroup => ChildGraph is not null;
    public bool HasPorts => !IsGraphGroup;

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
        GraphSnapshot? childGraph = null)
    {
        _workspace = workspace;
        Definition = definition;
        Id = id ?? Guid.NewGuid();
        _name = name;
        _x = x;
        _y = y;
        _curve = definition.Kind == GraphNodeKind.FloatFCurve ? curve ?? FCurve.CreateDefault() : null;
        ChildGraph = definition.Kind == GraphNodeKind.GraphGroup ? childGraph ?? new GraphSnapshot([], []) : null;
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
