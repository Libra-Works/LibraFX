using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Models.Graph;

public sealed record ProceduralModifier
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Kind { get; init; }
    public int Axis { get; init; } = 1;
    public uint Flags { get; init; } = 1;
    public uint Count { get; init; } = 1;
    public uint Seed { get; init; } = 1;
    public float[] Values { get; init; } = new float[16];
}

public sealed record ProceduralPixelOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public uint Kind { get; init; }
    public uint InputA { get; init; }
    public uint InputB { get; init; }
    public uint Seed { get; init; } = 1;
    public float[] Values { get; init; } = new float[12];
}

/// <summary>Versioned authoring data. Resource identities live on graph edges, never numeric VM handles.</summary>
public sealed record ProceduralRecipe
{
    public int Version { get; init; } = 1;
    public int Shape { get; init; } = 16;
    public uint SegmentsU { get; init; } = 32;
    public uint SegmentsV { get; init; } = 8;
    public float[] Size { get; init; } = [1, 1, 1];
    public float InnerRadius { get; init; } = .6f;
    public float SweepDegrees { get; init; } = 360;
    public float Turns { get; init; } = 2;
    public float TubeRadius { get; init; } = .1f;
    public uint PivotMode { get; init; }
    public float[] Pivot { get; init; } = [0, 0, 0];
    public uint PivotVertex { get; init; }
    public bool Capped { get; init; } = true;
    public bool AnimatedBase { get; init; }
    public ProceduralModifier[] Modifiers { get; init; } = [];
    public int Resolution { get; init; } = 256;
    public ProceduralPixelOperation[] Operations { get; init; } = [];
    public uint OutputRegister { get; init; }
    public string Blend { get; init; } = "alpha";
    public bool DoubleSided { get; init; } = true;
    public float Emission { get; init; } = 1;
}

