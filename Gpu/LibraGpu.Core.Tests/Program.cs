using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using LibraGpu.Core;

internal static partial class Program
{
    private static int passed;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(GpuProgram p, string code)
    {
        var diagnostics = GpuCompiler.Validate(p);
        Check(diagnostics.Any(d => d.Code == code), $"Expected {code}, got {string.Join(",", diagnostics.Select(d => d.Code))}");
        try { GpuCompiler.Compile(p); throw new InvalidOperationException("Invalid program compiled."); }
        catch (GpuValidationException) { }
    }
    private static void JsonReject(string text, string? code = null, GpuLimits? limits = null)
    {
        try { GpuCompiler.Parse(text, limits); throw new InvalidOperationException("Invalid JSON accepted."); }
        catch (GpuValidationException e) { if (code is not null) Check(e.Diagnostics.Any(d => d.Code == code), $"Expected {code}."); }
    }
    private static void DslReject(string text)
    {
        try { GpuDsl.Compile(text, "k", KernelStage.Update); throw new InvalidOperationException("Invalid DSL accepted."); }
        catch (GpuValidationException e) { Check(e.Diagnostics.Any(d => d.Span is not null), "DSL diagnostic needs text span."); }
    }
    private static GpuNode C(string id, GpuType t, params double[] value) => new() { Id = id, Op = GpuOp.Constant, Type = t, Value = value.ToList() };
    private static GpuProgram One(params GpuNode[] nodes) => new()
    {
        Id = "p",
        Capacity = 128,
        Kernels = [new() { Id = "k", Stage = KernelStage.Update, Nodes = nodes.ToList() }],
        Flow = [new() { Id = "f", Kind = FlowKind.Update, Kernel = "k" }]
    };
    private static GpuProgram WithWrite(GpuProgram p, string attr, string value) => p with
    { Kernels = [p.Kernels[0] with { Writes = [new("w", attr, value)] }] };
    private static void Run(string name, Action action)
    { action(); passed++; Console.WriteLine($"PASS {name}"); }
    public static int Main(string[] args)
    {
        try
        {
            Run("particle and uniform ABI", () =>
            {
                Check(Marshal.SizeOf<LibraGpuParticle>() == 80, "Particle stride");
                Check(Marshal.OffsetOf<LibraGpuParticle>(nameof(LibraGpuParticle.PositionAge)).ToInt32() == 0, "Position offset");
                Check(Marshal.OffsetOf<LibraGpuParticle>(nameof(LibraGpuParticle.VelocityLifetime)).ToInt32() == 16, "Velocity offset");
                Check(Marshal.OffsetOf<LibraGpuParticle>(nameof(LibraGpuParticle.Color)).ToInt32() == 32, "Color offset");
                Check(Marshal.OffsetOf<LibraGpuParticle>(nameof(LibraGpuParticle.SizeRotation)).ToInt32() == 48, "Size offset");
                Check(Marshal.OffsetOf<LibraGpuParticle>(nameof(LibraGpuParticle.Seed)).ToInt32() == 64, "Seed offset");
                Check(Marshal.OffsetOf<LibraGpuParticle>(nameof(LibraGpuParticle.Alive)).ToInt32() == 68, "Alive offset");
                Check(Marshal.SizeOf<LibraGpuUniforms>() == 48, "Uniform stride");
                Check(Marshal.OffsetOf<LibraGpuUniforms>(nameof(LibraGpuUniforms.EmitterPosition)).ToInt32() == 32, "Emitter offset");
            });
            Run("roundtrip deterministic exports and metadata isolation", () =>
            {
                var p = GpuSamples.CreateEmitter();
                var c = GpuCompiler.Compile(p);
                var json = GpuCompiler.Serialize(p);
                Check(GpuCompiler.Serialize(GpuCompiler.Parse(json)) == json, "JSON roundtrip");
                var reordered = p with { Kernels = p.Kernels.AsEnumerable().Reverse().Select(k => k with { Nodes = k.Nodes.AsEnumerable().Reverse().ToList() }).ToList() };
                var other = GpuCompiler.Compile(reordered);
                Check(c.ContentHash == other.ContentHash, "Canonical hash must ignore node/kernel list ordering");
                Check(c.EmitUsf().Text == other.EmitUsf().Text, "Deterministic SSA");
                Check(!c.EmitUsf().Text.Contains("#pragma"), "USF excludes Unity directives");
                Check(c.EmitUnityCompute().Text.Contains("#pragma kernel LibraGpuKernel_0000"), "Unity kernels");
                Check(c.EmitHlslInclude().Text.Contains("#ifndef"), "Self-contained include guard");
                Check(c.Kernels.All(k => k.ThreadsX == 64 && k.DispatchGroupsX == 64), "Dispatch metadata");
                Check(c.ParticleBufferBytes == 4096UL * 80 && !c.RuntimeExecuted, "Export-only metadata");
                var metadata = JsonNode.Parse(c.ExportMetadataJson())!;
                Check(metadata["purpose"]!.GetValue<string>() == "SourceExport", "Metadata enum strings");
                Check(metadata["particleBuffer"]!.GetValue<string>() == "ParticleBuffer", "Runtime binding metadata");
                Check(metadata["flow"]![0]!["kernelId"]!.GetValue<string>() == "spawn", "Flow kernel references");
                Check(metadata["sourceHash"]!.GetValue<string>() == c.EmitUnityCompute().ContentHash, "Source hash");
                foreach (var map in c.EmitUsf().LineMappings)
                    Check(c.EmitUsf().Text.Split('\n')[map.Line - 1].Contains("r"), "Mapped source line");
                var before = c.EmitUsf().Text;
                p.Kernels.Clear(); p.Flow.Clear(); p.Attributes.Clear();
                Check(c.EmitUsf().Text == before, "Compiled object must snapshot frontend lists");
            });
            Run("strict JSON", () =>
            {
                var json = GpuCompiler.Serialize(GpuSamples.CreateEmitter());
                JsonReject(json.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"), "DuplicateProperty");
                JsonReject(json.Replace("\"version\": 1", "\"version\": 1, \"Version\": 1"), "DuplicateProperty");
                JsonReject(json.Replace("\"version\": 1", "\"version\": 1, \"unknown\": 1"), "InvalidJson");
                JsonReject(json.Replace("\"stage\": \"Spawn\"", "\"stage\": 0"), "InvalidJson");
                JsonReject(json.Replace("\"capacity\": 4096", "\"capacity\": 1e400"), "NonFinite");
                JsonReject(json.Replace("\"capacity\": 4096", "\"capacity\": 1.5"), "InvalidJson");
                JsonReject("null", "JsonNull");
                JsonReject(json, null, new() { MaxJsonBytes = 100 });
                JsonReject(json, "PropertyLimit", new() { MaxPropertiesPerObject = 1 });
                JsonReject(json, "InvalidJson", new() { MaxJsonDepth = 2 });
                var obj = JsonNode.Parse(json)!.AsObject(); obj["kernels"] = null;
                JsonReject(obj.ToJsonString(), "NullCollection");
            });
            Run("graph references cycles and IDs", () =>
            {
                Reject(One(C("x", GpuType.Float, 1), C("x", GpuType.Float, 2)), "DuplicateId");
                Reject(One(new GpuNode() { Id = "a", Op = GpuOp.Add, Type = GpuType.Float, Inputs = ["missing", "missing"] }), "UndefinedNode");
                Reject(One(C("c", GpuType.Float, 1),
                    new() { Id = "a", Op = GpuOp.Add, Type = GpuType.Float, Inputs = ["b", "c"] },
                    new() { Id = "b", Op = GpuOp.Add, Type = GpuType.Float, Inputs = ["a", "c"] }), "Cycle");
                Reject(One(C("a", GpuType.Float, 1)) with { Flow = [new() { Id = "k", Kind = FlowKind.Update, Kernel = "k" }] }, "DuplicateId");
                Reject(One(new GpuNode() { Id = "cpuSignal", Op = (GpuOp)999, Type = GpuType.Float }), "UnsupportedOp");
            });
            Run("numeric type and literal validation", () =>
            {
                Reject(One(C("a", GpuType.Float, double.NaN)), "Literal");
                Reject(One(C("a", GpuType.Float, double.MaxValue)), "Literal");
                Reject(One(C("a", GpuType.Int, 1.5)), "Literal");
                Reject(One(C("a", GpuType.UInt, -1)), "Literal");
                Reject(One(C("a", GpuType.Bool, 2)), "Literal");
                Reject(One(C("a", GpuType.Float3, 1, 2)), "Components");
                Reject(One(C("a", GpuType.Quaternion, 0, 0, 0, 1)), "UnsupportedType");
                Reject(One(C("a", GpuType.Float2, 1, 2), C("b", GpuType.Float3, 1, 2, 3),
                    new() { Id = "m", Op = GpuOp.Mul, Type = GpuType.Float3, Inputs = ["a", "b"] }), "TypeMismatch");
                Reject(One(C("a", GpuType.Bool, 1),
                    new() { Id = "m", Op = GpuOp.Add, Type = GpuType.Bool, Inputs = ["a", "a"] }), "TypeMismatch");
                Reject(One(C("a", GpuType.Float, 1),
                    new() { Id = "m", Op = GpuOp.Compose, Type = GpuType.Float2, Inputs = ["a", "a", "a"] }), "TypeMismatch");
                Reject(One(new GpuNode() { Id = "a", Op = GpuOp.ReadUniform, Type = GpuType.Float, Uniform = "hostPointer" }), "TypeMismatch");
                Reject(One(C("a", GpuType.Float, 1) with { Attribute = "Position" }), "Payload");
                Reject(One(C("a", GpuType.Float2, 1, 2),
                    new() { Id = "c", Op = GpuOp.Component, Type = GpuType.Float, Inputs = ["a"], ComponentIndex = 2 }), "TypeMismatch");
                var broadcast = One(C("scalar", GpuType.Float, 2), C("vec", GpuType.Float3, 1, 2, 3),
                    new() { Id = "result", Op = GpuOp.Add, Type = GpuType.Float3, Inputs = ["scalar", "vec"] });
                Check(GpuCompiler.Validate(broadcast).Count == 0, "Float scalar broadcast allowed");
                Check(GpuCompiler.Compile(broadcast).EmitUsf().Text.Contains("float3(r"), "Broadcast explicitly lowered");
            });
            Run("writes and spawn/update lifecycle", () =>
            {
                Reject(WithWrite(One(C("a", GpuType.Float, 1)), "Unknown", "a"), "IllegalWrite");
                Reject(WithWrite(One(C("a", GpuType.Float, 1)), "Position", "a"), "TypeMismatch");
                Reject(WithWrite(One(C("a", GpuType.Float, 1)), "Age", "missing"), "UndefinedNode");
                var p = WithWrite(One(C("a", GpuType.Float, 1)), "Age", "a");
                Reject(p with { Kernels = [p.Kernels[0] with { Writes = [new("w", "Age", "a"), new("w2", "Age", "a")] }] }, "DuplicateWrite");
                var explicitAge = GpuCompiler.Compile(p).EmitUsf().Text;
                Check(!explicitAge.Contains("p.positionAge.w +="), "Explicit Age write overrides default advance");
                Check(GpuCompiler.Compile(One()).EmitUsf().Text.Contains("p.positionAge.w +="), "Default Age advances");
                var spawn = GpuCompiler.Compile(GpuSamples.CreateEmitter()).EmitUsf().Text;
                Check(spawn.Contains("if (ParticleBuffer[i].seedAlive.y != 0u) return;"), "Spawn must not overwrite live particles");
                Check(spawn.Contains("p.velocityLifetime = float4(0.0f, 0.0f, 0.0f, 1.0f)"), "Spawn lifetime default");
                Check(spawn.Contains("p.positionAge.w >= p.velocityLifetime.w"), "Safe death");
                Check(spawn.Contains("GetDimensions(actualCapacity, actualStride)"), "Runtime buffer capacity bounds");
                Check(!spawn.Contains("isfinite(") && spawn.Contains("asuint(a)"), "Finite guards survive fast-math shader compiler assumptions");
                Check(spawn.Contains("LibraGpuIsFinite(LibraGpuDeltaTime)"), "Default age uses the portable finite guard");
            });
            Run("resources flow limits and overflow", () =>
            {
                var p = GpuSamples.CreateEmitter();
                Reject(p with { Capacity = uint.MaxValue }, "Capacity");
                Reject(p with { Capacity = 0 }, "Capacity");
                Reject(p with { Version = 2 }, "Version");
                Reject(p with { Resources = [new() { Id = "billboardTexture", Width = uint.MaxValue, Height = uint.MaxValue }] }, "ResourceSize");
                Reject(p with { Resources = [new() { Id = "billboardTexture", Width = 1, Height = 0 }] }, "ResourceSize");
                Reject(p with { Resources = [new() { Id = "billboardTexture", Kind = ResourceKind.StorageTexture2D }] }, "UnsupportedResource");
                Reject(p with { Resources = [] }, "UndefinedResource");
                Reject(p with { Flow = [new() { Id = "future", Kind = FlowKind.ComputePass }] }, "UnsupportedPass");
                Reject(p with { Flow = p.Flow.AsEnumerable().Reverse().ToList() }, "FlowOrder");
                Reject(p with { Flow = [new() { Id = "bad", Kind = FlowKind.Update, Kernel = "spawn" }] }, "StageMismatch");
                Reject(p with { Flow = [new() { Id = "bad", Kind = FlowKind.Spawn, Kernel = "none" }] }, "UndefinedKernel");
                Reject(p with { Flow = [new() { Id = "f1", Kind = FlowKind.Update, Kernel = "update" }, new() { Id = "f2", Kind = FlowKind.Update, Kernel = "update" }] }, "StageLimit");
                Check(GpuCompiler.Validate(p, new() { MaxNodesPerKernel = 1 }).Any(d => d.Code == "Limit"), "Node limits");
                var nodes = p.Kernels[0].Nodes.ToList(); nodes.Add(null!);
                Reject(p with { Kernels = [p.Kernels[0] with { Nodes = nodes }] }, "Id");
            });
            Run("safe identifier and binding generation", () =>
            {
                var p = One(C("userName; #include bad", GpuType.Float, 1)) with { Id = "p\"; malicious", Kernels = [] };
                p = p with { Kernels = [new() { Id = "shader name\n", Stage = KernelStage.Update }] };
                Reject(p, "Id");
                p = One(C("userName; #include bad", GpuType.Float, 1));
                var c = GpuCompiler.Compile(p);
                Check(!c.EmitUsf().Text.Contains("userName"), "Never use user IDs as shader identifiers");
                var custom = c.EmitUsf(new() { ParticleBuffer = "User_Particles", UniformBuffer = "User_Params", ParticleRegister = 3, UniformRegister = 2 });
                Check(custom.Text.Contains("User_Particles : register(u3)"), "Custom safe binding");
                foreach (var binding in new[] { new ShaderBindings { ParticleBuffer = "float" }, new() { ParticleBuffer = "evil;\n#include" }, new() { TextureRegisterBase = int.MaxValue }, new() { SamplerRegisterBase = 16 } })
                {
                    try { c.EmitUsf(binding); throw new InvalidOperationException("Bad binding accepted."); }
                    catch (ArgumentException) { }
                }
            });
            Run("DSL AST shares IR and preserves generated expression IDs", () =>
            {
                var source = """
                    let direction = normalize(float3(1, 2, 3));
                    @Velocity = direction * 4;
                    @Position += @Velocity * dt;
                    @Position.x = @Position.x + 1;
                    @Color.rgb = float3(0.1, 0.2, 0.3);
                    @Color.a = clamp(1 - @Age / @Lifetime, 0, 1);
                    @Seed = 4294967295u;
                    @Alive = select(true, false, true);
                    """;
                var ast = GpuDsl.Parse(source);
                var kernel = GpuDsl.Lower(ast, "k", KernelStage.Update);
                Check(kernel.Nodes.All(n => n.SourceSpan is not null), "Nodes carry source spans");
                Check(kernel.Writes.Count == 5, "Sequential assignments collapse to final stores");
                var generated = GpuDsl.Emit(kernel);
                var reconstructed = GpuDsl.Compile(generated.Text, "k", KernelStage.Update, options: new()
                { NodeIdsBySpan = generated.NodeIdsBySpan, WriteIdsBySpan = generated.WriteIdsBySpan });
                Check(reconstructed.Writes.Select(w => w.Id).SequenceEqual(kernel.Writes.Select(w => w.Id)), "Write IDs preserved");
                Check(kernel.Nodes.All(n => reconstructed.Nodes.Any(r => r.Id == n.Id)), "Generated grammar preserves original expression IDs");
                var p = One() with { Kernels = [kernel] };
                var c = GpuCompiler.Compile(p);
                Check(c.EmitUsf().LineMappings.Any(m => m.Span is not null), "Shader mappings retain DSL spans");
            });
            Run("DSL rejects unsafe or unsupported constructs", () =>
            {
                foreach (var text in new[]
                {
                    "while(true) {}", "for(;;) {}", "@Position = injectedHlsl();", "@Position = float3(1,2);",
                    "@Position.w = 1;", "@Color.xx = float2(1,2);", "@Age = param(\"secret\");",
                    "@Age = 1e400;", "@Seed = -1u;", "@Alive = 1;", "let dt = 1;", "let a = 1; let a = 2;",
                    "@Age = unknown;", "@Age = 1 // missing semicolon"
                }) DslReject(text);
                DslReject("@Age = " + new string('(', 70) + "1" + new string(')', 70) + ";");
                var k = GpuDsl.Compile("@Seed = 2147483647u; @Age = -1;", "k", KernelStage.Update);
                Check(k.Nodes.Any(n => n.Op == GpuOp.Constant && n.Value.Contains(-1)), "Signed literal");
                var minimal = GpuDsl.Compile("@Position = float3(1,2,3);", "k", KernelStage.Update, new() { MaxNodesPerKernel = 1 });
                Check(minimal.Nodes.Count == 1, "Literal vector respects the actual lowered node limit");
            });
            Run("DSL negation preserves shared constants and mapped duplicate reads", () =>
            {
                var k = GpuDsl.Compile("let a = 1; @Age = a; @Rotation = -a; @Lifetime = a;", "k", KernelStage.Update);
                Check(k.Writes.Where(w => w.Attribute is "Age" or "Lifetime").All(w => k.Nodes.Any(n => n.Id == w.Value && n.Op == GpuOp.Constant)), "Shared constant preserved");
                var visual = One(
                    new GpuNode { Id = "text_000000", Op = GpuOp.ReadAttribute, Type = GpuType.Float3, Attribute = "Position" },
                    new GpuNode { Id = "secondRead", Op = GpuOp.ReadAttribute, Type = GpuType.Float3, Attribute = "Position" },
                    new GpuNode { Id = "sum", Op = GpuOp.Add, Type = GpuType.Float3, Inputs = ["text_000000", "secondRead"] }).Kernels[0];
                var source = GpuDsl.Emit(visual);
                var copy = GpuDsl.Compile(source.Text, "k", KernelStage.Update, options: new() { NodeIdsBySpan = source.NodeIdsBySpan, WriteIdsBySpan = source.WriteIdsBySpan });
                Check(visual.Nodes.All(n => copy.Nodes.Any(c => c.Id == n.Id)), "Both visual read-node IDs retained");
            });
            Run("register packet exact little endian and GUID contract", () =>
            {
                var p = WithWrite(One(C("unsigned", GpuType.UInt, uint.MaxValue), C("signed", GpuType.Int, int.MinValue), C("float", GpuType.Float, 1.25)), "Seed", "unsigned");
                const string guid = "00112233-4455-6677-8899-aabbccddeeff";
                p = p with { Kernels = [p.Kernels[0] with { Id = guid }], Flow = [new() { Id = "f", Kind = FlowKind.Update, Kernel = guid }] };
                var c = GpuCompiler.Compile(p).RegisterKernels[0];
                var bytes = c.SerializeLittleEndian();
                Check(Marshal.SizeOf<GpuInstruction>() == 64, "Instruction stride");
                Check(Marshal.OffsetOf<GpuInstruction>(nameof(GpuInstruction.FloatImmediate)).ToInt32() == 32, "Float offset");
                Check(Marshal.OffsetOf<GpuInstruction>(nameof(GpuInstruction.Integer0)).ToInt32() == 48, "Integer offset");
                Check(Convert.ToHexString(bytes.AsSpan(32, 16)) == "00112233445566778899AABBCCDDEEFF", "Canonical big endian GUID");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)) == c.Instructions.Count, "Count");
                Check(bytes.Length == 64 + c.Instructions.Count * 64, "Packet size");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(48, 4)) == 1, "Default age flag");
                for (int i = 0; i < c.Instructions.Count; i++)
                {
                    var instruction = c.Instructions[i];
                    int o = 64 + i * 64;
                    Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o + 4, 4)) == instruction.Destination, "Register LE");
                    if (instruction.Type == GpuType.UInt && instruction.Opcode == GpuInstructionOpcode.Constant)
                        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o + 48, 4)) == uint.MaxValue, "Uint immediate is exact");
                    if (instruction.Type == GpuType.Int)
                        Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o + 48, 4)) == 0x80000000u, "Signed bits not float-cast");
                    if (instruction.Type == GpuType.Float)
                        Check(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o + 32, 4)) == 1.25f, "Float LE");
                }
                Check(c.Instructions[^1].Opcode == GpuInstructionOpcode.WriteAttribute, "SSA stores last");
                bytes[0] = 0;
                Check(c.SerializeLittleEndian()[0] == 'L', "Packet arrays are independent");
            });
            Run("all safe operations shader fixture", () => Check(GpuCompiler.Validate(AllOps()).Count == 0, "Fixture validates"));
            RunExtensionTests();
            if (args.Length == 2 && args[0] == "--export")
            {
                Directory.CreateDirectory(args[1]);
                foreach (var (name, p) in new[] { ("sample", GpuSamples.CreateEmitter()), ("allops", AllOps()),
                    ("noise",GpuSamples.CreateNoiseProbe()),("noiseguards",GpuSamples.CreateNoiseGuardProbe()),
                    ("math",GpuSamples.CreateMathProbe()),("flowmap",FlowMapProbe()) })
                {
                    var c = GpuCompiler.Compile(p);
                    File.WriteAllText(Path.Combine(args[1], name + ".hlsl"), c.EmitHlslInclude().Text);
                    File.WriteAllText(Path.Combine(args[1], name + ".usf"), c.EmitUsf().Text);
                    File.WriteAllText(Path.Combine(args[1], name + ".compute"), c.EmitUnityCompute().Text);
                    File.WriteAllText(Path.Combine(args[1], name + ".json"), c.CanonicalJson);
                    File.WriteAllText(Path.Combine(args[1], name + ".metadata.json"), c.ExportMetadataJson());
                    File.WriteAllLines(Path.Combine(args[1], name + ".entries"), c.Kernels.Select(k => k.EntryPoint));
                    if (name is "math" or "noise" or "noiseguards")
                        foreach (var kernel in c.RegisterKernels)
                            File.WriteAllBytes(Path.Combine(args[1], $"{name}.{kernel.KernelId}.lgpk"), kernel.SerializeLittleEndian());
                }
                ExportReferenceCases(args[1]);
                File.WriteAllText(Path.Combine(args[1], "LibraGpuSupport.hlsl"), GpuShaderSupport.EmitHlslInclude());
            }
            Console.WriteLine($"{passed} focused GPU tests passed; shaders exported, not GPU-executed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static GpuProgram FlowMapProbe()
    {
        var resources = new List<ResourceDeclaration>
        {
            new()
            {
                Id = "wind",
                Kind = ResourceKind.FlowMap2D,
                Width = 2,
                Height = 2,
                FlowMap = new()
                {
                    AxisU = GpuAxis.Y, AxisV = GpuAxis.X,
                    WorldMinimumU = -1, WorldMaximumU = 1,
                    WorldMinimumV = -1, WorldMaximumV = 1
                }
            }
        };
        var kernel = GpuDsl.Compile("""
            @Velocity = sampleFlowMap("wind", @Position);
            @Size = sampleFlowMap("wind", float2(0.25, 0.75));
            """, "wind-update", KernelStage.Update, resources: resources);
        return One() with
        {
            Id = "flow-map-probe",
            Resources = resources,
            Kernels = [kernel],
            Flow = [new() { Id = "update", Kind = FlowKind.Update, Kernel = kernel.Id }]
        };
    }

    private static GpuProgram AllOps()
    {
        var k = GpuDsl.Compile("""
            let a = 1.5;
            let b = 2.5;
            let s = sin(a) + cos(b);
            let minimum = min(a, b);
            let maximum = max(a, b);
            let bounded = clamp(s, minimum, maximum);
            let vector = float3(bounded, a, b);
            let norm = normalize(vector);
            let magnitude = length(vector);
            let scaled = norm * a / b - norm;
            let randomValue = random(@Seed);
            @Position = select(@Alive, scaled, vector);
            @Velocity = normalize(float3(0, 0, 0));
            @Color = float4(randomValue, s, magnitude, 1);
            @Size = normalize(float2(a, b)) / float2(0, b);
            let q = normalize(float4(a, b, s, 1)) / a;
            @Rotation = q.w;
            @Age = 3 / 0;
            @Seed = min(7u, 4u) + max(2u, 5u) - 1u * 2u / 0u;
            let signed = clamp(-2147483648i, -2147483648i, 2147483647i) / -1i;
            let signedMath = min(signed, 1i) + max(signed, 2i) - 3i * 4i;
            @Alive = select(true, @Alive, false);
            """, "k", KernelStage.Update);
        return One() with { Kernels = [k] };
    }
}
