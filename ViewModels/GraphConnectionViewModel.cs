using Avalonia;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class GraphConnectionViewModel : ObservableObject
{
    public GraphNodeViewModel Source { get; }

    public GraphNodeViewModel Target { get; }
    public string? TargetPortId { get; }
    public string? SourcePortId { get; }
    public int SignalOrder { get; private set; }
    public int SignalOrderCount { get; private set; }
    public string? SignalOrderHelp => SignalOrder == 0 ? null :
        LocalizationService.Shared.Get("Editor.SignalExecutionOrderHelp");

    public double StartX => Source.DisplayX + Source.OutputAnchor(SourcePortId).X;

    public double StartY => Source.DisplayY + Source.OutputAnchor(SourcePortId).Y;

    public double EndX => Target.DisplayX + Target.InputAnchor(TargetPortId).X;

    public double EndY => Target.DisplayY + Target.InputAnchor(TargetPortId).Y;

    public Point StartPoint => new(StartX, StartY);

    public Point EndPoint => new(EndX, EndY);

    public string Description
    {
        get
        {
            var sourcePort = Source.OutputPorts.FirstOrDefault(port => port.Id == SourcePortId);
            var targetPort = Target.InputPorts.FirstOrDefault(port => port.Id == TargetPortId);
            var sourceLabel = SourcePortId is null ? "" : $" [{sourcePort?.Name ?? SourcePortId}]";
            var targetLabel = TargetPortId is null ? "" : $" [{targetPort?.Name ?? TargetPortId}]";
            var type = sourcePort?.PortType ?? Source.Definition.GetOutputPortType(SourcePortId) ?? Source.PortType;
            var description = $"{Source.Name}{sourceLabel} → {Target.Name}{targetLabel} ({GraphNodeDefinition.DisplayPortType(type)})";
            return SignalOrder == 0 ? description :
                LocalizationService.Shared.Get("Editor.SignalExecutionOrder", SignalOrder, SignalOrderCount) + " — " + description;
        }
    }

    internal void SetSignalOrder(int order, int count)
    {
        if (SignalOrder == order && SignalOrderCount == count) return;
        SignalOrder = order;
        SignalOrderCount = count;
        OnPropertyChanged(nameof(SignalOrder));
        OnPropertyChanged(nameof(SignalOrderCount));
        OnPropertyChanged(nameof(SignalOrderHelp));
        OnPropertyChanged(nameof(Description));
    }

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SignalOrderHelp));
    }

    public GraphConnectionViewModel(GraphNodeViewModel source, GraphNodeViewModel target,
        string? targetPortId = null, string? sourcePortId = null)
    {
        Source = source;
        Target = target;
        TargetPortId = targetPortId;
        SourcePortId = sourcePortId;
        Source.PropertyChanged += OnNodePropertyChanged;
        Target.PropertyChanged += OnNodePropertyChanged;
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphNodeViewModel.Name) or nameof(GraphNodeViewModel.InputPorts) or
            nameof(GraphNodeViewModel.OutputPorts) or nameof(GraphNodeViewModel.PortType))
        {
            OnPropertyChanged(nameof(Description));
        }

        if (e.PropertyName is nameof(GraphNodeViewModel.DisplayX) or nameof(GraphNodeViewModel.DisplayY) or
            nameof(GraphNodeViewModel.RenderedWidth) or nameof(GraphNodeViewModel.InputPorts) or
            nameof(GraphNodeViewModel.OutputPorts) or "PortGeometry")
        {
            OnPropertyChanged(nameof(StartX));
            OnPropertyChanged(nameof(StartY));
            OnPropertyChanged(nameof(EndX));
            OnPropertyChanged(nameof(EndY));
            OnPropertyChanged(nameof(StartPoint));
            OnPropertyChanged(nameof(EndPoint));
        }
    }

}
