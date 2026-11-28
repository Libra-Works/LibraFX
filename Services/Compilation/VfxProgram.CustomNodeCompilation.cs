using LiberaFx.Models.Graph;
using System.Text.Json;

namespace LiberaFx.Services.Compilation;

public sealed partial class VfxProgram
{
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
                    number is >= 1 and <= 57 ||
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
                        "Float" => "float", "Bool" => "bool", "FloatX2" => "vector2", "FloatX3" => "vector3",
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
                        if (width == 1) defaults[0] = parameterType == "bool" ? portDefault.GetBoolean() ? 1 : 0 : portDefault.GetDouble();
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
                (opcode == VfxOpcode.Scalar && definition.InputPorts is null && incoming[node.Id].Any(connection => connection.TargetPortId is not null)) ||
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
                var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type is "float" or "bool")
                    ?? throw new InvalidDataException($"Node '{node.Name}' is missing float parameter '{id}'.");
                var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == parameter.Type)?.Value
                    ?? parameter.DefaultValue;
                if (parameter.Type == "bool")
                    return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() ? 1 : 0 :
                        throw new InvalidDataException($"Node '{node.Name}' has an invalid Bool parameter '{id}'.");
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
                curveCode.Add(new VfxCurveInstruction(node.Id, FCurve.ExpandWeightedCurves(node.Curve.Keys).ToArray()));
            }
            else if (opcode is VfxOpcode.Scalar or VfxOpcode.Vector2 or VfxOpcode.Vector3 or VfxOpcode.Vector4 or VfxOpcode.Color)
            {
                var width = outputWidth;
                var values = node.PortType is "Float" or "Bool" ? [Parameter("value")] :
                    node.PortType == "Color" ? ReadColor(node, definition) : VectorParameter("value", width);
                Array.Copy(values, defaultA, values.Length);
                var inputPort = definition.InputPorts is null ? null : "a";
                inputA = Input(node, definition, inputPort, width, out inputASourceComponent, out inputAIsColor);
                inputAWidth = inputA < 0 ? 0 : width;
                if (node.PortType == "Float" && definition.InputPorts is null &&
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
        if (customCode.Count != 0 && version is >= 8 and < 11)
        {
            version = 11;
        }
        return new VfxProgram(legacy, typedCode, customCode.ToArray(), curveCode.ToArray(),
            version, gameIoPorts, enumValueCode.ToArray(), enumSelectCode.ToArray(), branchGuards.ToArray(),
            logicalCode.ToArray(), logicalSignalRoutes.ToArray(), valueTypes);
    }
}
