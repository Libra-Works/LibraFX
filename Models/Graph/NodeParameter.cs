using System.Text.Json;

namespace LiberaFx.Models.Graph;

public sealed record NodeParameterEditorDefinition(string Kind, double? Minimum, double? Maximum, double? Step);
public sealed record NodeParameterDefinition(string Id, string Name, string Type, JsonElement DefaultValue)
{
    public NodeParameterEditorDefinition? Editor { get; init; }
}
public sealed record NodeParameterSnapshot(string Id, string Type, JsonElement Value);

public static class NodeParameter
{
    public static bool IsValidValue(string type, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined || value.GetRawText().Length > 16384) return false;
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
            case "color":
                var length = type switch { "vector2" => 2, "vector3" => 3, _ => 4 };
                return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == length &&
                       value.EnumerateArray().All(item => IsFiniteNumber(item) &&
                           (type != "color" || item.GetDouble() is >= 0 and <= 1));
            default:
                return value.ValueKind != JsonValueKind.Undefined;
        }
    }

    public static bool ValuesEqual(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind && left.GetRawText() == right.GetRawText();

    private static bool IsFiniteNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
}
