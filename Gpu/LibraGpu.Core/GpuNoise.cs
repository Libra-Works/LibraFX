using System.Numerics;

namespace LibraGpu.Core;

public readonly record struct NoiseSample(float Value, Vector3 Derivative);

/// <summary>CPU reference for numerical validation of the original shader algorithms, not a particle-runtime fallback.
/// Inputs outside the finite domain return zero. Curl is the analytic curl of three scalar potentials.</summary>
public static class GpuNoise
{
    public const float CoordinateLimit = 1048576;
    public const int MaximumOctaves = 8;
    public static uint Hash(uint x)
    {
        unchecked { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; return x ^ (x >> 16); }
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float MaxAbs(Vector3 p) => MathF.Max(MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)), MathF.Abs(p.Z));
    private static Vector3 Lattice(int x, int y, int z, uint seed)
    {
        var h = Hash(unchecked((uint)x) ^ Hash(unchecked((uint)y) ^ 0x9e3779b9) ^ Hash(unchecked((uint)z) ^ 0x85ebca6b) ^ Hash(seed));
        uint direction = h % 6;
        float sign = (direction & 1) == 0 ? 1 : -1;
        return direction < 2 ? new(sign, 0, 0) : direction < 4 ? new(0, sign, 0) : new(0, 0, sign);
    }
    public static NoiseSample GradientNoiseDerivative3D(Vector3 p, uint seed = 0)
    {
        if (!Finite(p) || MaxAbs(p) > CoordinateLimit) return default;
        var cell = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        var f = p - cell;
        var t = f * f * f * (f * (f * 6 - new Vector3(15)) + new Vector3(10));
        var dt = 30 * f * f * (f - Vector3.One) * (f - Vector3.One);
        float value = 0; var gradient = Vector3.Zero;
        for (int corner = 0; corner < 8; corner++)
        {
            int x = corner & 1, y = (corner >> 1) & 1, z = (corner >> 2) & 1;
            var w = new Vector3(x != 0 ? t.X : 1 - t.X, y != 0 ? t.Y : 1 - t.Y, z != 0 ? t.Z : 1 - t.Z);
            var d = new Vector3(x != 0 ? dt.X : -dt.X, y != 0 ? dt.Y : -dt.Y, z != 0 ? dt.Z : -dt.Z);
            var g = Lattice((int)cell.X + x, (int)cell.Y + y, (int)cell.Z + z, seed);
            float v = Vector3.Dot(g, f - new Vector3(x, y, z)), weight = w.X * w.Y * w.Z;
            value += weight * v;
            gradient += weight * g + v * new Vector3(d.X * w.Y * w.Z, w.X * d.Y * w.Z, w.X * w.Y * d.Z);
        }
        return new(value, gradient);
    }
    public static float GradientNoise3D(Vector3 p, uint seed = 0) => Math.Clamp(GradientNoiseDerivative3D(p, seed).Value, -1, 1);
    private static bool FractalDomain(Vector3 p, int octaves, float gain)
        => octaves is >= 1 and <= MaximumOctaves && float.IsFinite(gain) && gain is >= 0 and <= 1 &&
            Finite(p) && MaxAbs(p) <= CoordinateLimit / (1 << (octaves - 1));
    public static NoiseSample FbmDerivative3D(Vector3 p, uint seed = 0, int octaves = 4, float gain = 0.5f)
    {
        if (!FractalDomain(p, octaves, gain)) return default;
        float value = 0, amplitude = 1, frequency = 1, weight = 0; var gradient = Vector3.Zero;
        for (int octave = 0; octave < MaximumOctaves; octave++)
            if (octave < octaves)
            {
                var n = GradientNoiseDerivative3D(p * frequency, Hash(seed ^ (uint)octave));
                value += amplitude * n.Value; gradient += amplitude * frequency * n.Derivative; weight += amplitude;
                amplitude *= gain; frequency *= 2;
            }
        return new(value / weight, gradient / weight);
    }
    public static float Fbm(Vector3 p, uint seed = 0, int octaves = 4, float gain = 0.5f) => Math.Clamp(FbmDerivative3D(p, seed, octaves, gain).Value, -1, 1);
    public static float Turbulence(Vector3 p, uint seed = 0, int octaves = 4, float gain = 0.5f)
    {
        if (!FractalDomain(p, octaves, gain)) return 0;
        float value = 0, amplitude = 1, frequency = 1, weight = 0;
        for (int octave = 0; octave < MaximumOctaves; octave++)
            if (octave < octaves)
            {
                value += amplitude * MathF.Abs(GradientNoiseDerivative3D(p * frequency, Hash(seed ^ (uint)octave)).Value);
                weight += amplitude; amplitude *= gain; frequency *= 2;
            }
        return Math.Clamp(value / weight, 0, 1);
    }
    public static Vector3 CurlNoise(Vector3 p, uint seed = 0, int octaves = 4, float gain = 0.5f)
    {
        if (!FractalDomain(p, octaves, gain)) return Vector3.Zero;
        var dx = FbmDerivative3D(p, Hash(seed ^ 0xa511e9b3), octaves, gain).Derivative;
        var dy = FbmDerivative3D(p, Hash(seed ^ 0x63d83595), octaves, gain).Derivative;
        var dz = FbmDerivative3D(p, Hash(seed ^ 0xb8d4c729), octaves, gain).Derivative;
        float weight = 0, frequencyWeight = 0, amplitude = 1, frequency = 1;
        for (int octave = 0; octave < MaximumOctaves; octave++)
            if (octave < octaves) { weight += amplitude; frequencyWeight += amplitude * frequency; amplitude *= gain; frequency *= 2; }
        return new Vector3(dz.Y - dy.Z, dx.Z - dz.X, dy.X - dx.Y) * (weight / (16 * frequencyWeight));
    }
}
