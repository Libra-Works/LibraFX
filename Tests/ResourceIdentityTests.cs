using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Compilation;

// Parent test runner must call Run(); this file does not start an editor or engine.
internal static class ResourceIdentityTests
{
    public static void Run()
    {
        var projectA = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var projectB = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var unity = new EngineProjectIdentity("Unity", Guid.NewGuid());
        var modelA = Guid.NewGuid();
        var modelB = Guid.NewGuid();
        var material = Guid.NewGuid();
        var parameter = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        const string sharedPath = "/Game/Models/Shared.Shared";
        const string oldPath = "/Game/Models/Old.Old";
        var asset = new EngineAsset(oldPath, "Old", "model", "Unreal", Materials:
        [
            new(0, "/Game/Materials/Old.Old", "Old", [
                new("OldName", "scalar", JsonSerializer.SerializeToElement(0.25))
            ])
        ]);
        var schema = RenderNodeSchema.Encode(asset);
        var pin = RenderNodeSchema.Describe(schema).Single().Id;
        var modelDef = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var sourceDef = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Float);
        var source = new GraphNodeSnapshot(sourceId, sourceDef.Kind, "Source", 0, 0,
            sourceDef.PortType, sourceDef.PortCategory);
        var node = new GraphNodeSnapshot(nodeId, modelDef.Kind, "Model", 100, 0,
            modelDef.PortType, modelDef.PortCategory, Parameters:
            [
                new("assetPath", "string", JsonSerializer.SerializeToElement(oldPath)),
                new("materialSchema", "string", JsonSerializer.SerializeToElement(schema)),
                new(pin, "float", JsonSerializer.SerializeToElement(0.85))
            ]);
        var graph = new GraphSnapshot([source, node], [new(sourceId, nodeId, pin)]);
        var document = new ResourceIdentityDocument(1,
        [
            new(modelA, "model", "Model A"), new(modelB, "model", "Model B"),
            new(material, "material", "Material", [
                new(parameter, "scalar", "Opacity", JsonSerializer.SerializeToElement(0.25),
                    JsonSerializer.SerializeToElement(0.0), JsonSerializer.SerializeToElement(1.0))
            ])
        ],
        [
            new(projectA, modelA, "model", new("unreal.object-path", sharedPath)),
            new(projectB, modelB, "model", new("unreal.object-path", sharedPath)),
            new(unity, modelA, "model", new("unity.addressable", "my-key")),
            new(projectA, material, "material", new("unreal.object-path", "/Game/Materials/New.New"))
        ],
        [
            new(projectA, material, parameter, "scalar",
                new("unreal.material-parameter.v1", """{"name":"Renamed","association":2,"index":-1}"""))
        ],
        [new(nodeId, "assetPath", modelA), new(nodeId, "material:0", material)],
        [new(nodeId, pin, material, parameter)]);
        using var store = new MemoryStream();
        ResourceIdentityStore.Write(store, document);
        store.Position = 0;
        document = ResourceIdentityStore.Read(store);
        var probe = new Probe();
        var resolverA = new ResourceIdentityResolver(document, projectA, probe);
        var resolverB = new ResourceIdentityResolver(document, projectB, probe);
        Require(resolverA.ResolveResource(modelA, "model").Status == ResourceResolutionStatus.Resolved, "A mapping");
        Require(resolverB.ResolveResource(modelA, "model").Status == ResourceResolutionStatus.Unregistered, "Same path is not a cross-project mapping");
        Require(resolverA.ResolveResource(modelB, "model").Status == ResourceResolutionStatus.Unregistered, "Same path is not identity");
        Require(resolverB.ResolveResource(modelB, "model").Status == ResourceResolutionStatus.Resolved, "B mapping");
        Require(new ResourceIdentityResolver(document, unity, probe).ResolveResource(modelA, "model").Status ==
            ResourceResolutionStatus.Resolved, "Unity opaque mapping");
        Require(resolverA.ResolveResource(modelA, "texture").Status == ResourceResolutionStatus.TypeMismatch, "Use kind");
        probe.Online = false;
        Require(resolverA.ResolveResource(modelA, "model").Status == ResourceResolutionStatus.Offline, "Offline");
        probe.Online = true;
        probe.Status = ResourceResolutionStatus.Missing;
        Require(resolverA.ResolveResource(modelA, "model").Status == ResourceResolutionStatus.Missing, "Missing");
        probe.Status = ResourceResolutionStatus.QueryFailure;
        Require(resolverA.ResolveResource(modelA, "model").Status == ResourceResolutionStatus.QueryFailure, "Query failure");
        probe.Status = ResourceResolutionStatus.Resolved;
        probe.Throw = true;
        Require(resolverA.ResolveResource(modelA, "model").Status == ResourceResolutionStatus.QueryFailure, "Adapter exception");
        probe.Throw = false;
        probe.ActualKind = "texture";
        Require(resolverA.ResolveResource(modelA, "model").Status == ResourceResolutionStatus.TypeMismatch, "Adapter kind");
        probe.ActualKind = null;

