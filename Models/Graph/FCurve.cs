using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Models.Graph;

public enum CurveInterpolation
{
    Constant,
    Linear,
    Cubic
}

[JsonConverter(typeof(FCurveKeyJsonConverter))]
public sealed record FCurveKey(
    Guid Id,
    double TimeSeconds,
    double Value,
    double InSlope,
    double OutSlope,
    CurveInterpolation Interpolation);

public sealed class FCurveKeyJsonConverter : JsonConverter<FCurveKey>
{
    private const double LegacyFramesPerSecond = 60;

    public override FCurveKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var id = GuidValue(root, "Id");
        var value = Number(root, "Value");
        var interpolationElement = Property(root, "Interpolation");
        var interpolation = JsonSerializer.Deserialize<CurveInterpolation>(interpolationElement.GetRawText(), options);
        var hasSeconds = TryNumber(root, "TimeSeconds", out var timeSeconds);
        var legacyFrame = hasSeconds ? 0 : Number(root, "Frame");
        var slopeScale = hasSeconds ? 1 : LegacyFramesPerSecond;
        return new FCurveKey(id, hasSeconds ? timeSeconds : legacyFrame / LegacyFramesPerSecond, value,
            Number(root, "InSlope") * slopeScale, Number(root, "OutSlope") * slopeScale, interpolation);
    }

    public override void Write(Utf8JsonWriter writer, FCurveKey value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", value.Id);
        writer.WriteNumber("TimeSeconds", value.TimeSeconds);
        writer.WriteNumber("Value", value.Value);
        writer.WriteNumber("InSlope", value.InSlope);
        writer.WriteNumber("OutSlope", value.OutSlope);
        writer.WritePropertyName("Interpolation");
        JsonSerializer.Serialize(writer, value.Interpolation, options);
        writer.WriteEndObject();
    }

    private static Guid GuidValue(JsonElement root, string name)
    {
        var property = Property(root, name);
        return property.ValueKind == JsonValueKind.String && property.TryGetGuid(out var value)
            ? value
            : throw new JsonException($"FCurve {name} must be a GUID.");
    }

    private static double Number(JsonElement root, string name)
    {
        var property = Property(root, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value)
            ? value
            : throw new JsonException($"FCurve {name} must be a number.");
    }

    private static bool TryNumber(JsonElement root, string name, out double value)
    {
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out value))
                    return true;
        value = 0;
        return false;
    }

    private static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
        throw new JsonException($"FCurve {name} is missing.");
    }
}

public sealed record FCurveSnapshot(IReadOnlyList<FCurveKey> Keys);

public static class FCurve
{
    public static FCurveSnapshot CreateDefault() => new([
        new(Guid.NewGuid(), 0, 0, 0, 0, CurveInterpolation.Linear),
        new(Guid.NewGuid(), 1, 1, 0, 0, CurveInterpolation.Linear)
    ]);

    public static void Validate(FCurveSnapshot curve)
    {
        if (curve.Keys is null || curve.Keys.Count > 10000)
        {
            throw new InvalidDataException("The F-Curve has invalid keyframes.");
        }

        var ids = new HashSet<Guid>();
        var previousTime = double.NegativeInfinity;
        foreach (var key in curve.Keys)
        {
            if (key is null || key.Id == Guid.Empty || !ids.Add(key.Id) ||
                !double.IsFinite(key.TimeSeconds) || Math.Abs(key.TimeSeconds) > 1_000_000 ||
                !double.IsFinite(key.Value) || Math.Abs(key.Value) > 1_000_000 ||
                !double.IsFinite(key.InSlope) || Math.Abs(key.InSlope) > 1_000_000 ||
                !double.IsFinite(key.OutSlope) || Math.Abs(key.OutSlope) > 1_000_000 ||
                !Enum.IsDefined(key.Interpolation) || key.TimeSeconds <= previousTime)
            {
                throw new InvalidDataException("The F-Curve contains an invalid or overlapping keyframe.");
            }

            previousTime = key.TimeSeconds;
        }
    }

    public static double Evaluate(FCurveSnapshot curve, double timeSeconds)
    {
        var keys = curve.Keys;
        if (keys.Count == 0) return 0;
        if (timeSeconds <= keys[0].TimeSeconds) return keys[0].Value;
        if (timeSeconds >= keys[^1].TimeSeconds) return keys[^1].Value;
        var low = 0;
        var high = keys.Count - 1;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (timeSeconds <= keys[middle].TimeSeconds) high = middle;
            else low = middle;
        }
        {
            var left = keys[low];
            var right = keys[high];
            var span = right.TimeSeconds - left.TimeSeconds;
            var t = (timeSeconds - left.TimeSeconds) / span;
            return left.Interpolation switch
            {
                CurveInterpolation.Constant => timeSeconds < right.TimeSeconds ? left.Value : right.Value,
                CurveInterpolation.Linear => left.Value + (right.Value - left.Value) * t,
                CurveInterpolation.Cubic =>
                    (2 * t * t * t - 3 * t * t + 1) * left.Value +
                    (t * t * t - 2 * t * t + t) * span * left.OutSlope +
                    (-2 * t * t * t + 3 * t * t) * right.Value +
                    (t * t * t - t * t) * span * right.InSlope,
                _ => throw new InvalidDataException("Unknown interpolation.")
            };
        }

    }
}
