using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

internal static class PreviewResourceBindings
{
    public static bool HasMappings(GraphSnapshot graph) =>
        graph.ResourceIdentities is { } document && (document.Uses.Count > 0 || document.ParameterUses.Count > 0) ||
        ResourceIdentityExport.HasEmbeddedMetadata(graph);

    public static async Task<ResourceIdentityRenderContext> ResolveAsync(GraphSnapshot graph,
        PreviewAssetStore assets, Guid peer, CancellationToken cancellation)
    {
        var document = ResourceIdentityExport.MergeEmbedded(graph, graph.ResourceIdentities ??
            new ResourceIdentityDocument(1, [], [], [], [], []));
        if (!assets.Supports(peer, "resourceIdentity"))
            throw new InvalidDataException("This preview peer cannot resolve project-scoped resource bindings.");
        var nodes = ResourceIdentityExport.ScopedNodes(graph);
        foreach (var binding in document.Uses.Select(use => (use.InstancePath, use.NodeId))
                     .Concat(document.ParameterUses.Select(use => (use.InstancePath, use.NodeId))))
        {
            if (!nodes.TryGetValue((ResourceIdentityScope.Key(binding.InstancePath), binding.NodeId), out var node) ||
                !RenderProgramBindings.IsRenderNode(node.Kind))
                throw new InvalidDataException($"Live resource mapping refers to a missing or unsupported render node {binding.NodeId}.");
        }
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = requestTimeout.Token;
        try
        {
            var state = await assets.EngineProjectAsync(peer, cancellation: token);
            if (state.ProjectState != "Ready" || state.Project is not { } project)
                throw new InvalidDataException("Preview project identity is not ready: " + state.ProjectState);
            var resources = new Dictionary<OpaqueBinding, ResourceProbeResult>();
            var parameters = new Dictionary<(OpaqueBinding, OpaqueBinding), ResourceProbeResult>();
            var resourceBindings = document.ResourceBindings.Where(item => item.Project == project)
                .ToDictionary(item => item.ResourceId);
            var parameterBindings = document.ParameterBindings.Where(item => item.Project == project)
                .ToDictionary(item => (item.ResourceId, item.ParameterId));
            foreach (var id in document.Uses.Select(use => use.ResourceId)
                         .Concat(document.ParameterUses.Select(use => use.ResourceId)).Distinct())
            {
                if (resourceBindings.TryGetValue(id, out var item) && !resources.ContainsKey(item.Binding))
                    resources.Add(item.Binding, await assets.ProbeResourceAsync(peer, project, item.Binding, token));
            }
            foreach (var id in document.ParameterUses.Select(use => (use.ResourceId, use.ParameterId)).Distinct())
            {
                if (resourceBindings.TryGetValue(id.ResourceId, out var material) &&
                    resources.TryGetValue(material.Binding, out var result) &&
                    result is { Status: ResourceResolutionStatus.Resolved, ActualKind: "material" } &&
                    parameterBindings.TryGetValue(id, out var entry) &&
                    !parameters.ContainsKey((material.Binding, entry.Binding)))
                    parameters.Add((material.Binding, entry.Binding),
                        await assets.ProbeParameterAsync(peer, project, material.Binding, entry.Binding, token));
            }
            var resolver = new ResourceIdentityResolver(document, project, new LiveProbe(project, resources, parameters));
            foreach (var use in document.Uses)
                RequireResolved(resolver.ResolveResource(use.ResourceId,
                    document.Resources.Single(item => item.Id == use.ResourceId).Kind), use.Location);
            foreach (var use in document.ParameterUses)
                RequireResolved(resolver.ResolveParameter(use.ResourceId, use.ParameterId,
                    document.Resources.Single(item => item.Id == use.ResourceId).Parameters!
                        .Single(item => item.Id == use.ParameterId).Kind), use.PinId);
            return new ResourceIdentityRenderContext(resolver, graph: graph);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidDataException("Live resource binding resolution timed out.");
        }
    }

    private static void RequireResolved(ResourceResolution result, string location)
    {
        if (result.Status != ResourceResolutionStatus.Resolved)
            throw new InvalidDataException($"Live resource binding '{location}': {result.Status}. {result.Detail}");
    }

    private sealed class LiveProbe(EngineProjectIdentity project,
        Dictionary<OpaqueBinding, ResourceProbeResult> resources,
        Dictionary<(OpaqueBinding, OpaqueBinding), ResourceProbeResult> parameters) : IResourceBindingProbe
    {
        public bool IsOnline(EngineProjectIdentity selected) => project == selected;

        public ResourceProbeResult QueryResource(EngineProjectIdentity selected, OpaqueBinding binding) =>
            IsOnline(selected) && resources.TryGetValue(binding, out var result)
                ? result : new(ResourceResolutionStatus.QueryFailure);

        public ResourceProbeResult QueryParameter(EngineProjectIdentity selected, OpaqueBinding material, OpaqueBinding binding) =>
            IsOnline(selected) && parameters.TryGetValue((material, binding), out var result)
                ? result : new(ResourceResolutionStatus.QueryFailure);
    }
}
