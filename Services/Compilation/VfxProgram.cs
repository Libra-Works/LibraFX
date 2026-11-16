using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public enum VfxOpcode : byte
{
    Scalar = 1, Time = 2, Add = 3, Subtract = 4, Multiply = 5, Divide = 6,
    Vector2 = 7, Vector3 = 8, Vector4 = 9, Color = 10,
    AddVector2 = 11, SubtractVector2 = 12, MultiplyVector2 = 13, DivideVector2 = 14,
    AddVector3 = 15, SubtractVector3 = 16, MultiplyVector3 = 17, DivideVector3 = 18,
    AddVector4 = 19, SubtractVector4 = 20, MultiplyVector4 = 21, DivideVector4 = 22,
    ScalarToVector2 = 23, ScalarToVector3 = 24, ScalarToVector4 = 25,
    BreakVector2 = 26, BreakVector3 = 27, BreakVector4 = 28,
    Vector2ToVector3 = 29, Vector2ToVector4 = 30, Vector3ToVector2 = 31,
    Vector3ToVector4 = 32, Vector4ToVector2 = 33, Vector4ToVector3 = 34,
    MaskVector2 = 35, MaskVector3 = 36, MaskVector4 = 37,
    ColorToVector4 = 38, Vector4ToColor = 39, FCurve = 42
}

public readonly record struct VfxInstruction(
    Guid NodeId, VfxOpcode Opcode, int InputA, int InputB, double DefaultA, double DefaultB);

public readonly record struct VfxTypedInstruction(
    Guid NodeId, VfxOpcode Opcode, int OutputWidth, int InputAWidth, int InputBWidth, byte TypeFlags,
    int InputASourceComponent, int InputBSourceComponent, int InputA, int InputB,
    double[] DefaultA, double[] DefaultB);

public readonly record struct VfxCustomInput(
    int SourceIndex, int SourceWidth, int SourceComponent, bool IsColor, double[] Defaults);

public readonly record struct VfxCustomInstruction(
    Guid NodeId, byte Opcode, int OutputWidth, bool OutputIsColor, Guid TypeId, int AbiVersion,
    int BranchCount, VfxCustomInput[] Inputs, int[] BranchTargets, int[][] BranchAdditionalTargets);

public readonly record struct VfxCurveInstruction(Guid NodeId, FCurveKey[] Keys);

public sealed class VfxProgram
{
    private const int HeaderSize = 12;
    private const int InstructionSize = 44;
    private const int TypedHeaderSize = 16;
    private const int TypedInstructionSize = 96;
    private const int MaxInstructions = 10000;
    private const int CurveInstructionSize = 256;
    private const int CurveKeySize = 40;
    private const int MaxCurveKeysTotal = 100000;
    private const int MaxSignalRoutes = 1000000;
    private const int GameIoHeaderSize = 8;
    private const int GameIoPortRecordSize = 72;
    private readonly VfxInstruction[] _instructions;
    private readonly VfxTypedInstruction[] _typedInstructions;
    private readonly VfxCustomInstruction[] _customInstructions;
    private readonly VfxCurveInstruction[] _curveInstructions;
    private readonly GameIoPort[] _gameIoPorts;
    private readonly ushort _version;

    public IReadOnlyList<VfxInstruction> Instructions { get; }
    public IReadOnlyList<VfxTypedInstruction> TypedInstructions { get; }
    public IReadOnlyList<VfxCustomInstruction> CustomInstructions { get; }
    public IReadOnlyList<VfxCurveInstruction> CurveInstructions { get; }
    public IReadOnlyList<GameIoPort> GameIoPorts { get; }
    public ushort BinaryVersion => _version;
    public int WorkSizeBytes => checked(_instructions.Length * (_version == 1 ? sizeof(double) : 4 * sizeof(double)) +
        ((_customInstructions.Sum(item => item.BranchAdditionalTargets.Sum(targets => targets.Length)) + 1) / 2) * sizeof(double));

    private VfxProgram(VfxInstruction[] instructions, VfxTypedInstruction[] typedInstructions,
        VfxCustomInstruction[] customInstructions, VfxCurveInstruction[] curveInstructions, ushort version,
        IReadOnlyList<GameIoPort>? gameIoPorts = null)
    {
        _instructions = instructions;
        _typedInstructions = typedInstructions;
        _customInstructions = customInstructions;
        _curveInstructions = curveInstructions;
        _gameIoPorts = gameIoPorts?.ToArray() ?? [];
        _version = version;
        Instructions = Array.AsReadOnly(instructions);
        TypedInstructions = Array.AsReadOnly(typedInstructions);
        CustomInstructions = Array.AsReadOnly(customInstructions);
        CurveInstructions = Array.AsReadOnly(curveInstructions);
        GameIoPorts = Array.AsReadOnly(_gameIoPorts);
    }

