using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibraGpu.Core;

public sealed record Fluid2DSettings
{
    public int Version { get; init; } = 1;
    public uint Width { get; init; } = 128;
    public uint Height { get; init; } = 128;
    public float DomainWidth { get; init; } = 1;
    public float DomainHeight { get; init; } = 1;
    public uint PressureIterations { get; init; } = 40;
    public float MaximumStep { get; init; } = 1f / 60;
    public uint MaximumSubsteps { get; init; } = 4;
    public float VelocityDissipation { get; init; } = 0.1f;
    public float DensityDissipation { get; init; } = 0.2f;
    public bool SourceEnabled { get; init; } = true;
    public float SourceX { get; init; } = 0.5f;
    public float SourceY { get; init; } = 0.12f;
    public float SourceRadius { get; init; } = 0.08f;
    public float SourceDensity { get; init; } = 10;
    public float SourceVelocityX { get; init; }
    public float SourceVelocityY { get; init; } = 2;
    public float Buoyancy { get; init; } = 1;
    public float ColorR { get; init; } = 1;
    public float ColorG { get; init; } = 1;
    public float ColorB { get; init; } = 1;
    public float ColorA { get; init; } = 1;
}

public enum Fluid2DField
{
    Velocity, VelocityScratch, Dye, DyeScratch, Divergence, Pressure, PressureScratch
}

public enum Fluid2DPass
{
    Clear, AdvectVelocity, ApplyForces, Divergence, Jacobi, ProjectVelocity, AdvectDye, Publish, RenderDye
}

public sealed record Fluid2DResource(string Id, string Kind, string Format, uint Width, uint Height, ulong Bytes);
public sealed record Fluid2DDispatch(Fluid2DPass Pass, Fluid2DField? Read0 = null,
    Fluid2DField? Read1 = null, Fluid2DField? Write0 = null, Fluid2DField? Write1 = null)
{
    public string EntryPoint => "LibraFluid" + Pass;
}

public sealed record Fluid2DFrame(uint Substeps, float StepDelta, double DiscardedDelta);
public sealed record Fluid2DExportMetadata(int Version, string Kind, string Purpose, bool RuntimeExecuted,
    string ContentHash, string SourceHash, ShaderFormat Format, Fluid2DSettings Settings,
    IReadOnlyList<Fluid2DResource> Resources, IReadOnlyList<Fluid2DDispatch> Passes, int UniformBufferBytes);

