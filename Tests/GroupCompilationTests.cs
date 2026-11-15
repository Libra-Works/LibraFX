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
    }
}
