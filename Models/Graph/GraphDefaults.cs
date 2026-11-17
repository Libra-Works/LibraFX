namespace LiberaFx.Models.Graph;

public static class GraphDefaults
{
    public static GraphSnapshot CreateEffect()
    {
        var start = GraphNodeDefinition.Core.Single(definition => definition.Kind.Value == "OnStart");
        return new GraphSnapshot([
            new(Guid.NewGuid(), start.Kind, start.Name, 80, 80, start.PortType, start.PortCategory)
        ], []);
    }
}
