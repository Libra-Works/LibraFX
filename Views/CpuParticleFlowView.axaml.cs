using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed record CpuParticleFlowStage(GraphNodeViewModel? Node, string Title,
    string Detail, string ButtonText, bool HasPredecessor, bool IsSelected)
{
    public bool CanActivate => Node is not null;
}

public partial class CpuParticleFlowView : UserControl
{
    private GraphWorkspaceViewModel? _graph;
    private bool _attached;

    public CpuParticleFlowView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Observe(true);
            RefreshStages();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Observe(false);
            _attached = false;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        Observe(false);
        _graph = DataContext as GraphWorkspaceViewModel;
        Observe(_attached);
        RefreshStages();
        base.OnDataContextChanged(e);
    }

    private void Observe(bool subscribe)
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        if (_graph is not null)
        {
            _graph.Nodes.CollectionChanged -= OnNodesChanged;
            _graph.PropertyChanged -= OnGraphChanged;
        }
        if (subscribe)
        {
            LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
            if (_graph is not null)
            {
                _graph.Nodes.CollectionChanged += OnNodesChanged;
                _graph.PropertyChanged += OnGraphChanged;
            }
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshStages();
    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshStages();

    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphWorkspaceViewModel.IsCpuParticleOverview) or
            nameof(GraphWorkspaceViewModel.SelectedNode))
        {
            RefreshStages();
        }
    }

    private void RefreshStages()
    {
        if (StageList is null)
        {
            return;
        }
        if (_graph is not { IsCpuParticleOverview: true } graph)
        {
            StageList.ItemsSource = Array.Empty<CpuParticleFlowStage>();
            return;
        }
        var text = LocalizationService.Shared;
        StageList.ItemsSource = new[]
        {
            Stage("CpuParticleSpawn", "Spawn", 1, "OpenStage"),
            Stage("CpuParticleUpdate", "Update", 2, "OpenStage"),
            Stage("CpuParticleSpriteDraw", "Draw", 3, "InspectDraw")
        };

        CpuParticleFlowStage Stage(string kind, string phase, int index, string action)
        {
            var node = graph.Nodes.FirstOrDefault(item => CpuParticleSchema.Name(item.Definition.Kind) == kind);
            return new(node, $"{index}. {text.Get("Cpu.Flow." + phase)}",
                text.Get("Cpu.Flow." + phase + "Detail"), text.Get("Cpu.Flow." + action),
                index > 1, node is not null && ReferenceEquals(node, graph.SelectedNode));
        }
    }

    private void OnStageClick(object? sender, RoutedEventArgs e)
    {
        if (_graph is not { } graph ||
            sender is not Button { Tag: CpuParticleFlowStage { Node: { } node } } ||
            !graph.Nodes.Contains(node))
        {
            return;
        }
        graph.ActivateEditingWorkspace();
        if (CpuParticleSchema.IsStage(node.Definition.Kind))
        {
            graph.EnterGraph(node);
        }
        else
        {
            graph.SelectNode(node);
        }
    }

    private void OnAddNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_graph is { IsCpuParticleOverview: true, IsReadOnly: false } graph)
        {
            graph.RunAuthoringAction(() => graph.CreateNode(GraphNodeKind.Comment, 80, 400));
        }
    }
}
