using Avalonia;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiberaFx.ViewModels;

public sealed class GraphConnectionViewModel : ObservableObject
{
    public GraphNodeViewModel Source { get; }

    public GraphNodeViewModel Target { get; }
    public string? TargetPortId { get; }

    public double StartX => Source.DisplayX + GraphNodeViewModel.NodeWidth - GraphNodeViewModel.PortHorizontalOffset;

    public double StartY => Source.DisplayY + GraphNodeViewModel.PortCenterOffset;

    public double EndX => Target.DisplayX + GraphNodeViewModel.PortHorizontalOffset;

    public double EndY => Target.DisplayY + GraphNodeViewModel.PortCenterOffset +
        InputOffset();

    public Point StartPoint => new(StartX, StartY);

    public Point EndPoint => new(EndX, EndY);

    public string Description => $"{Source.Name} → {Target.Name}{(TargetPortId is null ? "" : $" [{TargetPortId}]")} ({Source.DisplayPortType})";

    public GraphConnectionViewModel(GraphNodeViewModel source, GraphNodeViewModel target, string? targetPortId = null)
    {
        Source = source;
        Target = target;
        TargetPortId = targetPortId;
        Source.PropertyChanged += OnNodePropertyChanged;
        Target.PropertyChanged += OnNodePropertyChanged;
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Name))
        {
            OnPropertyChanged(nameof(Description));
        }

        if (e.PropertyName is nameof(GraphNodeViewModel.DisplayX) or nameof(GraphNodeViewModel.DisplayY))
        {
            OnPropertyChanged(nameof(StartX));
            OnPropertyChanged(nameof(StartY));
            OnPropertyChanged(nameof(EndX));
            OnPropertyChanged(nameof(EndY));
            OnPropertyChanged(nameof(StartPoint));
            OnPropertyChanged(nameof(EndPoint));
        }
    }

    private double InputOffset()
    {
        for (var index = 0; index < Target.InputPorts.Count; index++)
            if (Target.InputPorts[index].Id == TargetPortId) return 5 + index * 24;
        return 0;
    }
}
