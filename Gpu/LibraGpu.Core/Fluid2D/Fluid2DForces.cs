namespace LibraGpu.Core;

public enum Fluid2DForceKind { Acceleration, Noise, CurlNoise, CellPosition, Time, Density, Temperature, Add, Multiply, Sine, Smoothstep }

public sealed record Fluid2DForceNode
{
    public Guid Id { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? InputA { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? InputB { get; init; }
    public Fluid2DForceKind Kind { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Strength { get; init; } = 1;
    public float Frequency { get; init; } = 1;
    public uint Seed { get; init; }
    public uint Octaves { get; init; } = 3;
    public float Roughness { get; init; } = .5f;
    public float AnimationSpeed { get; init; }
}

public static class Fluid2DForceCompiler
{
    public const int MaximumNodes = 8;
    public const int UniformVectorCount = 25;

    public static void Validate(IReadOnlyList<Fluid2DForceNode>? nodes)
    {
        if (nodes is null) return;
        var ids = new HashSet<Guid>();
        if (nodes.Count > MaximumNodes) throw new ArgumentException("At most eight cell-force nodes are supported.");
        foreach (var node in nodes)
        {
            if (node is null || node.Id == Guid.Empty || !Enum.IsDefined(node.Kind) ||
                node.InputA is { } a && !ids.Contains(a) || node.InputB is { } b && !ids.Contains(b) || !ids.Add(node.Id) ||
                !Range(node.X, -1000, 1000) || !Range(node.Y, -1000, 1000) || !Range(node.Strength, 0, 100) ||
                !Range(node.Frequency, .001f, 1000) || node.Seed > 16777215 || node.Octaves is < 1 or > 8 ||
                !Range(node.Roughness, 0, 1) || !Range(node.AnimationSpeed, -100, 100))
                throw new ArgumentException("Invalid cell-force node identity or parameters.");
        }
    }

    public static float[] Uniforms(IReadOnlyList<Fluid2DForceNode>? nodes, double time = 0)
    {
        Validate(nodes);
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        var data = new float[UniformVectorCount * 4];
        data[0] = nodes?.Count ?? 0;
        data[1] = (float)(time % 4096);
        for (var i = 0; i < (nodes?.Count ?? 0); i++)
        {
            var node = nodes![i];
            var offset = 4 + i * 12;
            data[offset] = (float)node.Kind; data[offset + 1] = node.X;
            data[offset + 2] = node.Y; data[offset + 3] = node.Strength;
            data[offset + 4] = node.Frequency; data[offset + 5] = node.Seed;
            data[offset + 6] = node.Octaves; data[offset + 7] = node.Roughness;
            data[offset + 8] = node.AnimationSpeed;
            data[offset + 9] = node.InputA is { } a ? 1 + nodes!.ToList().FindIndex(value => value.Id == a) : 0;
            data[offset + 10] = node.InputB is { } b ? 1 + nodes!.ToList().FindIndex(value => value.Id == b) : 0;
            data[offset + 11] = node.Kind is not (Fluid2DForceKind.CellPosition or Fluid2DForceKind.Time or Fluid2DForceKind.Density or Fluid2DForceKind.Temperature) &&
                !nodes!.Any(value => value.InputA == node.Id || value.InputB == node.Id) ? 1 : 0;
        }
        return data;
    }

    private static bool Range(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
}
