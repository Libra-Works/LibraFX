namespace LiberaFx.Models.Graph;

public enum GraphValueType : byte
{
    None = 0,
    Float = 1,
    Int32 = 2,
    Vector2 = 3,
    Vector3 = 4,
    Vector4 = 5,
    Color = 6,
    Enum = 7
}

public static class GraphValueTypeSchema
{
    public static bool TryGetValueType(string? portType, out GraphValueType valueType)
    {
        valueType = portType switch
        {
            "Float" => GraphValueType.Float,
            // Bool keeps its authoring identity; the stable runtime ABI stores false/true as 0/1.
            "Bool" => GraphValueType.Float,
            "Int32" => GraphValueType.Int32,
            "FloatX2" => GraphValueType.Vector2,
            "FloatX3" => GraphValueType.Vector3,
            "FloatX4" => GraphValueType.Vector4,
            "Color" => GraphValueType.Color,
            _ when portType?.StartsWith("enum:", StringComparison.Ordinal) == true &&
                   GraphEnumDefinition.TryGetTypeId(portType, out _) => GraphValueType.Enum,
            _ => GraphValueType.None
        };
        return valueType != GraphValueType.None;
    }

    public static int Width(GraphValueType valueType) => valueType switch
    {
        GraphValueType.Float or GraphValueType.Int32 or GraphValueType.Enum => 1,
        GraphValueType.Vector2 => 2,
        GraphValueType.Vector3 => 3,
        GraphValueType.Vector4 or GraphValueType.Color => 4,
        _ => 0
    };

    public static string ParameterType(GraphValueType valueType) => valueType switch
    {
        GraphValueType.Float => "float",
        GraphValueType.Int32 => "int",
        GraphValueType.Vector2 => "vector2",
        GraphValueType.Vector3 => "vector3",
        GraphValueType.Vector4 => "vector4",
        GraphValueType.Color => "color",
        GraphValueType.Enum => "int",
        _ => throw new InvalidDataException($"Unsupported graph value type '{valueType}'.")
    };
}
