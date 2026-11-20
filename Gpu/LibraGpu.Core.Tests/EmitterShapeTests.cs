using LibraGpu.Core;

internal static partial class Program
{
    private static void VerifyEmitterShapes()
    {
        var starter = GpuSamples.CreateEmitter();
        var spawnFlow = starter.Flow.Single(flow => flow.Kind == FlowKind.Spawn);
        Check(spawnFlow.SpawnShape?.Kind == GpuSpawnShapeKind.Point, "New starter explicitly authors Point");

        var oldDocument = starter with
        {
            Flow = starter.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = null }
                : flow).ToList()
        };
        var oldJson = GpuCompiler.Serialize(oldDocument);
        var oldSpawn = System.Text.Json.JsonDocument.Parse(oldJson).RootElement.GetProperty("flow")
            .EnumerateArray().Single(flow => flow.GetProperty("kind").GetString() == "Spawn");
        Check(!oldSpawn.TryGetProperty("spawnShape", out _) &&
            GpuCompiler.Parse(oldJson).Flow.Single(flow => flow.Kind == FlowKind.Spawn).SpawnShape is null,
            "Legacy documents retain the absent shape field and parse as the old default");
        var oldCompiled = GpuCompiler.Compile(oldJson);
        var pointCompiled = GpuCompiler.Compile(starter);
        Check(oldCompiled.RegisterKernels.Single(kernel => kernel.Stage == KernelStage.Spawn).Instructions.Count ==
            pointCompiled.RegisterKernels.Single(kernel => kernel.Stage == KernelStage.Spawn).Instructions.Count &&
            oldCompiled.EmitUnityCompute().Text == pointCompiled.EmitUnityCompute().Text,
            "Legacy default and explicit Point preserve the previous Spawn behavior");
        var emitterPosition = new[] { 4.25, -3.5, 8.75 };
        Check(EvaluateSpawnPosition(oldCompiled, 17, emitterPosition).SequenceEqual(emitterPosition),
            "Legacy Spawn still writes the host emitter position");
        var transformed = GpuCompiler.Compile(starter with { TransformEmitterShape = true });
        double[][] rows = [[0, -2, 0, 10], [3, 0, 0, 20], [0, 0, 4, 30], [0, 0, 0, 1]];
        var transformedPosition = EvaluateSpawnPosition(transformed, 17, emitterPosition, rows);
        Check(transformedPosition.Zip(new[] { 17d, 32.75, 65d }).All(pair => Math.Abs(pair.First - pair.Second) < 1e-5),
            "Emitter Matrix applies translation, rotation and scale to the authored spawn point");
        Check(transformed.GetExportMetadata().EmitterMatrix && transformed.EmitUnityCompute().Text.Contains("LibraGpuEmitterRows"),
            "Matrix-enabled source and metadata advertise the optional emitter rows");

        var center = new[] { 11.5, -2.25, 4.75 };
        var box = CompileShape(starter, new()
        {
            Kind = GpuSpawnShapeKind.Box,
            HalfExtents = [2, 0.5, 3]
        });
        var boxSamples = Enumerable.Range(1, 4096)
            .Select(seed => EvaluateSpawnPosition(box, (uint)seed, center)).ToArray();
        for (var axis = 0; axis < 3; axis++)
        {
            Check(boxSamples.All(sample => sample[axis] >= center[axis] - new[] { 2d, 0.5d, 3d }[axis] - 1e-5 &&
                sample[axis] <= center[axis] + new[] { 2d, 0.5d, 3d }[axis] + 1e-5),
                "Box sampling stays within its centered half-extents");
            Check(Math.Abs(boxSamples.Average(sample => sample[axis]) - center[axis]) < 0.06,
                "Box samples are distributed on both sides of the center");
        }
        var authoredPosition = starter.Kernels.Single(kernel => kernel.Stage == KernelStage.Spawn)
            .Writes.Single(write => write.Attribute == "Position").Value;
        var customCenterProgram = starter with
        {
            Kernels = starter.Kernels.Select(kernel => kernel.Stage == KernelStage.Spawn
                ? kernel with
                {
                    Nodes = kernel.Nodes.Select(node => node.Id == authoredPosition
                        ? node with
                        {
                            Op = GpuOp.Constant,
                            Value = center.ToList(),
                            Uniform = null,
                            Inputs = []
                        }
                        : node).ToList()
                }
                : kernel).ToList()
        };
        var customCenterBox = CompileShape(customCenterProgram, new()
        {
            Kind = GpuSpawnShapeKind.Box,
            HalfExtents = [1, 1, 1]
        });
        var customCenterSample = EvaluateSpawnPosition(customCenterBox, 21, emitterPosition);
        Check(Enumerable.Range(0, 3).All(axis =>
                Math.Abs(customCenterSample[axis] - center[axis]) <= 1.0001),
            "Box offsets are centered on the authored Spawn Position value");
        var boxKernel = box.RegisterKernels.Single(kernel => kernel.Stage == KernelStage.Spawn);
        var boxPacket = boxKernel.SerializeLittleEndian();
        Check(boxKernel.Instructions.Any(instruction => instruction.Opcode == GpuInstructionOpcode.Random) &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(boxPacket.AsSpan(4, 4)) == 1 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(boxPacket.AsSpan(8, 4)) == 64 &&
            boxPacket.Length == CompiledRegisterKernel.HeaderBytes +
                boxKernel.Instructions.Count * CompiledRegisterKernel.InstructionStride &&
            boxKernel.Instructions.All(instruction => (uint)instruction.Opcode <= 41) &&
            boxKernel.RegisterCount <= 128 &&
            box.EmitUnityCompute().Text.Contains("LibraGpuRandom(") &&
            box.EmitUsf().Text.Contains("LibraGpuRandom(") &&
            box.EmitHlslInclude().Text.Contains("LibraGpuRandom("),
            "Box lowering retains the version-1 packet contract used by Unreal/Godot and every shader format");
        Check(GpuBackendRegistry.BuiltIn.Validate("unreal-lgpk", box).Count == 0 &&
            GpuBackendRegistry.BuiltIn.Validate("unity-compute", box).Count == 0,
            "Existing Unreal and Unity backend capability profiles accept lowered shapes");

        var sphere = CompileShape(starter, new() { Kind = GpuSpawnShapeKind.Sphere, Radius = 2 });
        var sphereSamples = Enumerable.Range(1, 4096)
            .Select(seed => EvaluateSpawnPosition(sphere, (uint)seed, center))
            .Select(sample => new[]
            {
                sample[0] - center[0], sample[1] - center[1], sample[2] - center[2]
            }).ToArray();
        Check(sphereSamples.All(sample => Length(sample) <= 2.0001),
            "Sphere samples remain inside the authored radius");
        var averageNormalizedRadiusCubed = sphereSamples.Average(sample => Math.Pow(Length(sample) / 2, 3));
        Check(Math.Abs(averageNormalizedRadiusCubed - 0.5) < 0.04,
            "Sphere radial samples follow the cube-root uniform-volume distribution");
        Check(GpuBackendRegistry.BuiltIn.Validate("unreal-lgpk", sphere).Count == 0 &&
            GpuBackendRegistry.BuiltIn.Validate("unity-compute", sphere).Count == 0 &&
            sphere.EmitUnityCompute().Text.Contains("LibraGpuPow(") &&
            sphere.EmitUnityCompute().Text.Contains("LibraGpuSqrt("),
            "Sphere lowering uses existing backend-supported math operations");

        var circle = CompileShape(starter, new() { Kind = GpuSpawnShapeKind.Circle, Radius = 1.75 });
        var circleSamples = Enumerable.Range(1, 2048)
            .Select(seed => EvaluateSpawnPosition(circle, (uint)seed, center)).ToArray();
        Check(circleSamples.All(sample => Math.Abs(Math.Sqrt(
                Math.Pow(sample[0] - center[0], 2) + Math.Pow(sample[1] - center[1], 2)) - 1.75) < 0.0001 &&
                Math.Abs(sample[2] - center[2]) < 1e-5),
            "Circle samples its XY circumference and leaves the center Z unchanged");

        var noPositionWrite = starter with
        {
            Kernels = starter.Kernels.Select(kernel => kernel.Stage == KernelStage.Spawn
                ? kernel with { Writes = kernel.Writes.Where(write => write.Attribute != "Position").ToList() }
                : kernel).ToList(),
            Flow = starter.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Circle, Radius = 0 } }
                : flow).ToList()
        };
        var zeroShape = GpuCompiler.Compile(noPositionWrite);
        Check(EvaluateSpawnPosition(zeroShape, 23, emitterPosition).SequenceEqual(emitterPosition),
            "Zero-radius shapes use emitterPosition when Spawn has no Position write");
        var noPositionCircle = GpuCompiler.Compile(noPositionWrite with
        {
            Flow = noPositionWrite.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Circle, Radius = 1.25 } }
                : flow).ToList()
        });
        var noPositionCircleSample = EvaluateSpawnPosition(noPositionCircle, 29, emitterPosition);
        Check(Math.Abs(Math.Sqrt(Math.Pow(noPositionCircleSample[0] - emitterPosition[0], 2) +
            Math.Pow(noPositionCircleSample[1] - emitterPosition[1], 2)) - 1.25) < 0.0001 &&
            Math.Abs(noPositionCircleSample[2] - emitterPosition[2]) < 1e-5,
            "Circle without a Position write is centered on host emitterPosition");

        var invalidBox = starter with
        {
            Flow = starter.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Box, HalfExtents = [1, -1, 1] } }
                : flow).ToList()
        };
        Check(GpuCompiler.Validate(invalidBox).Any(diagnostic => diagnostic.Code == "SpawnShape"),
            "Negative half-extents fail validation");
        var invalidRadius = starter with
        {
            Flow = starter.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Sphere, Radius = -1 } }
                : flow).ToList()
        };
        Check(GpuCompiler.Validate(invalidRadius).Any(diagnostic => diagnostic.Code == "SpawnShape"),
            "Negative radius fails validation");
        var malformedBox = starter with
        {
            Flow = starter.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Box, HalfExtents = [1, 2] } }
                : flow).ToList()
        };
        Check(GpuCompiler.Validate(malformedBox).Any(diagnostic => diagnostic.Code == "SpawnShape"),
            "Malformed half-extents fail validation");
        var nonfiniteRadius = starter with
        {
            Flow = starter.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Sphere, Radius = double.NaN } }
                : flow).ToList()
        };
        Check(GpuCompiler.Validate(nonfiniteRadius).Any(diagnostic => diagnostic.Code == "SpawnShape"),
            "Nonfinite dimensions fail validation before shader or packet conversion");
        var shapeOnUpdate = starter with
        {
            Flow = starter.Flow.Select(flow => flow.Kind == FlowKind.Update
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Point } }
                : flow).ToList()
        };
        Check(GpuCompiler.Validate(shapeOnUpdate).Any(diagnostic => diagnostic.Code == "SpawnShape"),
            "Spawn shape settings cannot be attached to Update");
        Check(box.CanonicalJson.Contains("\"kind\": \"Box\"") &&
            GpuCompiler.Parse(box.CanonicalJson).Flow.Single(flow => flow.Kind == FlowKind.Spawn)
                .SpawnShape is { Kind: GpuSpawnShapeKind.Box, HalfExtents: [2, 0.5, 3] },
            "Authored shape settings survive GPU IR export and parse roundtrip");
    }

    private static CompiledGpuProgram CompileShape(GpuProgram program, GpuSpawnShapeSettings shape)
    {
        var source = program with
        {
            Flow = program.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = shape }
                : flow).ToList()
        };
        return GpuCompiler.Compile(source);
    }

    private static double Length(double[] value) =>
        Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]);

    private static double[] EvaluateSpawnPosition(CompiledGpuProgram program, uint seed, double[] emitterPosition, double[][]? emitterRows = null)
    {
        var kernel = program.RegisterKernels.Single(candidate => candidate.Stage == KernelStage.Spawn);
        var registers = new double[kernel.RegisterCount][];
        double[] Read(uint index) => registers[index];
        uint Hash(uint value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7feb352du;
                value ^= value >> 15;
                value *= 0x846ca68bu;
                return value ^ (value >> 16);
            }
        }
        foreach (var instruction in kernel.Instructions)
        {
            if (instruction.Opcode == GpuInstructionOpcode.WriteAttribute)
            {
                if (instruction.Aux == (uint)GpuAttributeSlot.Position)
                    return Read(instruction.A).ToArray();
                continue;
            }
            if (instruction.Destination == CompiledRegisterKernel.MissingRegister)
                continue;
            var components = Components(instruction.Type);
            var a = instruction.A == CompiledRegisterKernel.MissingRegister ? [] : Read(instruction.A);
            var b = instruction.B == CompiledRegisterKernel.MissingRegister ? [] : Read(instruction.B);
            registers[instruction.Destination] = instruction.Opcode switch
            {
                GpuInstructionOpcode.Constant => IsFloatType(instruction.Type)
                    ? new[]
                    {
                        (double)instruction.FloatImmediate.X,
                        instruction.FloatImmediate.Y,
                        instruction.FloatImmediate.Z,
                        instruction.FloatImmediate.W
                    }.Take(components).ToArray()
                    : [instruction.Type == GpuType.UInt ? instruction.Integer0 : unchecked((uint)instruction.Integer0)],
                GpuInstructionOpcode.ReadAttribute when instruction.Aux == (uint)GpuAttributeSlot.Seed => [seed],
                GpuInstructionOpcode.ReadAttribute when instruction.Aux == (uint)GpuAttributeSlot.Position => emitterPosition,
                GpuInstructionOpcode.ReadUniform when instruction.Aux == (uint)GpuUniformSlot.EmitterPosition => emitterPosition,
                GpuInstructionOpcode.ReadUniform when instruction.Aux is >= 3 and <= 6 => emitterRows![instruction.Aux - 3],
                GpuInstructionOpcode.Component => [a[instruction.Aux]],
                GpuInstructionOpcode.Dot => [Enumerable.Range(0, a.Length).Sum(i => (double)((float)a[i] * (float)b[i]))],
                GpuInstructionOpcode.Add when instruction.Type == GpuType.UInt =>
                    [unchecked((uint)a[0] + (uint)b[0])],
                GpuInstructionOpcode.Add => Binary(a, b, components, (left, right) => left + right),
                GpuInstructionOpcode.Sub => Binary(a, b, components, (left, right) => left - right),
                GpuInstructionOpcode.Mul => Binary(a, b, components, (left, right) => left * right),
                GpuInstructionOpcode.Max => Binary(a, b, components, MathF.Max),
                GpuInstructionOpcode.Pow => Binary(a, b, components, MathF.Pow),
                GpuInstructionOpcode.Sqrt => Unary(a, components, MathF.Sqrt),
                GpuInstructionOpcode.Sin => Unary(a, components, MathF.Sin),
                GpuInstructionOpcode.Cos => Unary(a, components, MathF.Cos),
                GpuInstructionOpcode.Random => [(double)(Hash((uint)a[0]) >> 8) * (1f / 16777216f)],
                GpuInstructionOpcode.Compose => new[] { a[0], b[0] }
                    .Concat(instruction.C == CompiledRegisterKernel.MissingRegister ? System.Array.Empty<double>() : Read(instruction.C))
                    .Concat(instruction.D == CompiledRegisterKernel.MissingRegister ? System.Array.Empty<double>() : Read(instruction.D)).ToArray(),
                _ => throw new InvalidOperationException($"Unexpected Spawn shape test opcode {instruction.Opcode}.")
            };
        }
        throw new InvalidOperationException("Spawn packet did not write Position.");

        static double[] Binary(double[] left, double[] right, int count, Func<float, float, float> operation)
        {
            left = BroadcastValues(left, count);
            right = BroadcastValues(right, count);
            return Enumerable.Range(0, count)
                .Select(index => (double)operation((float)left[index], (float)right[index])).ToArray();
        }
        static double[] Unary(double[] value, int count, Func<float, float> operation) =>
            Enumerable.Range(0, count).Select(index => (double)operation((float)value[index])).ToArray();
    }

    private static int Components(GpuType type) => type switch
    {
        GpuType.Float2 => 2,
        GpuType.Float3 => 3,
        GpuType.Float4 => 4,
        _ => 1
    };

    private static bool IsFloatType(GpuType type) =>
        type is GpuType.Float or GpuType.Float2 or GpuType.Float3 or GpuType.Float4;

    private static double[] BroadcastValues(double[] value, int components) => value.Length == components
        ? value
        : Enumerable.Repeat(value[0], components).ToArray();
}
