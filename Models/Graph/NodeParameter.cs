using System.Text.Json;

namespace LiberaFx.Models.Graph;

public sealed record NodeParameterEditorDefinition(string Kind, double? Minimum, double? Maximum, double? Step);
public sealed record NodeParameterCondition(string Parameter, JsonElement Expected);
public sealed record NodeParameterDefinition(string Id, string Name, string Type, JsonElement DefaultValue)
{
    public NodeParameterEditorDefinition? Editor { get; init; }
    public NodeParameterCondition? EnabledWhen { get; init; }
}
public sealed record NodeParameterSnapshot(string Id, string Type, JsonElement Value);

public static class NodeParameter
{
    public static bool IsValidValue(string type, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined || value.GetRawText().Length > 16384) return false;
        if (type.StartsWith("enum:", StringComparison.Ordinal))
            return GraphEnumDefinition.TryGetTypeId(type, out _) &&
                   value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
        switch (type)
        {
            case "float":
                return IsFiniteNumber(value);
            case "int":
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
            case "bool":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case "string":
                return value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 4096;
            case "vector2":
            case "vector3":
            case "vector4":
                var length = type switch { "vector2" => 2, "vector3" => 3, _ => 4 };
                return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == length &&
                       value.EnumerateArray().All(IsFiniteNumber);
            case "color":
                return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 4 &&
                       value.EnumerateArray().All(IsFiniteNumber) &&
                       new RgbaColor(value[0].GetDouble(), value[1].GetDouble(),
                           value[2].GetDouble(), value[3].GetDouble()).IsValid;
            default:
                return value.ValueKind != JsonValueKind.Undefined;
        }
    }

    public static bool ValuesEqual(JsonElement left, JsonElement right) =>
        JsonElement.DeepEquals(left, right);

    private static bool IsFiniteNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
}
