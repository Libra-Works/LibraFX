using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class MaterialAuthoringTests
{
    public static void Run()
    {
        VerifyAssignmentAndHistory();
        VerifyLegacyAndMalformedSchemas();
        VerifyIdentityGuard();
        VerifyIdentityGuard(inline: true);
        VerifyIdentityGuard(texture: true);
        VerifyMappedParameterlessMaterial(nested: false);
        VerifyMappedParameterlessMaterial(nested: true);
    }

    private static EngineAsset Material(string name, params EngineMaterialParameter[] parameters) =>
        new($"/Game/Materials/{name}.{name}", name, "material", "Unreal",
            Materials: [new(0, $"/Game/Materials/{name}.{name}", name, parameters)]);

    private static EngineMaterialParameter Vector(string name) =>
        new(name, "vector", JsonSerializer.SerializeToElement(new[] { 1d, 1, 1, 1 }));

    private static EngineMaterialParameter Scalar(string name) =>
        new(name, "scalar", JsonSerializer.SerializeToElement(0.5));

    private static void VerifyAssignmentAndHistory()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var model = RenderNodeAuthoring.CreateModel(graph,
            new("/Engine/BasicShapes/Cube.Cube", "Cube", "model", "Unreal", Materials:
                [new(0, "/Game/Materials/Empty.Empty", "Empty", []),
                 new(1, "/Game/Materials/Second.Second", "Second", [Scalar("Other")])]));
        var originalPath = model.GetParameterValue(model.Definition.Parameters.Single(parameter =>
            parameter.Id == "assetPath")).GetString();
        Require(RenderNodeSchema.ReadMaterials(model.Definition, model.Parameters).Count == 2,
            "Slots without exposed parameters must remain selectable.");
        var material = Material("First", Vector("Color"), Scalar("Opacity"));
        var target = ResourceAssignment.TargetForParameter(model, RenderNodeSchema.MaterialSlotId(0));
        Require(target is { Kind: ResourceTargetKind.RenderMaterial } &&
            ResourceAssignment.AssetClassForParameter(model, target.Id) == "material",
            "The material property must open a material-filtered resource selection.");
        ResourceAssignment.Assign(graph, model, material, target!);
        var color = model.InputPorts.Single(port => port.Name == "Color").Id;
        var opacity = model.InputPorts.Single(port => port.Name == "Opacity").Id;
        var colorSource = graph.CreateNode(GraphNodeKind.FloatX4);
        var opacitySource = graph.CreateNode(GraphNodeKind.Float);
        Require(graph.TryConnectNodes(colorSource, model, out _, targetPortId: color) &&
            graph.TryConnectNodes(opacitySource, model, out _, targetPortId: opacity),
            "Metadata-driven pins must accept typed graph connections.");
        graph.SetParameter(model, color, JsonSerializer.SerializeToElement(new[] { 0.2, 0.3, 1, 1 }));
        var before = graph.CreateSnapshot();
        graph.AssignRenderMaterial(model, Material("Replacement", Vector("Color"), Scalar("Roughness")), 0);
        Require(model.InputPorts.Any(port => port.Id == color) && model.InputPorts.All(port => port.Id != opacity) &&
            graph.Connections.Count(connection => connection.Target == model) == 1 &&
            graph.Connections.Single(connection => connection.Target == model).TargetPortId == color &&
            model.GetParameterValue(model.Definition.Parameters.Single(parameter => parameter.Id == color))[0].GetDouble() == 0.2,
            "Replacement must retain compatible values/links and remove obsolete pins/links.");
        Require(model.GetParameterValue(model.Definition.Parameters.Single(parameter =>
            parameter.Id == "assetPath")).GetString() == originalPath &&
            RenderNodeSchema.ReadMaterials(model.Definition, model.Parameters).Single(slot => slot.Slot == 1).Path ==
            "/Game/Materials/Second.Second", "Material replacement must not change the mesh or another slot.");
        Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()),
            "One Undo must restore the previous material, values, pins and connections.");
        Require(graph.History.Redo(), "Material replacement must support Redo.");
        model = graph.GetNode(model.Id);
        graph.AssignRenderMaterial(model, Material("NoParameters"), 0);
        var index = new AssetReferenceIndex([new(
            new("material-test", ReferenceResourceKind.Project, "Material test", "project"), graph.CreateSnapshot())]);
        Require(index.Errors.Count == 0 && index.Uses.Any(use =>
            use.Target.Binding == "/Game/Materials/NoParameters.NoParameters" && use.Parameter == "material[0]"),
            "The resource index must track materials without exposed parameters.");
        using (var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 0)))
            Require(scene.RootElement.GetProperty("nodes")[0].GetProperty("materials").EnumerateArray()
                .Any(slot => slot.GetProperty("slot").GetInt32() == 0 &&
                    slot.GetProperty("path").GetString() == "/Game/Materials/NoParameters.NoParameters"),
                "A material without exposed parameters must still reach the renderer.");
        graph.SelectNode(model);
        var definition = model.Definition.Parameters.Single(parameter => parameter.Id == RenderNodeSchema.MaterialSlotId(0));
        var editor = new NodeParameterEditorViewModel(graph, model, definition);
        Require(editor.IsMaterialSlot && !editor.ShowPublish, "Material references require dedicated selection, not value publication.");
        var host = new ParameterEditorHost { DataContext = editor };
        var window = new Window { Content = host };
        try
        {
            window.Show();
            Require(host.GetVisualDescendants().OfType<TextBox>().Single().IsReadOnly,
                "Typing a path must not silently bypass material metadata discovery.");
        }
        finally { window.Close(); }
        using var saved = new MemoryStream();
        new ProjectFileService().SaveAsync(saved, graph).GetAwaiter().GetResult();
        saved.Position = 0;
        var loaded = new ProjectFileService().LoadAsync(saved).GetAwaiter().GetResult();
        Require(GraphSnapshot.ContentEquals(graph.CreateSnapshot(), loaded.Graph), "Material slots must survive project persistence.");
        var copied = new GraphWorkspaceViewModel(new UndoRedoManager());
        copied.PasteSnapshot(loaded.Graph, 1000, 1000);
        var copy = copied.Nodes.Single(node => node.Definition.TypeName == "ModelDraw");
        Require(RenderNodeSchema.ReadMaterials(copy.Definition, copy.Parameters).Count == 2,
            "Copy/paste must preserve parameterless and secondary material slots.");
        var unchanged = graph.CreateSnapshot();
        try
        {
            graph.AssignRenderMaterial(model, Material("Incomplete") with { Materials = null }, 0);
            throw new Exception("Missing material metadata was accepted.");
        }
        catch (InvalidDataException) { }
        Require(GraphSnapshot.ContentEquals(unchanged, graph.CreateSnapshot()), "Invalid metadata must not partially edit a graph.");
    }

    private static void VerifyLegacyAndMalformedSchemas()
    {
        var asset = new EngineAsset("/Engine/BasicShapes/Cube.Cube", "Cube", "model", "Unreal",
            Materials: [new(0, "/Game/Materials/Legacy.Legacy", "Legacy", [Vector("Color")])]);
        var current = RenderNodeSchema.Encode(asset);
        var entries = RenderNodeSchema.Describe(current);
        var legacy = JsonSerializer.Serialize(entries, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var definition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        NodeParameterSnapshot[] Values(string schema) => [new("materialSchema", "string", JsonSerializer.SerializeToElement(schema))];
        Require(RenderNodeSchema.ReadMaterials(definition, Values(legacy)).Single().Path == asset.Materials![0].Path &&
            RenderNodeSchema.Read(definition, Values(legacy)).Single().Id == entries.Single().Id,
            "Legacy array schemas must preserve material paths and pin identity.");
        foreach (var invalid in new[]
        {
            current.Replace("\"version\":2", "\"version\":3", StringComparison.Ordinal),
            current.Replace("\"version\":2", "\"version\":\"2\"", StringComparison.Ordinal),
            "{\"version\":2,\"materials\":[],\"parameters\":" + legacy + "}"
        })
        {
            try
            {
                _ = RenderNodeSchema.Effective(definition, Values(invalid));
                throw new Exception("An unsupported or inconsistent material schema was accepted.");
            }
            catch (InvalidDataException) { }
        }
    }

    private static void VerifyIdentityGuard(bool inline = false, bool texture = false)
    {
        var document = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = inline ? document.CreateNode(GraphNodeKind.GraphGroup) : null;
        var graph = group is null ? document : document.OpenInlineGraph(group);
        var node = RenderNodeAuthoring.CreateModel(graph,
            new("/Engine/BasicShapes/Cube.Cube", "Cube", "model", "Unreal"));
        if (texture)
            graph.AssignRenderMaterial(node, Material("Textured",
                new EngineMaterialParameter("Albedo", "texture", JsonSerializer.SerializeToElement(""))), 0);
        var resourceId = Guid.NewGuid();
        var location = texture
            ? "texture:" + RenderNodeSchema.Read(node.Definition, node.Parameters).Single().Id : "material:0";
        document.SetResourceIdentityDocument(new(1, [new(resourceId, texture ? "texture" : "material", "Explicit resource")],
            [], [], [new(node.Id, location, resourceId) { InstancePath = group is null ? [] : [group.Id] }], []), "Register resource");
        node = graph.GetNode(node.Id);
        var before = graph.CreateSnapshot();
        try
        {
            graph.AssignRenderMaterial(node, Material("Different", Vector("Color")), 0);
            throw new Exception("A legacy selection silently replaced an explicit portable material identity.");
        }
        catch (InvalidOperationException) { }
        Require(GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Identity-managed material rejection must be atomic.");
    }

    private static void VerifyMappedParameterlessMaterial(bool nested)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var node = new GraphNodeSnapshot(Guid.NewGuid(), definition.Kind, "Parameterless model", 0, 0,
            definition.PortType, definition.PortCategory, Parameters:
            [new("assetPath", "string", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube")),
             new("materialSchema", "string", JsonSerializer.SerializeToElement(RenderNodeSchema.EncodeMaterials(
                 [new(3, "/Game/Materials/Legacy.Legacy", "Plain material", [])])))]);
        var group = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.SpawnableGroup);
        var groupId = Guid.NewGuid();
        GraphSnapshot Wrap(GraphNodeSnapshot model) => nested
            ? new([new(groupId, group.Kind, "Nested model", 0, 0, group.PortType, group.PortCategory,
                ChildGraph: new([model], []))], []) : new([model], []);
        var graph = Wrap(node);
        var project = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var resource = Guid.NewGuid();
        var binding = new OpaqueBinding("unreal.object-path", "/Game/Materials/Mapped.Mapped");
        var identities = new ResourceIdentityDocument(1, [new(resource, "material", "Mapped material")],
            [new(project, resource, "material", binding)], [],
            [new(node.Id, "material:3", resource, nested ? [groupId] : [])], []);
        var probe = new ResourceIdentitySnapshotProbe(new(1, project, true,
            [new(binding, ResourceResolutionStatus.Resolved, "material")], []));
        var context = new ResourceIdentityRenderContext(new(identities, project, probe), graph: graph);
        var package = SpawnableGroupPackage.Compile(graph, resourceContext: context);
        using var rendered = JsonDocument.Parse(nested ? package.Templates.Single().RenderConfiguration :
            package.RootRenderConfiguration);
        var material = rendered.RootElement.GetProperty("nodes")[0].GetProperty("materials")[0];
        Require(material.GetProperty("slot").GetInt32() == 3 && material.GetProperty("path").GetString() == binding.Value,
            "Root and Spawnable mappings must resolve material slots independently of exposed parameters.");
        var missingModel = node with { Parameters = node.Parameters!.Where(parameter => parameter.Id != "assetPath").ToArray() };
        try
        {
            _ = SpawnableGroupPackage.Compile(Wrap(missingModel), resourceContext: context);
            throw new Exception("A mapped parameterless material without a model was silently ignored.");
        }
        catch (InvalidDataException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
