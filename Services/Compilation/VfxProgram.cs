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
    ColorToVector4 = 38, Vector4ToColor = 39, FCurve = 42,
    EnumValue = 43, EnumSelect = 44, BranchTime = 45, LogicalEventSignal = 47,
    Int32 = 48, Int32Add = 49, Int32Subtract = 50, Int32Multiply = 51,
    Int32Divide = 52, FloatToInt32 = 53, Int32ToFloat = 54,
    CurrentPhase = 55, PhaseTime = 56
}

public readonly record struct VfxInstruction(
    Guid NodeId, VfxOpcode Opcode, int InputA, int InputB, double DefaultA, double DefaultB);

public readonly record struct VfxTypedInstruction(
    Guid NodeId, VfxOpcode Opcode, int OutputWidth, int InputAWidth, int InputBWidth, byte TypeFlags,
    int InputASourceComponent, int InputBSourceComponent, int InputA, int InputB,
    double[] DefaultA, double[] DefaultB, int BranchGuardId = -1);

public readonly record struct VfxNodeValueTypes(GraphValueType OutputType, GraphValueType[] InputTypes);

public readonly record struct VfxEnumValueInstruction(Guid NodeId, Guid TypeId, int InputIndex, int DefaultValue);
public readonly record struct VfxEnumCaseTarget(int Value, int SourceIndex, double[] DefaultValue);
public readonly record struct VfxEnumSelectInstruction(Guid NodeId, Guid TypeId, int SelectorIndex,
    int OutputWidth, VfxEnumCaseTarget[] Cases, int DefaultSourceIndex, double[] Fallback);
public readonly record struct VfxBranchGuard(int SelectorIndex, Guid EnumTypeId, int[] CaseValues,
    int[] DefaultExcludedValues, bool IncludesDefault, int ParentGuardId);
public readonly record struct VfxLogicalInstruction(Guid NodeId, Guid TypeId, int AbiVersion,
    uint Configuration, int NumericInputCount, int SignalInputCount, int OutputWidth, int SignalBranchCount);
