using LiberaFx.Models.Graph;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public bool IsCpuParticleOverview
    {
        get
        {
            var graph = _document._rootSnapshot;
            GraphNodeSnapshot? owner = null;
            foreach (var id in _graphPath)
            {
                owner = graph.Nodes.FirstOrDefault(node => node.Id == id);
                if (owner?.ChildGraph is not { } child)
                {
                    return false;
                }
                graph = child;
            }
            return owner is not null && CpuParticleSchema.IsContainer(owner.Kind);
        }
    }
}
