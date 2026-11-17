namespace LiberaFx.Models.Graph;

public sealed record GraphConversionDefinition(
    string? InputPortId = null, string? OutputPortId = null, string Mode = "explicit");

public sealed record GraphConversionRule(GraphNodeDefinition Node, string? InputPortId,
    string? OutputPortId, string SourceType, string TargetType)
{
    public string Identifier => $"{Node.QualifiedName}[{InputPortId ?? "(default)"} -> {OutputPortId ?? "(default)"}]";
}

public enum GraphConversionStatus { Missing, Unique, Ambiguous }

public sealed record GraphConversionResolution(IReadOnlyList<GraphConversionRule> Candidates)
{
    public GraphConversionStatus Status => Candidates.Count switch
    {
        0 => GraphConversionStatus.Missing,
        1 => GraphConversionStatus.Unique,
        _ => GraphConversionStatus.Ambiguous
    };

    public string Describe() => Status switch
    {
        GraphConversionStatus.Missing => "No explicit conversion is registered for these types.",
        GraphConversionStatus.Unique => $"Insert an explicit conversion node: {Candidates[0].Identifier}.",
        _ => "Ambiguous conversion; explicitly choose a node and its output: " +
             string.Join(", ", Candidates.Select(rule => rule.Identifier)) + "."
    };
}

public sealed class GraphConversionRegistry
{
    public IReadOnlyList<GraphConversionRule> Rules { get; }

    public GraphConversionRegistry(IEnumerable<GraphNodeDefinition> definitions)
    {
        var rules = new List<GraphConversionRule>();
        foreach (var node in definitions)
        {
            ValidateDefinition(node);
            foreach (var conversion in node.Conversions)
                rules.Add(new(node, conversion.InputPortId, conversion.OutputPortId,
                    node.GetInputPortType(conversion.InputPortId)!,
                    node.GetOutputPortType(conversion.OutputPortId)!));
        }
        Rules = Array.AsReadOnly(rules.OrderBy(rule => rule.Identifier, StringComparer.Ordinal).ToArray());
    }

    public GraphConversionResolution Resolve(string sourceType, string targetType) =>
        new(Array.AsReadOnly(Rules.Where(rule =>
            rule.SourceType == sourceType && rule.TargetType == targetType).ToArray()));

    public static void ValidateDefinition(GraphNodeDefinition node)
    {
        if (node.Conversions.Count > 32)
            throw new InvalidDataException($"Too many conversion declarations on '{node.QualifiedName}'.");
        var ports = new HashSet<(string?, string?)>();
        foreach (var conversion in node.Conversions)
        {
            if (conversion is null || conversion.Mode != "explicit" ||
                !ports.Add((conversion.InputPortId, conversion.OutputPortId)) ||
                node.GetInputPortCategory(conversion.InputPortId) != GraphPortCategory.Parameter ||
                node.GetOutputPortCategory(conversion.OutputPortId) != GraphPortCategory.Parameter ||
                node.GetInputPortType(conversion.InputPortId) is not { } source ||
                node.GetOutputPortType(conversion.OutputPortId) is not { } target ||
                source == target || source == "Signal" || target == "Signal")
                throw new InvalidDataException(
                    $"Invalid conversion declaration on '{node.QualifiedName}': " +
                    "use distinct parameter types, existing exact port IDs and mode 'explicit'.");
        }
    }
}
