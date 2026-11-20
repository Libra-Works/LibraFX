using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibraGpu.Core;

public enum Fluid2DModel { Smoke, FreeSurface, ShallowWater, Combustion }

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Vorticity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Fluid2DModel Model { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Gravity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? InitialWaterDepth { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MaximumWaterSpeed { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? FuelBurnRate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? IgnitionTemperature { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? HeatYield { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? CoolingRate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? SourceTemperature { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Fluid2DForceNode>? Forces { get; init; }
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
    Clear, AdvectVelocity, ApplyForces, Divergence, Jacobi, ProjectVelocity, AdvectDye, Publish, RenderDye,
    InitializeWater, ShallowWater, RenderFields, RenderColor
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
    IReadOnlyList<Fluid2DResource> Resources, IReadOnlyList<Fluid2DDispatch> Passes, int UniformBufferBytes)
{
    public CompiledGpuResourcePlan? ResourcePlan { get; init; }
}

public static class Fluid2DCompiler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = true,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static void Validate(Fluid2DSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Fluid2DForceCompiler.Validate(settings.Forces);
        if (settings.Version != 1 || settings.Width is < 8 or > 1024 || settings.Height is < 8 or > 1024 ||
            settings.PressureIterations is < 1 or > 128 || settings.MaximumSubsteps is < 1 or > 64 ||
            !Enum.IsDefined(settings.Model) ||
            !Range(settings.Gravity ?? 9.81f, .001f, 100) ||
            !Range(settings.InitialWaterDepth ?? .25f, .0001f, 1000) ||
            !Range(settings.MaximumWaterSpeed ?? 4, .001f, 1000) ||
            !Range(settings.FuelBurnRate ?? 4, 0, 100) ||
            !Range(settings.IgnitionTemperature ?? .5f, .001f, 100) ||
            !Range(settings.HeatYield ?? 2, 0, 100) ||
            !Range(settings.CoolingRate ?? 1, 0, 100) ||
            !Range(settings.SourceTemperature ?? 1, 0, 100) ||
            !Range(settings.MaximumStep, 0.0001f, 0.05f) ||
            !Range(settings.DomainWidth, 0.001f, 10000) || !Range(settings.DomainHeight, 0.001f, 10000) ||
            !Range(settings.VelocityDissipation, 0, 100) || !Range(settings.DensityDissipation, 0, 100) ||
            !Range(settings.SourceX, 0, 1) || !Range(settings.SourceY, 0, 1) ||
            !Range(settings.SourceRadius, 0.0001f, 10000) || !Range(settings.SourceDensity, 0, 1000) ||
            !Range(settings.SourceVelocityX, -1000, 1000) || !Range(settings.SourceVelocityY, -1000, 1000) ||
            !Range(settings.Buoyancy, -100, 100) || !Range(settings.Vorticity, 0, 100) || !Range(settings.ColorR, 0, 1) ||
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
        resources.Add(new("FieldOutput", "StorageSampledTexture2D", "RGBA32Float", settings.Width, settings.Height,
            (ulong)settings.Width * settings.Height * 16));
        resources.Add(new("ColorOutput", "StorageSampledTexture2D", "RGBA16Float", settings.Width, settings.Height,
            (ulong)settings.Width * settings.Height * 8));
        return resources.AsReadOnly();
    }

    public static IReadOnlyList<Fluid2DDispatch> Step(Fluid2DSettings settings)
    {
        Validate(settings);
        if (settings.Model == Fluid2DModel.ShallowWater)
            return new Fluid2DDispatch[]
            {
                new(Fluid2DPass.ShallowWater, Fluid2DField.Dye, Write0: Fluid2DField.DyeScratch),
                new(Fluid2DPass.Publish, Fluid2DField.DyeScratch, Fluid2DField.DyeScratch,
                    Fluid2DField.Velocity, Fluid2DField.Dye),
                new(Fluid2DPass.RenderDye, Fluid2DField.Dye),
                new(Fluid2DPass.RenderFields, Fluid2DField.Velocity, Fluid2DField.Dye),
                new(Fluid2DPass.RenderColor, Fluid2DField.Dye)
            };
        var passes = new List<Fluid2DDispatch>
        {
            new(Fluid2DPass.AdvectVelocity, Fluid2DField.Velocity, Write0: Fluid2DField.VelocityScratch),
            new(Fluid2DPass.ApplyForces, Fluid2DField.VelocityScratch, Fluid2DField.Dye, Fluid2DField.Velocity),
            new(Fluid2DPass.Divergence, Fluid2DField.Velocity, Fluid2DField.Dye, Fluid2DField.Divergence)
        };
        if (settings.Model != Fluid2DModel.FreeSurface) passes.Add(new(Fluid2DPass.Clear, Write0: Fluid2DField.Pressure));
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
        passes.Add(new(Fluid2DPass.RenderFields, Fluid2DField.Velocity, Fluid2DField.Dye));
        passes.Add(new(Fluid2DPass.RenderColor, Fluid2DField.Dye));
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
        var maximumStep = MaximumStableStep(settings);
        var duration = Math.Min(delta, maximumStep * settings.MaximumSubsteps);
        var substeps = (uint)Math.Clamp(Math.Ceiling(duration / maximumStep), 1, settings.MaximumSubsteps);
        return new(substeps, (float)(duration / substeps), delta - duration);
    }

    public static double MaximumStableStep(Fluid2DSettings settings)
    {
        var step = (double)settings.MaximumStep;
        if (settings.Model is not (Fluid2DModel.FreeSurface or Fluid2DModel.ShallowWater)) return step;
        var speed = (double)(settings.MaximumWaterSpeed ?? 4);
        if (settings.Model == Fluid2DModel.ShallowWater)
            speed += Math.Sqrt((settings.Gravity ?? 9.81f) * (settings.InitialWaterDepth ?? .25f) * 16);
        // Two-dimensional CFL bound; all water kernels bound their velocity by this speed.
        return Math.Min(step, .4 / (speed * (settings.Width / (double)settings.DomainWidth + settings.Height / (double)settings.DomainHeight)));
    }

    /// <summary>One substep, with versioned field contents and an explicit visualization consumer.</summary>
    public static GpuResourcePlan ResourcePlan(Fluid2DSettings settings)
    {
        Validate(settings);
        var resources = Enum.GetValues<Fluid2DField>().Select(field => new GpuFlowResource
        {
            Id = field.ToString(), Kind = GpuFlowResourceKind.StorageBuffer,
            Format = GpuResourceFormat.Float4, Width = settings.Width * settings.Height,
            Lifetime = field is Fluid2DField.Velocity or Fluid2DField.Dye || settings.Model == Fluid2DModel.FreeSurface && field is Fluid2DField.Pressure or Fluid2DField.PressureScratch
                ? GpuResourceLifetime.Persistent : GpuResourceLifetime.Temporary,
            Initialization = field is Fluid2DField.Velocity or Fluid2DField.Dye || settings.Model == Fluid2DModel.FreeSurface && field is Fluid2DField.Pressure or Fluid2DField.PressureScratch
                ? GpuResourceInitialization.Zero : GpuResourceInitialization.Undefined
        }).ToList();
        resources.Add(new()
        {
            Id = "DyeOutput", Kind = GpuFlowResourceKind.Texture2D, Format = GpuResourceFormat.Rgba16Float,
            Width = settings.Width, Height = settings.Height, Lifetime = GpuResourceLifetime.Persistent,
            Initialization = GpuResourceInitialization.Zero
        });
        resources.Add(new()
        {
            Id = "FieldOutput", Kind = GpuFlowResourceKind.Texture2D, Format = GpuResourceFormat.Rgba32Float,
            Width = settings.Width, Height = settings.Height, Lifetime = GpuResourceLifetime.Persistent,
            Initialization = GpuResourceInitialization.Zero
        });
        resources.Add(new()
        {
            Id = "ColorOutput", Kind = GpuFlowResourceKind.Texture2D, Format = GpuResourceFormat.Rgba16Float,
            Width = settings.Width, Height = settings.Height, Lifetime = GpuResourceLifetime.Persistent,
            Initialization = GpuResourceInitialization.Zero
        });
        var versions = resources.ToDictionary(r => r.Id, _ => 0u, StringComparer.Ordinal);
        var passes = new List<GpuResourcePass>();
        foreach (var dispatch in Step(settings))
        {
            var uses = new List<GpuResourceUse>();
            if (dispatch.Read0 is { } read0) Read("Read0", read0);
            if (dispatch.Read1 is { } read1) Read("Read1", read1);
            if (dispatch.Write0 is { } write0) Write("Write0", write0);
            if (dispatch.Write1 is { } write1) Write("Write1", write1);
            if (dispatch.Pass == Fluid2DPass.RenderDye)
                uses.Add(new("Output", "DyeOutput", ++versions["DyeOutput"], GpuResourceAccess.StorageWrite));
            if (dispatch.Pass == Fluid2DPass.RenderFields)
                uses.Add(new("Output", "FieldOutput", ++versions["FieldOutput"], GpuResourceAccess.StorageWrite));
            if (dispatch.Pass == Fluid2DPass.RenderColor)
                uses.Add(new("Output", "ColorOutput", ++versions["ColorOutput"], GpuResourceAccess.StorageWrite));
            passes.Add(new() { Id = $"step-{passes.Count:D4}", Operation = dispatch.EntryPoint, Uses = uses });

            void Read(string binding, Fluid2DField field) =>
                uses.Add(new(binding, field.ToString(), versions[field.ToString()], GpuResourceAccess.StorageRead));
            void Write(string binding, Fluid2DField field) =>
                uses.Add(new(binding, field.ToString(), ++versions[field.ToString()], GpuResourceAccess.StorageWrite));
        }
        passes.Add(new()
        {
            Id = "visualize", Operation = "SampleDyeOutput",
            Uses = [new("Texture", "DyeOutput", versions["DyeOutput"], GpuResourceAccess.Sample),
                new("Fields", "FieldOutput", versions["FieldOutput"], GpuResourceAccess.Sample)]
        });
        passes.Add(new()
        {
            Id = "expose-color", Operation = "SampleColorOutput",
            Uses = [new("Texture", "ColorOutput", versions["ColorOutput"], GpuResourceAccess.Sample)]
        });
        return new() { Resources = resources, Passes = passes };
    }

    public static float[] Uniforms(Fluid2DSettings settings, float delta, double time = 0)
    {
        Validate(settings);
        if (!Range(delta, 0, settings.MaximumStep))
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }
        float[] values =
        [
            settings.Width, settings.Height, delta, settings.Vorticity,
            settings.DomainWidth, settings.DomainHeight, settings.VelocityDissipation, settings.DensityDissipation,
            settings.SourceX, settings.SourceY, settings.SourceRadius, settings.SourceDensity,
            settings.SourceVelocityX, settings.SourceVelocityY, settings.Buoyancy, settings.SourceEnabled ? 1 : 0,
            settings.ColorR, settings.ColorG, settings.ColorB, settings.ColorA,
            (float)settings.Model, settings.Gravity ?? 9.81f, settings.InitialWaterDepth ?? .25f,
            settings.Model == Fluid2DModel.Combustion ? settings.SourceTemperature ?? 1 : settings.MaximumWaterSpeed ?? 4,
            settings.FuelBurnRate ?? 4, settings.IgnitionTemperature ?? .5f, settings.HeatYield ?? 2, settings.CoolingRate ?? 1
        ];
        return [.. values, .. Fluid2DForceCompiler.Uniforms(settings.Forces, time)];
    }

    public static Fluid2DExportMetadata Metadata(Fluid2DSettings settings, ShaderFormat format)
    {
        var json = Serialize(settings);
        var source = Fluid2DShaders.EmitHlsl(format);
        return new(2, "Fluid2D", "SourceExport", false, ShaderEmitter.Hash(json), ShaderEmitter.Hash(source),
            format, settings, Resources(settings), Step(settings), 512)
        {
            ResourcePlan = GpuResourcePlanCompiler.Compile(ResourcePlan(settings))
        };
    }
}