    public static VfxProgram Compile(GraphSnapshot graph)
    {
        ValidateForCompilation(graph);
        if (ContainsSpawnable(graph))
            throw new InvalidDataException("Compile Spawnable Groups with SpawnableGroupPackage so templates are not discarded.");
        if (Flatten(graph).Any(node => SpawnableGroupSchema.IsInput(node.Kind) ||
            SpawnableGroupSchema.IsOutput(node.Kind) || node.Kind.Value is "LifeTime" or "OnStart" or "InstanceAge"))
            throw new InvalidDataException("Lifecycle and Spawn boundary nodes require a Spawnable Group package.");
        return CompileForPackage(graph);
    }

    internal static VfxProgram CompileForPackage(GraphSnapshot graph)
    {
        ValidateForCompilation(graph);
        graph = GroupExposedItems.Materialize(graph);
        ValidateForCompilation(graph);
        var (loweredGameIo, gameIoPorts) = GameIoSchema.Lower(graph);
        graph = LowerLifecycle(loweredGameIo);
#if LIBERAFX_CONVERTER
        GraphTypeDiagnostics.Validate(graph);
#else
        var typeProblems = GraphTypeDiagnostics.Analyze(graph);
        if (typeProblems.Count != 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, typeProblems.Select(problem =>
                LocalizationService.Shared.Get("Messages.GraphCompileMismatch",
                    problem.NodeId, problem.InputPortId ?? "input", problem.SourceNodeId,
                    problem.SourceType, problem.ExpectedType))));
#endif
        graph = GroupBoundaryLowering.Lower(graph);
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
        _ = RenderProgramBindings.CreateScene(graph, 0);
        var nodes = Flatten(graph)
            .Where(node => node.Kind != GraphNodeKind.Comment && node.Kind != GraphNodeKind.GraphGroup &&
                !GpuParticleEditorService.IsGpu(node.Kind) &&
                !RenderProgramBindings.IsRenderNode(node.Kind))
            .ToArray();
        if (nodes.Length > MaxInstructions) throw new InvalidDataException("Too many runtime nodes.");
        var byId = nodes.ToDictionary(node => node.Id);
        var definitions = GraphNodeDefinition.All.ToDictionary(item => item.Kind);
        if (nodes.Any(node => definitions[node.Kind].Runtime is not null ||
                              node.Kind == GraphNodeKind.SpawnableGroup || node.Kind == GraphNodeKind.Signal))
            return CompileWithCustomNodes(graph, nodes, definitions, gameIoPorts);
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

