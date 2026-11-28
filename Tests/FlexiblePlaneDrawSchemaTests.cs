using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class FlexiblePlaneDrawSchemaTests
{
    private static GraphNodeSnapshot Node(string id, params NodeParameterSnapshot[] parameters)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.TypeId == Guid.Parse(id));
        return new(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0,
            definition.PortType, definition.PortCategory, Parameters: parameters);
    }

    private static NodeParameterSnapshot Parameter<T>(string id, string type, T value) =>
        new(id, type, JsonSerializer.SerializeToElement(value));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Expected invalid Flexible Plane data to be rejected.");
    }

    public static void Run()
    {
        var parsed = FlexiblePlaneDrawSchema.ParsePoints(
            "[{\"position\":[0,0,0],\"width\":2,\"color\":[1,0,0,1]}," +
            "{\"position\":[2,0,0],\"width\":3,\"color\":[0,0,1,1]}]");
        Require(parsed.Length == 2 && parsed[1].Width == 3, "Point profiles must survive parsing.");
        Reject(() => FlexiblePlaneDrawSchema.ParsePoints(
            "[{\"position\":[0,0],\"width\":1,\"color\":[1,1,1,1]}]"));
        Reject(() => FlexiblePlaneDrawSchema.ParsePoints(
            "[{\"position\":[0,0,0],\"width\":-1,\"color\":[1,1,1,1]}," +
            "{\"position\":[1,0,0],\"width\":1,\"color\":[1,1,1,1]}]"));

        var draw = Node(FlexiblePlaneDrawSchema.DrawId, Parameter("assetPath", "string", "/Game/T_Ribbon"));
        var list = Node(FlexiblePlaneDrawSchema.ListId);
        var positionDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.FloatX3);
        var position = new GraphNodeSnapshot(Guid.NewGuid(), positionDefinition.Kind, "Position", 0, 0,
            positionDefinition.PortType, positionDefinition.PortCategory);
        var nodes = new[] { draw, list, position }.ToDictionary(node => node.Id);
        var edges = new[] { new GraphConnectionSnapshot(list.Id, draw.Id, "points"),
            new GraphConnectionSnapshot(position.Id, list.Id, "position0") };
        var bindings = edges.ToLookup(edge => edge.TargetNodeId);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var result = JsonSerializer.SerializeToElement(
            FlexiblePlaneDrawSchema.CreateDraw(draw, nodes, bindings), options);
        Require(result.GetProperty("kind").GetString() == "flexiblePlane" &&
            result.GetProperty("flexiblePlane").GetProperty("sourceKind").GetString() == "list",
            "Draw metadata must preserve the list source.");
        Require(result.GetProperty("assetPath").GetString() == "/Game/T_Ribbon" &&
            result.GetProperty("materials").GetArrayLength() == 0 &&
            result.GetProperty("textures").GetArrayLength() == 0,
            "The resolved primary texture is assetPath; no material is selected in metadata.");
        var input = result.GetProperty("inputs").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == "position0");
        Require(input.GetProperty("sourceNodeId").GetGuid() == position.Id &&
            input.GetProperty("width").GetInt32() == 3, "Connected position must stay a VM binding.");
        var exported = SpawnableGroupPackageBinary.Deserialize(
            EffectBinary.Compile(new GraphSnapshot(nodes.Values.ToArray(), edges)));
        var index = new AssetReferenceIndex([new ReferenceSource(
            new ReferenceResource("project:flexible-plane", ReferenceResourceKind.Project, "Ribbon", "project"),
            new GraphSnapshot(nodes.Values.ToArray(), edges))]);
        var textureUse = index.Uses.Single(use => use.NodeId == draw.Id && use.Parameter == "assetPath");
        Require(index.Errors.Count == 0 && textureUse.Target.AssetClass == "texture" && !textureUse.Inferred,
            "Ribbon textures must be indexed as explicit texture references, not guessed strings.");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(exported.ParentBytecode.AsSpan(8, 4)) == 1,
            "Point metadata must not become VM instructions or discard its numeric source.");
        using (var scene = JsonDocument.Parse(exported.RootRenderConfiguration))
        {
            Require(scene.RootElement.GetProperty("nodes").EnumerateArray().Single()
                    .GetProperty("kind").GetString() == "flexiblePlane",
                "Unified export must preserve the Flexible Plane draw.");
        }

        var history = Node(FlexiblePlaneDrawSchema.HistoryId);
        nodes = new[] { draw, history, position }.ToDictionary(node => node.Id);
        edges = [new(history.Id, draw.Id, "points"), new(position.Id, history.Id, "position")];
        result = JsonSerializer.SerializeToElement(FlexiblePlaneDrawSchema.CreateDraw(draw,
            nodes, edges.ToLookup(edge => edge.TargetNodeId)), options);
        Require(result.GetProperty("flexiblePlane").GetProperty("capacity").GetInt32() == 64 &&
            result.GetProperty("flexiblePlane").GetProperty("maxAge").GetDouble() == 1,
            "History defaults must reach metadata.");
        Reject(() => FlexiblePlaneDrawSchema.CreateDraw(draw, nodes,
            Array.Empty<GraphConnectionSnapshot>().ToLookup(edge => edge.TargetNodeId)));

        var procTexDef = GraphNodeDefinition.All.Single(d => d.TypeName == "ProceduralTexture");
        var procMatDef = GraphNodeDefinition.All.Single(d => d.TypeName == "ProceduralMaterial");
        var floatDef = GraphNodeDefinition.All.Single(d => d.Kind == GraphNodeKind.Float);
        var procTex = new GraphNodeSnapshot(Guid.NewGuid(), procTexDef.Kind, "ProcTex", 0, 0,
            procTexDef.PortType, procTexDef.PortCategory);
        var procMat = new GraphNodeSnapshot(Guid.NewGuid(), procMatDef.Kind, "ProcMat", 0, 0,
            procMatDef.PortType, procMatDef.PortCategory);
        var procDrawTexOnly = Node(FlexiblePlaneDrawSchema.DrawId);
        var procTexExport = SpawnableGroupPackageBinary.Deserialize(
            EffectBinary.Compile(new GraphSnapshot(
                [procDrawTexOnly, list, procTex],
                [new(list.Id, procDrawTexOnly.Id, "points"),
                 new(procTex.Id, procDrawTexOnly.Id, "proceduralTexture", "value")])));
        using (var scene = JsonDocument.Parse(procTexExport.RootRenderConfiguration))
        {
            var root = scene.RootElement;
            var planeNode = root.GetProperty("nodes").EnumerateArray().Single();
            Require(planeNode.GetProperty("assetPath").GetString() == "builtin:procedural-texture" &&
                planeNode.GetProperty("proceduralTexture").GetGuid() == procTex.Id &&
                planeNode.GetProperty("proceduralMaterial").ValueKind == JsonValueKind.Null &&
                root.GetProperty("procedural_resources").EnumerateArray().Any(r => r.GetProperty("nodeId").GetGuid() == procTex.Id),
                "FlexiblePlaneDraw must compile with an attached ProceduralTexture and collect it into procedural_resources.");
        }

        var procDrawMat = Node(FlexiblePlaneDrawSchema.DrawId);
        var procMatExport = SpawnableGroupPackageBinary.Deserialize(
            EffectBinary.Compile(new GraphSnapshot(
                [procDrawMat, list, procMat, procTex],
                [new(list.Id, procDrawMat.Id, "points"),
                 new(procMat.Id, procDrawMat.Id, "proceduralMaterial", "value"),
                 new(procTex.Id, procMat.Id, "texture0", "value")])));
        using (var scene = JsonDocument.Parse(procMatExport.RootRenderConfiguration))
        {
            var root = scene.RootElement;
            var planeNode = root.GetProperty("nodes").EnumerateArray().Single();
            var resourceIds = root.GetProperty("procedural_resources").EnumerateArray()
                .Select(r => r.GetProperty("nodeId").GetGuid()).ToHashSet();
            Require(planeNode.GetProperty("assetPath").GetString() == "builtin:procedural-material" &&
                planeNode.GetProperty("proceduralMaterial").GetGuid() == procMat.Id &&
                resourceIds.Contains(procMat.Id) && resourceIds.Contains(procTex.Id),
                "FlexiblePlaneDraw must compile with ProceduralMaterial and collect both material and its sampled textures.");
        }

        var hostSchema = RenderNodeSchema.EncodeMaterials([new EngineMaterial(0, "/Game/M_Ribbon", "M_Ribbon",
        [
            new EngineMaterialParameter("Opacity", "scalar", JsonSerializer.SerializeToElement(0.5), 2, -1),
            new EngineMaterialParameter("MaskTex", "texture", JsonSerializer.SerializeToElement("/Game/T_Mask"), 2, -1)
        ])]);
        var hostDrawDef = RenderNodeSchema.Effective(GraphNodeDefinition.All.Single(d => d.Kind == draw.Kind),
            [Parameter("materialSchema", "string", hostSchema)]);
        var opacityParamId = RenderNodeSchema.Read(hostDrawDef, [Parameter("materialSchema", "string", hostSchema)])
            .Single(p => p.Name == "Opacity").Id;
        var opacitySource = new GraphNodeSnapshot(Guid.NewGuid(), floatDef.Kind, "OpacitySrc", 0, 0,
            floatDef.PortType, floatDef.PortCategory);
        var hostDraw = Node(FlexiblePlaneDrawSchema.DrawId,
            Parameter("assetPath", "string", "/Game/T_Ribbon"),
            Parameter("materialSchema", "string", hostSchema));
        var hostExport = SpawnableGroupPackageBinary.Deserialize(
            EffectBinary.Compile(new GraphSnapshot(
                [hostDraw, list, opacitySource],
                [new(list.Id, hostDraw.Id, "points"),
                 new(opacitySource.Id, hostDraw.Id, opacityParamId)])));
        using (var scene = JsonDocument.Parse(hostExport.RootRenderConfiguration))
        {
            var planeNode = scene.RootElement.GetProperty("nodes").EnumerateArray().Single();
            var matSlot = planeNode.GetProperty("materials").EnumerateArray().Single();
            var texEntry = planeNode.GetProperty("textures").EnumerateArray().Single();
            var matInput = planeNode.GetProperty("inputs").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == opacityParamId);
            Require(matSlot.GetProperty("slot").GetInt32() == 0 &&
                matSlot.GetProperty("path").GetString() == "/Game/M_Ribbon" &&
                texEntry.GetProperty("name").GetString() == "MaskTex" &&
                texEntry.GetProperty("path").GetString() == "/Game/T_Mask" &&
                matInput.GetProperty("sourceNodeId").GetGuid() == opacitySource.Id &&
                matInput.GetProperty("materialParameter").GetProperty("name").GetString() == "Opacity",
                "FlexiblePlaneDraw must export host material slots, textures, and animated material parameters.");
        }
    }
}