public static class Fluid2DCompiler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = true,
        MaxDepth = 8
    };

    public static void Validate(Fluid2DSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Version != 1 || settings.Width is < 8 or > 1024 || settings.Height is < 8 or > 1024 ||
            settings.PressureIterations is < 1 or > 128 || settings.MaximumSubsteps is < 1 or > 16 ||
            !Range(settings.MaximumStep, 0.0001f, 0.05f) ||
            !Range(settings.DomainWidth, 0.001f, 10000) || !Range(settings.DomainHeight, 0.001f, 10000) ||
            !Range(settings.VelocityDissipation, 0, 100) || !Range(settings.DensityDissipation, 0, 100) ||
            !Range(settings.SourceX, 0, 1) || !Range(settings.SourceY, 0, 1) ||
            !Range(settings.SourceRadius, 0.0001f, 10000) || !Range(settings.SourceDensity, 0, 1000) ||
            !Range(settings.SourceVelocityX, -1000, 1000) || !Range(settings.SourceVelocityY, -1000, 1000) ||
            !Range(settings.Buoyancy, -100, 100) || !Range(settings.ColorR, 0, 1) ||
            !Range(settings.ColorG, 0, 1) || !Range(settings.ColorB, 0, 1) || !Range(settings.ColorA, 0, 1))
        {
            throw new ArgumentException("Invalid or unsupported 2D fluid settings.", nameof(settings));
        }
    }

    private static bool Range(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    public static string Serialize(Fluid2DSettings settings)
    {
        Validate(settings);
        return JsonSerializer.Serialize(settings, Json);
    }

    public static Fluid2DSettings Parse(string json)
    {
        if (json is null || json.Length > 16384)
        {
            throw new ArgumentException("2D fluid settings exceed the input limit.", nameof(json));
        }
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("2D fluid settings must be an object.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException("Duplicate 2D fluid settings property.");
            }
        }
        var settings = JsonSerializer.Deserialize<Fluid2DSettings>(json, Json)
            ?? throw new JsonException("2D fluid settings are missing.");
        Validate(settings);
        return settings;
    }

    public static IReadOnlyList<Fluid2DResource> Resources(Fluid2DSettings settings)
    {
        Validate(settings);
        var resources = Enum.GetValues<Fluid2DField>().Select(field =>
            new Fluid2DResource(field.ToString(), "StorageBuffer", "Float4", settings.Width, settings.Height,
                (ulong)settings.Width * settings.Height * 16)).ToList();
        resources.Add(new("DyeOutput", "StorageSampledTexture2D", "RGBA16Float", settings.Width, settings.Height,
            (ulong)settings.Width * settings.Height * 8));
        return resources.AsReadOnly();
    }

    public static IReadOnlyList<Fluid2DDispatch> Step(Fluid2DSettings settings)
    {
        Validate(settings);
        var passes = new List<Fluid2DDispatch>
        {
            new(Fluid2DPass.AdvectVelocity, Fluid2DField.Velocity, Write0: Fluid2DField.VelocityScratch),
            new(Fluid2DPass.ApplyForces, Fluid2DField.VelocityScratch, Fluid2DField.Dye, Fluid2DField.Velocity),
            new(Fluid2DPass.Divergence, Fluid2DField.Velocity, Write0: Fluid2DField.Divergence),
            new(Fluid2DPass.Clear, Write0: Fluid2DField.Pressure)
        };
        var pressure = Fluid2DField.Pressure;
        var scratch = Fluid2DField.PressureScratch;
        for (var index = 0u; index < settings.PressureIterations; index++)
        {
            passes.Add(new(Fluid2DPass.Jacobi, pressure, Fluid2DField.Divergence, scratch));
            (pressure, scratch) = (scratch, pressure);
        }
        passes.Add(new(Fluid2DPass.ProjectVelocity, Fluid2DField.Velocity, pressure, Fluid2DField.VelocityScratch));
        passes.Add(new(Fluid2DPass.AdvectDye, Fluid2DField.Dye, Fluid2DField.VelocityScratch, Fluid2DField.DyeScratch));
        passes.Add(new(Fluid2DPass.Publish, Fluid2DField.VelocityScratch, Fluid2DField.DyeScratch,
            Fluid2DField.Velocity, Fluid2DField.Dye));
        passes.Add(new(Fluid2DPass.RenderDye, Fluid2DField.Dye));
        return passes.AsReadOnly();
    }

    public static Fluid2DFrame Frame(Fluid2DSettings settings, double delta)
    {
        Validate(settings);
        if (!double.IsFinite(delta) || delta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }
        if (delta == 0)
        {
            return new(0, 0, 0);
        }
        var duration = Math.Min(delta, (double)settings.MaximumStep * settings.MaximumSubsteps);
        var substeps = (uint)Math.Clamp(Math.Ceiling(duration / settings.MaximumStep), 1, settings.MaximumSubsteps);
        return new(substeps, (float)(duration / substeps), delta - duration);
    }

    public static float[] Uniforms(Fluid2DSettings settings, float delta)
    {
        Validate(settings);
        if (!Range(delta, 0, settings.MaximumStep))
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }
        return
        [
            settings.Width, settings.Height, delta, 0,
            settings.DomainWidth, settings.DomainHeight, settings.VelocityDissipation, settings.DensityDissipation,
            settings.SourceX, settings.SourceY, settings.SourceRadius, settings.SourceDensity,
            settings.SourceVelocityX, settings.SourceVelocityY, settings.Buoyancy, settings.SourceEnabled ? 1 : 0,
            settings.ColorR, settings.ColorG, settings.ColorB, settings.ColorA
        ];
    }

    public static Fluid2DExportMetadata Metadata(Fluid2DSettings settings, ShaderFormat format)
    {
        var json = Serialize(settings);
        var source = Fluid2DShaders.EmitHlsl(format);
        return new(1, "Fluid2D", "SourceExport", false, ShaderEmitter.Hash(json), ShaderEmitter.Hash(source),
            format, settings, Resources(settings), Step(settings), 80);
    }
}