            if (opcode == VfxOpcode.FCurve)
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
                var parameterType = width == 1 ? null : $"vector{width}";
                double[] defaultsA = parameterType is null ? [Parameter("a")] : VectorParameter("a", width);
                double[] defaultsB = parameterType is null ? [Parameter("b")] : VectorParameter("b", width);
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
        var version = gameIoPorts.Count != 0 ? (ushort)7 :
            curveCode.Count != 0 ? (ushort)4 : needsTyped ? (ushort)2 : (ushort)1;
        var legacy = typedCode.Select(item => new VfxInstruction(item.NodeId, item.Opcode, item.InputA, item.InputB,
            item.DefaultA[0], item.DefaultB[0])).ToArray();
        return new VfxProgram(legacy, typedCode, [], curveCode.ToArray(), version, gameIoPorts);
    }

    private static VfxProgram CompileWithCustomNodes(
        GraphSnapshot graph, GraphNodeSnapshot[] nodes,
        IReadOnlyDictionary<GraphNodeKind, GraphNodeDefinition> definitions,
        IReadOnlyList<GameIoPort> gameIoPorts)
    {
        if (nodes.Length > MaxInstructions) throw new InvalidDataException("Too many runtime nodes.");
        var byId = nodes.ToDictionary(node => node.Id);
        var connections = graphConnections(graph, nodes).ToArray();
        var incoming = connections.ToLookup(connection => connection.TargetNodeId);
        var outgoing = connections.ToLookup(connection => connection.SourceNodeId);
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
        var indices = order.Select((node, index) => (node.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var typedCode = new VfxTypedInstruction[order.Count];
        var customCode = new List<VfxCustomInstruction>();
        var curveCode = new List<VfxCurveInstruction>();
        var effective = nodes.ToDictionary(node => node.Id, node =>
        {
            var definition = SpawnableGroupSchema.Effective(definitions[node.Kind], node.ChildGraph);
            if (node.Kind == GraphNodeKind.SpawnableGroup)
                return definition with { TypeId = SpawnableGroupPackage.SignalTypeId(node.Id, node.ChildGraph!),
                    OutputPorts = [new GraphOutputPortDefinition("then", "Then", "Signal", GraphPortCategory.Signal)],
                    Runtime = new GraphRuntimeDefinition("custom", 1, "signal") };
            if (node.Kind == GraphNodeKind.Signal)
                return definition with { TypeId = SpawnableGroupPackage.SignalPassthroughTypeId,
                    Runtime = new GraphRuntimeDefinition("custom", 1, "signal"),
                    InputPorts = [new GraphInputPortDefinition("signal", "Signal", "Signal", GraphPortCategory.Signal)],
                    OutputPorts = [new GraphOutputPortDefinition("then", "Then", "Signal", GraphPortCategory.Signal)] };
            return definition;
        });

        int Input(GraphNodeSnapshot node, GraphNodeDefinition definition, string? port, int expectedWidth,
            out int sourceComponent, out bool sourceIsColor)
        {
            sourceComponent = 0;
            sourceIsColor = false;
            var matches = incoming[node.Id].Where(connection => connection.TargetPortId == port).ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException($"Node '{node.Name}' has multiple connections to one input.");
            if (matches.Length == 0) return -1;
            var source = byId[matches[0].SourceNodeId];
            var sourceDefinition = effective[source.Id];
            var sourceType = sourceDefinition.GetOutputPortType(matches[0].SourcePortId);
            if (sourceType is null)
                throw new InvalidDataException($"Node '{node.Name}' is connected to an invalid source output.");
            var sourceWidth = Width(sourceType);
            if (sourceDefinition.OutputPorts is { } sourcePorts)
            {
                var portIndex = -1;
                for (var index = 0; index < sourcePorts.Count; index++)
                    if (sourcePorts[index].Id == matches[0].SourcePortId) portIndex = index;
                if (portIndex < 0 || portIndex > 3)
                    throw new InvalidDataException($"Node '{node.Name}' is connected to an invalid source output.");
                if (source.Kind.Value.StartsWith("BreakVector", StringComparison.Ordinal))
                {
                    if (expectedWidth != 1) throw new InvalidDataException($"Node '{node.Name}' input width mismatch.");
                    sourceComponent = portIndex;
                }
                else if (portIndex != 0)
                    throw new InvalidDataException($"Node '{node.Name}' is connected to an unsupported source output.");
            }
            if (sourceWidth != expectedWidth)
                throw new InvalidDataException($"Node '{node.Name}' input '{port}' expects {expectedWidth} components.");
            sourceIsColor = sourceType == "Color";
            return indices[source.Id];
        }

        for (var index = 0; index < order.Count; index++)
        {
            var node = order[index];
            var definition = effective[node.Id];
            if (definition.Runtime is { } runtime)
            {
                var isSignal = runtime.Execution == "signal";
                var inputPorts = (definition.InputPorts ?? []).Where(port =>
                    definition.GetInputPortCategory(port.Id) == GraphPortCategory.Parameter).ToArray();
                var signalInputs = (definition.InputPorts ?? []).Where(port =>
                    definition.GetInputPortCategory(port.Id) == GraphPortCategory.Signal).ToArray();
                var parameterOutputs = (definition.OutputPorts ?? []).Where(port =>
                    definition.GetOutputPortCategory(port.Id) == GraphPortCategory.Parameter).ToArray();
                var signalOutputs = (definition.OutputPorts ?? []).Where(port =>
                    definition.GetOutputPortCategory(port.Id) == GraphPortCategory.Signal).ToArray();
                if (inputPorts.Length > 4 || signalInputs.Length > 1 ||
                    (isSignal ? parameterOutputs.Length != 0 || signalOutputs.Length is < 1 or > 4 :
                        signalInputs.Length != 0 || signalOutputs.Length != 0 || parameterOutputs.Length != 1))
                    throw new InvalidDataException($"Custom node '{node.Name}' has unsupported port layout.");
                var customOutputWidth = isSignal ? 0 : Width(definition.GetOutputPortType(parameterOutputs[0].Id)!);
                var outputColor = !isSignal && definition.GetOutputPortType(parameterOutputs[0].Id) == "Color";
                var customInputs = new VfxCustomInput[inputPorts.Length];
                for (var inputIndex = 0; inputIndex < inputPorts.Length; inputIndex++)
                {
                    var port = inputPorts[inputIndex];
                    var portType = definition.GetInputPortType(port.Id)!;
                    var width = Width(portType);
                    var defaults = new double[4];
                    var parameterType = portType switch
                    {
                        "Float" => "float", "FloatX2" => "vector2", "FloatX3" => "vector3",
                        "FloatX4" => "vector4", "Color" => "color",
                        _ => throw new InvalidDataException($"Custom input '{port.Id}' is not numeric.")
                    };
                    var parameter = definition.Parameters.FirstOrDefault(item =>
                        item.Id == port.Id && item.Type == parameterType);
                    var value = parameter is null ? port.DefaultValue :
                        node.Parameters?.FirstOrDefault(item => item.Id == port.Id && item.Type == parameterType)?.Value
                        ?? parameter.DefaultValue;
                    if (value is { } portDefault)
                    {
                        if (!NodeParameter.IsValidValue(parameterType, portDefault))
                            throw new InvalidDataException($"Custom input '{port.Id}' has an invalid default.");
                        if (width == 1) defaults[0] = portDefault.GetDouble();
                        else
                            for (var lane = 0; lane < width; lane++)
                                defaults[lane] = portDefault[lane].GetDouble();
                    }
                    else if (portType == "Color") defaults[3] = 1;
                    var source = Input(node, definition, port.Id, width, out var sourceComponent, out var sourceColor);
                    var inputIsColor = portType == "Color";
                    if (source >= 0 && sourceColor != inputIsColor)
                        throw new InvalidDataException($"Custom input '{port.Id}' has incompatible Color identity.");
                    customInputs[inputIndex] = new VfxCustomInput(source, source < 0 ? 0 : width,
                        sourceComponent, inputIsColor, defaults);
                }

                var branchTargets = new int[signalOutputs.Length];
                var additionalTargets = new int[signalOutputs.Length][];
                for (var branch = 0; branch < signalOutputs.Length; branch++)
                {
                    var targets = outgoing[node.Id].Where(connection =>
                        connection.SourcePortId == (node.Kind == GraphNodeKind.Signal ? null : signalOutputs[branch].Id)).ToArray();
                    var targetIndices = targets.Select(edge =>
                    {
                        var target = byId[edge.TargetNodeId];
                        if (effective[target.Id].Runtime?.Execution != "signal")
                            throw new InvalidDataException("Signal branches must target registered signal nodes.");
                        return indices[target.Id];
                    }).ToArray();
                    branchTargets[branch] = targetIndices.Length == 0 ? -1 : targetIndices[0];
                    additionalTargets[branch] = targetIndices.Skip(1).ToArray();
                }
                customCode.Add(new VfxCustomInstruction(node.Id,
                    isSignal ? (byte)41 : (byte)40, customOutputWidth, outputColor, definition.TypeId,
                    runtime.AbiVersion, signalOutputs.Length, customInputs, branchTargets, additionalTargets));
                typedCode[index] = new(node.Id, (VfxOpcode)(isSignal ? 41 : 40), customOutputWidth,
                    0, 0, outputColor ? (byte)1 : (byte)0, 0, 0, -1, -1,
                    new double[4], new double[4]);
                continue;
            }

            if (!TryOpcode(node.Kind.Value, out var opcode))
                throw new InvalidDataException($"Node '{node.Name}' ({node.Kind}) is not supported by the runtime.");
            var typeWidth = Width(node.PortType);
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
                    throw new InvalidDataException($"Node '{node.Name}' has invalid float parameter '{id}'.");
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

            if (opcode == VfxOpcode.FCurve)
            {
                if (node.Curve is null) throw new InvalidDataException($"Node '{node.Name}' has no F-Curve data.");
                curveCode.Add(new VfxCurveInstruction(node.Id, node.Curve.Keys.ToArray()));
            }
            else if (opcode is VfxOpcode.Scalar or VfxOpcode.Vector2 or VfxOpcode.Vector3 or VfxOpcode.Vector4 or VfxOpcode.Color)
            {
                var width = outputWidth;
                var values = node.PortType == "Float" ? [Parameter("value")] :
                    node.PortType == "Color" ? ReadColor(node, definition) : VectorParameter("value", width);
                Array.Copy(values, defaultA, values.Length);
                var inputPort = definition.InputPorts is null ? null : "a";
                inputA = Input(node, definition, inputPort, width, out inputASourceComponent, out inputAIsColor);
                inputAWidth = inputA < 0 ? 0 : width;
                if (node.PortType == "Float" &&
                    incoming[node.Id].Any(connection => connection.TargetPortId is not null))
                    throw new InvalidDataException($"Node '{node.Name}' has an unsupported runtime input.");
            }
            else if (opcode == VfxOpcode.Time) outputWidth = 1;
            else if (IsArithmetic(opcode))
            {
                var width = outputWidth;
                var defaultsA = width == 1 ? [Parameter("a")] : VectorParameter("a", width);
                var defaultsB = width == 1 ? [Parameter("b")] : VectorParameter("b", width);
                Array.Copy(defaultsA, defaultA, defaultsA.Length);
                Array.Copy(defaultsB, defaultB, defaultsB.Length);
                inputA = Input(node, definition, "a", width, out inputASourceComponent, out inputAIsColor);
                inputB = Input(node, definition, "b", width, out inputBSourceComponent, out inputBIsColor);
                inputAWidth = inputA < 0 ? 0 : width;
                inputBWidth = inputB < 0 ? 0 : width;
            }
            else if (opcode is VfxOpcode.BreakVector2 or VfxOpcode.BreakVector3 or VfxOpcode.BreakVector4)
            {
                inputA = Input(node, definition, "a", outputWidth,
                    out inputASourceComponent, out inputAIsColor);
                if (inputA < 0) throw new InvalidDataException($"Node '{node.Name}' requires an input connection.");
                inputAWidth = outputWidth;
            }
            else
            {
                var setup = Conversion(opcode, node, definition, incoming[node.Id].Any());
                outputWidth = setup.OutputWidth;
                inputA = Input(node, definition, "a", setup.InputWidth,
                    out inputASourceComponent, out inputAIsColor);
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
        var legacy = typedCode.Select(item => new VfxInstruction(item.NodeId, item.Opcode, item.InputA, item.InputB,
            item.DefaultA[0], item.DefaultB[0])).ToArray();
        ValidateCurveBudget(curveCode);
        var extraRoutes = customCode.Sum(item => item.BranchAdditionalTargets.Sum(targets => (long)targets.Length));
        if (extraRoutes > MaxSignalRoutes) throw new InvalidDataException("The graph contains too many Signal routes.");
        return new VfxProgram(legacy, typedCode, customCode.ToArray(), curveCode.ToArray(),
            gameIoPorts.Count != 0 ? (ushort)7 : extraRoutes != 0 ? (ushort)5 : curveCode.Count != 0 ? (ushort)4 : (ushort)3,
            gameIoPorts);
    }

    public byte[] ToBinary()
    {
        if (_version == 1)
        {
            var bytes = new byte[checked(HeaderSize + _instructions.Length * InstructionSize)];
            "LFXB"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
            bytes[6] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), _instructions.Length);
            for (var index = 0; index < _instructions.Length; index++)
            {
                var span = bytes.AsSpan(HeaderSize + index * InstructionSize, InstructionSize);
                var instruction = _instructions[index];
                instruction.NodeId.TryWriteBytes(span, bigEndian: true, out _);
                span[16] = (byte)instruction.Opcode;
                BinaryPrimitives.WriteInt32LittleEndian(span[20..], instruction.InputA);
                BinaryPrimitives.WriteInt32LittleEndian(span[24..], instruction.InputB);
                BinaryPrimitives.WriteDoubleLittleEndian(span[28..], instruction.DefaultA);
                BinaryPrimitives.WriteDoubleLittleEndian(span[36..], instruction.DefaultB);
            }
            return bytes;
        }

        var instructionSize = _version >= 3 ? CurveInstructionSize : TypedInstructionSize;
        var instructionBytes = checked(TypedHeaderSize + _typedInstructions.Length * instructionSize);
        var curveBytes = checked(_curveInstructions.Sum(item => item.Keys.Length) * CurveKeySize);
        var routeBytes = checked(_customInstructions.Sum(item =>
            item.BranchAdditionalTargets.Sum(targets => targets.Length)) * sizeof(int));
        var gameIoBytes = _version >= 6 ? CreateGameIoMetadata(_gameIoPorts) : [];
        var typedBytes = new byte[checked(instructionBytes + curveBytes + routeBytes + gameIoBytes.Length)];
        "LFXB"u8.CopyTo(typedBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(typedBytes.AsSpan(4), _version);
        typedBytes[6] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(typedBytes.AsSpan(8), _typedInstructions.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(typedBytes.AsSpan(12), (ushort)instructionSize);
        var customById = _customInstructions.ToDictionary(item => item.NodeId);
        var curvesById = _curveInstructions.ToDictionary(item => item.NodeId);
        var curveOffset = 0;
        var routeOffset = 0;
        for (var index = 0; index < _typedInstructions.Length; index++)
        {
            var span = typedBytes.AsSpan(TypedHeaderSize + index * instructionSize, instructionSize);
            var instruction = _typedInstructions[index];
            instruction.NodeId.TryWriteBytes(span, bigEndian: true, out _);
            if (_version >= 3 && customById.TryGetValue(instruction.NodeId, out var custom))
            {
                span[16] = custom.Opcode;
                span[17] = (byte)custom.OutputWidth;
                span[18] = (byte)custom.Inputs.Length;
                span[19] = (byte)custom.BranchCount;
                span[20] = custom.OutputIsColor ? (byte)1 : (byte)0;
                custom.TypeId.TryWriteBytes(span[24..], bigEndian: true, out _);
                BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)custom.AbiVersion);
                for (var input = 0; input < custom.Inputs.Length; input++)
                {
                    var item = custom.Inputs[input];
                    var inputSpan = span[(48 + input * 8)..];
                    BinaryPrimitives.WriteInt32LittleEndian(inputSpan, item.SourceIndex);
                    inputSpan[4] = (byte)item.SourceWidth;
                    inputSpan[5] = (byte)item.SourceComponent;
                    inputSpan[6] = item.IsColor ? (byte)1 : (byte)0;
                    for (var lane = 0; lane < 4; lane++)
                        BinaryPrimitives.WriteDoubleLittleEndian(span[(80 + input * 32 + lane * 8)..],
                            item.Defaults[lane]);
                }
                for (var input = custom.Inputs.Length; input < 4; input++)
                    BinaryPrimitives.WriteInt32LittleEndian(span[(48 + input * 8)..], -1);
                for (var branch = 0; branch < 4; branch++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(span[(208 + branch * 4)..],
                        branch < custom.BranchCount ? custom.BranchTargets[branch] : -1);
                    if (_version < 5 || branch >= custom.BranchCount) continue;
                    var extra = custom.BranchAdditionalTargets[branch];
                    if (extra.Length == 0) continue;
                    BinaryPrimitives.WriteInt32LittleEndian(span[(224 + branch * 8)..], routeOffset);
                    BinaryPrimitives.WriteInt32LittleEndian(span[(228 + branch * 8)..], extra.Length);
                    foreach (var target in extra)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(typedBytes.AsSpan(instructionBytes + curveBytes + routeOffset), target);
                        routeOffset += sizeof(int);
                    }
                }
                continue;
            }
            span[16] = (byte)instruction.Opcode;
            span[17] = (byte)instruction.OutputWidth;
            span[18] = (byte)instruction.InputAWidth;
            span[19] = (byte)instruction.InputBWidth;
            span[20] = instruction.TypeFlags;
            span[21] = (byte)instruction.InputASourceComponent;
            span[22] = (byte)instruction.InputBSourceComponent;
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], instruction.InputA);
            BinaryPrimitives.WriteInt32LittleEndian(span[28..], instruction.InputB);
            for (var component = 0; component < 4; component++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(span[(32 + component * 8)..], instruction.DefaultA[component]);
                BinaryPrimitives.WriteDoubleLittleEndian(span[(64 + component * 8)..], instruction.DefaultB[component]);
            }
            if (instruction.Opcode == VfxOpcode.FCurve)
            {
                var curve = curvesById[instruction.NodeId];
                BinaryPrimitives.WriteInt32LittleEndian(span[224..], curveOffset);
                BinaryPrimitives.WriteInt32LittleEndian(span[228..], curve.Keys.Length);
                foreach (var key in curve.Keys)
                {
                    var keySpan = typedBytes.AsSpan(instructionBytes + curveOffset, CurveKeySize);
                    BinaryPrimitives.WriteDoubleLittleEndian(keySpan, key.TimeSeconds);
                    BinaryPrimitives.WriteDoubleLittleEndian(keySpan[8..], key.Value);
                    BinaryPrimitives.WriteDoubleLittleEndian(keySpan[16..], key.InSlope);
                    BinaryPrimitives.WriteDoubleLittleEndian(keySpan[24..], key.OutSlope);
                    keySpan[32] = (byte)key.Interpolation;
                    curveOffset += CurveKeySize;
                }
            }
        }
        gameIoBytes.CopyTo(typedBytes, instructionBytes + curveBytes + routeBytes);
        return typedBytes;
    }

    private static byte[] CreateGameIoMetadata(IReadOnlyList<GameIoPort> ports)
    {
        if (ports.Count == 0) return [];
        var encodedNames = ports.Select(port => Encoding.UTF8.GetBytes(port.Name)).ToArray();
        var size = checked(GameIoHeaderSize + encodedNames.Sum(name => GameIoPortRecordSize + name.Length + 1));
        var bytes = new byte[size];
        "LFIO"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)ports.Count));
        var offset = GameIoHeaderSize;
        for (var index = 0; index < ports.Count; index++)
        {
            var port = ports[index];
            var name = encodedNames[index];
            var runtimeNodeIds = port.RuntimeNodeIds ?? [port.NodeId];
            if (runtimeNodeIds.Count is not (1 or 4) || name.Length is 0 or > 128 ||
                (port.Width == 16) != (runtimeNodeIds.Count == 4))
                throw new InvalidDataException($"Game I/O port '{port.Name}' has invalid runtime bindings.");
            for (var row = 0; row < runtimeNodeIds.Count; row++)
                runtimeNodeIds[row].TryWriteBytes(bytes.AsSpan(offset + row * 16), bigEndian: true, out _);
            bytes[offset + 64] = (byte)port.Kind;
            bytes[offset + 65] = checked((byte)port.Width);
            bytes[offset + 66] = port.IsColor ? (byte)1 : (byte)0;
            bytes[offset + 67] = checked((byte)runtimeNodeIds.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 68), checked((ushort)name.Length));
            name.CopyTo(bytes, offset + GameIoPortRecordSize);
            offset += GameIoPortRecordSize + name.Length + 1;
        }
        return bytes;
    }

    private static void ValidateCurveBudget(IReadOnlyCollection<VfxCurveInstruction> curves)
    {
        if (curves.Sum(curve => curve.Keys.Length) > MaxCurveKeysTotal)
            throw new InvalidDataException("The graph contains too many F-Curve keyframes.");
    }

    private static double[] ReadColor(GraphNodeSnapshot node, GraphNodeDefinition definition)
    {
        var parameter = definition.Parameters.FirstOrDefault(item => item.Id == "value" && item.Type == "color")
            ?? throw new InvalidDataException($"Node '{node.Name}' is missing color parameter 'value'.");
        var value = node.Parameters?.FirstOrDefault(item => item.Id == "value" && item.Type == "color")?.Value
            ?? parameter.DefaultValue;
        if (!NodeParameter.IsValidValue("color", value))
            throw new InvalidDataException($"Node '{node.Name}' has an invalid color parameter.");
        return value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
    }

    private static (int OutputWidth, int InputWidth, double[] Parameters) Conversion(
        VfxOpcode opcode, GraphNodeSnapshot node, GraphNodeDefinition definition, bool connected)
    {
        var sourceWidth = opcode switch
        {
            VfxOpcode.ScalarToVector2 or VfxOpcode.ScalarToVector3 or VfxOpcode.ScalarToVector4 => 1,
            VfxOpcode.Vector2ToVector3 or VfxOpcode.Vector2ToVector4 => 2,
            VfxOpcode.Vector3ToVector2 or VfxOpcode.Vector3ToVector4 => 3,
            VfxOpcode.Vector4ToVector2 or VfxOpcode.Vector4ToVector3 or
                VfxOpcode.MaskVector2 or VfxOpcode.MaskVector3 or VfxOpcode.MaskVector4 or
                VfxOpcode.ColorToVector4 or VfxOpcode.Vector4ToColor => 4,
            _ => throw new InvalidDataException($"Node '{node.Name}' is not a supported conversion.")
        };
        if (!connected) throw new InvalidDataException($"Node '{node.Name}' requires an input connection.");
        var outputWidth = Width(node.PortType);
        var parameters = new double[4];
        if (opcode is VfxOpcode.Vector2ToVector3 or VfxOpcode.Vector2ToVector4 or
                 VfxOpcode.Vector3ToVector4)
        {
            parameters[0] = FloatParameter(node, definition, "fill");
        }
        else if (opcode is VfxOpcode.MaskVector2 or VfxOpcode.MaskVector3 or VfxOpcode.MaskVector4)
        {
            var parameter = definition.Parameters.FirstOrDefault(item => item.Id == "channels" && item.Type == "string")
                ?? throw new InvalidDataException($"Node '{node.Name}' is missing string parameter 'channels'.");
            var channels = node.Parameters?.FirstOrDefault(item => item.Id == "channels" && item.Type == "string")?.Value
                ?? parameter.DefaultValue;
            if (channels.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Node '{node.Name}' has invalid channels.");
            var text = channels.GetString()!;
            if (text.Length != outputWidth || text.Any(channel => channel is not ('x' or 'y' or 'z' or 'w')))
                throw new InvalidDataException($"Node '{node.Name}' channels must contain exactly {outputWidth} of x, y, z, or w.");
            for (var index = 0; index < text.Length; index++) parameters[index] = "xyzw".IndexOf(text[index]);
        }
        return (outputWidth, sourceWidth, parameters);
    }

    private static double FloatParameter(GraphNodeSnapshot node, GraphNodeDefinition definition, string id)
    {
        var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == "float")
            ?? throw new InvalidDataException($"Node '{node.Name}' is missing float parameter '{id}'.");
        var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == "float")?.Value
            ?? parameter.DefaultValue;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException($"Node '{node.Name}' has invalid parameter '{id}'.");
        return number;
    }

    private static IEnumerable<GraphNodeSnapshot> Flatten(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            yield return node;
            if (node.ChildGraph is not null && !GpuParticleEditorService.IsGpu(node.Kind) &&
                node.Kind != GraphNodeKind.SpawnableGroup)
                foreach (var child in Flatten(node.ChildGraph))
                    yield return child;
        }
    }

    internal static void ValidateForCompilation(GraphSnapshot graph)
    {
        // Validate shape before any schema/pin lookup can enumerate a malformed
        // Spawnable child. Do not turn null/missing collections into an empty,
        // successfully compiled effect.
        static void Shape(GraphSnapshot? current, int depth)
        {
            if (current is null || current.Nodes is null || current.Connections is null)
                throw new InvalidDataException("A graph requires non-null nodes and connections collections.");
            if (depth > 12) throw new InvalidDataException("The graph hierarchy is too deep.");
            foreach (var node in current.Nodes)
            {
                if (node is null) throw new InvalidDataException("The graph contains a null node.");
                if (node.ChildGraph is { } child) Shape(child, depth + 1);
            }
        }
        Shape(graph, 0);
        GraphValidator.Validate(graph);
    }

    private static GraphSnapshot LowerLifecycle(GraphSnapshot graph)
    {
        var starts = graph.Nodes.Where(node => node.Kind.Value == "OnStart").Select(node => node.Id).ToHashSet();
        var lives = graph.Nodes.Where(node => node.Kind.Value == "LifeTime").Select(node => node.Id).ToHashSet();
        var outputs = graph.Nodes.Where(node => SpawnableGroupSchema.IsOutput(node.Kind)).Select(node => node.Id).ToHashSet();
        return graph with
        {
            Nodes = graph.Nodes.Select(node => node.Kind.Value switch
            {
                "OnStart" => node with { Kind = GraphNodeKind.Signal },
                "InstanceAge" => node with { Kind = GraphNodeKind.Time },
                "LifeTime" => node with { Kind = GraphNodeKind.Float, Parameters =
                    [new NodeParameterSnapshot("value", "float", node.Parameters?.SingleOrDefault(p => p.Id == "seconds")?.Value
                        ?? JsonSerializer.SerializeToElement(2d))] },
                "SpawnOutputVector3" => node with { Kind = GraphNodeKind.FloatX3 },
                _ => node.ChildGraph is { } child && node.Kind != GraphNodeKind.SpawnableGroup &&
                    !GpuParticleEditorService.IsGpu(node.Kind) ? node with { ChildGraph = LowerLifecycle(child) } : node
            }).ToArray(),
            Connections = graph.Connections.Select(edge => edge with
            {
                SourcePortId = starts.Contains(edge.SourceNodeId) ? null : edge.SourcePortId,
                TargetPortId = lives.Contains(edge.TargetNodeId) || outputs.Contains(edge.TargetNodeId) ? null : edge.TargetPortId
            }).ToArray()
        };
    }

    private static bool ContainsSpawnable(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.Kind == GraphNodeKind.SpawnableGroup ||
        node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind) && ContainsSpawnable(child));

    private static IEnumerable<GraphConnectionSnapshot> graphConnections(GraphSnapshot graph, IReadOnlyList<GraphNodeSnapshot> nodes)
    {
        var ids = nodes.Select(node => node.Id).ToHashSet();
        return graph.Connections.Concat(graph.Nodes.Where(node => node.ChildGraph is not null && node.Kind != GraphNodeKind.SpawnableGroup)
            .SelectMany(node => graphConnections(node.ChildGraph!, nodes)))
            .Where(connection => ids.Contains(connection.SourceNodeId) && ids.Contains(connection.TargetNodeId));
    }

    private static int Width(string portType) => portType switch
    {
        "Float" => 1, "FloatX2" => 2, "FloatX3" => 3, "FloatX4" or "Color" => 4,
        _ => throw new InvalidDataException($"Port type '{portType}' is not supported by the runtime.")
    };

    private static bool IsArithmetic(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.Add && (byte)opcode <= (byte)VfxOpcode.Divide ||
        (byte)opcode >= (byte)VfxOpcode.AddVector2 && (byte)opcode <= (byte)VfxOpcode.DivideVector4;

    private static bool IsSingleInput(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.ScalarToVector2 && (byte)opcode <= (byte)VfxOpcode.Vector4ToColor;

    private static bool TryOpcode(string kind, out VfxOpcode opcode)
    {
        opcode = kind switch
        {
            "Float" or "SpawnInputFloat" => VfxOpcode.Scalar, "Time" => VfxOpcode.Time,
            "Add" => VfxOpcode.Add, "Subtract" => VfxOpcode.Subtract,
            "Multiply" => VfxOpcode.Multiply, "Divide" => VfxOpcode.Divide,
            "FloatX2" or "SpawnInputVector2" => VfxOpcode.Vector2, "FloatX3" or "SpawnInputVector3" => VfxOpcode.Vector3,
            "FloatX4" or "SpawnInputVector4" => VfxOpcode.Vector4,
            "Color" or "SpawnInputColor" or "554aaa8a-74a0-4620-9e28-d1e7bd20ffda" => VfxOpcode.Color,
            "AddVector2" => VfxOpcode.AddVector2, "SubtractVector2" => VfxOpcode.SubtractVector2,
            "MultiplyVector2" => VfxOpcode.MultiplyVector2, "DivideVector2" => VfxOpcode.DivideVector2,
            "AddVector3" => VfxOpcode.AddVector3, "SubtractVector3" => VfxOpcode.SubtractVector3,
            "MultiplyVector3" => VfxOpcode.MultiplyVector3, "DivideVector3" => VfxOpcode.DivideVector3,
            "AddVector4" => VfxOpcode.AddVector4, "SubtractVector4" => VfxOpcode.SubtractVector4,
            "MultiplyVector4" => VfxOpcode.MultiplyVector4, "DivideVector4" => VfxOpcode.DivideVector4,
            "ScalarToVector2" => VfxOpcode.ScalarToVector2, "ScalarToVector3" => VfxOpcode.ScalarToVector3,
            "ScalarToVector4" => VfxOpcode.ScalarToVector4,
            "BreakVector2" => VfxOpcode.BreakVector2, "BreakVector3" => VfxOpcode.BreakVector3,
            "BreakVector4" => VfxOpcode.BreakVector4,
            "Vector2ToVector3" => VfxOpcode.Vector2ToVector3, "Vector2ToVector4" => VfxOpcode.Vector2ToVector4,
            "Vector3ToVector2" => VfxOpcode.Vector3ToVector2, "Vector3ToVector4" => VfxOpcode.Vector3ToVector4,
            "Vector4ToVector2" => VfxOpcode.Vector4ToVector2, "Vector4ToVector3" => VfxOpcode.Vector4ToVector3,
            "MaskVector2" => VfxOpcode.MaskVector2, "MaskVector3" => VfxOpcode.MaskVector3,
            "MaskVector4" => VfxOpcode.MaskVector4, "ColorToVector4" => VfxOpcode.ColorToVector4,
            "Vector4ToColor" => VfxOpcode.Vector4ToColor,
            "FloatFCurve" => VfxOpcode.FCurve,
            _ => 0
        };
        return opcode != 0;
    }
}
