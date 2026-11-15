using System.Numerics;

namespace LibraGpu.Core;

/// <summary>Reference semantics for the guarded math extension. Shaders apply scalar functions per vector lane.
/// New float operations return zero for nonfinite input/result; existing 0..17 operations retain their prior policies.</summary>
public static class GpuMath
{
    private static float Finite(float x) => float.IsFinite(x) ? x : 0;
    private static bool Finite(Vector3 x) => float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z);
    private static bool Finite(Vector2 x) => float.IsFinite(x.X) && float.IsFinite(x.Y);
    private static bool Finite(Vector4 x) => float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z) && float.IsFinite(x.W);
    public static float Abs(float a) => float.IsFinite(a) ? MathF.Abs(a) : 0;
    public static int Abs(int a) => a == int.MinValue ? a : Math.Abs(a);
    public static uint Abs(uint a) => a;
    public static float Floor(float a) => float.IsFinite(a) ? MathF.Floor(a) : 0;
    public static float Ceil(float a) => float.IsFinite(a) ? MathF.Ceiling(a) : 0;
    public static float Round(float a) => float.IsFinite(a) ? MathF.Round(a, MidpointRounding.ToEven) : 0;
    public static float Frac(float a) => float.IsFinite(a) ? a - MathF.Floor(a) : 0;
    public static float Sign(float a) => float.IsFinite(a) ? MathF.Sign(a) : 0;
    public static int Sign(int a) => Math.Sign(a);
    public static uint Sign(uint a) => a > 0 ? 1u : 0u;
    public static float Mod(float a, float b) => float.IsFinite(a) && float.IsFinite(b) && b != 0 ? Finite(a % b) : 0;
    public static int Mod(int a, int b) => b == 0 || a == int.MinValue && b == -1 ? 0 : a % b;
    public static uint Mod(uint a, uint b) => b == 0 ? 0 : a % b;
    public static float Sqrt(float a) => float.IsFinite(a) && a > 0 ? MathF.Sqrt(a) : 0;
    public static float Exp(float a) => float.IsFinite(a) ? Finite(MathF.Exp(a)) : 0;
    public static float Log(float a) => float.IsFinite(a) && a > 0 ? MathF.Log(a) : 0;
    public static float Pow(float a, float b)
    {
        if (!float.IsFinite(a) || !float.IsFinite(b)) return 0;
        if (a == 0) return b == 0 ? 1 : 0;
        if (a < 0 && b != MathF.Floor(b)) return 0;
        float v = Finite(MathF.Pow(MathF.Abs(a), b));
        return a < 0 && MathF.Abs(b) % 2 == 1 ? -v : v;
    }
    public static float Lerp(float a, float b, float amount)
        => float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(amount) ? Finite(a + (b - a) * amount) : 0;
    public static float Saturate(float a) => float.IsFinite(a) ? Math.Clamp(a, 0, 1) : 0;
    public static float Smoothstep(float edge0, float edge1, float a)
    {
        if (!float.IsFinite(edge0) || !float.IsFinite(edge1) || !float.IsFinite(a) || edge0 == edge1) return 0;
        float t = Saturate(Finite((a - edge0) / (edge1 - edge0)));
        return t * t * (3 - 2 * t);
    }
    public static float Remap(float a, Vector2 sourceRange, Vector2 targetRange)
    {
        if (!float.IsFinite(a) || !float.IsFinite(sourceRange.X) || !float.IsFinite(sourceRange.Y) ||
            !float.IsFinite(targetRange.X) || !float.IsFinite(targetRange.Y) || sourceRange.X == sourceRange.Y) return 0;
        return Finite(targetRange.X + (a - sourceRange.X) / (sourceRange.Y - sourceRange.X) * (targetRange.Y - targetRange.X));
    }
    public static float Dot(Vector3 a, Vector3 b) => Finite(a) && Finite(b) ? Finite(Vector3.Dot(a, b)) : 0;
    public static float Dot(Vector2 a, Vector2 b) => Finite(a) && Finite(b) ? Finite(Vector2.Dot(a, b)) : 0;
    public static float Dot(Vector4 a, Vector4 b) => Finite(a) && Finite(b) ? Finite(Vector4.Dot(a, b)) : 0;
    public static Vector3 Cross(Vector3 a, Vector3 b)
    {
        if (!Finite(a) || !Finite(b)) return Vector3.Zero;
        var value = Vector3.Cross(a, b); return Finite(value) ? value : Vector3.Zero;
    }
    public static float Distance(Vector3 a, Vector3 b) => Finite(a) && Finite(b) ? Finite(Vector3.Distance(a, b)) : 0;
    public static float Distance(Vector2 a, Vector2 b) => Finite(a) && Finite(b) ? Finite(Vector2.Distance(a, b)) : 0;
    public static float Distance(Vector4 a, Vector4 b) => Finite(a) && Finite(b) ? Finite(Vector4.Distance(a, b)) : 0;
    public static float Distance(float a, float b) => float.IsFinite(a) && float.IsFinite(b) ? Finite(MathF.Abs(a - b)) : 0;
}
