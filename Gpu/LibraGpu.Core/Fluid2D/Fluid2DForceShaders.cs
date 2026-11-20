namespace LibraGpu.Core;

public static partial class Fluid2DShaders
{
    private const string ForceCommon = """
uint LibraFluidNoiseHash(uint value)
{
    value ^= value >> 16; value *= 0x7feb352du;
    value ^= value >> 15; value *= 0x846ca68bu;
    return value ^ (value >> 16);
}
float LibraFluidNoiseCorner(int2 cell, uint seed)
{
    uint value = LibraFluidNoiseHash(uint(cell.x) ^ LibraFluidNoiseHash(uint(cell.y)) ^ LibraFluidNoiseHash(seed));
    return float(value & 0x00ffffffu) / 8388607.5 - 1.0;
}
float3 LibraFluidNoiseDerivative(float2 position, uint seed)
{
    if (max(abs(position.x), abs(position.y)) > 1048576.0) return float3(0, 0, 0);
    float2 lower = floor(position), f = position - lower;
    int2 cell = int2(lower);
    float2 t = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float2 derivative = 30.0 * f * f * (f - 1.0) * (f - 1.0);
    float a = LibraFluidNoiseCorner(cell, seed), b = LibraFluidNoiseCorner(cell + int2(1, 0), seed);
    float c = LibraFluidNoiseCorner(cell + int2(0, 1), seed), d = LibraFluidNoiseCorner(cell + int2(1, 1), seed);
    float value = lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y);
    return float3(value, lerp(b - a, d - c, t.y) * derivative.x,
        lerp(c - a, d - b, t.x) * derivative.y);
}
float2 LibraFluidAuthoredForce(int2 cell, float density, float temperature)
{
    float2 position = ((float2(cell) + float2(0.5, 0.5)) / LibraFluidGrid.xy - float2(0.5, 0.5)) * LibraFluidDomain.xy;
    float2 total = float2(0, 0);
    int count = int(LibraFluidForceNodes[0].x);
    float2 values[8];
    for (int reset = 0; reset < 8; ++reset) values[reset] = float2(0, 0);
    for (int node = 0; node < 8; ++node)
    {
        if (node >= count) break;
        float4 info = LibraFluidForceNodes[1 + node * 3];
        float4 noise = LibraFluidForceNodes[2 + node * 3];
        float4 animation = LibraFluidForceNodes[3 + node * 3];
        int kind = int(info.x);
        int inputA = int(animation.y) - 1, inputB = int(animation.z) - 1;
        float2 a = inputA >= 0 && inputA < node ? values[clamp(inputA, 0, 7)] : float2(0, 0);
        float2 b = inputB >= 0 && inputB < node ? values[clamp(inputB, 0, 7)] : float2(0, 0);
        float2 result = float2(0, 0);
        if (kind == 0) result = info.yz * info.w;
        else if (kind == 3) result = position;
        else if (kind == 4) result = float2(LibraFluidForceNodes[0].y, LibraFluidForceNodes[0].y);
        else if (kind == 5) result = float2(density, density);
        else if (kind == 6) result = float2(temperature, temperature);
        else if (kind == 7) result = (a + b) * info.w;
        else if (kind == 8) result = (a * b) * info.w;
        else if (kind == 9) result = sin(a) * info.w;
        else if (kind == 10)
        {
            float2 t = clamp((a - info.y) / max(info.z - info.y, 0.000001), 0.0, 1.0);
            result = t * t * (3.0 - 2.0 * t) * info.w;
        }
        else if (kind == 1 || kind == 2)
        {
            float2 noisePosition = inputA >= 0 ? a : position;
            float time = inputB >= 0 ? b.x : LibraFluidForceNodes[0].y;
            float2 animated = noisePosition + time * animation.x * float2(1, 0.61803399);
            float2 force = float2(0, 0);
            float weight = 0.0, amplitude = 1.0, frequency = noise.x;
            for (int octave = 0; octave < 8; ++octave)
            {
                if (octave >= int(noise.z)) break;
                uint seed = LibraFluidNoiseHash(uint(noise.y) ^ uint(octave));
                float3 n = LibraFluidNoiseDerivative(animated * frequency, seed);
                if (kind == 2) force += amplitude * frequency * float2(n.z, -n.y);
                else force += amplitude * float2(n.x, LibraFluidNoiseDerivative(animated * frequency, seed ^ 0x9e3779b9u).x);
                weight += amplitude; amplitude *= noise.w; frequency *= 2.0;
            }
            result = info.w * force / max(weight, 0.000001);
        }
        values[node] = result;
        if (animation.w > 0.5) total += result;
    }
    return total;
}
""";
}