public static class ProceduralResourceSchema
{
    public const string ModelId = "89e9b18d-9ea2-48c0-bb26-a89000000001";
    public const string TextureId = "89e9b18d-9ea2-48c0-bb26-a89000000002";
    public const string MaterialId = "89e9b18d-9ea2-48c0-bb26-a89000000003";
    public const int MaximumRecipeCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static readonly string[] ShapeNames = ["Ribbon", "Slash", "Spiral", "Wave", "Ring", "Arc", "Disk", "Torus",
        "Cylinder", "Cone", "Flare", "Sphere", "Hemisphere", "Capsule", "Plane", "Box", "Cross", "Spike", "Leaf",
        "Swirl", "FlameRing", "DoubleHelix", "Coil", "Knot"];
    public static readonly string[] ModifierNames = ["Twist", "Bend", "Taper", "Wave", "Swirl", "Noise", "Round",
        "Inflate", "Transform", "Mirror", "Array", "Subdivide", "Solidify"];
    public static readonly string[] PixelNames = ["UV", "Constant", "Time", "TransformUV", "Linear", "Radial", "Ring",
        "Stripes", "Checker", "ValueNoise", "GradientNoise", "Cellular", "CellEdges", "Fractal", "Ridged", "Curl",
        "WarpUV", "Add", "Multiply", "Subtract", "Min", "Max", "Lerp", "Power", "Saturate", "Invert", "Smoothstep",
        "ColorRamp", "Channel", "AlphaMask", "TextureSample", "Output", "NormalMap"];
    public static string? Name(GraphNodeKind kind) => kind.Value switch
    {
        ModelId or "ProceduralModel" => "ProceduralModel",
        TextureId or "ProceduralTexture" => "ProceduralTexture",
        MaterialId or "ProceduralMaterial" => "ProceduralMaterial",
        _ => null
    };
    public static bool IsMetadata(GraphNodeKind kind) => Name(kind) is not null;
    public static bool Contains(GraphSnapshot graph) => graph.Nodes.Any(node => IsMetadata(node.Kind) ||
        node.ChildGraph is { } child && Contains(child));
    public static string Port(Guid stage, int block) => $"stage_{stage:N}_{block}";
    public static string Encode(ProceduralRecipe recipe) => JsonSerializer.Serialize(recipe, JsonOptions);
    public static ProceduralPixelOperation CreatePixel(uint kind, uint input)
    {
        var p = new float[12];
        switch (kind)
        {
            case 1: p[0] = p[1] = p[2] = p[3] = 1; break;
            case 3: p[0] = p[1] = 1; p[5] = p[6] = .5f; break;
            case 4: p[0] = 1; break;
            case 5: p[0] = p[1] = .5f; p[2] = 2; break;
            case 6: p[0] = p[1] = .5f; p[2] = .35f; p[3] = .05f; p[4] = .02f; break;
            case 7: p[0] = 8; break;
            case >= 8 and <= 15:
                p[0] = p[1] = 8;
                if (kind >= 9) { p[2] = .1f; p[3] = .05f; }
                if (kind is 13 or 14) { p[4] = 4; p[5] = .5f; }
                break;
            case 16: p[0] = .1f; p[1] = .5f; break;
            case 22: p[0] = .5f; break;
            case 23: p[0] = 2; break;
            case 26: p[1] = 1; break;
            case 27: p[4] = p[5] = p[6] = p[7] = p[9] = 1; break;
            case 32: p[0] = 2; p[1] = .005f; p[2] = 0; break;
        }
        return new() { Kind = kind, InputA = input, Values = p };
    }
    public static ProceduralRecipe Default(string kind) => kind == "ProceduralModel" ? new() { Shape = 14 } : new()
    {
        // An untouched producer can be read repeatedly before any recipe is
        // persisted. Its local stage identities must already be stable.
        Operations = [
            new() { Id = new("89e9b18d-9ea2-48c0-bb26-a89000000011"), Kind = 5, Values = [.5f, .5f, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0] },
            new() { Id = new("89e9b18d-9ea2-48c0-bb26-a89000000012"), Kind = 25, InputA = 1, Values = new float[12] },
            new() { Id = new("89e9b18d-9ea2-48c0-bb26-a89000000013"), Kind = 24, InputA = 2, Values = new float[12] }
        ],
        OutputRegister = 3
    };
    public static ProceduralRecipe Read(GraphNodeDefinition definition, IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        var text = parameters?.FirstOrDefault(p => p.Id == "proceduralState")?.Value;
        if (text is null || text.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(text.Value.GetString()))
        {
            return Default(Name(definition.Kind) ?? throw new InvalidDataException("Not a procedural producer."));
        }
        if (text.Value.ValueKind != JsonValueKind.String || text.Value.GetString()!.Length > MaximumRecipeCharacters)
        {
            throw new InvalidDataException("Invalid procedural recipe size or type.");
        }
        try
        {
            var recipe = JsonSerializer.Deserialize<ProceduralRecipe>(text.Value.GetString()!, JsonOptions)
                ?? throw new InvalidDataException("Missing procedural recipe.");
            Validate(recipe, Name(definition.Kind)!);
            return recipe;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid procedural recipe JSON.", error);
        }
    }
    public static ProceduralRecipe ReadEffective(GraphNodeDefinition definition, IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        var recipe = Read(definition, parameters);
        float[] Merge(Guid id, float[] defaults)
        {
            var result = (float[])defaults.Clone();
            for (var block = 0; block < result.Length / 4; ++block)
            {
                var saved = parameters?.SingleOrDefault(p => p.Id == Port(id, block));
                if (saved is null) continue;
                if (saved.Type != "vector4" || !NodeParameter.IsValidValue("vector4", saved.Value))
                    throw new InvalidDataException("Invalid procedural default block.");
                var components = saved.Value.EnumerateArray().ToArray();
                for (var c = 0; c < 4; ++c)
                {
                    if (!components[c].TryGetSingle(out var value) || !float.IsFinite(value))
                        throw new InvalidDataException("Procedural values must be finite Float32 numbers.");
                    result[block * 4 + c] = value;
                }
            }
            return result;
        }
        var effective = recipe with
        {
            Modifiers = recipe.Modifiers.Select(m => m with { Values = Merge(m.Id, m.Values) }).ToArray(),
            Operations = recipe.Operations.Select(op => op with { Values = Merge(op.Id, op.Values) }).ToArray()
        };
        Validate(effective, Name(definition.Kind)!);
        return effective;
    }
    public static void Validate(ProceduralRecipe r, string kind)
    {
        static bool Finite(float[]? values, int count) => values is not null && values.Length == count &&
            values.All(x => float.IsFinite(x) && Math.Abs(x) <= 1000000);
        if (r.Version != 1 || r.Modifiers is null || r.Operations is null || !Finite(r.Size, 3) ||
            !Finite(r.Pivot, 3) || r.Modifiers.Length > 16 || r.Operations.Length > 32 ||
            r.Shape is < 0 or >= 24 || r.SegmentsU is < 1 or > 256 || r.SegmentsV is < 1 or > 128 ||
            r.Size.Any(x => x < 0 || x > 10000) || !float.IsFinite(r.InnerRadius) || r.InnerRadius is < 0 or > 1 ||
            !float.IsFinite(r.SweepDegrees) || r.SweepDegrees is <= 0 or > 360 ||
            !float.IsFinite(r.Turns) || r.Turns is <= 0 or > 64 ||
            !float.IsFinite(r.TubeRadius) || r.TubeRadius is < 0 or > .5f || r.PivotMode > 7 ||
            r.Resolution is < 16 or > 1024 || (r.Resolution & (r.Resolution - 1)) != 0 ||
            r.Blend is not ("alpha" or "additive") || !float.IsFinite(r.Emission) || r.Emission is < 0 or > 64)
        {
            throw new InvalidDataException("Procedural recipe exceeds its supported limits.");
        }
        var ids = new HashSet<Guid>();
        foreach (var m in r.Modifiers)
        {
            if (m is null || m.Id == Guid.Empty || !ids.Add(m.Id) || m.Kind is < 0 or >= 13 || m.Axis is < 0 or > 2 ||
                (m.Flags & ~31u) != 0 || !Finite(m.Values, 16) || m.Count is < 1 or > 64 ||
                m.Count > (m.Kind == 10 ? 64u : m.Kind == 11 ? 3u : m.Kind == 5 ? 8u : 1u) ||
                m.Values[15] != 0 || m.Kind == 6 && (m.Values[0] is < 0 or > 1 || m.Values[1] < 0) ||
                m.Kind == 12 && (m.Values[0] < 0 || m.Values[1] is < -1 or > 1) ||
                m.Kind == 5 && (m.Values[1] < 0 || m.Values[4] is < 0 or > 1))
            {
                throw new InvalidDataException("Invalid procedural model modifier.");
            }
        }
        ids.Clear();
        if (kind == "ProceduralModel" && r.Operations.Length != 0 || kind != "ProceduralModel" && r.Modifiers.Length != 0)
            throw new InvalidDataException("Procedural producer contains operations from another resource domain.");
        for (var i = 0; i < r.Operations.Length; ++i)
        {
            var op = r.Operations[i];
            if (op is null || op.Id == Guid.Empty || !ids.Add(op.Id) || op.Kind >= (uint)PixelNames.Length || op.InputA > i || op.InputB > i ||
                !Finite(op.Values, 12) || op.Kind == 30 && kind != "ProceduralMaterial" ||
                op.Kind is 13 or 14 && (op.Values[4] is < 1 or > 8 || op.Values[4] != MathF.Floor(op.Values[4]) || op.Values[5] is < 0 or > 1) ||
                op.Kind == 28 && (op.Values[0] is < 0 or > 3 || op.Values[0] != MathF.Floor(op.Values[0])) ||
                op.Kind == 30 && (op.Values[0] is < 0 or > 3 || op.Values[0] != MathF.Floor(op.Values[0])))
            {
                throw new InvalidDataException("Invalid procedural pixel operation or forward reference.");
            }
        }
        if (kind != "ProceduralModel" && (r.Operations.Length == 0 || r.OutputRegister < 1 || r.OutputRegister > r.Operations.Length))
        {
            throw new InvalidDataException("Procedural pixel output register is invalid.");
        }
        if (kind == "ProceduralModel") ValidateModelBudget(r);
    }
    public static void ValidateModelBudget(ProceduralRecipe r, bool connected = false)
    {
        var u = (ulong)r.SegmentsU; var v = (ulong)(r.Shape == 15 ? r.SegmentsU : r.SegmentsV);
        var patches = r.Shape == 15 ? 6ul : r.Shape is 16 or 21 ? 2ul : 1ul;
        var vertices = (u + 1) * (v + 1) * patches;
        var triangles = 2 * u * v * patches; var edges = (3 * u * v + u + v) * patches;
        var boundary = 2 * (u + v) * patches;
        var tube = r.Shape is 21 or 22;
        var caps = r.Capped ? (r.Shape is 8 or 9 or 10 or 17 || tube ? 2 * patches : r.Shape == 12 ? 1ul : 0ul) : 0ul;
        var segments = tube ? v : u;
        vertices += caps * (segments + 2); triangles += caps * segments;
        edges += caps * (2 * segments + 1); boundary += caps * (segments + 2);
        if (r.PivotMode == 7 && r.PivotVertex >= vertices)
            throw new InvalidDataException("Pivot vertex is outside the undeformed source mesh.");
        var animated = connected || r.AnimatedBase || r.Modifiers.Any(m => (m.Flags & 16) != 0 ||
            (m.Flags & 1) != 0 && m.Kind is 3 or 5 && m.Values[3] != 0);
        var maxVertices = animated ? 16384ul : 65536ul; var maxTriangles = animated ? 32768ul : 131072ul;
        void Check()
        {
            if (vertices > maxVertices || triangles > maxTriangles)
                throw new InvalidDataException($"Procedural topology exceeds the {(animated ? "animated" : "static")} limit ({maxVertices} vertices, {maxTriangles} triangles).");
        }
        Check();
        foreach (var m in r.Modifiers.Where(m => (m.Flags & 1) != 0))
        {
            if (m.Kind == 9 && (m.Flags & 8) != 0 || m.Kind == 10)
            {
                var copies = m.Kind == 9 ? 2u : m.Count;
                vertices *= copies; triangles *= copies; edges *= copies; boundary *= copies;
            }
            if (m.Kind == 11)
            {
                for (var level = 0; level < m.Count; ++level)
                {
                    vertices += edges; edges = 2 * edges + 3 * triangles; triangles *= 4; boundary *= 2; Check();
                }
            }
            if (m.Kind == 12)
            {
                vertices = 2 * vertices + 4 * boundary; triangles = 2 * triangles + 2 * boundary;
                edges = 2 * edges + 5 * boundary; boundary *= 6;
            }
            Check();
        }
    }
    public static GraphNodeDefinition Effective(GraphNodeDefinition definition, IReadOnlyList<NodeParameterSnapshot>? parameters)
    {
        if (!IsMetadata(definition.Kind))
        {
            return definition;
        }
        var recipe = Read(definition, parameters);
        var ports = (definition.InputPorts ?? []).ToList();
        var values = definition.Parameters.ToList();
        void Add(Guid id, float[] data, string label)
        {
            for (var block = 0; block < data.Length / 4; ++block)
            {
                var key = Port(id, block);
                ports.Add(new(key, $"{label} · {block + 1}", "FloatX4"));
                values.Add(new(key, label, "vector4", JsonSerializer.SerializeToElement(data.Skip(block * 4).Take(4))) { Hidden = true });
            }
        }
        foreach (var (modifier, index) in recipe.Modifiers.Select((m, i) => (m, i)))
            Add(modifier.Id, modifier.Values, $"{index + 1}. {ModifierNames[modifier.Kind]}");
        foreach (var (op, index) in recipe.Operations.Select((op, i) => (op, i)))
            Add(op.Id, op.Values, $"{index + 1}. {PixelNames[op.Kind]}");
        return definition with { InputPorts = ports, Parameters = values };
    }
}
