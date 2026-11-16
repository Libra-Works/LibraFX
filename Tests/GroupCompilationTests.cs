using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class GroupCompilationTests
{
    public static void Run()
    {
        var directory = Path.Combine(AppDataDirectory.Root, "conversion-groups");
        var library = new GroupAssetLibrary(directory);
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var scalar = workspace.CreateNode(GraphNodeKind.Float);
        workspace.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(1d));
        var asset = library.Create("Example", workspace.CreateSnapshot());
        var groupType = GraphNodeDefinition.Core.Single(definition => definition.Kind == GraphNodeKind.GraphGroup);
        GraphNodeSnapshot Instance() => new(Guid.NewGuid(), groupType.Kind, "Instance", 0, 0,
            groupType.PortType, groupType.PortCategory,
            ChildGraph: asset.Graph with { Nodes = asset.Graph.Nodes.Select(node => node with { Id = Guid.NewGuid() }).ToArray() },
            GroupAsset: new(asset.Id, 1, false));
        var input = new GraphSnapshot([Instance(), Instance()], []);
        workspace.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(9d));
        library.Save(asset.Id, 1, "Updated", workspace.CreateSnapshot());
        var resolver = new GroupCompilationResolver([library]);
        var resolved = resolver.Resolve(input);
        if (input.Nodes.Any(node => node.GroupAsset!.Revision != 1) ||
            resolved.Nodes.Any(node => node.GroupAsset!.Revision != 2 ||
                node.ChildGraph!.Nodes.Single().Parameters!.Single(parameter => parameter.Id == "value").Value.GetDouble() != 9) ||
            resolved.Nodes.SelectMany(node => node.ChildGraph!.Nodes).Select(node => node.Id).Distinct().Count() != 2 ||
            !GraphSnapshot.ContentEquals(resolved, resolver.Resolve(input)))
            throw new InvalidOperationException("Latest group compilation mutated source data, reused instance IDs or produced nondeterministic IDs.");
        if (!GraphSnapshot.ContentEquals(input, new GroupCompilationResolver([], useEmbedded: true).Resolve(input)))
            throw new InvalidOperationException("Frozen compilation changed an embedded graph.");
        try
        {
            new GroupCompilationResolver([]).Resolve(input);
            throw new Exception("A missing group library silently used an old embedded version.");
        }
        catch (InvalidDataException) { }
        var cycle = new GraphSnapshot([Instance() with { ChildGraph = new([], []) }], []);
        library.Save(asset.Id, 2, "Cycle", cycle);
        try
        {
            resolver.Resolve(input);
            throw new Exception("A cyclic group dependency was not rejected.");
        }
        catch (InvalidDataException) { }
        VerifyBoundaryIdentity(library);
    }

    private static void VerifyBoundaryIdentity(GroupAssetLibrary library)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = workspace.CreateNode(GraphNodeKind.SpawnableGroup);
        if (!workspace.EnterGraph(group)) throw new InvalidOperationException("Cannot enter group.");
        var first = workspace.CreateNode(new GraphNodeKind("SpawnInputVector3"));
        var second = workspace.CreateNode(new GraphNodeKind("SpawnInputVector3"));
        first.Name = second.Name = "Shared name";
        var output = workspace.CreateNode(new GraphNodeKind("SpawnOutputVector3"));
        workspace.NavigateToGraph(0);
        group = workspace.GetNode(group.Id);
        var source = workspace.CreateNode(GraphNodeKind.FloatX3);
        var target = workspace.CreateNode(GraphNodeKind.FloatX3);
        if (!workspace.TryConnectNodes(source, group, out _, SpawnableGroupSchema.PortId(first.Id)) ||
            !workspace.TryConnectNodes(group, target, out _, sourcePortId: SpawnableGroupSchema.OutputPortId(output.Id)))
            throw new InvalidOperationException("Could not connect group boundaries.");
        var snapshot = workspace.CreateSnapshot();
        var copy = new GraphWorkspaceViewModel(new UndoRedoManager());
        copy.PasteSnapshot(snapshot, 0, 0);
        var pasted = copy.CreateSnapshot();
        GraphValidator.Validate(pasted);
        var pastedGroup = pasted.Nodes.Single(node => node.Kind == GraphNodeKind.SpawnableGroup);
        var pastedInput = pastedGroup.ChildGraph!.Nodes[0];
        var pastedOutput = pastedGroup.ChildGraph.Nodes[2];
        if (pastedInput.Id == first.Id ||
            !pasted.Connections.Any(edge => edge.TargetNodeId == pastedGroup.Id &&
                edge.TargetPortId == SpawnableGroupSchema.PortId(pastedInput.Id)) ||
            !pasted.Connections.Any(edge => edge.SourceNodeId == pastedGroup.Id &&
                edge.SourcePortId == SpawnableGroupSchema.OutputPortId(pastedOutput.Id)))
            throw new InvalidOperationException("Pasting a group changed its boundary identity or wiring.");

        var asset = library.Create("Boundary identity", snapshot.Nodes.Single(node => node.Id == group.Id).ChildGraph!);
        var linked = snapshot with { Nodes = snapshot.Nodes.Select(node => node.Id == group.Id
            ? node with { GroupAsset = new GroupAssetReference(asset.Id, asset.Revision, false) } : node).ToArray() };
        var resolved = new GroupCompilationResolver([library]).Resolve(linked);
        var resolvedGroup = resolved.Nodes.Single(node => node.Id == group.Id);
        var resolvedInput = resolvedGroup.ChildGraph!.Nodes[0];
        var resolvedOutput = resolvedGroup.ChildGraph.Nodes[2];
        if (resolvedInput.Id == first.Id ||
            !resolved.Connections.Any(edge => edge.TargetNodeId == group.Id &&
                edge.TargetPortId == SpawnableGroupSchema.PortId(resolvedInput.Id)) ||
            !resolved.Connections.Any(edge => edge.SourceNodeId == group.Id &&
                edge.SourcePortId == SpawnableGroupSchema.OutputPortId(resolvedOutput.Id)))
            throw new InvalidOperationException("Resolving a linked group changed its boundary identity or wiring.");

        var outer = library.Create("Nested boundary identity", snapshot);
        var outerDefinition = GraphNodeDefinition.Core.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var outerNode = new GraphNodeSnapshot(Guid.NewGuid(), outerDefinition.Kind, "Outer", 0, 0,
            outerDefinition.PortType, outerDefinition.PortCategory, ChildGraph: outer.Graph,
            GroupAsset: new(outer.Id, outer.Revision, false));
        var resolvedOuter = new GroupCompilationResolver([library]).Resolve(new GraphSnapshot([outerNode], []));
        var cloned = resolvedOuter.Nodes.Single().ChildGraph!;
        if (!GraphSnapshot.EquivalentIgnoringIds(outer.Graph, cloned))
            throw new InvalidOperationException("Cloned nested group ports were mistaken for local asset changes.");
        var nestedGroup = cloned.Nodes.Single(node => node.Kind == GraphNodeKind.SpawnableGroup);
        var otherInput = nestedGroup.ChildGraph!.Nodes[1];
        var altered = cloned with { Connections = cloned.Connections.Select(edge =>
            edge.TargetNodeId == nestedGroup.Id
                ? edge with { TargetPortId = SpawnableGroupSchema.PortId(otherInput.Id) } : edge).ToArray() };
        if (GraphSnapshot.EquivalentIgnoringIds(outer.Graph, altered))
            throw new InvalidOperationException("A real boundary wiring change was mistaken for an ID-only clone.");
    }
}
