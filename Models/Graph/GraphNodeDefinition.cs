using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Models.Graph;

[JsonConverter(typeof(GraphNodeKindConverter))]
public readonly record struct GraphNodeKind(string Value)
{
    public static GraphNodeKind Float => new("Float");
    public static GraphNodeKind FloatX2 => new("FloatX2");
    public static GraphNodeKind FloatX3 => new("FloatX3");
    public static GraphNodeKind FloatX4 => new("FloatX4");
    public static GraphNodeKind Int32 => new("Int32");
    public static GraphNodeKind Signal => new("Signal");
    public static GraphNodeKind InputProperty => new("InputProperty");
    public static GraphNodeKind InputSignal => new("InputSignal");
    public static GraphNodeKind OutputProperty => new("OutputProperty");
    public static GraphNodeKind OutputSignal => new("OutputSignal");
    public static GraphNodeKind FloatFCurve => new("FloatFCurve");
    public static GraphNodeKind GraphGroup => new("GraphGroup");
    public static GraphNodeKind SpawnableGroup => new("SpawnableGroup");
    public static GraphNodeKind Comment => new("Comment");
    public static GraphNodeKind Time => new("Time");
    public static GraphNodeKind Add => new("Add");
    public static GraphNodeKind Subtract => new("Subtract");
    public static GraphNodeKind Multiply => new("Multiply");
    public static GraphNodeKind Divide => new("Divide");
    public static GraphNodeKind AddVector2 => new("AddVector2");
    public static GraphNodeKind SubtractVector2 => new("SubtractVector2");
    public static GraphNodeKind MultiplyVector2 => new("MultiplyVector2");
    public static GraphNodeKind DivideVector2 => new("DivideVector2");
    public static GraphNodeKind AddVector3 => new("AddVector3");
    public static GraphNodeKind SubtractVector3 => new("SubtractVector3");
    public static GraphNodeKind MultiplyVector3 => new("MultiplyVector3");
    public static GraphNodeKind DivideVector3 => new("DivideVector3");
    public static GraphNodeKind AddVector4 => new("AddVector4");
    public static GraphNodeKind SubtractVector4 => new("SubtractVector4");
    public static GraphNodeKind MultiplyVector4 => new("MultiplyVector4");
    public static GraphNodeKind DivideVector4 => new("DivideVector4");
    public static GraphNodeKind ScalarToVector2 => new("ScalarToVector2");
    public static GraphNodeKind ScalarToVector3 => new("ScalarToVector3");
    public static GraphNodeKind ScalarToVector4 => new("ScalarToVector4");
    public static GraphNodeKind BreakVector2 => new("BreakVector2");
    public static GraphNodeKind BreakVector3 => new("BreakVector3");
    public static GraphNodeKind BreakVector4 => new("BreakVector4");
    public static GraphNodeKind Vector2ToVector3 => new("Vector2ToVector3");
    public static GraphNodeKind Vector2ToVector4 => new("Vector2ToVector4");
    public static GraphNodeKind Vector3ToVector2 => new("Vector3ToVector2");
    public static GraphNodeKind Vector3ToVector4 => new("Vector3ToVector4");
    public static GraphNodeKind Vector4ToVector2 => new("Vector4ToVector2");
    public static GraphNodeKind Vector4ToVector3 => new("Vector4ToVector3");
    public static GraphNodeKind MaskVector2 => new("MaskVector2");
    public static GraphNodeKind MaskVector3 => new("MaskVector3");
    public static GraphNodeKind MaskVector4 => new("MaskVector4");
    public static GraphNodeKind ColorToVector4 => new("ColorToVector4");
    public static GraphNodeKind Vector4ToColor => new("Vector4ToColor");
    public override string ToString() => Value ?? string.Empty;
}

public sealed class GraphNodeKindConverter : JsonConverter<GraphNodeKind>
{
    private static readonly string[] LegacyKinds = ["Float", "FloatX2", "FloatX3", "FloatX4", "Int32", "Signal", "FloatFCurve"];