public readonly record struct VfxLogicalSignalRoute(int SourceIndex, int SourceBranch,
    int TargetIndex, int TargetInputSlot);

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
    private const int EnumBranchHeaderSize = 16;
    private const int LogicalEventHeaderSize = 16;
    private const int ValueTypeHeaderSize = 8;
    private const int ValueTypeRecordSize = 8;
    private sealed class BranchGuardBuilder(Guid selectorNodeId, Guid enumTypeId)
    {
        public Guid SelectorNodeId { get; } = selectorNodeId;
        public Guid EnumTypeId { get; } = enumTypeId;
        public HashSet<int> CaseValues { get; } = [];
        public HashSet<int> DefaultExcludedValues { get; } = [];
        public bool IncludesDefault { get; set; }
    }
    private readonly VfxInstruction[] _instructions;
    private readonly VfxTypedInstruction[] _typedInstructions;
    private readonly VfxCustomInstruction[] _customInstructions;
    private readonly VfxEnumValueInstruction[] _enumValueInstructions;
    private readonly VfxEnumSelectInstruction[] _enumSelectInstructions;
    private readonly VfxBranchGuard[] _branchGuards;
    private readonly VfxLogicalInstruction[] _logicalInstructions;
    private readonly VfxLogicalSignalRoute[] _logicalSignalRoutes;
    private readonly VfxNodeValueTypes[] _valueTypes;
    private readonly VfxCurveInstruction[] _curveInstructions;
    private readonly GameIoPort[] _gameIoPorts;
    private readonly ushort _version;

    public IReadOnlyList<VfxInstruction> Instructions { get; }
    public IReadOnlyList<VfxTypedInstruction> TypedInstructions { get; }
    public IReadOnlyList<VfxCustomInstruction> CustomInstructions { get; }
    public IReadOnlyList<VfxEnumValueInstruction> EnumValueInstructions { get; }
    public IReadOnlyList<VfxEnumSelectInstruction> EnumSelectInstructions { get; }
    public IReadOnlyList<VfxBranchGuard> BranchGuards { get; }
    public IReadOnlyList<VfxLogicalInstruction> LogicalInstructions { get; }
    public IReadOnlyList<VfxLogicalSignalRoute> LogicalSignalRoutes { get; }
    public IReadOnlyList<VfxNodeValueTypes> ValueTypes { get; }
    public IReadOnlyList<VfxCurveInstruction> CurveInstructions { get; }
    public IReadOnlyList<GameIoPort> GameIoPorts { get; }
    public ushort BinaryVersion => _version;
    public int WorkSizeBytes => checked(_instructions.Length * (_version == 1 ? sizeof(double) : 4 * sizeof(double)) +
        ((_customInstructions.Sum(item => item.BranchAdditionalTargets.Sum(targets => targets.Length)) + 1) / 2) * sizeof(double) +
        _branchGuards.Length * 24);

    private VfxProgram(VfxInstruction[] instructions, VfxTypedInstruction[] typedInstructions,
        VfxCustomInstruction[] customInstructions, VfxCurveInstruction[] curveInstructions, ushort version,
        IReadOnlyList<GameIoPort>? gameIoPorts = null,
        VfxEnumValueInstruction[]? enumValueInstructions = null,
        VfxEnumSelectInstruction[]? enumSelectInstructions = null,
        VfxBranchGuard[]? branchGuards = null,
        VfxLogicalInstruction[]? logicalInstructions = null,
        VfxLogicalSignalRoute[]? logicalSignalRoutes = null,
        VfxNodeValueTypes[]? valueTypes = null)
    {
        _instructions = instructions;
        _typedInstructions = typedInstructions;
        _customInstructions = customInstructions;
        _enumValueInstructions = enumValueInstructions ?? [];
        _enumSelectInstructions = enumSelectInstructions ?? [];
        _branchGuards = branchGuards ?? [];
        _logicalInstructions = logicalInstructions ?? [];
        _logicalSignalRoutes = logicalSignalRoutes ?? [];
        _valueTypes = valueTypes ?? [];
        _curveInstructions = curveInstructions;
        _gameIoPorts = gameIoPorts?.ToArray() ?? [];
        _version = version;
        Instructions = Array.AsReadOnly(instructions);
        TypedInstructions = Array.AsReadOnly(typedInstructions);
        CustomInstructions = Array.AsReadOnly(customInstructions);
        EnumValueInstructions = Array.AsReadOnly(_enumValueInstructions);
        EnumSelectInstructions = Array.AsReadOnly(_enumSelectInstructions);
        BranchGuards = Array.AsReadOnly(_branchGuards);
        LogicalInstructions = Array.AsReadOnly(_logicalInstructions);
        LogicalSignalRoutes = Array.AsReadOnly(_logicalSignalRoutes);
        ValueTypes = Array.AsReadOnly(_valueTypes);
        CurveInstructions = Array.AsReadOnly(curveInstructions);
        GameIoPorts = Array.AsReadOnly(_gameIoPorts);
    }

    public static VfxProgram Compile(GraphSnapshot graph) => Compile(graph, out _);

    public static VfxProgram Compile(GraphSnapshot graph, out byte[] renderConfiguration,
        IRenderResourceBindingContext? resourceContext = null, VfxCompileOptions? options = null)
    {
        ValidateForCompilation(graph);
        if (Flatten(graph).Any(node => GameIoSchema.IsInstanceNode(node.Kind)))
            throw new InvalidDataException("Instance Game I/O requires a Spawnable Group package; export .lfxp.");
        if (Flatten(graph).Any(node => node.SignalKeys?.Any(key => key.Mode == SignalTimelineMode.Runtime) == true))
            throw new InvalidDataException("Runtime Signal timeline keys require a Spawnable Group package; export .lfxp.");
        var starts = graph.Nodes.Where(node => node.Kind.Value == "OnStart").ToArray();
        if (starts.Length == 1 && !graph.Connections.Any(edge => edge.SourceNodeId == starts[0].Id))
            graph = graph with { Nodes = graph.Nodes.Where(node => node.Id != starts[0].Id).ToArray() };
        if (ContainsSpawnable(graph))
            throw new InvalidDataException("Compile Spawnable Groups with SpawnableGroupPackage so templates are not discarded.");
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

    private static VfxProgram CompileWithCustomNodes(
        GraphSnapshot graph, GraphNodeSnapshot[] nodes,
        IReadOnlyDictionary<GraphNodeKind, GraphNodeDefinition> definitions,
        IReadOnlyList<GameIoPort> gameIoPorts,
        IReadOnlyDictionary<Guid, Guid>? signalTypeOverrides)
    {
        if (nodes.Length > MaxInstructions) throw new InvalidDataException("Too many runtime nodes.");
        var byId = nodes.ToDictionary(node => node.Id);
        var connections = graphConnections(graph, nodes).ToArray();
        var incoming = connections.ToLookup(connection => connection.TargetNodeId);
        var outgoing = connections.ToLookup(connection => connection.SourceNodeId);
        var precedence = new Dictionary<Guid, HashSet<Guid>>();
        var guardsByNode = new Dictionary<Guid, Dictionary<Guid, BranchGuardBuilder>>();
        var selectorNodes = nodes.Where(node => GraphEnumBranchSchema.TryGetSelector(
            node.Kind, out _, out _, out _)).ToArray();
        foreach (var selectorNode in selectorNodes)
        {
            GraphEnumBranchSchema.TryGetSelector(selectorNode.Kind, out var enumTypeId,
                out _, out var enumValues);
            var selectorEdges = incoming[selectorNode.Id]
                .Where(edge => edge.TargetPortId == "selector").ToArray();
            if (selectorEdges.Length != 1)
                throw new InvalidDataException($"Enum Select '{selectorNode.Name}' requires one Enum selector connection.");
            var selectorSourceId = selectorEdges[0].SourceNodeId;
            var selectorSource = byId[selectorSourceId];
            var selectorType = definitions[selectorSource.Kind].GetOutputPortType(selectorEdges[0].SourcePortId);
            if (selectorType != GraphEnumBranchSchema.EnumPortType(enumTypeId))
                throw new InvalidDataException($"Enum Select '{selectorNode.Name}' has a mismatched Enum type.");

            var selectorAncestors = new HashSet<Guid>();
            var selectorStack = new Stack<Guid>();
            selectorStack.Push(selectorSourceId);
            while (selectorStack.TryPop(out var ancestor))
            {
                if (!selectorAncestors.Add(ancestor)) continue;
                foreach (var edge in incoming[ancestor])
                    selectorStack.Push(edge.SourceNodeId);
            }

            var explicitCaseValues = new HashSet<int>();
            var caseClosures = new List<(int? Value, bool IsDefault, HashSet<Guid> Nodes)>();
            foreach (var enumValue in enumValues)
            {
                var portId = GraphEnumBranchSchema.CasePortId(enumValue.Value);
                var branchEdges = incoming[selectorNode.Id].Where(edge => edge.TargetPortId == portId).ToArray();
                if (branchEdges.Length > 1)
                    throw new InvalidDataException($"Enum Select '{selectorNode.Name}' has multiple connections for case '{enumValue.Name}'.");
                if (branchEdges.Length == 0) continue;
                explicitCaseValues.Add(enumValue.Value);
                caseClosures.Add((enumValue.Value, false,
                    TraceParameterClosure(branchEdges[0].SourceNodeId, selectorNode.Id)));
            }
            var defaultEdges = incoming[selectorNode.Id].Where(edge => edge.TargetPortId == "default").ToArray();
            if (defaultEdges.Length > 1)
                throw new InvalidDataException($"Enum Select '{selectorNode.Name}' has multiple Default connections.");
            if (defaultEdges.Length == 1)
                caseClosures.Add((null, true,
                    TraceParameterClosure(defaultEdges[0].SourceNodeId, selectorNode.Id)));

            foreach (var branch in caseClosures)
            {
                var renderNodeId = branch.Nodes.FirstOrDefault(branchNodeId =>
                {
                    var branchNode = byId[branchNodeId];
                    var branchDefinition = definitions[branchNode.Kind];
                    return RenderNodeSchema.IsRenderNode(branchDefinition) ||
                        branchDefinition.TypeName == "GpuParticle";
                });
                if (renderNodeId != Guid.Empty)
                    throw new InvalidDataException(
                        $"Enum Select '{selectorNode.Name}' cannot guard GPU or rendering nodes yet.");

                var branchWork = new Queue<Guid>(branch.Nodes);
                var propagatedNodes = new HashSet<Guid>();
                while (branchWork.TryDequeue(out var branchNodeId))
                {
                    if (!propagatedNodes.Add(branchNodeId)) continue;
                    if (selectorAncestors.Contains(branchNodeId)) continue;
                    if (byId[branchNodeId].Kind.Value is "CurrentPhase" or "PhaseTime") continue;
                    if (!guardsByNode.TryGetValue(branchNodeId, out var nodeGuards))
                        guardsByNode.Add(branchNodeId, nodeGuards = []);
                    if (!nodeGuards.TryGetValue(selectorNode.Id, out var guard))
                        nodeGuards.Add(selectorNode.Id, guard = new(selectorSourceId, enumTypeId));
                    if (branch.IsDefault)
                    {
                        guard.IncludesDefault = true;
                        guard.DefaultExcludedValues.UnionWith(explicitCaseValues);
                    }
                    else if (branch.Value is { } value)
                        guard.CaseValues.Add(value);
                    if (!precedence.TryGetValue(selectorSourceId, out var targets))
                        precedence.Add(selectorSourceId, targets = []);
                    targets.Add(branchNodeId);

                    foreach (var edge in outgoing[branchNodeId])
                    {
                        if (edge.TargetNodeId == selectorNode.Id ||
                            selectorAncestors.Contains(edge.TargetNodeId)) continue;
                        var target = byId[edge.TargetNodeId];
                        var targetDefinition = target.Kind == GraphNodeKind.SpawnableGroup
                            ? SpawnableGroupSchema.Effective(definitions[target.Kind], target.ChildGraph)
                            : definitions[target.Kind];
                        if (RenderNodeSchema.IsRenderNode(targetDefinition) ||
                            targetDefinition.TypeName == "GpuParticle")
                            throw new InvalidDataException(
                                $"Enum Select '{selectorNode.Name}' cannot guard GPU or rendering nodes yet.");
                        var category = targetDefinition.GetInputPortCategory(edge.TargetPortId);
                        if (category is not (GraphPortCategory.Parameter or GraphPortCategory.Signal)) continue;
                        branchWork.Enqueue(edge.TargetNodeId);
                    }
                }
            }
        }
        if (guardsByNode.Values.Any(guards => guards.Count > 1))
            throw new InvalidDataException("A node cannot belong to multiple Enum execution regions; nest independent branches in separate groups.");

        HashSet<Guid> TraceParameterClosure(Guid start, Guid stopAt)
        {
            var closure = new HashSet<Guid>();
            var stack = new Stack<Guid>();
            stack.Push(start);
            while (stack.TryPop(out var current))
            {
                if (current == stopAt)
                    throw new InvalidDataException("An Enum branch value cannot depend on its own Enum Select output.");
                if (!closure.Add(current)) continue;
                foreach (var edge in incoming[current])
                {
                    var targetDefinition = definitions[byId[current].Kind];
                    if (targetDefinition.GetInputPortCategory(edge.TargetPortId) == GraphPortCategory.Parameter)
                        stack.Push(edge.SourceNodeId);
                }
            }
            return closure;
        }

        var degree = nodes.ToDictionary(node => node.Id, node => incoming[node.Id].Count());
        foreach (var (sourceId, targets) in precedence)
            foreach (var targetId in targets)
                if (!connections.Any(edge => edge.SourceNodeId == sourceId && edge.TargetNodeId == targetId))
                    degree[targetId]++;
        var queue = new Queue<GraphNodeSnapshot>(nodes.Where(node => degree[node.Id] == 0));
        var order = new List<GraphNodeSnapshot>(nodes.Length);
        while (queue.TryDequeue(out var node))
        {
            order.Add(node);
            foreach (var connection in outgoing[node.Id])
                if (--degree[connection.TargetNodeId] == 0) queue.Enqueue(byId[connection.TargetNodeId]);
            if (precedence.TryGetValue(node.Id, out var targets))
                foreach (var targetId in targets)
                    if (!connections.Any(edge => edge.SourceNodeId == node.Id && edge.TargetNodeId == targetId) &&
                        --degree[targetId] == 0)
                        queue.Enqueue(byId[targetId]);
        }
        if (order.Count != nodes.Length) throw new InvalidDataException("The graph contains a cycle.");
        var indices = order.Select((node, index) => (node.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var typedCode = new VfxTypedInstruction[order.Count];
        var customCode = new List<VfxCustomInstruction>();
        var logicalCode = new List<VfxLogicalInstruction>();
        var logicalSignalRoutes = new List<VfxLogicalSignalRoute>();
        var curveCode = new List<VfxCurveInstruction>();
        var enumValueCode = new List<VfxEnumValueInstruction>();
        var enumSelectCode = new List<VfxEnumSelectInstruction>();
        var effective = nodes.ToDictionary(node => node.Id, node =>
        {
            var definition = SpawnableGroupSchema.Effective(definitions[node.Kind], node.ChildGraph);
            if (node.Kind == GraphNodeKind.SpawnableGroup)
            {
                var inputs = node.ChildGraph!.Nodes.Where(child => SpawnableGroupSchema.IsInput(child.Kind)).ToArray();
                var useExtendedCapture = inputs.Length > 4 ||
                    inputs.Sum(input => SpawnableGroupSchema.InputWidth(input.PortType)) > 16;
                return definition with { TypeId = SpawnableGroupPackage.SignalTypeId(node.Id, node.ChildGraph),
                    InputPorts = (definition.InputPorts ?? []).Where(port =>
                        port.Id == "spawn" || !useExtendedCapture &&
                        definition.GetInputPortCategory(port.Id) == GraphPortCategory.Parameter).ToArray(),
                    OutputPorts = [new GraphOutputPortDefinition("then", "Then", "Signal", GraphPortCategory.Signal)],
                    Runtime = new GraphRuntimeDefinition("custom", 1, "signal") };
            }
            if (node.Kind == GraphNodeKind.Signal)
                return definition with { TypeId = signalTypeOverrides is not null &&
                        signalTypeOverrides.TryGetValue(node.Id, out var typeId)
                            ? typeId : SpawnableGroupPackage.SignalPassthroughTypeId,
                    Runtime = new GraphRuntimeDefinition("custom", 1, "signal"),
                    InputPorts = [new GraphInputPortDefinition("signal", "Signal", "Signal", GraphPortCategory.Signal)],
                    OutputPorts = [new GraphOutputPortDefinition("then", "Then", "Signal", GraphPortCategory.Signal)] };
            return definition;
        });

        // Event-derived values may change after the ordinary parameter pass.
        // Only Core-owned stateless math is permitted on that late-refresh path;
        // arbitrary custom callbacks, lifetime capture and RNG must not be rerun.
        static bool SupportsLateRefresh(GraphNodeDefinition definition)
        {
            if (definition.Runtime is not { AbiVersion: 1, Execution: "parameter" }) return false;
            var id = definition.TypeId.ToString("N");
            if (!byte.TryParse(id.AsSpan(30, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var number)) return false;
            return id.StartsWith("74305ac191b24c338d440000000000", StringComparison.Ordinal) &&
                    number is >= 1 and <= 47 ||
                id.StartsWith("b0af61d229f64cc9a71e0000000000", StringComparison.Ordinal) &&
                    (number is >= 1 and <= 13 or >= 15 and <= 18);
        }

        var lateValues = new HashSet<Guid>();
        foreach (var node in order)
        {
            var definition = effective[node.Id];
            var isEventValue = GraphLogicalSchema.IsAnd(definition) || GraphLogicalSchema.IsOr(definition) ||
                GraphLogicalSchema.IsSignalToNumber(definition) || GraphLogicalSchema.IsSignalCapture(definition) ||
                GraphLogicalSchema.IsQualityBranch(definition) || GraphLogicalSchema.IsLoopClock(definition);
            var dependsOnEventValue = incoming[node.Id].Any(edge =>
                lateValues.Contains(edge.SourceNodeId) &&
                effective[edge.SourceNodeId].GetOutputPortCategory(edge.SourcePortId) == GraphPortCategory.Parameter &&
                definition.GetInputPortCategory(edge.TargetPortId) == GraphPortCategory.Parameter);
            if (dependsOnEventValue &&
                (GraphLogicalSchema.IsLogical(definition) && !GraphLogicalSchema.IsSignalCapture(definition) ||
                 definition.Runtime is { Execution: "parameter" } && !SupportsLateRefresh(definition)))
                throw new InvalidDataException(
                    $"Node '{node.Name}' cannot consume a same-update Signal-derived value: " +
                    "its runtime callback is not supported by late pure refresh.");
            if (isEventValue || dependsOnEventValue && definition.Runtime?.Execution != "signal")
                lateValues.Add(node.Id);
        }

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
            if (node.Kind.Value is "CurrentPhase" or "PhaseTime")
            {
                var phase = node.Kind.Value == "CurrentPhase";
                typedCode[index] = new(node.Id, phase ? VfxOpcode.CurrentPhase : VfxOpcode.PhaseTime,
                    1, 0, 0, 0, 0, 0, -1, -1, new double[4], new double[4]);
                if (phase)
                    enumValueCode.Add(new(node.Id, Guid.Parse("4a94e882-39e8-4efb-92c7-180000000030"), -1, 0));
                continue;
            }
            if (GraphEnumBranchSchema.TryGetValueDefinition(node.Kind, out var enumValueDefinition))
            {
                if (!GraphEnumDefinition.TryGetTypeId(enumValueDefinition.PortType, out var enumTypeId))
                    throw new InvalidDataException($"Enum value node '{node.Name}' has no stable Enum type.");
                var enumParameter = enumValueDefinition.Parameters.Single(item => item.Id == "value");
                var enumValue = node.Parameters?.SingleOrDefault(item =>
                    item.Id == "value" && item.Type == enumParameter.Type)?.Value ?? enumParameter.DefaultValue;
                if (!NodeParameter.IsValidValue(enumParameter.Type, enumValue) ||
                    !enumValue.TryGetInt32(out var enumNumber) ||
                    !GraphEnumDefinition.All[enumTypeId].Values.Any(item => item.Value == enumNumber))
                    throw new InvalidDataException($"Enum value node '{node.Name}' has an invalid value.");
                var input = Input(node, definition, "value", 1, out _, out _);
                if (input >= 0)
                {
                    var sourceType = effective[byId[connections.Single(edge =>
                        edge.TargetNodeId == node.Id && edge.TargetPortId == "value").SourceNodeId].Id]
                        .GetOutputPortType(connections.Single(edge =>
                            edge.TargetNodeId == node.Id && edge.TargetPortId == "value").SourcePortId);
                    if (sourceType != enumValueDefinition.PortType)
                        throw new InvalidDataException($"Enum value node '{node.Name}' received a mismatched Enum type.");
                }
                var fallback = new double[4];
                fallback[0] = enumNumber;
                typedCode[index] = new(node.Id, VfxOpcode.EnumValue, 1, input < 0 ? 0 : 1, 0,
                    0, 0, 0, input, -1, fallback, new double[4]);
                enumValueCode.Add(new(node.Id, enumTypeId, input, enumNumber));
                continue;
            }
            if (GraphEnumBranchSchema.TryGetSelector(node.Kind, out var enumSelectorType,
                    out var outputType, out var enumCases))
            {
                var enumOutputWidth = Width(outputType);
                var selector = Input(node, definition, "selector", 1, out _, out _);
                if (selector < 0) throw new InvalidDataException($"Enum Select '{node.Name}' requires an Enum selector.");
                if (enumCases.Count > 32)
                    throw new InvalidDataException($"Enum Select '{node.Name}' supports at most 32 cases.");
                var cases = new VfxEnumCaseTarget[enumCases.Count];
                for (var caseIndex = 0; caseIndex < enumCases.Count; caseIndex++)
                {
                    var enumCase = enumCases[caseIndex];
                    var casePortId = GraphEnumBranchSchema.CasePortId(enumCase.Value);
                    var source = Input(node, definition, casePortId,
                        enumOutputWidth, out _, out _);
                    var casePort = definition.InputPorts!.Single(port => port.Id == casePortId);
                    var caseDefault = casePort.DefaultValue ?? GraphEnumBranchSchema.DefaultValue(outputType);
                    var defaultComponents = enumOutputWidth == 1
                        ? new[] { caseDefault.GetDouble(), 0d, 0d, 0d }
                        : caseDefault.EnumerateArray().Select(value => value.GetDouble())
                            .Concat(Enumerable.Repeat(0d, 4 - enumOutputWidth)).ToArray();
                    cases[caseIndex] = new(enumCase.Value, source, defaultComponents);
                }
                var defaultSource = Input(node, definition, "default", enumOutputWidth, out _, out _);
                var fallbackParameter = definition.Parameters.Single(item => item.Id == "fallback");
                var fallbackValue = node.Parameters?.SingleOrDefault(item =>
                    item.Id == fallbackParameter.Id && item.Type == fallbackParameter.Type)?.Value ??
                    fallbackParameter.DefaultValue;
                if (!NodeParameter.IsValidValue(fallbackParameter.Type, fallbackValue))
                    throw new InvalidDataException($"Enum Select '{node.Name}' has an invalid Fallback value.");
                var fallback = enumOutputWidth == 1
                    ? new[] { fallbackValue.GetDouble(), 0d, 0d, 0d }
                    : fallbackValue.EnumerateArray().Select(item => item.GetDouble())
                        .Concat(Enumerable.Repeat(0d, 4 - enumOutputWidth)).ToArray();
                typedCode[index] = new(node.Id, VfxOpcode.EnumSelect, enumOutputWidth, 1, 0,
                    0, 0, 0, selector, -1, fallback, new double[4]);
                enumSelectCode.Add(new(node.Id, enumSelectorType, selector, enumOutputWidth,
                    cases, defaultSource, fallback));
                continue;
            }
            if (node.Kind == GraphEnumBranchSchema.BranchTime.Kind)
            {
                typedCode[index] = new(node.Id, VfxOpcode.BranchTime, 1, 0, 0,
                    0, 0, 0, -1, -1, new double[4], new double[4]);
                continue;
            }
            if (GraphLogicalSchema.IsLogical(definition))
            {
                GraphLogicalSchema.ValidateNodeParameters(definition, node.Parameters);
                var inputPorts = (definition.InputPorts ?? [])
                    .Where(port => definition.GetInputPortCategory(port.Id) == GraphPortCategory.Parameter)
                    .ToArray();
                var signalInputs = (definition.InputPorts ?? [])
                    .Where(port => definition.GetInputPortCategory(port.Id) == GraphPortCategory.Signal)
                    .ToArray();
                var parameterOutputs = (definition.OutputPorts ?? [])
                    .Where(port => definition.GetOutputPortCategory(port.Id) == GraphPortCategory.Parameter)
                    .ToArray();
                var signalOutputs = (definition.OutputPorts ?? [])
                    .Where(port => definition.GetOutputPortCategory(port.Id) == GraphPortCategory.Signal)
                    .ToArray();
                var loopClock = GraphLogicalSchema.IsLoopClock(definition);
                var qualityBranch = GraphLogicalSchema.IsQualityBranch(definition);
                var signalCapture = GraphLogicalSchema.IsSignalCapture(definition);
                var expectedNumericInputs = signalCapture ? 1 : loopClock ? 4 : GraphLogicalSchema.IsCompare(definition) ? 2 : 0;
                var expectedSignalInputs = GraphLogicalSchema.IsAnd(definition) ||
                    GraphLogicalSchema.IsOr(definition) ? 3 :
                    qualityBranch || signalCapture ? 1 :
                    GraphLogicalSchema.IsSignalToNumber(definition) || loopClock ? 2 : 0;
                var expectedBranches = qualityBranch ? 2 : signalCapture ? 1 : loopClock ? 3 : GraphLogicalSchema.IsCompare(definition) ||
                    GraphLogicalSchema.IsAnd(definition) || GraphLogicalSchema.IsOr(definition) ? 1 : 0;
                var expectedNumericInputIds = signalCapture ? new[] { "value" } : loopClock ? new[] { "time", "length", "count", "bounded" } :
                    GraphLogicalSchema.IsCompare(definition)
                    ? GraphLogicalSchema.CompareInputPortIds : Array.Empty<string>();
                var expectedSignalInputIds = qualityBranch || signalCapture ? new[] { "signal" } :
                    loopClock ? new[] { "arm", "reset" } : GraphLogicalSchema.IsAnd(definition) ||
                    GraphLogicalSchema.IsOr(definition) ? GraphLogicalSchema.GateSignalInputPortIds :
                    GraphLogicalSchema.IsSignalToNumber(definition) || loopClock
                        ? GraphLogicalSchema.SignalToNumberInputPortIds : Array.Empty<string>();
                var expectedParameterOutputId = GraphLogicalSchema.IsCompare(definition) || qualityBranch
                    ? GraphLogicalSchema.ConditionOutput :
                    GraphLogicalSchema.IsSignalToNumber(definition) || loopClock || signalCapture
                        ? GraphLogicalSchema.ValueOutput : GraphLogicalSchema.PulseOutput;
                var expectedSignalOutputIds = qualityBranch ? new[] { "whenTrue", "whenFalse" } :
                    signalCapture ? new[] { "signal" } : loopClock ? new[] { "boundary", "complete", "armedBoundary" } :
                    expectedBranches == 0 ? Array.Empty<string>() :
                    new[] { GraphLogicalSchema.IsCompare(definition)
                        ? GraphLogicalSchema.EventOutput : GraphLogicalSchema.SignalOutput };
                if (inputPorts.Length != expectedNumericInputs ||
                    signalInputs.Length != expectedSignalInputs ||
                    parameterOutputs.Length != 1 || signalOutputs.Length != expectedBranches ||
                    !inputPorts.Select(port => port.Id).SequenceEqual(expectedNumericInputIds) ||
                    !signalInputs.Select(port => port.Id).SequenceEqual(expectedSignalInputIds) ||
                    parameterOutputs[0].Id != expectedParameterOutputId ||
                    !signalOutputs.Select(port => port.Id).SequenceEqual(expectedSignalOutputIds) ||
                    definition.GetOutputPortType(parameterOutputs[0].Id) != (loopClock || signalCapture ? "FloatX4" : "Float") ||
                    signalInputs.Any(port => definition.GetInputPortType(port.Id) != "Signal"))
                    throw new InvalidDataException($"Logical node '{node.Name}' has an unsupported port layout.");

                var logicalInputs = new VfxCustomInput[inputPorts.Length];
                for (var inputIndex = 0; inputIndex < inputPorts.Length; inputIndex++)
                {
                    var port = inputPorts[inputIndex];
                    var inputType = signalCapture ? "FloatX4" : "Float";
                    var inputWidth = signalCapture ? 4 : 1;
                    if (definition.GetInputPortType(port.Id) != inputType)
                        throw new InvalidDataException($"Logical input '{port.Id}' must be {inputType}.");
                    var source = Input(node, definition, port.Id, inputWidth, out var sourceComponent, out var sourceColor);
                    if (sourceColor)
                        throw new InvalidDataException($"Logical input '{port.Id}' cannot be Color.");
                    var parameter = definition.Parameters.FirstOrDefault(item => item.Id == port.Id && item.Type == "float");
                    var value = parameter is null ? port.DefaultValue :
                        node.Parameters?.SingleOrDefault(item => item.Id == port.Id)?.Value ?? parameter.DefaultValue;
                    var defaults = new double[4];
                    if (value is { } defaultValue)
                    {
                        if (!NodeParameter.IsValidValue(signalCapture ? "vector4" : "float", defaultValue))
                            throw new InvalidDataException($"Logical input '{port.Id}' has an invalid default.");
                        if (signalCapture)
                            defaultValue.EnumerateArray().Select(item => item.GetDouble()).ToArray().CopyTo(defaults, 0);
                        else defaults[0] = defaultValue.GetDouble();
                        if (defaults.Any(item => !double.IsFinite(item)))
                            throw new InvalidDataException($"Logical input '{port.Id}' has a nonfinite default.");
                    }
                    logicalInputs[inputIndex] = new(source, source < 0 ? 0 : inputWidth,
                        sourceComponent, false, defaults);
                }

                var branchTargets = new int[signalOutputs.Length];
                var additionalTargets = new int[signalOutputs.Length][];
                for (var branch = 0; branch < signalOutputs.Length; branch++)
                {
                    var targets = outgoing[node.Id].Where(connection =>
                        connection.SourcePortId == signalOutputs[branch].Id).ToArray();
                    var targetIndices = new int[targets.Length];
                    for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                    {
                        var edge = targets[targetIndex];
                        var target = byId[edge.TargetNodeId];
                        var targetDefinition = effective[target.Id];
                        var targetSignalInputs = (targetDefinition.InputPorts ?? [])
                            .Where(port => targetDefinition.GetInputPortCategory(port.Id) == GraphPortCategory.Signal)
                            .ToArray();
                        var isLogicalTarget = GraphLogicalSchema.IsLogical(targetDefinition);
                        var targetSlot = isLogicalTarget
                            ? Array.FindIndex(targetSignalInputs, port => port.Id == edge.TargetPortId)
                            : 0;
                        if ((!isLogicalTarget && targetDefinition.Runtime?.Execution != "signal") ||
                            targetSlot < 0 || targetSlot > byte.MaxValue)
                            throw new InvalidDataException(
                                $"Logical Signal output '{signalOutputs[branch].Id}' has an invalid target.");
                        targetIndices[targetIndex] = indices[target.Id];
                        logicalSignalRoutes.Add(new(index, branch, indices[target.Id], targetSlot));
                    }
                    branchTargets[branch] = targetIndices.Length == 0 ? -1 : targetIndices[0];
                    additionalTargets[branch] = targetIndices.Skip(1).ToArray();
                }

                var logicalOutputWidth = Width(definition.GetOutputPortType(parameterOutputs[0].Id)!);
                var configuration = GraphLogicalSchema.Configuration(node, definition);
                logicalCode.Add(new(node.Id, definition.TypeId, 1, configuration,
                    logicalInputs.Length, signalInputs.Length, logicalOutputWidth, signalOutputs.Length));
                customCode.Add(new(node.Id, (byte)VfxOpcode.LogicalEventSignal, logicalOutputWidth, false,
                    definition.TypeId, 1, signalOutputs.Length, logicalInputs, branchTargets, additionalTargets));
                typedCode[index] = new(node.Id, VfxOpcode.LogicalEventSignal, logicalOutputWidth,
                    0, 0, 0, 0, 0, -1, -1, new double[4], new double[4]);
                continue;
            }
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
                    (isSignal ? parameterOutputs.Length != 0 || signalOutputs.Length > 4 :
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
                        var targetDefinition = effective[target.Id];
                        var isLogicalTarget = GraphLogicalSchema.IsLogical(targetDefinition);
                        if (!isLogicalTarget && targetDefinition.Runtime?.Execution != "signal")
                            throw new InvalidDataException("Signal branches must target registered signal nodes.");
                        var targetSignalInputs = (targetDefinition.InputPorts ?? [])
                            .Where(port => targetDefinition.GetInputPortCategory(port.Id) == GraphPortCategory.Signal)
                            .ToArray();
                        var targetSlot = isLogicalTarget
                            ? Array.FindIndex(targetSignalInputs, port => port.Id == edge.TargetPortId)
                            : 0;
                        if (targetSlot < 0 || targetSlot > byte.MaxValue)
                            throw new InvalidDataException("A Signal route has an invalid target input slot.");
                        logicalSignalRoutes.Add(new(index, branch, indices[target.Id], targetSlot));
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

            if (opcode == VfxOpcode.Int32)
            {
                defaultA[0] = IntParameter("value");
                inputA = Input(node, definition, null, 1, out inputASourceComponent, out inputAIsColor);
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
                var integerArithmetic = IsInt32Arithmetic(opcode);
                var defaultsA = integerArithmetic ? [IntParameter("a")] :
                    width == 1 ? [Parameter("a")] : VectorParameter("a", width);
                var defaultsB = integerArithmetic ? [IntParameter("b")] :
                    width == 1 ? [Parameter("b")] : VectorParameter("b", width);
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
        var branchGuards = new List<VfxBranchGuard>();
        var branchGuardByNode = new Dictionary<Guid, int>();
        foreach (var (nodeId, nodeGuards) in guardsByNode)
        {
            var guard = nodeGuards.Values.Single();
            var guardId = branchGuards.Count;
            branchGuards.Add(new(indices[guard.SelectorNodeId], guard.EnumTypeId,
                guard.CaseValues.Order().ToArray(), guard.DefaultExcludedValues.Order().ToArray(),
                guard.IncludesDefault, -1));
            branchGuardByNode.Add(nodeId, guardId);
            typedCode[indices[nodeId]] = typedCode[indices[nodeId]] with { BranchGuardId = guardId };
        }
        foreach (var node in order.Where(item => item.Kind == GraphEnumBranchSchema.BranchTime.Kind))
            if (!branchGuardByNode.ContainsKey(node.Id))
                throw new InvalidDataException("Enum Branch Time must be inside an Enum Select case or Default branch.");

        var legacy = typedCode.Select(item => new VfxInstruction(item.NodeId, item.Opcode, item.InputA, item.InputB,
            item.DefaultA[0], item.DefaultB[0])).ToArray();
        ValidateCurveBudget(curveCode);
        var extraRoutes = customCode.Sum(item => item.BranchAdditionalTargets.Sum(targets => (long)targets.Length));
        if (extraRoutes > MaxSignalRoutes) throw new InvalidDataException("The graph contains too many Signal routes.");
        if (logicalSignalRoutes.Count > MaxSignalRoutes)
            throw new InvalidDataException("The graph contains too many logical Signal routes.");
        var valueTypes = order.Select((node, index) =>
            CreateValueTypes(typedCode[index].Opcode, node, effective[node.Id])).ToArray();
        if (typedCode.Select((instruction, index) => (instruction, index))
            .Any(item => ((byte)item.instruction.Opcode is 40 or 41) &&
                (valueTypes[item.index].OutputType == GraphValueType.Int32 ||
                 valueTypes[item.index].InputTypes.Contains(GraphValueType.Int32))))
            throw new InvalidDataException(
                "Custom runtime ABI v1 does not carry semantic numeric types; Int32 custom ports are unsupported.");
        var hasInt32 = valueTypes.Any(item => item.OutputType == GraphValueType.Int32 ||
            item.InputTypes.Contains(GraphValueType.Int32));
        var hasEnumBranches = enumValueCode.Count != 0 || enumSelectCode.Count != 0 || branchGuards.Count != 0;
        var version = nodes.Any(node => node.Kind.Value is "CurrentPhase" or "PhaseTime" or
            "EnterStart" or "EnterLoop" or "EnterEnd" or "LoopClock" or "QualityBranch" or "SignalCaptureVector4") ? (ushort)11 :
            hasInt32 ? (ushort)10 : logicalCode.Count != 0 ? (ushort)9 :
            hasEnumBranches ? (ushort)8 : gameIoPorts.Count != 0 ? (ushort)7 :
            extraRoutes != 0 ? (ushort)5 : curveCode.Count != 0 ? (ushort)4 : (ushort)3;
        return new VfxProgram(legacy, typedCode, customCode.ToArray(), curveCode.ToArray(),
            version, gameIoPorts, enumValueCode.ToArray(), enumSelectCode.ToArray(), branchGuards.ToArray(),
            logicalCode.ToArray(), logicalSignalRoutes.ToArray(), valueTypes);
    }

    internal byte[] ToPackageBinary() => _version == 1
        ? new VfxProgram(_instructions, _typedInstructions, _customInstructions, _curveInstructions,
            2, _gameIoPorts).ToBinary()
        : ToBinary();

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
        var enumBranchBytes = _version >= 8 ? CreateEnumBranchMetadata() : [];
        var logicalEventBytes = _version >= 9 ? CreateLogicalEventMetadata() : [];
        var valueTypeBytes = _version >= 10 ? CreateValueTypeMetadata() : [];
        var typedBytes = new byte[checked(instructionBytes + curveBytes + routeBytes +
            enumBranchBytes.Length + gameIoBytes.Length + logicalEventBytes.Length + valueTypeBytes.Length)];
        "LFXB"u8.CopyTo(typedBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(typedBytes.AsSpan(4), _version);
        typedBytes[6] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(typedBytes.AsSpan(8), _typedInstructions.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(typedBytes.AsSpan(12), (ushort)instructionSize);
        var customById = _customInstructions.ToDictionary(item => item.NodeId);
        var curvesById = _curveInstructions.ToDictionary(item => item.NodeId);
        var enumValuesById = _enumValueInstructions.ToDictionary(item => item.NodeId);
        var enumSelectsById = _enumSelectInstructions.ToDictionary(item => item.NodeId);
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
                BinaryPrimitives.WriteInt32LittleEndian(span[(_version >= 11 ? 44 : 112)..], instruction.BranchGuardId);
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
            if (_version >= 8)
            {
                BinaryPrimitives.WriteInt32LittleEndian(span[112..], instruction.BranchGuardId);
                if (enumValuesById.TryGetValue(instruction.NodeId, out var enumValue))
                    enumValue.TypeId.TryWriteBytes(span[96..], bigEndian: true, out _);
                else if (enumSelectsById.TryGetValue(instruction.NodeId, out var enumSelect))
                    enumSelect.TypeId.TryWriteBytes(span[96..], bigEndian: true, out _);
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
        enumBranchBytes.CopyTo(typedBytes, instructionBytes + curveBytes + routeBytes);
        gameIoBytes.CopyTo(typedBytes, instructionBytes + curveBytes + routeBytes + enumBranchBytes.Length);
        logicalEventBytes.CopyTo(typedBytes, instructionBytes + curveBytes + routeBytes +
            enumBranchBytes.Length + gameIoBytes.Length);
        valueTypeBytes.CopyTo(typedBytes, instructionBytes + curveBytes + routeBytes +
            enumBranchBytes.Length + gameIoBytes.Length + logicalEventBytes.Length);
        return typedBytes;
    }

    private byte[] CreateValueTypeMetadata()
    {
        if (_valueTypes.Length != _typedInstructions.Length)
            throw new InvalidDataException("Int32 bytecode requires one explicit value-type record per instruction.");
        var bytes = new byte[checked(ValueTypeHeaderSize + _valueTypes.Length * ValueTypeRecordSize)];
        "LFTY"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), ValueTypeRecordSize);
        var offset = ValueTypeHeaderSize;
        foreach (var item in _valueTypes)
        {
            if (item.InputTypes.Length > 4)
                throw new InvalidDataException("An instruction has too many typed numeric inputs.");
            bytes[offset] = (byte)item.OutputType;
            bytes[offset + 1] = checked((byte)item.InputTypes.Length);
            for (var input = 0; input < item.InputTypes.Length; input++)
                bytes[offset + 2 + input] = (byte)item.InputTypes[input];
            offset += ValueTypeRecordSize;
        }
        return bytes;
    }

    private byte[] CreateLogicalEventMetadata()
    {
        const int instructionRecordSize = 32;
        const int routeRecordSize = 12;
        var size = checked(LogicalEventHeaderSize + _logicalInstructions.Length * instructionRecordSize +
            _logicalSignalRoutes.Length * routeRecordSize);
        var bytes = new byte[size];
        "LFLE"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6),
            checked((ushort)_logicalInstructions.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),
            checked((uint)_logicalSignalRoutes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)size));
        var indexById = _typedInstructions.Select((item, index) => (item.NodeId, Index: index))
            .ToDictionary(item => item.NodeId, item => item.Index);
        var offset = LogicalEventHeaderSize;
        foreach (var item in _logicalInstructions)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)indexById[item.NodeId]));
            item.TypeId.TryWriteBytes(bytes.AsSpan(offset + 4, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 20), checked((uint)item.AbiVersion));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24), item.Configuration);
            bytes[offset + 28] = checked((byte)item.NumericInputCount);
            bytes[offset + 29] = checked((byte)item.SignalInputCount);
            bytes[offset + 30] = checked((byte)item.OutputWidth);
            bytes[offset + 31] = checked((byte)item.SignalBranchCount);
            offset += instructionRecordSize;
        }
        foreach (var route in _logicalSignalRoutes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)route.SourceIndex));
            bytes[offset + 4] = checked((byte)route.SourceBranch);
            bytes[offset + 5] = checked((byte)route.TargetInputSlot);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8),
                checked((uint)route.TargetIndex));
            offset += routeRecordSize;
        }
        return bytes;
    }

    private byte[] CreateEnumBranchMetadata()
    {
        var indexById = _typedInstructions.Select((item, index) => (item.NodeId, Index: index))
            .ToDictionary(item => item.NodeId, item => item.Index);
        const int enumValueRecordSize = 24;
        const int selectRecordSize = 68;
        const int guardRecordSize = 40;
        const int enumCaseRecordSize = 40;
        var selectRecordsOffset = EnumBranchHeaderSize + _enumValueInstructions.Length * enumValueRecordSize;
        var guardRecordsOffset = selectRecordsOffset + _enumSelectInstructions.Length * selectRecordSize;
        var caseDataOffset = guardRecordsOffset + _branchGuards.Length * guardRecordSize;
        var guardDataOffset = caseDataOffset + _enumSelectInstructions.Sum(item => item.Cases.Length * enumCaseRecordSize);
        var size = guardDataOffset + _branchGuards.Sum(item =>
            (item.CaseValues.Length + item.DefaultExcludedValues.Length) * 4);
        var bytes = new byte[size];
        "LFEB"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), checked((ushort)_branchGuards.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), checked((ushort)_enumValueInstructions.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), checked((ushort)_enumSelectInstructions.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)size));
        var offset = EnumBranchHeaderSize;
        foreach (var item in _enumValueInstructions)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), indexById[item.NodeId]);
            item.TypeId.TryWriteBytes(bytes.AsSpan(offset + 4, 16), bigEndian: true, out _);
            offset += 24;
        }
        var caseOffset = caseDataOffset;
        for (var index = 0; index < _enumSelectInstructions.Length; index++)
        {
            var item = _enumSelectInstructions[index];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), indexById[item.NodeId]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), item.SelectorIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), item.DefaultSourceIndex);
            item.TypeId.TryWriteBytes(bytes.AsSpan(offset + 12, 16), bigEndian: true, out _);
            bytes[offset + 28] = checked((byte)item.OutputWidth);
            bytes[offset + 29] = checked((byte)item.Cases.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 32), caseOffset);
            for (var lane = 0; lane < 4; lane++)
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset + 36 + lane * 8), item.Fallback[lane]);
            offset += selectRecordSize;
            foreach (var branch in item.Cases)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(caseOffset), branch.Value);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(caseOffset + 4), branch.SourceIndex);
                for (var lane = 0; lane < 4; lane++)
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(caseOffset + 8 + lane * 8),
                        branch.DefaultValue[lane]);
                caseOffset += enumCaseRecordSize;
            }
        }
        offset = guardRecordsOffset;
        foreach (var item in _branchGuards)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), item.SelectorIndex);
            item.EnumTypeId.TryWriteBytes(bytes.AsSpan(offset + 4, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 20), item.ParentGuardId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24), item.IncludesDefault ? 1u : 0u);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 28), checked((ushort)item.CaseValues.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 30), checked((ushort)item.DefaultExcludedValues.Length));
            var guardIndex = (offset - guardRecordsOffset) / guardRecordSize;
            var guardValuesOffset = guardDataOffset + _branchGuards.Take(guardIndex)
                .Sum(guard => (guard.CaseValues.Length + guard.DefaultExcludedValues.Length) * 4);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 32), guardValuesOffset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 36),
                guardValuesOffset + item.CaseValues.Length * 4);
            offset += guardRecordSize;
            foreach (var value in item.CaseValues)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(guardValuesOffset), value);
                guardValuesOffset += 4;
            }
            foreach (var value in item.DefaultExcludedValues)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(guardValuesOffset), value);
                guardValuesOffset += 4;
            }
        }
        return bytes;
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
            VfxOpcode.FloatToInt32 or VfxOpcode.Int32ToFloat => 1,
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

    private static GraphSnapshot LowerLifecycle(GraphSnapshot graph, bool hasLife)
    {
        var starts = graph.Nodes.Where(node => node.Kind.Value is "OnStart" or "OwnerDestroy" or "OnDestroy").Select(node => node.Id).ToHashSet();
        var lives = graph.Nodes.Where(node => node.Kind.Value == "LifeTime").Select(node => node.Id).ToHashSet();
        var outputs = graph.Nodes.Where(node => SpawnableGroupSchema.IsOutput(node.Kind)).Select(node => node.Id).ToHashSet();
        var signalBoundaries = graph.Nodes.Where(node =>
            SpawnableGroupSchema.IsSignalInput(node.Kind) || SpawnableGroupSchema.IsSignalOutput(node.Kind))
            .Select(node => node.Id).ToHashSet();
        var lifetimeReads = graph.Nodes.Where(node => node.Kind.Value is "InstanceRemaining" or "InstanceNormalizedAge")
            .Select(node => node.Id).ToHashSet();
        if (!hasLife && graph.Nodes.Any(node => node.Kind.Value == "InstanceRemaining"))
            throw new InvalidDataException("Instance Remaining requires a finite Life Time; infinite remaining cannot be represented by runtime numeric nodes.");
        return graph with
        {
            Nodes = graph.Nodes.Select(node => node.Kind.Value switch
            {
                "OnStart" => node with { Kind = GraphNodeKind.Signal },
                "OwnerDestroy" => node with { Kind = GraphNodeKind.Signal },
                "OnDestroy" => node with { Kind = GraphNodeKind.Signal },
                "InstanceAge" => node with { Kind = GraphNodeKind.Time },
                "InstanceRemaining" => node with { Kind = new GraphNodeKind("LifecycleRuntimeRemaining") },
                "InstanceNormalizedAge" when hasLife => node with { Kind = new GraphNodeKind("LifecycleRuntimeNormalizedAge") },
                "InstanceNormalizedAge" => node with { Kind = GraphNodeKind.Float, Parameters =
                    [new NodeParameterSnapshot("value", "float", JsonSerializer.SerializeToElement(0d))] },
                "LifeTime" => node with { Kind = GraphNodeKind.Float, Parameters =
                    [new NodeParameterSnapshot("value", "float", node.Parameters?.SingleOrDefault(p => p.Id == "seconds")?.Value
                        ?? JsonSerializer.SerializeToElement(2d))] },
                "SpawnOutputFloat" => node with { Kind = GraphNodeKind.Float },
                "SpawnOutputVector2" => node with { Kind = GraphNodeKind.FloatX2 },
                "SpawnOutputVector3" => node with { Kind = GraphNodeKind.FloatX3 },
                "SpawnOutputVector4" => node with { Kind = GraphNodeKind.FloatX4 },
                "SpawnOutputColor" => node with { Kind = GraphNodeDefinition.All.Single(item =>
                    item.TypeName == "Color" && item.Namespace == "LiberaFx").Kind },
                "SpawnInputSignal" or "SpawnOutputSignal" => node with { Kind = GraphNodeKind.Signal },
                _ => node.ChildGraph is { } child && node.Kind != GraphNodeKind.SpawnableGroup &&
                    !GpuParticleEditorService.IsGpu(node.Kind) ? node with { ChildGraph = LowerLifecycle(child, hasLife) } : node
            }).ToArray(),
            Connections = graph.Connections.Select(edge => edge with
            {
                SourcePortId = starts.Contains(edge.SourceNodeId) || signalBoundaries.Contains(edge.SourceNodeId) ? null :
                    hasLife && lifetimeReads.Contains(edge.SourceNodeId) ? "value" : edge.SourcePortId,
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
        "Float" or "Int32" => 1, "FloatX2" => 2, "FloatX3" => 3, "FloatX4" or "Color" => 4,
        _ when GraphEnumBranchSchema.IsEnumPort(portType) => 1,
        _ => throw new InvalidDataException($"Port type '{portType}' is not supported by the runtime.")
    };

    private static bool IsArithmetic(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.Add && (byte)opcode <= (byte)VfxOpcode.Divide ||
        (byte)opcode >= (byte)VfxOpcode.AddVector2 && (byte)opcode <= (byte)VfxOpcode.DivideVector4 ||
        IsInt32Arithmetic(opcode);

    private static bool IsInt32Arithmetic(VfxOpcode opcode) =>
        opcode is VfxOpcode.Int32Add or VfxOpcode.Int32Subtract or
            VfxOpcode.Int32Multiply or VfxOpcode.Int32Divide;

    private static bool IsSingleInput(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.ScalarToVector2 && (byte)opcode <= (byte)VfxOpcode.Vector4ToColor ||
        opcode is VfxOpcode.FloatToInt32 or VfxOpcode.Int32ToFloat;

    private static VfxNodeValueTypes CreateValueTypes(
        VfxOpcode opcode, GraphNodeSnapshot node, GraphNodeDefinition definition)
    {
        GraphValueType PortType(string? type, string description)
        {
            if (!GraphValueTypeSchema.TryGetValueType(type, out var valueType))
                throw new InvalidDataException($"Node '{node.Name}' has unsupported {description} type '{type}'.");
            return valueType;
        }

        var parameterInputs = (definition.InputPorts ?? [])
            .Where(port => definition.GetInputPortCategory(port.Id) == GraphPortCategory.Parameter)
            .ToArray();
        var parameterOutputs = (definition.OutputPorts ?? [])
            .Where(port => definition.GetOutputPortCategory(port.Id) == GraphPortCategory.Parameter)
            .ToArray();
        var outputType = definition.Runtime?.Execution == "signal" ||
            parameterOutputs.Length == 0 && definition.OutputPorts is not null
                ? GraphValueType.None
                : PortType(parameterOutputs.FirstOrDefault()?.PortType ?? node.PortType, "output");
        outputType = opcode switch
        {
            VfxOpcode.BreakVector2 => GraphValueType.Vector2,
            VfxOpcode.BreakVector3 => GraphValueType.Vector3,
            VfxOpcode.BreakVector4 => GraphValueType.Vector4,
            _ => outputType
        };

        string? InputPort(int index) => definition.InputPorts is null ? null :
            index < parameterInputs.Length ? parameterInputs[index].Id : null;
        string?[] inputPortIds = opcode switch
        {
            VfxOpcode.Time or VfxOpcode.FCurve or VfxOpcode.BranchTime => [],
            VfxOpcode.EnumValue => ["value"],
            VfxOpcode.EnumSelect => ["selector"],
            VfxOpcode.Int32 => [InputPort(0)],
            VfxOpcode.Int32Add or VfxOpcode.Int32Subtract or VfxOpcode.Int32Multiply or
                VfxOpcode.Int32Divide => ["a", "b"],
            VfxOpcode.FloatToInt32 or VfxOpcode.Int32ToFloat => ["a"],
            _ when opcode is VfxOpcode.LogicalEventSignal => parameterInputs.Select(port => (string?)port.Id).ToArray(),
            _ when (byte)opcode == 40 || (byte)opcode == 41 =>
                parameterInputs.Select(port => (string?)port.Id).ToArray(),
            _ when IsArithmetic(opcode) => ["a", "b"],
            _ when opcode == VfxOpcode.Scalar ||
                   opcode is VfxOpcode.Vector2 or VfxOpcode.Vector3 or VfxOpcode.Vector4 or VfxOpcode.Color =>
                [InputPort(0)],
            _ when IsSingleInput(opcode) ||
                   opcode is VfxOpcode.BreakVector2 or VfxOpcode.BreakVector3 or VfxOpcode.BreakVector4 =>
                ["a"],
            _ => []
        };
        var inputTypes = inputPortIds.Select(portId =>
        {
            var type = portId is null
                ? definition.GetInputPortType(null) ?? node.PortType
                : definition.GetInputPortType(portId);
            return PortType(type, "input");
        }).ToArray();
        if (inputTypes.Length > 4)
            throw new InvalidDataException($"Node '{node.Name}' has too many numeric inputs.");
        return new(outputType, inputTypes);
    }

    private static bool TryOpcode(string kind, out VfxOpcode opcode)
    {
        opcode = kind switch
        {
            "Float" or "SpawnInputFloat" => VfxOpcode.Scalar, "Time" => VfxOpcode.Time,
            "Int32" => VfxOpcode.Int32,
            "Int32Add" => VfxOpcode.Int32Add, "Int32Subtract" => VfxOpcode.Int32Subtract,
            "Int32Multiply" => VfxOpcode.Int32Multiply, "Int32Divide" => VfxOpcode.Int32Divide,
            "FloatToInt32" => VfxOpcode.FloatToInt32, "Int32ToFloat" => VfxOpcode.Int32ToFloat,
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
