using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LibraGpu.Core;

namespace LiberaFx.Services;

public enum ResourceTargetKind { RenderAsset, MaterialTexture, GpuTexture }
public sealed record ResourceAssignmentTarget(Guid NodeId, ResourceTargetKind Kind, string Id, string Label)
{
    public override string ToString() => Label;
}

public static class ResourceAssignment
{
    public static ResourceAssignmentTarget? TargetForParameter(GraphNodeViewModel node, string parameterId)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterId);
        if (!RenderNodeSchema.IsRenderNode(node.Definition)) return null;
        if (parameterId == "assetPath")
            return new(node.Id, ResourceTargetKind.RenderAsset, parameterId,
                LocalizationService.Shared.Get("Resources.RenderTarget", node.Name));
        var parameter = RenderNodeSchema.Read(node.Definition, node.Parameters)
            .FirstOrDefault(candidate => candidate.Id == parameterId && candidate.Kind == "texture");
        return parameter is null ? null : new(node.Id, ResourceTargetKind.MaterialTexture, parameter.Id,
            LocalizationService.Shared.Get("Resources.MaterialTarget",
                parameter.Slot, parameter.Name, parameter.Association, parameter.Index));
    }

    public static string? AssetClassForParameter(GraphNodeViewModel node, string parameterId)
    {
        if (TargetForParameter(node, parameterId) is not { } target) return null;
        return target.Kind == ResourceTargetKind.RenderAsset
            ? node.Definition.TypeName == "ModelDraw" ? "model" : "texture"
            : "texture";
    }

    public static IReadOnlyList<ResourceAssignmentTarget> Targets(GraphNodeViewModel node, EngineAsset asset)
    {
        var targets = new List<ResourceAssignmentTarget>();
        if (node.IsGpuParticle && asset.AssetClass == "texture")
            targets.AddRange(GpuParticleEditorService.ReadState(node).Program.Resources
                .Where(resource => resource.Kind == ResourceKind.Texture2D)
                .Select(resource => new ResourceAssignmentTarget(node.Id, ResourceTargetKind.GpuTexture,
                    resource.Id, LocalizationService.Shared.Get("Resources.GpuTarget", resource.Id))));
        if (!RenderNodeSchema.IsRenderNode(node.Definition)) return targets;
        var expected = node.Definition.TypeName == "ModelDraw" ? "model" : "texture";
        if (asset.AssetClass == expected)
            targets.Add(new(node.Id, ResourceTargetKind.RenderAsset, "assetPath",
                LocalizationService.Shared.Get("Resources.RenderTarget", node.Name)));
        if (asset.AssetClass == "texture")
            targets.AddRange(RenderNodeSchema.Read(node.Definition, node.Parameters)
                .Where(parameter => parameter.Kind == "texture")
                .Select(parameter => new ResourceAssignmentTarget(node.Id, ResourceTargetKind.MaterialTexture,
                    parameter.Id, LocalizationService.Shared.Get("Resources.MaterialTarget",
                        parameter.Slot, parameter.Name, parameter.Association, parameter.Index))));
        return targets;
    }

    public static void Assign(GraphWorkspaceViewModel graph, GraphNodeViewModel node, EngineAsset asset,
        ResourceAssignmentTarget target)
    {
        if (!graph.Nodes.Contains(node) || !Targets(node, asset).Any(current =>
            current.NodeId == target.NodeId && current.Kind == target.Kind && current.Id == target.Id))
            throw new ArgumentException("The selected resource target is no longer available.");
        PreviewAssetStore.ValidateAssetPath(asset.Path);
        switch (target.Kind)
        {
            case ResourceTargetKind.RenderAsset:
                RenderNodeAuthoring.AssignAsset(graph, node, asset);
                break;
            case ResourceTargetKind.MaterialTexture:
                graph.SetParameter(node, target.Id, System.Text.Json.JsonSerializer.SerializeToElement(asset.Path));
                break;
            case ResourceTargetKind.GpuTexture:
                GpuParticleEditorService.AssignTextureResource(graph, node, asset, target.Id);
                break;
        }
    }
}
