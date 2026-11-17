using System.Globalization;
using System.Text.Json;

namespace LiberaFx.Models.Graph;

public static class SequenceSchema
{
    public const int MaximumOutputs = 32;
    public const string OutputCountParameter = "outputs";

    public static bool IsSequence(GraphNodeKind kind) => kind == GraphNodeKind.Sequence;
    public static string OutputId(int index) => "then_" + index.ToString(CultureInfo.InvariantCulture);

    public static int OutputCount(IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        var stored = parameters?.FirstOrDefault(parameter => parameter.Id == OutputCountParameter);
        if (stored is null) return 2;
        if (stored.Type != "int" || stored.Value.ValueKind != JsonValueKind.Number ||
            !stored.Value.TryGetInt32(out var count) || count is < 1 or > MaximumOutputs)
            throw new InvalidDataException($"Sequence requires between 1 and {MaximumOutputs} outputs.");
        return count;
    }

    public static GraphNodeDefinition Effective(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters) =>
        !IsSequence(definition.Kind) ? definition : definition with
        {
            OutputPorts = Enumerable.Range(0, OutputCount(parameters)).Select(index =>
                new GraphOutputPortDefinition(OutputId(index), (index + 1).ToString(CultureInfo.InvariantCulture),
                    "Signal", GraphPortCategory.Signal)).ToArray()
        };

    public static GraphSnapshot Lower(GraphSnapshot graph)
    {
        var sequences = graph.Nodes.Where(node => IsSequence(node.Kind)).ToDictionary(node => node.Id);
        if (sequences.Count == 0) return graph;
        var branches = sequences.ToDictionary(pair => pair.Key, pair =>
        {
            var order = Enumerable.Range(0, OutputCount(pair.Value.Parameters))
                .Select((index) => (Id: OutputId(index), Index: index)).ToDictionary(item => item.Id, item => item.Index);
            return new Queue<GraphConnectionSnapshot>(graph.Connections.Where(edge => edge.SourceNodeId == pair.Key)
                .OrderBy(edge => order[edge.SourcePortId!]));
        });
        return graph with
        {
            Nodes = graph.Nodes.Select(node => sequences.ContainsKey(node.Id)
                ? node with { Kind = GraphNodeKind.Signal, Parameters = null } : node).ToArray(),
            Connections = graph.Connections.Select(original =>
            {
                var edge = branches.TryGetValue(original.SourceNodeId, out var ordered) ? ordered.Dequeue() : original;
                return edge with
                {
                    SourcePortId = sequences.ContainsKey(edge.SourceNodeId) ? null : edge.SourcePortId,
                    TargetPortId = sequences.ContainsKey(edge.TargetNodeId) ? null : edge.TargetPortId
                };
            }).ToArray()
        };
    }
}