        var before = JsonSerializer.Serialize(graph);
        using var legacy = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph, 1));
        using var mapped = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph, 1, new ResourceIdentityRenderContext(resolverA)));
        var oldNode = legacy.RootElement.GetProperty("nodes")[0];
        var newNode = mapped.RootElement.GetProperty("nodes")[0];
        Require(oldNode.GetProperty("assetPath").GetString() == oldPath, "Legacy path");
        Require(newNode.GetProperty("assetPath").GetString() == sharedPath, "Resolved path");
        Require(newNode.GetProperty("materials")[0].GetProperty("path").GetString() == "/Game/Materials/New.New", "Material path");
        var input = newNode.GetProperty("inputs").EnumerateArray().Single(item => item.GetProperty("id").GetString() == pin);
        Require(input.GetProperty("sourceNodeId").GetGuid() == sourceId &&
            input.GetProperty("defaults")[0].GetDouble() == 0.85 &&
            input.GetProperty("materialParameter").GetProperty("name").GetString() == "Renamed",
            "Mapped parameter keeps pin, wire and value");
        Require(JsonSerializer.Serialize(graph) == before, "Resolution must not migrate or rewrite the graph");
        var embedded = new EmbeddedResourceIdentities(1,
            [new("assetPath", modelA), new("material:0", material)], [new(pin, material, parameter)]);
        var embeddedNode = node with
        {
            Parameters = [.. node.Parameters!,
                new(ResourceIdentityExport.EmbeddedParameterId, ResourceIdentityExport.EmbeddedParameterType,
                    JsonSerializer.SerializeToElement(embedded))]
        };
        var embeddedGraph = graph with { Nodes = [source, embeddedNode] };
        GraphValidator.ValidateDocument(embeddedGraph);
        var embeddedBefore = JsonSerializer.Serialize(embeddedGraph);
        var materialBinding = document.ResourceBindings.Single(item => item.Project == projectA && item.ResourceId == material).Binding;
        var parameterBinding = document.ParameterBindings.Single().Binding;
        var snapshot = new ResourceBindingSnapshot(1, projectA, true,
        [
            new(document.ResourceBindings.Single(item => item.Project == projectA && item.ResourceId == modelA).Binding,
                ResourceResolutionStatus.Resolved, "model"),
            new(materialBinding, ResourceResolutionStatus.Resolved, "material")
        ],
        [new(materialBinding, parameterBinding, ResourceResolutionStatus.Resolved, "scalar")]);
        var embeddedMap = document with { Uses = [], ParameterUses = [] };
        using var manifest = JsonDocument.Parse(ResourceIdentityExport.CreateManifest(embeddedGraph, embeddedMap,
            projectA, new ResourceIdentitySnapshotProbe(snapshot)));
        var manifestInput = manifest.RootElement.GetProperty("renderConfiguration").GetProperty("nodes")[0]
            .GetProperty("inputs").EnumerateArray().Single(item => item.GetProperty("id").GetString() == pin);
        Require(manifest.RootElement.GetProperty("parameterUses")[0].GetProperty("parameterId").GetGuid() == parameter &&
            manifestInput.GetProperty("sourceNodeId").GetGuid() == sourceId &&
            manifestInput.GetProperty("defaults")[0].GetDouble() == 0.85 &&
            manifestInput.GetProperty("materialParameter").GetProperty("name").GetString() == "Renamed",
            "Embedded identities must survive export without losing the legacy pin, wire or value.");
        Require(JsonSerializer.Serialize(embeddedGraph) == embeddedBefore, "Embedded export changed its input.");
        var moved = document with
        {
            ResourceBindings = document.ResourceBindings.Select(binding =>
                binding.Project == projectA && binding.ResourceId == modelA
                    ? binding with { Binding = new("unreal.object-path", "/Game/Moved/M.M") } : binding).ToArray()
        };
        var movedResult = new ResourceIdentityResolver(moved, projectA, probe).ResolveResource(modelA, "model");
        Require(movedResult.Binding?.Value == "/Game/Moved/M.M" && modelA == moved.Resources[0].Id, "Move preserves identity");
        var withoutParameter = document with { ParameterBindings = [] };
        Require(new ResourceIdentityResolver(withoutParameter, projectA, probe).ResolveParameter(material, parameter, "scalar").Status ==
            ResourceResolutionStatus.Unregistered, "Parameter requires explicit mapping");
        try
        {
            _ = RenderProgramBindings.CreateScene(graph, 1,
                new ResourceIdentityRenderContext(new ResourceIdentityResolver(withoutParameter, projectA, probe)));
            throw new Exception("Unregistered parameter was silently delivered.");
        }
        catch (InvalidDataException) { }
        try
        {
            _ = RenderProgramBindings.CreateScene(graph, 1, new ResourceIdentityRenderContext(resolverB));
            throw new Exception("Different project silently reused a binding.");
        }
        catch (InvalidDataException) { }
        var invalidRange = document with
        {
            Resources = document.Resources.Select(resource => resource.Id == material
                ? resource with { Parameters = [resource.Parameters![0] with { Maximum = JsonSerializer.SerializeToElement(0.1) }] }
                : resource).ToArray()
        };
        try { ResourceIdentityStore.Validate(invalidRange); throw new Exception("Invalid range accepted."); }
        catch (InvalidDataException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Probe : IResourceBindingProbe
    {
        public bool Online { get; set; } = true;
        public ResourceResolutionStatus Status { get; set; } = ResourceResolutionStatus.Resolved;
        public string? ActualKind { get; set; }
        public bool Throw { get; set; }
        public bool IsOnline(EngineProjectIdentity project) => Online;
        public ResourceProbeResult QueryResource(EngineProjectIdentity project, OpaqueBinding binding)
        {
            if (Throw) throw new InvalidOperationException("adapter error");
            return new(Status, ActualKind ?? (binding.Value.Contains("Materials", StringComparison.Ordinal) ? "material" : "model"));
        }
        public ResourceProbeResult QueryParameter(EngineProjectIdentity project, OpaqueBinding resourceBinding, OpaqueBinding parameterBinding) =>
            new(Status, ActualKind ?? "scalar");
    }
}
