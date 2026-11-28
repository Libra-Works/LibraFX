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
    CurveInterpolation Interpolation,
    double InWeight = FCurve.DefaultTangentWeight,
    double OutWeight = FCurve.DefaultTangentWeight);

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
        var inWeight = TryNumber(root, "InWeight", out var parsedInWeight) ? parsedInWeight : FCurve.DefaultTangentWeight;
        var outWeight = TryNumber(root, "OutWeight", out var parsedOutWeight) ? parsedOutWeight : FCurve.DefaultTangentWeight;
        return new FCurveKey(id, hasSeconds ? timeSeconds : legacyFrame / LegacyFramesPerSecond, value,
            Number(root, "InSlope") * slopeScale, Number(root, "OutSlope") * slopeScale, interpolation,
            inWeight, outWeight);
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
        if (Math.Abs(value.InWeight - FCurve.DefaultTangentWeight) > 1e-9)
        {
            writer.WriteNumber("InWeight", value.InWeight);
        }
        if (Math.Abs(value.OutWeight - FCurve.DefaultTangentWeight) > 1e-9)
        {
            writer.WriteNumber("OutWeight", value.OutWeight);
        }
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
    public const double DefaultTangentWeight = 1.0 / 3.0;
    public const double SplitPairToleranceSeconds = 5e-4;

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
                !double.IsFinite(key.InWeight) || key.InWeight < 0.01 || key.InWeight > 0.99 ||
                !double.IsFinite(key.OutWeight) || key.OutWeight < 0.01 || key.OutWeight > 0.99 ||
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
                CurveInterpolation.Cubic => EvaluateCubic(left, right, span, t),
                _ => throw new InvalidDataException("Unknown interpolation.")
            };
        }

    }

    private static double EvaluateCubic(FCurveKey left, FCurveKey right, double span, double t)
    {
        if (Math.Abs(left.OutWeight - DefaultTangentWeight) < 1e-9 &&
            Math.Abs(right.InWeight - DefaultTangentWeight) < 1e-9)
        {
            return (2 * t * t * t - 3 * t * t + 1) * left.Value +
                   (t * t * t - 2 * t * t + t) * span * left.OutSlope +
                   (-2 * t * t * t + 3 * t * t) * right.Value +
                   (t * t * t - t * t) * span * right.InSlope;
        }

        var w0 = Math.Clamp(left.OutWeight, 0.01, 0.99);
        var w1 = Math.Clamp(right.InWeight, 0.01, 0.99);
        var x1 = w0;
        var x2 = 1.0 - w1;
        var lo = 0.0;
        var hi = 1.0;
        var u = t;
        for (var iter = 0; iter < 8; iter++)
        {
            var inv = 1.0 - u;
            var x = 3 * inv * inv * u * x1 + 3 * inv * u * u * x2 + u * u * u;
            var err = x - t;
            if (Math.Abs(err) < 1e-9)
            {
                break;
            }
            if (err < 0)
            {
                lo = u;
            }
            else
            {
                hi = u;
            }
            var dx = 3 * inv * inv * x1 + 6 * inv * u * (x2 - x1) + 3 * u * u * (1.0 - x2);
            var next = dx > 1e-9 ? u - err / dx : (lo + hi) * 0.5;
            u = next > lo && next < hi ? next : (lo + hi) * 0.5;
        }

        var oneMinusU = 1.0 - u;
        var y0 = left.Value;
        var y1 = left.Value + w0 * span * left.OutSlope;
        var y2 = right.Value - w1 * span * right.InSlope;
        var y3 = right.Value;
        return oneMinusU * oneMinusU * oneMinusU * y0 +
               3 * oneMinusU * oneMinusU * u * y1 +
               3 * oneMinusU * u * u * y2 +
               u * u * u * y3;
    }

    public static IReadOnlyList<FCurveKey> ExpandWeightedCurves(IReadOnlyList<FCurveKey> keys)
    {
        var hasWeighted = false;
        for (var i = 0; i < keys.Count - 1; i++)
        {
            if (keys[i].Interpolation == CurveInterpolation.Cubic &&
                (Math.Abs(keys[i].OutWeight - DefaultTangentWeight) > 1e-9 ||
                 Math.Abs(keys[i + 1].InWeight - DefaultTangentWeight) > 1e-9))
            {
                hasWeighted = true;
                break;
            }
        }
        if (!hasWeighted)
        {
            return keys;
        }

        const int segments = 16;
        var result = new List<FCurveKey>(keys.Count * 4);
        for (var i = 0; i < keys.Count; i++)
        {
            var current = keys[i];
            if (i < keys.Count - 1 && current.Interpolation == CurveInterpolation.Cubic &&
                (Math.Abs(current.OutWeight - DefaultTangentWeight) > 1e-9 ||
                 Math.Abs(keys[i + 1].InWeight - DefaultTangentWeight) > 1e-9))
            {
                var next = keys[i + 1];
                var span = next.TimeSeconds - current.TimeSeconds;
                var w0 = Math.Clamp(current.OutWeight, 0.01, 0.99);
                var w1 = Math.Clamp(next.InWeight, 0.01, 0.99);
                var x1 = w0;
                var x2 = 1.0 - w1;
                var y0 = current.Value;
                var y1 = current.Value + w0 * span * current.OutSlope;
                var y2 = next.Value - w1 * span * next.InSlope;
                var y3 = next.Value;
                result.Add(current);
                for (var s = 1; s < segments; s++)
                {
                    var u = s / (double)segments;
                    var inv = 1.0 - u;
                    var x = 3 * inv * inv * u * x1 + 3 * inv * u * u * x2 + u * u * u;
                    var y = inv * inv * inv * y0 + 3 * inv * inv * u * y1 + 3 * inv * u * u * y2 + u * u * u * y3;
                    var dx = 3 * inv * inv * x1 + 6 * inv * u * (x2 - x1) + 3 * u * u * (1.0 - x2);
                    var dy = 3 * inv * inv * (y1 - y0) + 6 * inv * u * (y2 - y1) + 3 * u * u * (y3 - y2);
                    var slope = Math.Clamp(dy / (span * Math.Max(1e-9, dx)), -1_000_000, 1_000_000);
                    result.Add(new FCurveKey(Guid.NewGuid(), current.TimeSeconds + span * x, y, slope, slope, CurveInterpolation.Cubic));
                }
            }
            else
            {
                result.Add(current);
            }
        }
        return result;
    }
}
