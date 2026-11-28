using System.Text.Json;
using LibraGpu.Core;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Services.Sprite;

public static class ParticleSpriteAuthoring
{
    public static bool Supports(GraphNodeViewModel? node) => node is not null &&
        (CpuParticleSchema.IsContainer(node.Definition.Kind) || CpuParticleSchema.Name(node.Definition.Kind)=="CpuParticleSpriteDraw" ||
         GpuParticleEditorService.IsGpu(node.Definition.Kind));

    public static (SpriteAnimationData? Animation, SpriteCompositionData? Composition, string Path) Read(GraphNodeViewModel node)
    {
        if (GpuParticleEditorService.IsGpu(node.Definition.Kind))
        {
            var state=GpuParticleEditorService.ReadState(node);
            var render=state.Program.Flow.FirstOrDefault(flow => flow.Kind==FlowKind.RenderBillboard);
            var settings=render is null ? null : state.RendererSettings.GetValueOrDefault(render.Id);
            return (settings?.SpriteAnimation,settings?.SpriteComposition,
                render?.Resource is { } resource ? state.ResourceAssetPaths.GetValueOrDefault(resource) ?? "" : "");
        }
        var parameters=CpuParticleSchema.IsContainer(node.Definition.Kind)
            ? node.ChildGraph?.Nodes.Single(n => CpuParticleSchema.Name(n.Kind)=="CpuParticleSpriteDraw").Parameters : node.Parameters;
        JsonElement Value(string id) => parameters?.FirstOrDefault(p => p.Id==id)?.Value ?? default;
        return (SpriteAnimationData.Deserialize(Value("spriteData")),SpriteCompositionData.Deserialize(Value("compositionData")),
            Value("assetPath").ValueKind==JsonValueKind.String ? Value("assetPath").GetString()! : "builtin:white");
    }

    public static GraphNodeViewModel Apply(GraphWorkspaceViewModel graph,GraphNodeViewModel target,EngineAsset asset,
        SpriteAnimationData? animation,SpriteCompositionData? composition)
    {
        if (!graph.Nodes.Contains(target) || !Supports(target)) throw new InvalidOperationException("Select a CPU or GPU particle node in the active graph.");
        if (!string.Equals(asset.AssetClass,"texture",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select a texture.");
        EngineAssetPath.Validate(asset.Path);
        _=ParticleSpriteAnimation.Compile(animation,composition) ?? throw new InvalidDataException("Create a sprite animation first.");
        if (GpuParticleEditorService.IsGpu(target.Definition.Kind))
        {
            var state=GpuParticleEditorService.ReadState(target);
            if (state.Fluid2D is not null) throw new InvalidOperationException("Select GPU Particle, not Fluid2D.");
            var renderers=new Dictionary<string,GpuRendererSettings>(state.RendererSettings);
            var paths=new Dictionary<string,string>(state.ResourceAssetPaths);
            var generated=state.GeneratedResources is null ? null : new Dictionary<string,GeneratedGpuResourceReference>(state.GeneratedResources);
            var flows=state.Program.Flow.Where(f => f.Kind==FlowKind.RenderBillboard).ToArray();
            if (flows.Length==0) throw new InvalidDataException("The GPU program has no billboard renderer.");
            foreach (var flow in flows)
            {
                renderers[flow.Id]=(renderers.GetValueOrDefault(flow.Id) ?? new GpuRendererSettings()) with
                { SpriteAnimation=animation,SpriteComposition=composition,SpriteAnimationEnabled=true };
                paths[flow.Resource!]=asset.Path;
                generated?.Remove(flow.Resource!);
            }
            var value=JsonSerializer.SerializeToElement(state with { RendererSettings=renderers,ResourceAssetPaths=paths,GeneratedResources=generated },GpuParticleEditorService.Json);
            var updated=(target.Parameters ?? []).Where(p => p.Id!=GpuParticleEditorService.ParameterId)
                .Append(new NodeParameterSnapshot(GpuParticleEditorService.ParameterId,"gpuState",value)).ToArray();
            graph.History.Execute(new SetNodeParametersCommand(target,target.Parameters,updated,"particle sprite"));
            return target;
        }
        IReadOnlyList<NodeParameterSnapshot> Update(IReadOnlyList<NodeParameterSnapshot>? old) =>
            (old ?? []).Where(p => p.Id is not ("assetPath" or "textureSource" or "spriteData" or "compositionData" or "flipbookEnabled" or "spriteAnimationEnabled"))
            .Concat(new NodeParameterSnapshot[] {
                new("assetPath","string",JsonSerializer.SerializeToElement(asset.Path)),
                new("textureSource","enum:ab42721e-d2c3-43e9-a96d-c80000000001",JsonSerializer.SerializeToElement(0)),
                new("spriteData","spriteData",JsonSerializer.SerializeToElement(animation)),
                new("compositionData","string",JsonSerializer.SerializeToElement(composition?.Serialize() ?? "")),
                new("spriteAnimationEnabled","bool",JsonSerializer.SerializeToElement(true)),
                new("flipbookEnabled","bool",JsonSerializer.SerializeToElement(false)) }).ToArray();
        // Keep the authored sequence when toggled off; applying it enables it again.
        if (!CpuParticleSchema.IsContainer(target.Definition.Kind))
        {
            graph.History.Execute(new SetNodeParametersCommand(target,target.Parameters,Update(target.Parameters),"particle sprite"));
            return target;
        }
        var before=graph.CreateSnapshot();
        var after=before with { Nodes=before.Nodes.Select(n => n.Id!=target.Id ? n : n with
        { ChildGraph=n.ChildGraph! with { Nodes=n.ChildGraph!.Nodes.Select(draw => CpuParticleSchema.Name(draw.Kind)!="CpuParticleSpriteDraw"
            ? draw : draw with { Parameters=Update(draw.Parameters) }).ToArray() } }).ToArray() };
        graph.History.Execute(new ReplaceGraphSnapshotCommand(graph,before,after,"Edit particle sprite"));
        return graph.Nodes.Single(n => n.Id==target.Id);
    }
}
