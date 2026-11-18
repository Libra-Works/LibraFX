using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

internal static class GraphRevisionDiffTests
{
    public static void Run()
    {
        var id = Guid.NewGuid();
        var source = new GraphNodeSnapshot(id, GraphNodeKind.Float, "Value", 0, 0, "Float", GraphPortCategory.Parameter,
            Parameters: [new("value", "float", JsonSerializer.SerializeToElement(1d))]);
        var target = source with { Id = Guid.NewGuid(), Name = "Target" };
        var before = new GraphSnapshot([source, target], [new(source.Id, target.Id)]);
        var after = before with { Nodes = [source with { Parameters = [new("value", "float", JsonSerializer.SerializeToElement(2d))] }, target] };
        if (GraphRevisionDiff.Compare(before, after).Single(change => change.After?.Id == id).Kind != GraphDiffKind.Modified)
            throw new InvalidOperationException("Parameter changes must be detected.");
        if (GraphRevisionDiff.Compare(before, before with { Connections = [] }).Any(change => change.Kind != GraphDiffKind.Modified))
            throw new InvalidOperationException("Connection removals must mark both endpoints.");
        var group = source with { Id = Guid.NewGuid(), Kind = GraphNodeKind.GraphGroup, ChildGraph = before };
        var nested = GraphRevisionDiff.Compare(new([group], []), new([group with { ChildGraph = after }], []));
        if (!nested.Any(change => change.Path.Contains('/') && change.Kind == GraphDiffKind.Modified))
            throw new InvalidOperationException("Nested changes must not be hidden.");
        if (!ReferenceEquals(GraphRevisionDiff.Scope(new([group], []), [group.Id]), before) ||
            GraphRevisionDiff.Scope(new([], []), [group.Id]) is not null)
            throw new InvalidOperationException("Nested comparison must follow stable group IDs and retain a missing side.");
        if (GraphRevisionDiff.Compare(before, before with { AutoDestroyWhenIdle = false })
                .Single(change => change.Kind == GraphDiffKind.Modified).Path != "$graph")
            throw new InvalidOperationException("Graph-level lifecycle metadata must not be hidden.");
        if (GraphRevisionDiff.Compare(new([source], []), new([target], [])).Select(change => change.Kind)
            .Order().SequenceEqual(new[] { GraphDiffKind.Added, GraphDiffKind.Removed }.Order()) is false)
            throw new InvalidOperationException("Added and removed identities must be distinct.");
    }
}
