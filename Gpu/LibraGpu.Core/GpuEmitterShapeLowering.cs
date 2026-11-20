namespace LibraGpu.Core;

/// <summary>
/// Lowers authored Spawn emitter shapes to the existing typed math and Random operations.
/// The authoring JSON remains intact; generated nodes exist only in the compiled snapshot.
/// </summary>
internal static class GpuEmitterShapeLowering
{
    private const double Tau = Math.PI * 2;
    private delegate GpuNode AddNode(GpuOp op, GpuType type, params string[] inputs);
    private delegate GpuNode ConstantNode(GpuType type, params double[] values);

    public static GpuProgram Lower(GpuProgram program)
    {
        var spawn = program.Flow.SingleOrDefault(flow => flow.Kind == FlowKind.Spawn);
        if (spawn is null || !program.TransformEmitterShape && spawn.SpawnShape is not { Kind: not GpuSpawnShapeKind.Point }) return program;
        var shape = spawn.SpawnShape ?? new GpuSpawnShapeSettings();

        var kernelIndex = program.Kernels.FindIndex(kernel => kernel.Id == spawn.Kernel);
        if (kernelIndex < 0) return program;
        var kernel = program.Kernels[kernelIndex];
        var nodes = kernel.Nodes.ToList();
        var writes = kernel.Writes.ToList();
        var usedIds = nodes.Select(node => node.Id).Concat(writes.Select(write => write.Id))
            .ToHashSet(StringComparer.Ordinal);
        var nextId = 0;
        string Id()
        {
            string value;
            do value = $"__gpuShape_{nextId++}";
            while (!usedIds.Add(value));
            return value;
        }
        GpuNode Add(GpuOp op, GpuType type, params string[] inputs)
        {
            var node = new GpuNode { Id = Id(), Op = op, Type = type, Inputs = inputs.ToList() };
            nodes.Add(node);
            return node;
        }
        GpuNode Constant(GpuType type, params double[] values)
        {
            var node = new GpuNode { Id = Id(), Op = GpuOp.Constant, Type = type, Value = values.ToList() };
            nodes.Add(node);
            return node;
        }
        GpuNode Random(int index, GpuNode seed)
        {
            var randomSeed = seed;
            if (index != 0)
            {
                var offset = Constant(GpuType.UInt, index switch
                {
                    1 => 0x9e3779b9u,
                    2 => 0x85ebca6bu,
                    _ => unchecked((uint)(index * 0x27d4eb2d))
                });
                randomSeed = Add(GpuOp.Add, GpuType.UInt, seed.Id, offset.Id);
            }
            return Add(GpuOp.Random, GpuType.Float, randomSeed.Id);
        }

        var seed = Add(GpuOp.ReadAttribute, GpuType.UInt);
        nodes[^1] = nodes[^1] with { Attribute = "Seed" };
        var offset = shape.Kind switch
        {
            GpuSpawnShapeKind.Point => Constant(GpuType.Float3, 0, 0, 0),
            GpuSpawnShapeKind.Box => BoxOffset(shape, seed, Random, Add, Constant),
            GpuSpawnShapeKind.Sphere => SphereOffset(shape, seed, Random, Add, Constant),
            GpuSpawnShapeKind.Circle => CircleOffset(shape, seed, Random, Add, Constant),
            _ => throw new InvalidOperationException("Unsupported emitter shape.")
        };
        var positionWriteIndex = writes.FindIndex(write => write.Attribute == "Position");
        var position = positionWriteIndex >= 0
            ? writes[positionWriteIndex].Value
            : Add(GpuOp.ReadUniform, GpuType.Float3).Id;
        if (positionWriteIndex < 0)
            nodes[^1] = nodes[^1] with { Uniform = "emitterPosition" };
        var positioned = Add(GpuOp.Add, GpuType.Float3, position, offset.Id);
        if (program.TransformEmitterShape)
        {
            var components = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var component = Add(GpuOp.Component, GpuType.Float, positioned.Id);
                nodes[^1] = component with { ComponentIndex = i };
                components.Add(component.Id);
            }
            components.Add(Constant(GpuType.Float, 1).Id);
            var point = Add(GpuOp.Compose, GpuType.Float4, components.ToArray());
            var transformed = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var row = Add(GpuOp.ReadUniform, GpuType.Float4);
                nodes[^1] = row with { Uniform = "emitterRow" + i };
                transformed.Add(Add(GpuOp.Dot, GpuType.Float, row.Id, point.Id).Id);
            }
            positioned = Add(GpuOp.Compose, GpuType.Float3, transformed.ToArray());
        }
        if (positionWriteIndex >= 0)
            writes[positionWriteIndex] = writes[positionWriteIndex] with { Value = positioned.Id };
        else
            writes.Add(new AttributeWrite(Id(), "Position", positioned.Id));

        var kernels = program.Kernels.ToList();
        kernels[kernelIndex] = kernel with { Nodes = nodes, Writes = writes };
        return program with { Kernels = kernels };
    }

    private static GpuNode BoxOffset(GpuSpawnShapeSettings shape, GpuNode seed,
        Func<int, GpuNode, GpuNode> random, AddNode add, ConstantNode constant)
    {
        var coordinates = new List<string>(3);
        for (var axis = 0; axis < 3; axis++)
        {
            var unit = random(axis, seed);
            var doubled = add(GpuOp.Mul, GpuType.Float, [unit.Id, constant(GpuType.Float, 2).Id]);
            var centered = add(GpuOp.Sub, GpuType.Float, [doubled.Id, constant(GpuType.Float, 1).Id]);
            var scaled = add(GpuOp.Mul, GpuType.Float, [centered.Id, constant(GpuType.Float, shape.HalfExtents[axis]).Id]);
            coordinates.Add(scaled.Id);
        }
        return add(GpuOp.Compose, GpuType.Float3, coordinates.ToArray());
    }

    private static GpuNode SphereOffset(GpuSpawnShapeSettings shape, GpuNode seed,
        Func<int, GpuNode, GpuNode> random, AddNode add, ConstantNode constant)
    {
        var radialSample = random(0, seed);
        var zSample = random(1, seed);
        var angleSample = random(2, seed);
        var third = constant(GpuType.Float, 1d / 3d);
        var unitRadius = add(GpuOp.Pow, GpuType.Float, [radialSample.Id, third.Id]);
        var radius = add(GpuOp.Mul, GpuType.Float,
            [unitRadius.Id, constant(GpuType.Float, shape.Radius).Id]);
        var z = add(GpuOp.Sub, GpuType.Float,
            [constant(GpuType.Float, 1).Id,
                add(GpuOp.Mul, GpuType.Float, [constant(GpuType.Float, 2).Id, zSample.Id]).Id]);
        var zSquared = add(GpuOp.Mul, GpuType.Float, [z.Id, z.Id]);
        var radialSquared = add(GpuOp.Sub, GpuType.Float, [constant(GpuType.Float, 1).Id, zSquared.Id]);
        var radial = add(GpuOp.Sqrt, GpuType.Float,
            [add(GpuOp.Max, GpuType.Float, [radialSquared.Id, constant(GpuType.Float, 0).Id]).Id]);
        var angle = add(GpuOp.Mul, GpuType.Float,
            [angleSample.Id, constant(GpuType.Float, Tau).Id]);
        var cosine = add(GpuOp.Cos, GpuType.Float, [angle.Id]);
        var sine = add(GpuOp.Sin, GpuType.Float, [angle.Id]);
        var x = add(GpuOp.Mul, GpuType.Float, [cosine.Id, radial.Id]);
        var y = add(GpuOp.Mul, GpuType.Float, [sine.Id, radial.Id]);
        var direction = add(GpuOp.Compose, GpuType.Float3, [x.Id, y.Id, z.Id]);
        return add(GpuOp.Mul, GpuType.Float3, [direction.Id, radius.Id]);
    }

    private static GpuNode CircleOffset(GpuSpawnShapeSettings shape, GpuNode seed,
        Func<int, GpuNode, GpuNode> random, AddNode add, ConstantNode constant)
    {
        var angle = add(GpuOp.Mul, GpuType.Float,
            [random(0, seed).Id, constant(GpuType.Float, Tau).Id]);
        var x = add(GpuOp.Mul, GpuType.Float,
            [add(GpuOp.Cos, GpuType.Float, [angle.Id]).Id, constant(GpuType.Float, shape.Radius).Id]);
        var y = add(GpuOp.Mul, GpuType.Float,
            [add(GpuOp.Sin, GpuType.Float, [angle.Id]).Id, constant(GpuType.Float, shape.Radius).Id]);
        return add(GpuOp.Compose, GpuType.Float3,
            [x.Id, y.Id, constant(GpuType.Float, 0).Id]);
    }
}
