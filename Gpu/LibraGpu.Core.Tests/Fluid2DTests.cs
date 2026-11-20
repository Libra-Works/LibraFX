using System.Buffers.Binary;
using System.Text.Json;
using LibraGpu.Core;

internal static partial class Program
{
    private static void RunFluid2DTests()
    {
        Run("fluid resource hazards and pressure parity", () =>
        {
            foreach (var iterations in new uint[] { 1, 2, 39, 40, 128 })
            {
                var settings = new Fluid2DSettings { Width = 24, Height = 40, PressureIterations = iterations };
                var passes = Fluid2DCompiler.Step(settings);
                foreach (var pass in passes)
                {
                    var reads = new[] { pass.Read0, pass.Read1 }.OfType<Fluid2DField>();
                    var writes = new[] { pass.Write0, pass.Write1 }.OfType<Fluid2DField>().ToArray();
                    Check(!reads.Intersect(writes).Any() && writes.Distinct().Count() == writes.Length,
                        "A fluid dispatch aliases its immutable inputs or outputs.");
                }
                var pressure = passes.Last(pass => pass.Pass == Fluid2DPass.Jacobi).Write0;
                Check(passes.Single(pass => pass.Pass == Fluid2DPass.ProjectVelocity).Read1 == pressure,
                    "Pressure projection must read the final ping-pong version, including odd iteration counts.");
                Check(passes[^2] is { Pass: Fluid2DPass.Publish, Write0: Fluid2DField.Velocity, Write1: Fluid2DField.Dye } &&
                    passes[^1] is { Pass: Fluid2DPass.RenderDye, Read0: Fluid2DField.Dye },
                    "Only published state may become the displayed density.");
                var resources = Fluid2DCompiler.Resources(settings);
                Check(resources.Sum(resource => (decimal)resource.Bytes) == settings.Width * settings.Height * 120m,
                    "Both ping-pong storage and the native output texture must fit the declared allocation.");
            }
        });
        Run("fluid bounded stepping and uniform isolation", () =>
        {
            var settings = new Fluid2DSettings { MaximumStep = 0.02f, MaximumSubsteps = 3 };
            Check(Fluid2DCompiler.Frame(settings, 0) is { Substeps: 0, StepDelta: 0, DiscardedDelta: 0 },
                "Paused frames must not inject density or run compute.");
            var frame = Fluid2DCompiler.Frame(settings, 2);
            Check(frame.Substeps == 3 && frame.StepDelta <= settings.MaximumStep &&
                Math.Abs(frame.DiscardedDelta + frame.StepDelta * frame.Substeps - 2) < 1e-6,
                "A hitch must not cause unbounded GPU work or silently advance skipped time.");
            var uniforms = Fluid2DCompiler.Uniforms(settings with { SourceEnabled = false }, frame.StepDelta);
            Check(uniforms.Length == 20 && uniforms[15] == 0 && uniforms[11] == settings.SourceDensity,
                "Source gating must disable the source without changing the saved density or buoyancy.");
            uniforms[0] = 0;
            Check(Fluid2DCompiler.Uniforms(settings, frame.StepDelta)[0] == settings.Width,
                "One emitter's uniform upload must not modify another emitter.");
        });
        Run("fluid settings validation and persistence", () =>
        {
            var settings = new Fluid2DSettings { Width = 48, Height = 72, SourceEnabled = false, ColorB = 0.25f };
            var json = Fluid2DCompiler.Serialize(settings);
            Check(Fluid2DCompiler.Parse(json) == settings, "Fluid settings must roundtrip without losing source state.");
            foreach (var invalid in new[]
            {
                settings with { Version = 2 }, settings with { Width = 0 }, settings with { Height = 1025 },
                settings with { MaximumStep = float.NaN }, settings with { SourceRadius = 0 },
                settings with { DomainHeight = float.PositiveInfinity }, settings with { PressureIterations = 0 },
                settings with { MaximumSubsteps = uint.MaxValue }, settings with { ColorA = -1 }
            })
            {
                try
                {
                    Fluid2DCompiler.Step(invalid);
                    throw new InvalidOperationException("Invalid fluid allocation/solver settings were accepted.");
                }
                catch (ArgumentException)
                {
                }
            }
            foreach (var invalid in new[] { """{"version":1,"version":2}""", """{"width":48,"Width":64}""",
                """{"Width":48}""", """{"width":"48"}""", """{"unknown":true}""", "[]", "null" })
            {
                try
                {
                    Fluid2DCompiler.Parse(invalid);
                    throw new InvalidOperationException("Malformed fluid settings were accepted.");
                }
                catch (JsonException)
                {
                }
            }
            foreach (var delta in new[] { double.NaN, double.PositiveInfinity, -0.01 })
            {
                try
                {
                    Fluid2DCompiler.Frame(settings, delta);
                    throw new InvalidOperationException("Invalid frame delta was accepted.");
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
        });
    }

    private static void ExportFluid2D(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "LibraFluid2D.compute"), Fluid2DShaders.EmitHlsl(ShaderFormat.UnityCompute));
        File.WriteAllText(Path.Combine(directory, "LibraFluid2D.usf"), Fluid2DShaders.EmitHlsl(ShaderFormat.Usf));
        File.WriteAllText(Path.Combine(directory, "LibraFluid2DShaderSource.h"), Fluid2DShaders.EmitGodotHeader());
        foreach (var pass in Enum.GetValues<Fluid2DPass>())
        {
            File.WriteAllText(Path.Combine(directory, "LibraFluid" + pass + ".glsl"), Fluid2DShaders.EmitGlsl(pass));
        }
        var settings = new Fluid2DSettings { Width = 24, Height = 40, PressureIterations = 39 };
        File.WriteAllText(Path.Combine(directory, "fluid-settings.json"), Fluid2DCompiler.Serialize(settings));
        using var stream = File.Create(Path.Combine(directory, "fluid-contract.bin"));
        var bytes = new byte[4];
        void Write(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }
        Write(settings.Width);
        Write(settings.Height);
        Write(settings.PressureIterations);
        var uniforms = Fluid2DCompiler.Uniforms(settings, settings.MaximumStep);
        foreach (var value in uniforms)
        {
            Write(BitConverter.SingleToUInt32Bits(value));
        }
        var passes = Fluid2DCompiler.Step(settings);
        Write((uint)passes.Count);
        foreach (var pass in passes)
        {
            Write((uint)pass.Pass);
            Write(unchecked((uint)(pass.Read0 is { } read0 ? (int)read0 : -1)));
            Write(unchecked((uint)(pass.Read1 is { } read1 ? (int)read1 : -1)));
            Write(unchecked((uint)(pass.Write0 is { } write0 ? (int)write0 : -1)));
            Write(unchecked((uint)(pass.Write1 is { } write1 ? (int)write1 : -1)));
        }
    }
}
