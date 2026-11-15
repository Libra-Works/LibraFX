using System.Numerics;
using System.Text.Json;

internal static partial class Program
{
    private static void Near(float a, float b, float tolerance, string message)
        => Check(float.IsFinite(a) && MathF.Abs(a - b) <= tolerance, $"{message}: {a} vs {b}");
    private static void RunExtensionTests()
    {
        Run("new math policies and four-lane lowering", () =>
        {
            var p = LibraGpu.Core.GpuSamples.CreateMathProbe();
            Check(LibraGpu.Core.GpuCompiler.Validate(p).Count == 0, "Math fixture validates");
            var c = LibraGpu.Core.GpuCompiler.Compile(p);
            Check(c.Kernels.Count == 18, "All common math extension operations");
            foreach (var op in LibraGpu.Core.GpuOperationRegistry.Operations.Keys.Where(o => (int)o >= 19))
            {
                var defaults = LibraGpu.Core.GpuOperationRegistry.GetDefaultInputs(op);
                Check(LibraGpu.Core.GpuOperationRegistry.TryInferType(op, defaults.Select(i => i.DefaultType).ToArray(), out _), $"Valid registry defaults {op}");
            }
            Check(!LibraGpu.Core.GpuOperationRegistry.TryInferType(LibraGpu.Core.GpuOp.Lerp,
                [LibraGpu.Core.GpuType.Float2, LibraGpu.Core.GpuType.Float3, LibraGpu.Core.GpuType.Float], out _), "Mixed dimensions rejected");
            Check(LibraGpu.Core.GpuOperationRegistry.TryInferType(LibraGpu.Core.GpuOp.Lerp,
                [LibraGpu.Core.GpuType.Float4, LibraGpu.Core.GpuType.Float, LibraGpu.Core.GpuType.Float], out var t) && t == LibraGpu.Core.GpuType.Float4, "Lerp scalar broadcast");
            Check(!LibraGpu.Core.GpuOperationRegistry.TryInferType(LibraGpu.Core.GpuOp.Cross,
                [LibraGpu.Core.GpuType.Float4, LibraGpu.Core.GpuType.Float4], out _), "Cross requires float3");
            Check(LibraGpu.Core.GpuOperationRegistry.TryInferType(LibraGpu.Core.GpuOp.Dot,
                [LibraGpu.Core.GpuType.Float4, LibraGpu.Core.GpuType.Float4], out t) && t == LibraGpu.Core.GpuType.Float, "Dot float4->float");
            var remapDefaults = LibraGpu.Core.GpuOperationRegistry.GetDefaultInputs(LibraGpu.Core.GpuOp.Remap, LibraGpu.Core.GpuType.Float4);
            Check(remapDefaults[0].DefaultType == LibraGpu.Core.GpuType.Float4 && remapDefaults[1].DefaultValue.SequenceEqual(new double[] { 0, 1 }), "Remap defaults keep ranges while adapting lanes");
            foreach (var kernel in p.Kernels)
            {
                var text = LibraGpu.Core.GpuDsl.Emit(kernel);
                var copy = LibraGpu.Core.GpuDsl.Compile(text.Text, kernel.Id, kernel.Stage, options: new()
                { NodeIdsBySpan = text.NodeIdsBySpan, WriteIdsBySpan = text.WriteIdsBySpan });
                Check(kernel.Nodes.All(n => copy.Nodes.Any(x => x.Id == n.Id && x.Op == n.Op && x.Type == n.Type)), $"Math graph/text identity {kernel.Id}");
            }
            DslReject("@Age = dot(1,2);");
            DslReject("@Color = cross(float4(1,2,3,4),float4(1,2,3,4));");
            DslReject("@Age = pow(2i,3i);");
            DslReject("@Age = remap(1,0,1);");
            DslReject("@Color = smoothstep(float2(0,0),float3(1,1,1),1);");
        });
        Run("guarded scalar math numerical edges", () =>
        {
            Near(LibraGpu.Core.GpuMath.Mod(-5.5f, 2), -1.5f, 0, "Float mod follows dividend sign");
            Check(LibraGpu.Core.GpuMath.Mod(-5, 2) == -1 && LibraGpu.Core.GpuMath.Mod(int.MinValue, -1) == 0, "Signed remainder and overflow guard");
            Check(LibraGpu.Core.GpuMath.Abs(int.MinValue) == int.MinValue, "Int abs wrapping minimum");
            Check(LibraGpu.Core.GpuMath.Sign(uint.MaxValue) == 1 && LibraGpu.Core.GpuMath.Mod(uint.MaxValue, 0) == 0, "Exact uint guards");
            foreach (var (a, b) in new[] { (2.5f, 2f), (3.5f, 4f), (-2.5f, -2f), (-3.5f, -4f) })
                Near(LibraGpu.Core.GpuMath.Round(a), b, 0, "Ties-to-even round");
            Near(LibraGpu.Core.GpuMath.Frac(-1.25f), 0.75f, 0, "Frac is floor-relative");
            Near(LibraGpu.Core.GpuMath.Pow(-2, 3), -8, 1e-5f, "Negative integer power");
            Check(LibraGpu.Core.GpuMath.Pow(-2, 0.5f) == 0 && LibraGpu.Core.GpuMath.Pow(0, 0) == 1 && LibraGpu.Core.GpuMath.Pow(0, -1) == 0, "Power domain");
            Check(LibraGpu.Core.GpuMath.Exp(100) == 0 && LibraGpu.Core.GpuMath.Sqrt(-1) == 0 && LibraGpu.Core.GpuMath.Log(0) == 0, "Overflow/domain sanitization");
            Near(LibraGpu.Core.GpuMath.Lerp(0, 10, 2), 20, 0, "Lerp not clamped");
            Near(LibraGpu.Core.GpuMath.Smoothstep(0, 1, 0.5f), 0.5f, 0, "Smoothstep midpoint");
            Check(LibraGpu.Core.GpuMath.Smoothstep(1, 1, 1) == 0, "Equal-edge smoothstep");
            Near(LibraGpu.Core.GpuMath.Remap(3, new(0, 1), new(10, 20)), 40, 0, "Remap not clamped");
            Check(LibraGpu.Core.GpuMath.Remap(3, new(0, 0), new(10, 20)) == 0, "Degenerate remap");
            Near(LibraGpu.Core.GpuMath.Dot(new Vector3(1, 2, 3), new Vector3(4, 5, 6)), 32, 0, "Dot");
            Near(LibraGpu.Core.GpuMath.Dot(new Vector4(1, 2, 3, 4), new Vector4(5, 6, 7, 8)), 70, 0, "Four-component dot");
            Check(LibraGpu.Core.GpuMath.Cross(Vector3.UnitX, Vector3.UnitY) == Vector3.UnitZ, "Right-handed cross");
            foreach (var op in LibraGpu.Core.GpuOperationRegistry.Operations.Keys.Where(o => (int)o is >= 19 and <= 36 && o is not (LibraGpu.Core.GpuOp.Dot or LibraGpu.Core.GpuOp.Cross or LibraGpu.Core.GpuOp.Distance)))
                foreach (var a in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                    Check(ScalarMath(op, a, 2) == 0, $"Nonfinite math guard {op}");
        });
        Run("noise defaults types bounds and canonical identity", () =>
        {
            var kernel = LibraGpu.Core.GpuDsl.Compile("@Position = curlnoise(@Position); @Age = fbm(@Position); @Rotation = turbulence(@Position);", "k", LibraGpu.Core.KernelStage.Update);
            var p = One() with { Kernels = [kernel] };
            var c = LibraGpu.Core.GpuCompiler.Compile(p);
            Check(kernel.Nodes.Where(n => n.Op is LibraGpu.Core.GpuOp.Fbm or LibraGpu.Core.GpuOp.Turbulence or LibraGpu.Core.GpuOp.CurlNoise).All(n => n.Inputs.Count == 4), "DSL optional arguments materialized in IR");
            Check(LibraGpu.Core.GpuCompiler.Serialize(LibraGpu.Core.GpuCompiler.Parse(c.CanonicalJson)) == c.CanonicalJson, "New strings JSON roundtrip");
            var text = LibraGpu.Core.GpuDsl.Emit(kernel);
            var copy = LibraGpu.Core.GpuDsl.Compile(text.Text, "k", kernel.Stage, options: new() { NodeIdsBySpan = text.NodeIdsBySpan, WriteIdsBySpan = text.WriteIdsBySpan });
            Check(kernel.Nodes.All(n => copy.Nodes.Any(x => x.Id == n.Id && x.Op == n.Op)), "Noise graph/text same typed nodes");
            foreach (var source in new[] {"@Age=fbm(@Position,0u,0i,0.5);","@Age=turbulence(@Position,0u,9i,0.5);",
                "@Position=curlnoise(@Position,0u,4i,1.1);","@Age=fbm(@Position,0u,4,0.5);","@Age=gradientnoise3d(@Position,0);"})
                DslReject(source);
            JsonReject(c.CanonicalJson.Replace("\"Fbm\"", "\"UnknownFutureNoise\""), "InvalidJson");
            Check(c.EmitUsf().LineMappings.Any(m => m.Span is not null), "Noise source mappings");
            Check(LibraGpu.Core.GpuCompiler.Validate(LibraGpu.Core.GpuSamples.CreateNoiseGuardProbe()).Count == 0, "Computed invalid params are bounded runtime zeros");
        });
        Run("noise range determinism derivatives and lattice continuity", () =>
        {
            float maxDerivativeError = 0;
            bool seedVaries = false;
            foreach (uint seed in new[] { 0u, 7u, uint.MaxValue })
                for (int i = 0; i < 30; i++)
                {
                    var p = new Vector3(i * 0.173f - 2.11f, i * 0.239f + 0.417f, 1.73f - i * 0.127f);
                    var sample = LibraGpu.Core.GpuNoise.GradientNoiseDerivative3D(p, seed);
                    Check(sample.Value is >= -1 and <= 1, "Gradient noise range");
                    Check(LibraGpu.Core.GpuNoise.GradientNoise3D(p, seed) == LibraGpu.Core.GpuNoise.GradientNoise3D(p, seed), "Noise determinism");
                    seedVaries |= MathF.Abs(sample.Value - LibraGpu.Core.GpuNoise.GradientNoise3D(p, seed + 1)) > 1e-4f;
                    const float h = 0.001f;
                    foreach (var (axis, expected) in new[] { (Vector3.UnitX, sample.Derivative.X), (Vector3.UnitY, sample.Derivative.Y), (Vector3.UnitZ, sample.Derivative.Z) })
                    {
                        var central = (LibraGpu.Core.GpuNoise.GradientNoise3D(p + axis * h, seed) - LibraGpu.Core.GpuNoise.GradientNoise3D(p - axis * h, seed)) / (2 * h);
                        maxDerivativeError = MathF.Max(maxDerivativeError, MathF.Abs(central - expected));
                    }
                    for (int octaves = 1; octaves <= 8; octaves++)
                    {
                        float fbm = LibraGpu.Core.GpuNoise.Fbm(p, seed, octaves);
                        float turbulence = LibraGpu.Core.GpuNoise.Turbulence(p, seed, octaves);
                        var curl = LibraGpu.Core.GpuNoise.CurlNoise(p, seed, octaves);
                        Check(fbm is >= -1 and <= 1 && turbulence is >= 0 and <= 1, "Fractal output ranges");
                        Check(MathF.Abs(curl.X) <= 1 && MathF.Abs(curl.Y) <= 1 && MathF.Abs(curl.Z) <= 1, "Curl component bound (not vector normalization)");
                    }
                }
            Check(seedVaries && maxDerivativeError < 0.0015f, $"Seed variation and analytic derivatives {maxDerivativeError}");
            var left = LibraGpu.Core.GpuNoise.GradientNoiseDerivative3D(new(-0.0001f, 0.23f, 0.47f), 3);
            var right = LibraGpu.Core.GpuNoise.GradientNoiseDerivative3D(new(0.0001f, 0.23f, 0.47f), 3);
            Check(MathF.Abs(left.Value - right.Value) < 0.001f && Vector3.Distance(left.Derivative, right.Derivative) < 0.003f, "C1 lattice boundary");
        });
        Run("curl analytic field is divergence-free inside guarded domain", () =>
        {
            float max = 0;
            const float h = 0.001f;
            for (int i = 0; i < 25; i++)
            {
                var p = new Vector3(0.123f + i * 0.091f, 0.456f - i * 0.063f, -0.789f + i * 0.043f);
                var dx = (LibraGpu.Core.GpuNoise.CurlNoise(p + Vector3.UnitX * h, 7, 3) - LibraGpu.Core.GpuNoise.CurlNoise(p - Vector3.UnitX * h, 7, 3)) / (2 * h);
                var dy = (LibraGpu.Core.GpuNoise.CurlNoise(p + Vector3.UnitY * h, 7, 3) - LibraGpu.Core.GpuNoise.CurlNoise(p - Vector3.UnitY * h, 7, 3)) / (2 * h);
                var dz = (LibraGpu.Core.GpuNoise.CurlNoise(p + Vector3.UnitZ * h, 7, 3) - LibraGpu.Core.GpuNoise.CurlNoise(p - Vector3.UnitZ * h, 7, 3)) / (2 * h);
                max = MathF.Max(max, MathF.Abs(dx.X + dy.Y + dz.Z));
            }
            Check(max < 0.0015f, $"Curl central-difference divergence {max}");
            Console.WriteLine($"  measured max divergence {max:G6}, h={h}");
        });
        Run("noise nonfinite domain and bounded octave guards", () =>
        {
            foreach (var p in new[] { new Vector3(float.NaN, 0, 0), new(float.PositiveInfinity, 0, 0), new(1048577, 0, 0) })
            {
                Check(LibraGpu.Core.GpuNoise.GradientNoise3D(p) == 0 && LibraGpu.Core.GpuNoise.Fbm(p) == 0 &&
                    LibraGpu.Core.GpuNoise.Turbulence(p) == 0 && LibraGpu.Core.GpuNoise.CurlNoise(p) == Vector3.Zero, "Invalid noise domain zero");
            }
            foreach (int octave in new[] { int.MinValue, 0, 9, int.MaxValue })
                Check(LibraGpu.Core.GpuNoise.CurlNoise(new(1, 2, 3), 3, octave) == Vector3.Zero && LibraGpu.Core.GpuNoise.Fbm(new(1, 2, 3), 3, octave) == 0, "Octaves never unbounded");
            foreach (float gain in new[] { -1f, 1.1f, float.NaN, float.PositiveInfinity })
                Check(LibraGpu.Core.GpuNoise.Turbulence(new(1, 2, 3), 3, 4, gain) == 0, "Gain guard");
            Check(LibraGpu.Core.GpuNoise.Fbm(new(8193, 0, 0), 0, 8) == 0, "Highest octave coordinate bound");
            Near(LibraGpu.Core.GpuNoise.Fbm(new(0.2f, 0.3f, 0.4f), 7, 8, 0), LibraGpu.Core.GpuNoise.Fbm(new(0.2f, 0.3f, 0.4f), 7, 1, 0), 0, "Zero gain leaves first octave");
        });
        Run("append-only typed packet operands for math and noise", () =>
        {
            foreach (var p in new[] { LibraGpu.Core.GpuSamples.CreateMathProbe(), LibraGpu.Core.GpuSamples.CreateNoiseProbe() })
                foreach (var k in LibraGpu.Core.GpuCompiler.Compile(p).RegisterKernels)
                {
                    var types = new Dictionary<uint, LibraGpu.Core.GpuType>();
                    foreach (var instruction in k.Instructions)
                    {
                        if (instruction.Opcode == LibraGpu.Core.GpuInstructionOpcode.WriteAttribute) continue;
                        var op = (LibraGpu.Core.GpuOp)instruction.Opcode;
                        var d = LibraGpu.Core.GpuOperationRegistry.Operations[op];
                        var refs = new[] { instruction.A, instruction.B, instruction.C, instruction.D }.Take(d.MaxInputs).Where(r => r != uint.MaxValue).ToArray();
                        if (d.Policy is not (LibraGpu.Core.TypePolicy.Literal or LibraGpu.Core.TypePolicy.Attribute or LibraGpu.Core.TypePolicy.Uniform))
                            Check(LibraGpu.Core.GpuOperationRegistry.TypesMatch(op, instruction.Type, refs.Select(r => types[r]).ToArray()), $"Packet types {op}");
                        if ((int)op >= 19) Check((int)instruction.Opcode == (int)op && instruction.Aux == 0, "New ops no aux or opcode drift");
                        types[instruction.Destination] = instruction.Type;
                    }
                    var bytes = k.SerializeLittleEndian();
                    Check(bytes.Length == 64 + 64 * k.Instructions.Count, "Version1 packet stride preserved");
                }
            Check((uint)LibraGpu.Core.GpuInstructionOpcode.WriteAttribute == 18 && (int)LibraGpu.Core.GpuOp.CurlNoise == 40, "Store18 reservation");
        });
    }
    private static float ScalarMath(LibraGpu.Core.GpuOp op, float a, float b) => op switch
    {
        LibraGpu.Core.GpuOp.Abs => LibraGpu.Core.GpuMath.Abs(a),
        LibraGpu.Core.GpuOp.Floor => LibraGpu.Core.GpuMath.Floor(a),
        LibraGpu.Core.GpuOp.Ceil => LibraGpu.Core.GpuMath.Ceil(a),
        LibraGpu.Core.GpuOp.Round => LibraGpu.Core.GpuMath.Round(a),
        LibraGpu.Core.GpuOp.Frac => LibraGpu.Core.GpuMath.Frac(a),
        LibraGpu.Core.GpuOp.Mod => LibraGpu.Core.GpuMath.Mod(a, b),
        LibraGpu.Core.GpuOp.Sign => LibraGpu.Core.GpuMath.Sign(a),
        LibraGpu.Core.GpuOp.Pow => LibraGpu.Core.GpuMath.Pow(a, b),
        LibraGpu.Core.GpuOp.Sqrt => LibraGpu.Core.GpuMath.Sqrt(a),
        LibraGpu.Core.GpuOp.Exp => LibraGpu.Core.GpuMath.Exp(a),
        LibraGpu.Core.GpuOp.Log => LibraGpu.Core.GpuMath.Log(a),
        LibraGpu.Core.GpuOp.Lerp => LibraGpu.Core.GpuMath.Lerp(a, b, 0.25f),
        LibraGpu.Core.GpuOp.Smoothstep => LibraGpu.Core.GpuMath.Smoothstep(-1, 1, a),
        LibraGpu.Core.GpuOp.Remap => LibraGpu.Core.GpuMath.Remap(a, new(-1, 1), new(0, 10)),
        LibraGpu.Core.GpuOp.Saturate => LibraGpu.Core.GpuMath.Saturate(a),
        _ => throw new InvalidOperationException()
    };
    private static float[] Array(Vector3 p) => [p.X, p.Y, p.Z];
    private static void ExportReferenceCases(string directory)
    {
        var points = new List<(string Label, Vector3 P, uint Seed)>
        {
            ("origin",Vector3.Zero,0),("positive",new(0.23f,0.41f,0.67f),0),("negative",new(-1.23f,0.41f,-0.67f),7),
            ("seed_max",new(0.23f,0.41f,0.67f),uint.MaxValue),("seed_one",new(0.23f,0.41f,0.67f),1),
            ("lattice_boundary",new(-0.0001f,0.23f,0.47f),3),("beyond_octave8",new(8193,0.23f,0.47f),0),
            ("outside_domain",new(1048577,0,0),0),("computed_overflow",new(2,3,4),9)
        };
        var center = new Vector3(0.123f, 0.456f, -0.789f); const float h = 0.001f;
        points.Add(("divergence_center", center, 7));
        foreach (var (label, axis) in new[] { ("x", Vector3.UnitX), ("y", Vector3.UnitY), ("z", Vector3.UnitZ) })
        { points.Add(($"divergence_plus_{label}", center + axis * h, 7)); points.Add(($"divergence_minus_{label}", center - axis * h, 7)); }
        var cases = points.Select(x => new
        {
            label = x.Label,
            inputPosition = Array(x.P),
            seed = x.Seed,
            expectedPosition = Array(LibraGpu.Core.GpuNoise.CurlNoise(x.P, x.Seed, 3, 0.5f)),
            expectedVelocity = new[] { LibraGpu.Core.GpuNoise.GradientNoise3D(x.P, x.Seed), LibraGpu.Core.GpuNoise.Fbm(x.P, x.Seed, 3, 0.5f), LibraGpu.Core.GpuNoise.Turbulence(x.P, x.Seed, 3, 0.5f) },
            expectedColor = new[] { LibraGpu.Core.GpuNoise.GradientNoise3D(x.P, unchecked(x.Seed + 1)), LibraGpu.Core.GpuNoise.Fbm(x.P, x.Seed, 8, 1), LibraGpu.Core.GpuNoise.Turbulence(x.P, x.Seed, 8, 1), 1f },
            expectedSize = new[] { LibraGpu.Core.GpuNoise.Fbm(x.P, x.Seed, 1, 0), LibraGpu.Core.GpuNoise.Turbulence(x.P, x.Seed, 1, 0) }
        }).ToArray();
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "noise.reference.json"), JsonSerializer.Serialize(new { floatTolerance = 0.0002, finiteDifferenceEpsilon = h, divergenceTolerance = 0.006, cases }, options));
        var mathCases = new List<object>();
        foreach (var op in LibraGpu.Core.GpuOperationRegistry.Operations.Keys.Where(o => (int)o is >= 19 and <= 36))
            foreach (var inputs in new[] { new float[] { -2.5f, -1.5f, 0.25f, 4 }, new[] { 0f, 1, 2, 0.5f }, new[] { 100f, 1e30f, -1e30f, -0f } })
            {
                var a = new Vector3(1, 2, 3); var b = new Vector3(4, 5, 6);
                float[] expected = op == LibraGpu.Core.GpuOp.Cross ? Array(LibraGpu.Core.GpuMath.Cross(a, b)) :
                    op == LibraGpu.Core.GpuOp.Dot ? [LibraGpu.Core.GpuMath.Dot(a, b)] :
                    op == LibraGpu.Core.GpuOp.Distance ? [LibraGpu.Core.GpuMath.Distance(a, b)] :
                    inputs.Select((x, i) => ScalarMath(op, x, new[] { 0.5f, 2, -2, 0 }[i])).ToArray();
                mathCases.Add(new
                {
                    kernelId = op.ToString().ToLowerInvariant(),
                    inputPosition = Array(a),
                    inputVelocity = Array(b),
                    inputColor = inputs,
                    outputAttribute = op == LibraGpu.Core.GpuOp.Cross ? "Position" : op is LibraGpu.Core.GpuOp.Dot or LibraGpu.Core.GpuOp.Distance ? "Rotation" : "Color",
                    expected
                });
            }
        File.WriteAllText(Path.Combine(directory, "math.reference.json"), JsonSerializer.Serialize(new { absoluteTolerance = 0.0002, relativeTolerance = 0.0001, cases = mathCases }, options));
    }
}
