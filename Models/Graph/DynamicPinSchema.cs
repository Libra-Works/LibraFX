using System.Globalization;
using System.Text.Json;

namespace LiberaFx.Models.Graph;

public sealed record DynamicPinGroupDefinition(
    string ParameterId,
    bool IsOutput,
    string IdPrefix,
    string NameTemplate,
    string? PortType,
    GraphPortCategory Category,
    int DefaultCount = 2,
    int MinCount = 1,
    int MaxCount = 32);

public static class DynamicPinSchema
{
    public const int AbsoluteMaxPins = 64;

    public static string PinId(DynamicPinGroupDefinition group, int index) =>
        group.IdPrefix + index.ToString(CultureInfo.InvariantCulture);

    public static string PinName(DynamicPinGroupDefinition group, int index) =>
        group.NameTemplate
            .Replace("{0}", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{1}", (index + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public static int Count(DynamicPinGroupDefinition group, IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        var stored = parameters?.FirstOrDefault(parameter => parameter.Id == group.ParameterId);
        if (stored is null) return group.DefaultCount;
        if (stored.Type != "int" || stored.Value.ValueKind != JsonValueKind.Number ||
            !stored.Value.TryGetInt32(out var count) || count < group.MinCount || count > group.MaxCount)
            throw new InvalidDataException(
                $"Dynamic pin '{group.ParameterId}' requires between {group.MinCount} and {group.MaxCount} pins.");
        return count;
    }

    public static string SchemaKey(GraphNodeDefinition definition, IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        if (definition.DynamicPins is not { Count: > 0 } groups) return string.Empty;
        return string.Join(",", groups.Select(group => Count(group, parameters).ToString(CultureInfo.InvariantCulture)));
    }

    public static GraphNodeDefinition Effective(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        if (definition.DynamicPins is not { Count: > 0 } groups) return definition;
        var inputs = definition.InputPorts?.ToList();
        var outputs = definition.OutputPorts?.ToList();
        foreach (var group in groups)
        {
            var count = Count(group, parameters);
            var portType = group.PortType ?? definition.PortType;
            if (group.IsOutput)
            {
                outputs ??= [];
                for (var index = 0; index < count; index++)
                    outputs.Add(new GraphOutputPortDefinition(PinId(group, index), PinName(group, index), portType, group.Category));
            }
            else
            {
                inputs ??= [];
                for (var index = 0; index < count; index++)
                    inputs.Add(new GraphInputPortDefinition(PinId(group, index), PinName(group, index), portType, group.Category));
            }
        }
        return definition with
        {
            InputPorts = inputs?.AsReadOnly(),
            OutputPorts = outputs?.AsReadOnly()
        };
    }
}
