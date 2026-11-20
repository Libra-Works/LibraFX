using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public sealed partial class VfxProgram
{
    public static VfxProgram Compile(GraphSnapshot graph) => Compile(graph, out _);

    public static VfxProgram Compile(GraphSnapshot graph, out byte[] renderConfiguration,
        IRenderResourceBindingContext? resourceContext = null, VfxCompileOptions? options = null)
    {
        ValidateForCompilation(graph);
        CpuParticleSchema.ValidateContext(graph);
        if (CpuParticleCompilation.Contains(graph))
        {
            throw new InvalidDataException("CPU Particle requires an effect package; export .lfxb with a matching updated SDK.");
        }
#if LIBERAFX_CONVERTER
        if (Flatten(graph).Any(node => GpuParticleEditorService.IsGpu(node.Kind)))
        {
            throw new InvalidDataException("GPU systems require executable GPU export from the editor; CPU-only conversion cannot discard them.");
        }
#else
        if (GpuProgramDeliveryService.Containers(graph).Any(node =>
                GpuParticleEditorService.ReadState(node).Fluid2D is not null))
        {
            throw new InvalidDataException("2D fluid requires an effect package; export .lfxb with matching updated SDK adapters.");
        }
#endif
        if (Flatten(graph).Any(node => GameIoSchema.IsInstanceNode(node.Kind)))
        {
            throw new InvalidDataException("Instance Game I/O requires an effect package; export with EffectBinary to .lfxb.");
        }
        if (Flatten(graph).Any(node => node.SignalKeys?.Any(key => key.Mode == SignalTimelineMode.Runtime) == true))
        {
            throw new InvalidDataException("Runtime Signal timeline keys require an effect package; export with EffectBinary to .lfxb.");
        }
        var starts = graph.Nodes.Where(node => node.Kind.Value == "OnStart").ToArray();
        if (starts.Length == 1 && !graph.Connections.Any(edge => edge.SourceNodeId == starts[0].Id))
            graph = graph with { Nodes = graph.Nodes.Where(node => node.Id != starts[0].Id).ToArray() };
        if (ContainsSpawnable(graph))
            throw new InvalidDataException("Compile Spawnable Groups with SpawnableGroupPackage so templates are not discarded.");
        if (RenderProgramBindings.HasGpuDrawMatrix(graph))
        {
            throw new InvalidDataException("A GPU Draw Matrix requires an effect package; export with EffectBinary to .lfxb.");
        }
        if (SkeletalPoseSchema.ContainsSource(graph))
            throw new NotSupportedException(
                "Raw CPU program compilation cannot resolve SOURCE skeletal poses. Compile a SpawnableGroupPackage for Unity or Unreal package playback.");
        if (Flatten(graph).Any(node => SpawnableGroupSchema.IsInput(node.Kind) ||
            SpawnableGroupSchema.IsOutput(node.Kind) || SpawnableGroupSchema.IsSignalInput(node.Kind) ||
            SpawnableGroupSchema.IsSignalOutput(node.Kind) || node.Kind.Value is "LifeTime" or "OnStart" or
                "InstanceRemaining" or "InstanceNormalizedAge" or "OwnerDestroy" or "OnDestroy" or "Delay"))
            throw new InvalidDataException("Lifecycle and Spawn boundary nodes require a Spawnable Group package.");
        return CompileForPackage(graph, out renderConfiguration, resourceContext: resourceContext, options: options);
    }

    internal static VfxProgram CompileForPackage(GraphSnapshot graph,
        IReadOnlyDictionary<Guid, Guid>? signalTypeOverrides = null) =>
        CompileForPackage(graph, out _, signalTypeOverrides);

    internal static VfxProgram CompileForPackage(GraphSnapshot graph, out byte[] renderConfiguration,
        IReadOnlyDictionary<Guid, Guid>? signalTypeOverrides = null,
        IRenderResourceBindingContext? resourceContext = null, VfxCompileOptions? options = null)
    {
        ValidateForCompilation(graph);
        graph = CpuParticleCompilation.LowerEmitters(graph);
        // Strip terminal observers before lowering; never run reachability pruning.
        if (options?.IncludePreviewDebugDraw != true) graph = PreviewDebugDraw.Strip(graph);
        graph = GroupExposedItems.Materialize(graph);
        ValidateForCompilation(graph);
        graph = SkeletalPoseSchema.Lower(graph);
        var lifeNodeId = graph.Nodes.SingleOrDefault(node => node.Kind.Value == "LifeTime")?.Id;
        var (loweredGameIo, gameIoPorts) = GameIoSchema.Lower(graph);
        graph = LowerLifecycle(loweredGameIo, lifeNodeId is not null);
#if LIBERAFX_CONVERTER
        GraphTypeDiagnostics.Validate(graph);
#else
        var typeProblems = GraphTypeDiagnostics.Analyze(graph);
        if (typeProblems.Count != 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, typeProblems.Select(problem =>
                LocalizationService.Shared.Get("Messages.GraphCompileMismatch",
                    problem.NodeId, problem.InputPortId ?? "input", problem.SourceNodeId,
                    problem.SourceType, problem.ExpectedType) +
                " " + GraphConversionPresentation.Describe(problem.Conversion))));
#endif
        graph = GroupBoundaryLowering.Lower(graph);
        if (lifeNodeId is { } lifeId)
            graph = graph with { Connections = graph.Connections.Concat(graph.Nodes
                .Where(node => node.Kind.Value is "LifecycleRuntimeRemaining" or "LifecycleRuntimeNormalizedAge")
                .Select(node => new GraphConnectionSnapshot(lifeId, node.Id, "duration"))).ToArray() };
        var matrixLowering = GraphPolymorphism.LowerWithOutputs(graph);
        graph = matrixLowering.Graph;
        gameIoPorts = gameIoPorts.Select(port =>
        {
            if (port.Width != 16) return port with { RuntimeNodeIds = [port.NodeId] };
            if (!matrixLowering.Outputs.TryGetValue((port.NodeId, "value"), out var outputs) ||
                outputs.Count != 4)
                throw new InvalidDataException($"Matrix Game I/O port '{port.Name}' did not lower to four rows.");
            return port with { RuntimeNodeIds = outputs.Select(output => output.NodeId).ToArray() };
        }).ToArray();
        renderConfiguration = RenderProgramBindings.CreateSceneForPackage(graph, 0, resourceContext, options);
        graph = SequenceSchema.Lower(graph);
        var nodes = Flatten(graph)
            .Where(node => node.Kind != GraphNodeKind.Comment && node.Kind != GraphNodeKind.GraphGroup &&
                !GpuParticleEditorService.IsGpu(node.Kind) &&
                !FlexiblePlaneDrawSchema.IsMetadata(node.Kind) &&
                !RenderProgramBindings.IsRenderNode(node.Kind))
            .ToArray();
        if (nodes.Length > MaxInstructions) throw new InvalidDataException("Too many runtime nodes.");
        var byId = nodes.ToDictionary(node => node.Id);
        var definitions = GraphNodeDefinition.All.ToDictionary(item => item.Kind);
        if (nodes.Any(node => definitions[node.Kind].Runtime is not null ||
                              node.Kind == GraphNodeKind.SpawnableGroup || node.Kind == GraphNodeKind.Signal ||
                              GraphEnumBranchSchema.TryGetValueDefinition(node.Kind, out _) ||
                              GraphEnumBranchSchema.TryGetSelector(node.Kind, out _, out _, out _) ||
                              node.Kind.Value is "CurrentPhase" or "PhaseTime" ||
                              GraphLogicalSchema.IsLogical(definitions[node.Kind])))
            return CompileWithCustomNodes(graph, nodes, definitions, gameIoPorts, signalTypeOverrides);
        foreach (var node in nodes)
            if (!TryOpcode(node.Kind.Value, out _))
                throw new InvalidDataException($"Node '{node.Name}' ({node.Kind}) is not supported by the runtime.");
        var incoming = graphConnections(graph, nodes).ToLookup(connection => connection.TargetNodeId);
        var outgoing = graphConnections(graph, nodes).ToLookup(connection => connection.SourceNodeId);
        var degree = nodes.ToDictionary(node => node.Id, node => incoming[node.Id].Count());
        var queue = new Queue<GraphNodeSnapshot>(nodes.Where(node => degree[node.Id] == 0));
        var order = new List<GraphNodeSnapshot>(nodes.Length);
        while (queue.TryDequeue(out var node))
        {
            order.Add(node);
            foreach (var connection in outgoing[node.Id])
                if (--degree[connection.TargetNodeId] == 0) queue.Enqueue(byId[connection.TargetNodeId]);
        }
        if (order.Count != nodes.Length) throw new InvalidDataException("The graph contains a cycle.");
        var indices = order.Select((node, index) => (node.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var typedCode = new VfxTypedInstruction[order.Count];
        var curveCode = new List<VfxCurveInstruction>();
        var needsTyped = false;
        for (var index = 0; index < order.Count; index++)
        {
            var node = order[index];
            TryOpcode(node.Kind.Value, out var opcode);
            var definition = definitions[node.Kind];
            var typeWidth = Width(node.PortType);
            needsTyped |= typeWidth != 1 || node.PortType == "Color" ||
                          (byte)opcode > (byte)VfxOpcode.Divide;
            if (node.PortCategory != GraphPortCategory.Parameter ||
                (opcode == VfxOpcode.Time && incoming[node.Id].Any()) ||
                (opcode == VfxOpcode.Scalar && incoming[node.Id].Any(connection => connection.TargetPortId is not null)) ||
                (IsArithmetic(opcode) && incoming[node.Id].Any(connection => connection.TargetPortId is not ("a" or "b"))) ||
                (IsSingleInput(opcode) && incoming[node.Id].Any(connection => connection.TargetPortId != "a")))
                throw new InvalidDataException($"Node '{node.Name}' has unsupported runtime ports.");

            var outputWidth = typeWidth;
            var defaultA = new double[4];
            var defaultB = new double[4];
            var inputAWidth = 0;
            var inputBWidth = 0;
            var inputA = -1;
            var inputB = -1;
            var inputASourceComponent = 0;
            var inputBSourceComponent = 0;
            var inputAIsColor = false;
            var inputBIsColor = false;

            double Parameter(string id)
            {
                var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == "float")
                    ?? throw new InvalidDataException($"Node '{node.Name}' is missing float parameter '{id}'.");
                var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == "float")?.Value
                    ?? parameter.DefaultValue;
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new InvalidDataException($"Node '{node.Name}' has an invalid float parameter '{id}'.");
                return number;
            }

            double[] VectorParameter(string id, int width)
            {
                var parameterType = $"vector{width}";
                var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == parameterType)
                    ?? throw new InvalidDataException($"Node '{node.Name}' is missing {parameterType} parameter '{id}'.");
                var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == parameterType)?.Value
                    ?? parameter.DefaultValue;
                if (!NodeParameter.IsValidValue(parameterType, value))
                    throw new InvalidDataException($"Node '{node.Name}' has an invalid vector parameter '{id}'.");
                return value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
            }

            int IntParameter(string id)
            {
                var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == "int")
                    ?? throw new InvalidDataException($"Node '{node.Name}' is missing Int32 parameter '{id}'.");
                var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == "int")?.Value
                    ?? parameter.DefaultValue;
                if (!NodeParameter.IsValidValue("int", value) || !value.TryGetInt32(out var number))
                    throw new InvalidDataException($"Node '{node.Name}' has an invalid Int32 parameter '{id}'.");
                return number;
            }

            int Input(string? port, int expectedWidth, out int sourceComponent, out bool sourceIsColor)
            {
                sourceComponent = 0;
                sourceIsColor = false;
                var matches = incoming[node.Id].Where(connection => connection.TargetPortId == port).ToArray();
                if (matches.Length > 1)
                    throw new InvalidDataException($"Node '{node.Name}' has multiple connections to one input.");
                if (matches.Length == 0) return -1;
                var source = byId[matches[0].SourceNodeId];
                var sourceDefinition = definitions[source.Kind];
                var sourceType = sourceDefinition.GetOutputPortType(matches[0].SourcePortId);
                if (sourceType is null)
                    throw new InvalidDataException($"Node '{node.Name}' is connected to an invalid source output.");
                var sourceWidth = Width(sourceType);
                if (sourceWidth != expectedWidth)
                    throw new InvalidDataException($"Node '{node.Name}' input '{port}' expects {expectedWidth} components.");
                sourceIsColor = sourceType == "Color";
                if (sourceDefinition.OutputPorts is { } sourcePorts)
                {
                    var portIndex = -1;
                    for (var index = 0; index < sourcePorts.Count; index++)
                        if (sourcePorts[index].Id == matches[0].SourcePortId) portIndex = index;
                    if (portIndex < 0 || portIndex > 3)
                        throw new InvalidDataException($"Node '{node.Name}' is connected to an invalid source output.");
                    sourceComponent = portIndex;
                }
                return indices[source.Id];
            }

            if (opcode == VfxOpcode.Int32)
            {
                defaultA[0] = IntParameter("value");
                inputA = Input(null, 1, out inputASourceComponent, out inputAIsColor);
                inputAWidth = inputA < 0 ? 0 : 1;
            }
            else if (opcode == VfxOpcode.FCurve)
            {
                if (node.Curve is null) throw new InvalidDataException($"Node '{node.Name}' has no F-Curve data.");
                curveCode.Add(new VfxCurveInstruction(node.Id, node.Curve.Keys.ToArray()));
            }
            else if (opcode is VfxOpcode.Scalar or VfxOpcode.Vector2 or VfxOpcode.Vector3 or VfxOpcode.Vector4 or VfxOpcode.Color)
            {
                var width = outputWidth;
                double[] values = node.PortType == "Float" ? [Parameter("value")] :
                    node.PortType == "Color" ? ReadColor(node, definition) : VectorParameter("value", width);
                Array.Copy(values, defaultA, values.Length);
                var inputPort = definition.InputPorts is null ? null : "a";
                inputA = Input(inputPort, width,
                    out inputASourceComponent, out inputAIsColor);
                inputAWidth = inputA < 0 ? 0 : width;
                if (node.PortType == "Float" && incoming[node.Id].Any(connection => connection.TargetPortId is not null))
                    throw new InvalidDataException($"Node '{node.Name}' has an unsupported runtime input.");
            }
            else if (opcode == VfxOpcode.Time)
            {
                outputWidth = 1;
            }
            else if (IsArithmetic(opcode))
            {
                var width = outputWidth;
                var integerArithmetic = IsInt32Arithmetic(opcode);
                double[] defaultsA = integerArithmetic ? [IntParameter("a")] :
                    width == 1 ? [Parameter("a")] : VectorParameter("a", width);
                double[] defaultsB = integerArithmetic ? [IntParameter("b")] :
                    width == 1 ? [Parameter("b")] : VectorParameter("b", width);
                Array.Copy(defaultsA, defaultA, defaultsA.Length);
                Array.Copy(defaultsB, defaultB, defaultsB.Length);
                inputA = Input("a", width, out inputASourceComponent, out inputAIsColor);
                inputB = Input("b", width, out inputBSourceComponent, out inputBIsColor);
                inputAWidth = inputA < 0 ? 0 : width;
                inputBWidth = inputB < 0 ? 0 : width;
            }
            else if (opcode is VfxOpcode.BreakVector2 or VfxOpcode.BreakVector3 or VfxOpcode.BreakVector4)
            {
                outputWidth = typeWidth;
                inputA = Input("a", outputWidth, out inputASourceComponent, out inputAIsColor);
                if (inputA < 0) throw new InvalidDataException($"Node '{node.Name}' requires an input connection.");
                inputAWidth = outputWidth;
            }
            else
            {
                var setup = Conversion(opcode, node, definition, incoming[node.Id].Any());
                outputWidth = setup.OutputWidth;
                inputA = Input("a", setup.InputWidth, out inputASourceComponent, out inputAIsColor);
                inputAWidth = setup.InputWidth;
                Array.Copy(setup.Parameters, defaultA, setup.Parameters.Length);
            }

            if (outputWidth != typeWidth)
                throw new InvalidDataException($"Node '{node.Name}' declares an incompatible output type.");
            var typeFlags = (byte)(node.PortType == "Color" ? 1 : 0);
            if (inputAIsColor) typeFlags |= 2;
            if (inputBIsColor) typeFlags |= 4;
            typedCode[index] = new(node.Id, opcode, outputWidth, inputAWidth, inputBWidth,
                typeFlags, inputASourceComponent, inputBSourceComponent,
                inputA, inputB, defaultA, defaultB);
        }

        ValidateCurveBudget(curveCode);
        var valueTypes = order.Select((node, index) =>
            CreateValueTypes(typedCode[index].Opcode, node, definitions[node.Kind])).ToArray();
        var hasInt32 = valueTypes.Any(item => item.OutputType == GraphValueType.Int32 ||
            item.InputTypes.Contains(GraphValueType.Int32));
        var version = hasInt32 ? (ushort)10 : gameIoPorts.Count != 0 ? (ushort)7 :
            curveCode.Count != 0 ? (ushort)4 : needsTyped ? (ushort)2 : (ushort)1;
        var legacy = typedCode.Select(item => new VfxInstruction(item.NodeId, item.Opcode, item.InputA, item.InputB,
            item.DefaultA[0], item.DefaultB[0])).ToArray();
        return new VfxProgram(legacy, typedCode, [], curveCode.ToArray(), version, gameIoPorts,
            valueTypes: valueTypes);
    }
}
