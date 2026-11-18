using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

// Parent runner explicitly wires this suite; no engine or editor is started.
internal static class ResourceIdentityExportTests
{
    public static void Run()
    {
        var target = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var other = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var id = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var binding = new OpaqueBinding("unreal.object-path", "/Game/New/New.New");
        var definition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var embedded = new EmbeddedResourceIdentities(1, [new("assetPath", id)], []);
        var node = new GraphNodeSnapshot(nodeId, definition.Kind, "Model", 0, 0,
            definition.PortType, definition.PortCategory, Parameters:
            [
                new("assetPath", "string", JsonSerializer.SerializeToElement("/Game/Old/Old.Old")),
                new(ResourceIdentityExport.EmbeddedParameterId, ResourceIdentityExport.EmbeddedParameterType,
                    JsonSerializer.SerializeToElement(embedded))
            ]);
        var floatDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Float);
        var source = new GraphNodeSnapshot(Guid.NewGuid(), floatDefinition.Kind, "Value", 0, 0,
            floatDefinition.PortType, floatDefinition.PortCategory);
        var graph = new GraphSnapshot([node, source], []);
        GraphValidator.ValidateDocument(graph);
        var project = new LiberaFxProjectDocument
        {
            FormatVersion = 1,
            Nodes = [new(nodeId, node.Name, node.Kind.Value, node.X, node.Y, Parameters: node.Parameters),
                new(source.Id, source.Name, source.Kind.Value, source.X, source.Y)],
            Connections = []
        };
        using var serialized = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(project));
        var loaded = ProjectDocumentReader.LoadAsync(serialized).GetAwaiter().GetResult().Graph;
        Require(GraphSnapshot.ContentEquals(graph, loaded), "Opaque identity metadata did not survive project loading.");
        var original = JsonSerializer.Serialize(loaded);
        var map = new ResourceIdentityDocument(1, [new(id, "model", "Asset")],
            [new(target, id, "model", binding)], [], [], []);
        var snapshot = new ResourceBindingSnapshot(1, target, true,
            [new(binding, ResourceResolutionStatus.Resolved, "model")], []);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        using var snapshotFile = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(snapshot, jsonOptions));
        var probe = ResourceIdentitySnapshotProbe.Read(snapshotFile);
        var bytes = ResourceIdentityExport.CreateManifest(loaded, map, target, probe);
        using var manifest = JsonDocument.Parse(bytes);
        var root = manifest.RootElement;
        Require(root.GetProperty("project").GetProperty("projectId").GetGuid() == target.ProjectId, "Project scope");
        Require(root.GetProperty("uses")[0].GetProperty("resourceId").GetGuid() == id, "Original UUID");
        Require(root.GetProperty("uses")[0].GetProperty("binding").GetProperty("value").GetString() == binding.Value, "Explicit binding");
        Require(root.GetProperty("renderConfiguration").GetProperty("nodes")[0].GetProperty("assetPath").GetString() == binding.Value, "Compiled render delivery");
        Require(root.GetProperty("bytecode").GetBytesFromBase64().Length > 0, "Compiled bytecode");
        Require(root.GetProperty("evidence").GetString() == "caller-supplied-snapshot-not-live", "Snapshot provenance");
        Require(original == JsonSerializer.Serialize(loaded), "Export mutated the original graph.");
        Reject(() => ResourceIdentityExport.CreateManifest(loaded, map, other, probe), "Different project");
        Reject(() => ResourceIdentityExport.CreateManifest(loaded, map, new("Unity", target.ProjectId), probe), "Unsupported engine");
        Reject(() => ResourceIdentityExport.CreateManifest(loaded, map, target,
            new ResourceIdentitySnapshotProbe(snapshot with { Available = false })), "Offline");
        Reject(() => ResourceIdentityExport.CreateManifest(loaded, map, target,
            new ResourceIdentitySnapshotProbe(snapshot with { Resources = [] })), "Unknown is query failure, not missing");
        Reject(() => ResourceIdentityExport.CreateManifest(loaded, map, target,
            new ResourceIdentitySnapshotProbe(snapshot with { Resources = [new(binding, ResourceResolutionStatus.Missing)] })), "Explicit missing");
        Reject(() => ResourceIdentityExport.CreateManifest(loaded, map, target,
            new ResourceIdentitySnapshotProbe(snapshot with { Resources = [new(binding, ResourceResolutionStatus.Resolved, "texture")] })), "Wrong kind");
        Reject(() => ResourceIdentityExport.CreateManifest(loaded, map with
        {
            Uses = [new(nodeId, "assetPath", Guid.NewGuid())]
        }, target, probe), "Conflicting metadata");
        Reject(() => ResourceIdentityExport.CreateManifest(new GraphSnapshot([node with
        {
            Parameters = [new("assetPath", "string", JsonSerializer.SerializeToElement("/Game/Old/Old.Old"))]
        }], []), map, target, probe), "Legacy path cannot silently become portable");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
