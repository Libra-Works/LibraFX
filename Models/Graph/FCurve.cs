namespace LiberaFx.Models.Graph;

public enum CurveInterpolation
{
    Constant,
    Linear,
    Cubic
}

public sealed record FCurveKey(
    Guid Id,
    double Frame,
    double Value,
    double InSlope,
    double OutSlope,
    CurveInterpolation Interpolation);

public sealed record FCurveSnapshot(IReadOnlyList<FCurveKey> Keys);

public static class FCurve
{
    public static FCurveSnapshot CreateDefault() => new([
        new(Guid.NewGuid(), 0, 0, 0, 0, CurveInterpolation.Linear),
        new(Guid.NewGuid(), 60, 1, 0, 0, CurveInterpolation.Linear)
    ]);

    public static void Validate(FCurveSnapshot curve)
    {
        if (curve.Keys is null || curve.Keys.Count > 10000)
        {
            throw new InvalidDataException("The F-Curve has invalid keyframes.");
        }

        var ids = new HashSet<Guid>();
        var previousFrame = double.NegativeInfinity;
        foreach (var key in curve.Keys)
        {
            if (key is null || key.Id == Guid.Empty || !ids.Add(key.Id) ||
                !double.IsFinite(key.Frame) || Math.Abs(key.Frame) > 1_000_000 ||
                !double.IsFinite(key.Value) || Math.Abs(key.Value) > 1_000_000 ||
                !double.IsFinite(key.InSlope) || Math.Abs(key.InSlope) > 1_000_000 ||
                !double.IsFinite(key.OutSlope) || Math.Abs(key.OutSlope) > 1_000_000 ||
                !Enum.IsDefined(key.Interpolation) || key.Frame <= previousFrame)
            {
                throw new InvalidDataException("The F-Curve contains an invalid or overlapping keyframe.");
            }

            previousFrame = key.Frame;
        }
    }

    public static double Evaluate(FCurveSnapshot curve, double frame)
    {
        var keys = curve.Keys;
        if (keys.Count == 0) return 0;
        if (frame <= keys[0].Frame) return keys[0].Value;
        if (frame >= keys[^1].Frame) return keys[^1].Value;
        var low = 0;
        var high = keys.Count - 1;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (frame <= keys[middle].Frame) high = middle;
            else low = middle;
        }
        {
            var left = keys[low];
            var right = keys[high];
            var span = right.Frame - left.Frame;
            var t = (frame - left.Frame) / span;
            return left.Interpolation switch
            {
                CurveInterpolation.Constant => frame < right.Frame ? left.Value : right.Value,
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