    public override GraphNodeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } value)
        {
            var legacy = LegacyKinds.FirstOrDefault(kind => string.Equals(kind, value, StringComparison.OrdinalIgnoreCase));
            return new GraphNodeKind(legacy ?? (Guid.TryParse(value, out var id) ? id.ToString("D") : value));
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var index) && index >= 0 && index < LegacyKinds.Length)
            return new GraphNodeKind(LegacyKinds[index]);
        throw new JsonException("Invalid graph node type.");
    }

    public override void Write(Utf8JsonWriter writer, GraphNodeKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public enum GraphPortCategory
{
    Parameter,
    Signal
}

public sealed record GraphRuntimeDefinition(string Kind, int AbiVersion, string Execution);

public sealed record GraphNodeDefinition(
    GraphNodeKind Kind,
    Guid TypeId,
    string Namespace,
    string Name,
    string PortType,
    GraphPortCategory PortCategory,
    string Group)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<Guid, string> BuiltInIds = new()
    {
        [Guid.Parse("57980d8e-701f-443c-91f6-8db56a09d2fc")] = "GpuParticle",
        [Guid.Parse("5bf459b5-77aa-4ec5-a704-cc59df700001")] = "ModelDraw",
        [Guid.Parse("5bf459b5-77aa-4ec5-a704-cc59df700002")] = "SpriteDraw",
        [Guid.Parse("5bf459b5-77aa-4ec5-a704-cc59df700003")] = "SpriteAnimation",
        [Guid.Parse("19aa71e5-b3c7-453d-860e-baad3b9d0fc4")] = "Float",
        [Guid.Parse("d98eb6f5-ee2e-432a-9450-09e44dcc42e3")] = "FloatX2",
        [Guid.Parse("7ef61949-53c4-41fb-aabd-840b0a4680f7")] = "FloatX3",
        [Guid.Parse("0a613e91-1039-478e-9ca7-b8555a67f988")] = "FloatX4",
        [Guid.Parse("c7410281-9b6e-46f0-9bb7-334cc3b40767")] = "Int32",
        [Guid.Parse("a40e6f94-c2c1-48ed-9a7b-2087c422ad5a")] = "Signal",
        [Guid.Parse("f54171ee-4aa3-498b-b2ff-8c9983fc1501")] = "FloatFCurve",
        [Guid.Parse("149e6e41-9976-4b69-a74c-3f6e6b254e5a")] = "GraphGroup",
        [Guid.Parse("c0bc50ea-c514-41a2-9b28-9c2c931d2821")] = "SpawnableGroup",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000001")] = "GroupInputFloat",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000002")] = "GroupInputVector2",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000003")] = "GroupInputVector3",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000004")] = "GroupInputVector4",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000005")] = "GroupInputColor",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000006")] = "GroupInputSignal",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000007")] = "GroupInputMatrix",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000011")] = "GroupOutputFloat",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000012")] = "GroupOutputVector2",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000013")] = "GroupOutputVector3",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000014")] = "GroupOutputVector4",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000015")] = "GroupOutputColor",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000016")] = "GroupOutputSignal",
        [Guid.Parse("1774de17-a97b-4e31-b200-000000000017")] = "GroupOutputMatrix",
        [Guid.Parse("f43ba0db-ea8c-4c27-93ec-2da000000001")] = "InputProperty",
        [Guid.Parse("f43ba0db-ea8c-4c27-93ec-2da000000002")] = "InputSignal",
        [Guid.Parse("f43ba0db-ea8c-4c27-93ec-2da000000003")] = "OutputProperty",
        [Guid.Parse("f43ba0db-ea8c-4c27-93ec-2da000000004")] = "OutputSignal",
        [Guid.Parse("7ff078d2-dce1-42b0-93fb-b40200000001")] = "GameIoRuntimeOutputSignal",
        [Guid.Parse("eb710a39-f520-4c02-9140-ec1100000001")] = "LifeTime",
        [Guid.Parse("c982e1be-8f78-4d28-9a65-4f4aa45249e0")] = "InstanceAge",
        [Guid.Parse("c982e1be-8f78-4d28-9a65-4f4aa45249e1")] = "InstanceRemaining",
        [Guid.Parse("c982e1be-8f78-4d28-9a65-4f4aa45249e2")] = "InstanceNormalizedAge",
        [Guid.Parse("c982e1be-8f78-4d28-9a65-4f4aa45249e3")] = "OwnerDestroy",
        [Guid.Parse("c982e1be-8f78-4d28-9a65-4f4aa45249e4")] = "Delay",
        [Guid.Parse("c982e1be-8f78-4d28-9a65-4f4aa45249e5")] = "OnDestroy",
        [Guid.Parse("4a94e882-39e8-4efb-92c7-180000000001")] = "LifecycleRuntimeRemaining",
        [Guid.Parse("4a94e882-39e8-4efb-92c7-180000000002")] = "LifecycleRuntimeNormalizedAge",
        [Guid.Parse("eb710a39-f520-4c02-9140-ec1100000002")] = "OnStart",
        [Guid.Parse("eb710a39-f520-4c02-9140-ec1100000003")] = "SpawnOutputVector3",
        [Guid.Parse("fb8c8a83-0c9d-4870-98a1-08c0ee800001")] = "SpawnInputFloat",
        [Guid.Parse("fb8c8a83-0c9d-4870-98a1-08c0ee800002")] = "SpawnInputVector2",
        [Guid.Parse("fb8c8a83-0c9d-4870-98a1-08c0ee800003")] = "SpawnInputVector3",
        [Guid.Parse("fb8c8a83-0c9d-4870-98a1-08c0ee800004")] = "SpawnInputVector4",
        [Guid.Parse("fb8c8a83-0c9d-4870-98a1-08c0ee800005")] = "SpawnInputColor",
        [Guid.Parse("8f4bc759-b775-41ab-a7da-2ac5c17e29ed")] = "Comment",
        [Guid.Parse("e97a5832-31c5-461c-b7bb-c176c804c115")] = "Time",
        [Guid.Parse("c6de2a2a-a44b-41cb-8173-3c417a92bca5")] = "Add",
        [Guid.Parse("a49f3240-e62f-4d86-b3ab-312246e72db2")] = "Subtract",
        [Guid.Parse("3fec134e-1d88-438c-bbf2-4c8ead69bdaa")] = "Multiply",
        [Guid.Parse("3983558b-d822-44be-81c7-274fd62d62be")] = "Divide",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000001")] = "AddVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000002")] = "SubtractVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000003")] = "MultiplyVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000004")] = "DivideVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000005")] = "AddVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000006")] = "SubtractVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000007")] = "MultiplyVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000008")] = "DivideVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000009")] = "AddVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000010")] = "SubtractVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000011")] = "MultiplyVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000012")] = "DivideVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000013")] = "ScalarToVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000014")] = "ScalarToVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000015")] = "ScalarToVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000016")] = "BreakVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000017")] = "BreakVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000018")] = "BreakVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000019")] = "Vector2ToVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000020")] = "Vector2ToVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000021")] = "Vector3ToVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000022")] = "Vector3ToVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000023")] = "Vector4ToVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000024")] = "Vector4ToVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000025")] = "MaskVector2",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000026")] = "MaskVector3",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000027")] = "MaskVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000028")] = "ColorToVector4",
        [Guid.Parse("2c5e5bb8-117b-4a01-9a01-000000000029")] = "Vector4ToColor",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000001")] = "Abs",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000002")] = "Floor",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000003")] = "Round",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000004")] = "Sign",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000005")] = "Sqrt",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000006")] = "Pow",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000007")] = "Log",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000008")] = "Log10",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000009")] = "Sin",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000000a")] = "Cos",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000000b")] = "Tan",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000000c")] = "ATan",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000000d")] = "ATan2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000000e")] = "DegToRad",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000000f")] = "RadToDeg",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000010")] = "NormalizeRad",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000011")] = "NormalizeDeg",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000012")] = "Min",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000013")] = "Max",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000014")] = "Clamp",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000015")] = "Lerp",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000016")] = "Mad",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000017")] = "PI",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000018")] = "TwoPI",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000019")] = "LengthVector2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000001a")] = "DotVector2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000001b")] = "NormalizeVector2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000001c")] = "LengthVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000001d")] = "DotVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000001e")] = "NormalizeVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000001f")] = "LengthVector4",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000020")] = "DotVector4",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000021")] = "NormalizeVector4",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000022")] = "CrossVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000023")] = "AbsVector2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000024")] = "MinVector2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000025")] = "MaxVector2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000026")] = "LerpVector2",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000027")] = "AbsVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000028")] = "MinVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-000000000029")] = "MaxVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000002a")] = "LerpVector3",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000002b")] = "AbsVector4",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000002c")] = "MinVector4",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000002d")] = "MaxVector4",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000002e")] = "LerpVector4",
        [Guid.Parse("74305ac1-91b2-4c33-8d44-00000000002f")] = "ComposeVector4",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000101")] = "MatrixCompose",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000102")] = "MatrixBreak",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000103")] = "Transform",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000104")] = "MatrixMultiply",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000105")] = "TransformPoint",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000106")] = "TransformDirection",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000107")] = "MatrixInverse",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000108")] = "DecomposeTransform",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000109")] = "RotatePoint",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-00000000010a")] = "TranslatePoint",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-00000000010b")] = "OwnerMatrix",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000001")] = "MatrixRuntimeTransformRow0",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000002")] = "MatrixRuntimeTransformRow1",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000003")] = "MatrixRuntimeTransformRow2",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000004")] = "MatrixRuntimeTransformRow3",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000005")] = "MatrixRuntimeInverseRow0",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000006")] = "MatrixRuntimeInverseRow1",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000007")] = "MatrixRuntimeInverseRow2",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000008")] = "MatrixRuntimeInverseRow3",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000009")] = "MatrixRuntimeInverseValid",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-00000000000a")] = "MatrixRuntimeDecomposePosition",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-00000000000b")] = "MatrixRuntimeDecomposeRotation",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-00000000000c")] = "MatrixRuntimeDecomposeScale",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-00000000000d")] = "MatrixRuntimeDecomposeValid",
        [Guid.Parse("b0af61d2-29f6-4cc9-a71e-00000000000e")] = "MatrixRuntimeBoundary"
    };

    private static readonly List<string> Warnings = [];
    private static readonly object RegistryLock = new();
    private static IReadOnlyList<GraphNodeDefinition> _all = LoadAll();
    public static IReadOnlyList<GraphNodeDefinition> All => Volatile.Read(ref _all);
    public static IReadOnlyList<GraphNodeDefinition> Core { get; } = _all;
    public static IReadOnlyList<GraphNodeDefinition> AuthoringCore { get; } =
        Array.AsReadOnly(Core.Where(item => !item.IsInternalRuntime).ToArray());
    public static IReadOnlyList<string> LoadWarnings => Warnings;
    public string QualifiedName => $"{Namespace}.{TypeName}";
    public string TypeName { get; init; } = string.Empty;
    public bool IsInternalRuntime => Namespace == "LiberaFx" &&
        (TypeName.StartsWith("MatrixRuntime", StringComparison.Ordinal) ||
         TypeName.StartsWith("LifecycleRuntime", StringComparison.Ordinal) ||
         TypeName.StartsWith("GameIoRuntime", StringComparison.Ordinal));
    public string? DisplayNameKey { get; init; }
    public string? GroupNameKey { get; init; }
    public IReadOnlyList<NodeParameterDefinition> Parameters { get; init; } = [];
    public IReadOnlyList<GraphInputPortDefinition>? InputPorts { get; init; }
    public IReadOnlyList<GraphOutputPortDefinition>? OutputPorts { get; init; }
    public GraphRuntimeDefinition? Runtime { get; init; }
    public string KindDomain => Services.GpuParticleEditorService.IsGpu(Kind) ? "GpuParticle" : "Cpu";

    public GraphPortCategory GetInputPortCategory(string? portId) =>
        InputPorts?.FirstOrDefault(port => port.Id == portId)?.Category ?? PortCategory;

    public GraphPortCategory GetOutputPortCategory(string? portId) =>
        OutputPorts?.FirstOrDefault(port => port.Id == portId)?.Category ?? PortCategory;

    public string? GetInputPortType(string? portId)
    {
        if (InputPorts is null) return portId is null ? PortType : null;
        var port = InputPorts.FirstOrDefault(port => port.Id == portId);
        return port is null ? null : port.PortType ?? PortType;
    }

    public string? GetOutputPortType(string? portId)
    {
        if (OutputPorts is null) return portId is null ? PortType : null;
        var port = OutputPorts.FirstOrDefault(port => port.Id == portId);
        return port?.PortType ?? (port is null ? null : PortType);
    }

    public static bool TryFind(string? value, out GraphNodeDefinition? definition,
        IEnumerable<GraphNodeDefinition>? available = null)
    {
        var definitions = available ?? All;
        definition = definitions.FirstOrDefault(item =>
            string.Equals(item.Kind.Value, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.QualifiedName, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.TypeId.ToString("D"), value, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            var matches = definitions.Where(item => string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 1) definition = matches[0];
        }
        return definition is not null;
    }

    public static IReadOnlyList<GraphNodeDefinition> LoadDirectory(string directory, out IReadOnlyList<string> warnings)
    {
        var loaded = new List<GraphNodeDefinition>();
        var errors = new List<string>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var parsed = new List<GraphNodeDefinition>();
                using var stream = File.OpenRead(path);
                AddDefinition(stream, parsed, ids, names);
                var definition = parsed.Single();
                lock (RegistryLock)
                {
                    if (Core.Any(item => item.TypeId == definition.TypeId ||
                        string.Equals(item.QualifiedName, definition.QualifiedName, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Project definitions cannot replace Core node types.");
                    var existing = _all.FirstOrDefault(item => item.TypeId == definition.TypeId ||
                        string.Equals(item.QualifiedName, definition.QualifiedName, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        if (JsonSerializer.Serialize(existing, SerializerOptions) != JsonSerializer.Serialize(definition, SerializerOptions))
                            throw new InvalidDataException(
                                "This UUID or qualified name has a different registered definition. Use a unique type UUID and qualified name, or restart after updating its definition.");
                        loaded.Add(existing);
                    }
                    else
                    {
                        // Keep registered types immutable so project switching cannot invalidate open graphs or history.
                        Volatile.Write(ref _all, Array.AsReadOnly<GraphNodeDefinition>([.. _all, definition]));
                        loaded.Add(definition);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        warnings = errors.AsReadOnly();
        return loaded.AsReadOnly();
    }

    public static string DisplayPortType(string portType) => portType switch
    {
        "Float" => "Scalar",
        "FloatX2" => "Vector2",
        "FloatX3" => "Vector3",
        "FloatX4" => "Vector4",
        "Int32" => "Int",
        _ when GraphEnumDefinition.TryGetTypeId(portType, out var id) &&
               GraphEnumDefinition.All.TryGetValue(id, out var definition) => definition.Name,
        _ => portType
    };

    private static IReadOnlyList<GraphNodeDefinition> LoadAll()
    {
        var definitions = new List<GraphNodeDefinition>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Nodes");
        var builtInPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in BuiltInIds)
        {
            var path = Path.Combine(directory, name + ".json");
            builtInPaths.Add(path);
            try
            {
                using var stream = File.OpenRead(path);
                AddDefinition(stream, definitions, ids, names, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Trace.TraceWarning("Using embedded node definition for {0}: {1}", path, ex.Message);
                Warnings.Add($"{Path.GetFileName(path)}: {ex.Message} (using built-in definition)");
                using var stream = BuiltInAssets.Open("Nodes", name);
                AddDefinition(stream, definitions, ids, names, id);
            }
        }
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                if (builtInPaths.Contains(path) || Path.GetFileName(path).Equals("nodes.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var stream = File.OpenRead(path);
                    AddDefinition(stream, definitions, ids, names);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    Trace.TraceWarning("Ignoring invalid node definition {0}: {1}", path, ex.Message);
                    Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }

        return definitions.AsReadOnly();
    }

    private static void AddDefinition(Stream stream, List<GraphNodeDefinition> definitions, HashSet<Guid> ids, HashSet<string> names, Guid? expectedId = null)
    {
        var node = JsonSerializer.Deserialize<NodeFile>(stream, SerializerOptions);
        if (node is null || node.SchemaVersion != 1 || node.TypeId == Guid.Empty ||
            (expectedId.HasValue && node.TypeId != expectedId) ||
            !IsIdentifier(node.Kind) || string.IsNullOrWhiteSpace(node.Namespace) ||
            !node.Namespace.Split('.').All(IsIdentifier) ||
            string.IsNullOrWhiteSpace(node.Name) || string.IsNullOrWhiteSpace(node.Group) ||
            string.IsNullOrWhiteSpace(node.PortType) ||
            !Enum.TryParse(node.PortCategory, out GraphPortCategory category) || !Enum.IsDefined(category))
            throw new InvalidDataException("Invalid node definition.");
        var qualifiedName = $"{node.Namespace}.{node.Kind}";
        if (node.Namespace == "LiberaFx" && BuiltInIds.Any(item => item.Key != node.TypeId && item.Value == node.Kind))
            throw new InvalidDataException("A built-in qualified name belongs to another UUID.");
        if (ids.Contains(node.TypeId) || names.Contains(qualifiedName))
            throw new InvalidDataException("Duplicate node type UUID or qualified name.");
        var parameters = new List<NodeParameterDefinition>();
        List<GraphInputPortDefinition>? inputs = null;
        if (node.InputPorts is not null)
        {
            inputs = [];
            var inputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in node.InputPorts)
            {
                if (port is null || !IsIdentifier(port.Id) || !inputIds.Add(port.Id!) ||
                    string.IsNullOrWhiteSpace(port.Name) ||
                    (port.PortType is not null && (string.IsNullOrWhiteSpace(port.PortType) || port.PortType.Length > 128)) ||
                    inputs.Count >= 32)
                    throw new InvalidDataException("Invalid input port definition.");
                if (!TryPortCategory(port.Category, category, out var portCategory))
                    throw new InvalidDataException("Invalid input port category.");
                JsonElement? defaultValue = port.DefaultValue.ValueKind == JsonValueKind.Undefined
                    ? null : port.DefaultValue.Clone();
                var portType = port.PortType ?? node.PortType;
                if (node.Runtime is not null && portCategory == GraphPortCategory.Parameter &&
                    (portType is not ("Float" or "FloatX2" or "FloatX3" or "FloatX4" or "Color") ||
                     defaultValue is { } numericDefault && !NodeParameter.IsValidValue(
                         portType switch { "Float" => "float", "FloatX2" => "vector2", "FloatX3" => "vector3",
                             "FloatX4" => "vector4", _ => "color" }, numericDefault)))
                    throw new InvalidDataException("Custom parameter input ports require a numeric type and valid default.");
                if (node.Runtime is not null && portCategory == GraphPortCategory.Signal && portType != "Signal")
                    throw new InvalidDataException("Signal input ports must use the Signal type.");
                inputs.Add(new GraphInputPortDefinition(port.Id!, port.Name, port.PortType, portCategory, defaultValue));
            }
        }
        List<GraphOutputPortDefinition>? outputs = null;
        if (node.OutputPorts is not null)
        {
            outputs = [];
            var outputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in node.OutputPorts)
            {
                if (port is null || !IsIdentifier(port.Id) || !outputIds.Add(port.Id!) ||
                    string.IsNullOrWhiteSpace(port.Name) ||
                    (port.PortType is not null && (string.IsNullOrWhiteSpace(port.PortType) || port.PortType.Length > 128)) ||
                    outputs.Count >= 32)
                    throw new InvalidDataException("Invalid output port definition.");
                if (!TryPortCategory(port.Category, category, out var portCategory))
                    throw new InvalidDataException("Invalid output port category.");
                var portType = port.PortType ?? node.PortType;
                if (node.Runtime is not null && portCategory == GraphPortCategory.Signal && portType != "Signal")
                    throw new InvalidDataException("Signal output ports must use the Signal type.");
                outputs.Add(new GraphOutputPortDefinition(port.Id!, port.Name, port.PortType, portCategory));
            }
            if (outputs.Count == 0 && node.TypeId != Guid.Parse("57980d8e-701f-443c-91f6-8db56a09d2fc") &&
                !(BuiltInIds.TryGetValue(node.TypeId, out var builtInKind) &&
                  (GroupBoundarySchema.IsOutput(new GraphNodeKind(builtInKind)) ||
                   builtInKind is "OutputProperty" or "OutputSignal")))
                throw new InvalidDataException("Output port definitions must not be empty.");
        }
        var parameterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in node.Parameters ?? [])
        {
            if (parameter is null || !IsIdentifier(parameter.Id) || !parameterIds.Add(parameter.Id!) ||
                string.IsNullOrWhiteSpace(parameter.Name) || string.IsNullOrWhiteSpace(parameter.Type) ||
                parameter.Type.Length > 128 || !NodeParameter.IsValidValue(parameter.Type, parameter.DefaultValue))
                throw new InvalidDataException("Invalid node parameter definition.");
            if (parameter.Type.StartsWith("enum:", StringComparison.Ordinal) &&
                (!GraphEnumDefinition.TryGetTypeId(parameter.Type, out var enumId) ||
                 !GraphEnumDefinition.All.TryGetValue(enumId, out var enumDefinition) ||
                 !enumDefinition.Values.Any(item => item.Value == parameter.DefaultValue.GetInt32())))
                throw new InvalidDataException("Unknown enum or invalid default enum value.");
            NodeParameterEditorDefinition? editor = null;
            if (parameter.Editor is { } config)
            {
                if (string.IsNullOrWhiteSpace(config.Kind) || config.Kind.Length > 128 ||
                    (config.Step is { } step && (!double.IsFinite(step) || step <= 0)) ||
                    (config.Minimum is { } minimum && !double.IsFinite(minimum)) ||
                    (config.Maximum is { } maximum && !double.IsFinite(maximum)))
                    throw new InvalidDataException("Invalid parameter editor definition.");
                if (config.Kind == "radio" && parameter.Type != "bool")
                    throw new InvalidDataException("Radio editor requires a bool parameter.");
                if (config.Kind == "slider" &&
                    (parameter.Type is not ("float" or "int") ||
                     config.Minimum is not { } min || config.Maximum is not { } max || min >= max ||
                     (parameter.Type == "int" && (min < int.MinValue || max > int.MaxValue ||
                         Math.Truncate(min) != min || Math.Truncate(max) != max ||
                         (config.Step is { } intStep && Math.Truncate(intStep) != intStep)))))
                    throw new InvalidDataException("Invalid slider editor definition.");
                editor = new NodeParameterEditorDefinition(config.Kind, config.Minimum, config.Maximum, config.Step);
            }
            if (parameter.EnabledWhen is { Expected.ValueKind: JsonValueKind.Undefined })
                throw new InvalidDataException("Parameter enable condition requires a value.");
            var condition = parameter.EnabledWhen is { } when
                ? new NodeParameterCondition(when.Parameter ?? "", when.Expected.Clone()) : null;
            parameters.Add(new NodeParameterDefinition(parameter.Id!, parameter.Name, parameter.Type,
                parameter.DefaultValue.Clone())
            {
                Editor = editor, EnabledWhen = condition, DisplayNameKey = parameter.DisplayNameKey
            });
        }
        foreach (var parameter in parameters)
        {
            if (parameter.EnabledWhen is not { } condition) continue;
            var dependency = parameters.FirstOrDefault(item => item.Id == condition.Parameter);
            if (dependency is null || dependency.Id == parameter.Id ||
                !NodeParameter.IsValidValue(dependency.Type, condition.Expected))
                throw new InvalidDataException("Invalid parameter enable condition.");
        }
        if (node.TypeId == Guid.Parse("8f4bc759-b775-41ab-a7da-2ac5c17e29ed") &&
            (node.Kind != "Comment" ||
             !parameters.Any(item => item.Id == "text" && item.Type == "string") ||
             !parameters.Any(item => item.Id == "width" && item.Type == "float") ||
             !parameters.Any(item => item.Id == "height" && item.Type == "float") ||
             !parameters.Any(item => item.Id == "image" && item.Type == "commentImage")))
            throw new InvalidDataException("Invalid built-in comment definition.");
        GraphRuntimeDefinition? runtime = null;
        if (node.Runtime is { } runtimeFile)
        {
            if (runtimeFile.Kind != "custom" || runtimeFile.AbiVersion < 1 ||
                runtimeFile.Execution is not ("parameter" or "signal") ||
                (runtimeFile.Execution == "parameter" &&
                 (inputs?.Any(port => port.Category == GraphPortCategory.Signal) == true ||
                  outputs?.Any(port => port.Category == GraphPortCategory.Signal) == true)) ||
                (runtimeFile.Execution == "signal" &&
                 outputs?.Any(port => port.Category == GraphPortCategory.Signal) != true))
                throw new InvalidDataException("Invalid custom runtime declaration.");
            var dataInputCount = inputs?.Count(port => port.Category == GraphPortCategory.Parameter) ?? 0;
            var signalInputCount = inputs?.Count(port => port.Category == GraphPortCategory.Signal) ?? 0;
            var dataOutputCount = outputs?.Count(port => port.Category == GraphPortCategory.Parameter) ?? 0;
            var signalOutputCount = outputs?.Count(port => port.Category == GraphPortCategory.Signal) ?? 0;
            if (dataInputCount > 4 || signalInputCount > 1 ||
                (runtimeFile.Execution == "parameter" &&
                 (dataOutputCount != 1 || signalOutputCount != 0 ||
                  !IsRuntimeNumericType(outputs!.Single(port => port.Category == GraphPortCategory.Parameter).PortType ?? node.PortType))) ||
                (runtimeFile.Execution == "signal" &&
                 (dataOutputCount != 0 || signalOutputCount is < 1 or > 4)))
                throw new InvalidDataException("Custom runtime ports do not match the execution declaration.");
            runtime = new GraphRuntimeDefinition(runtimeFile.Kind, runtimeFile.AbiVersion, runtimeFile.Execution);
        }
        ids.Add(node.TypeId);
        names.Add(qualifiedName);
        var kind = new GraphNodeKind(BuiltInIds.GetValueOrDefault(node.TypeId) ?? node.TypeId.ToString("D"));
        definitions.Add(new GraphNodeDefinition(kind, node.TypeId, node.Namespace, node.Name, node.PortType, category, node.Group)
        {
            TypeName = node.Kind!,
            DisplayNameKey = node.DisplayNameKey,
            GroupNameKey = node.GroupNameKey,
            Parameters = parameters.AsReadOnly(),
            InputPorts = inputs?.AsReadOnly(),
            OutputPorts = outputs?.AsReadOnly(),
            Runtime = runtime
        });
    }

    private static bool TryPortCategory(string? value, GraphPortCategory fallback, out GraphPortCategory category)
    {
        category = fallback;
        return value is null || Enum.TryParse(value, ignoreCase: false, out category) && Enum.IsDefined(category);
    }

    private static bool IsRuntimeNumericType(string type) =>
        type is "Float" or "FloatX2" or "FloatX3" or "FloatX4" or "Color";

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private sealed class NodeFile
    {
        public int SchemaVersion { get; set; }
        public Guid TypeId { get; set; }
        public string? Namespace { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? DisplayNameKey { get; set; }
        public string? GroupNameKey { get; set; }
        public string? Group { get; set; }
        public string? PortType { get; set; }
        public string? PortCategory { get; set; }
        public List<NodeParameterFile>? Parameters { get; set; }
        public List<InputPortFile>? InputPorts { get; set; }
        public List<OutputPortFile>? OutputPorts { get; set; }
        public RuntimeFile? Runtime { get; set; }
    }

    private sealed class InputPortFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? PortType { get; set; }
        public string? Category { get; set; }
        public JsonElement DefaultValue { get; set; }
    }

    private sealed class OutputPortFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? PortType { get; set; }
        public string? Category { get; set; }
    }

    private sealed class RuntimeFile
    {
        public string? Kind { get; set; }
        public int AbiVersion { get; set; }
        public string? Execution { get; set; }
    }

    private sealed class NodeParameterFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? DisplayNameKey { get; set; }
        public string? Type { get; set; }
        public JsonElement DefaultValue { get; set; }
        public ParameterEditorFile? Editor { get; set; }
        public ParameterConditionFile? EnabledWhen { get; set; }
    }

    private sealed class ParameterConditionFile
    {
        public string? Parameter { get; set; }
        [JsonPropertyName("equals")]
        public JsonElement Expected { get; set; }
    }

    private sealed class ParameterEditorFile
    {
        public string? Kind { get; set; }
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }
        public double? Step { get; set; }
    }
}

public sealed record GraphInputPortDefinition(
    string Id, string Name, string? PortType = null, GraphPortCategory? Category = null,
    JsonElement? DefaultValue = null);
public sealed record GraphOutputPortDefinition(
    string Id, string Name, string? PortType = null, GraphPortCategory? Category = null);
