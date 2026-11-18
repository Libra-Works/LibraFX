namespace LiberaFx.Models.Graph;

/// <summary>
/// Stable authoring identities and value validation for comparison and Signal gate nodes.
/// The compiler lowers these nodes into the versioned logical instruction table.
/// </summary>
public static class GraphLogicalSchema
{
    public static readonly Guid CompareTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000001");
    public static readonly Guid SignalAndTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000002");
    public static readonly Guid SignalOrTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000003");
    public static readonly Guid SignalToNumberTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000004");
    public static readonly Guid LoopClockTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000005");
    public static readonly Guid QualityBranchTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000006");
    public static readonly Guid SignalCaptureTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000007");
    public static readonly Guid CompareOperatorTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000010");
    public static readonly Guid CompareModeTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000011");
    public static readonly Guid SignalAndModeTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000012");
    public static readonly Guid SignalToNumberModeTypeId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000013");

    public const string CompareKind = "Compare";
    public const string SignalAndKind = "SignalAnd";
    public const string SignalOrKind = "SignalOr";
    public const string SignalToNumberKind = "SignalToNumber";
    public const string OperationParameterId = "operation";
    public const string ModeParameterId = "mode";
    public const string InputA = "a";
    public const string InputB = "b";
    public const string ResetInput = "reset";
    public const string ConditionOutput = "condition";
    public const string EventOutput = "event";
    public const string PulseOutput = "pulse";
    public const string SignalOutput = "signal";
    public const string SignalInput = "signal";
    public const string ValueOutput = "value";
    public const byte AndInputSlotA = 0;
    public const byte AndInputSlotB = 1;
    public const byte ResetInputSlot = 2;
    public const byte SignalToNumberInputSlot = 0;
    public const byte SignalToNumberResetSlot = 1;

    public static readonly IReadOnlyList<string> CompareInputPortIds = Array.AsReadOnly(new[] { InputA, InputB });
    public static readonly IReadOnlyList<string> GateSignalInputPortIds =
        Array.AsReadOnly(new[] { InputA, InputB, ResetInput });
    public static readonly IReadOnlyList<string> SignalToNumberInputPortIds =
        Array.AsReadOnly(new[] { SignalInput, ResetInput });
    public static readonly IReadOnlyList<string> CompareOutputPortIds =
        Array.AsReadOnly(new[] { ConditionOutput, EventOutput });
    public static readonly IReadOnlyList<string> GateOutputPortIds =
        Array.AsReadOnly(new[] { PulseOutput, SignalOutput });

    public static bool IsCompare(GraphNodeKind kind) => kind.Value == CompareTypeId.ToString("D");
    public static bool IsAnd(GraphNodeKind kind) => kind.Value == SignalAndTypeId.ToString("D");
    public static bool IsOr(GraphNodeKind kind) => kind.Value == SignalOrTypeId.ToString("D");
    public static bool IsSignalToNumber(GraphNodeKind kind) => kind.Value == SignalToNumberTypeId.ToString("D");
    public static bool IsLoopClock(GraphNodeKind kind) => kind.Value == "LoopClock";
    public static bool IsQualityBranch(GraphNodeKind kind) => kind.Value == "QualityBranch";
    public static bool IsSignalCapture(GraphNodeKind kind) => kind.Value == "SignalCaptureVector4";
    public static bool IsLogical(GraphNodeKind kind) =>
        IsCompare(kind) || IsAnd(kind) || IsOr(kind) || IsSignalToNumber(kind) || IsLoopClock(kind) ||
        IsQualityBranch(kind) || IsSignalCapture(kind);

    public static bool IsCompare(GraphNodeDefinition definition) => definition.TypeId == CompareTypeId;
    public static bool IsAnd(GraphNodeDefinition definition) => definition.TypeId == SignalAndTypeId;
    public static bool IsOr(GraphNodeDefinition definition) => definition.TypeId == SignalOrTypeId;
    public static bool IsSignalToNumber(GraphNodeDefinition definition) => definition.TypeId == SignalToNumberTypeId;
    public static bool IsLoopClock(GraphNodeDefinition definition) => definition.TypeId == LoopClockTypeId;
    public static bool IsQualityBranch(GraphNodeDefinition definition) => definition.TypeId == QualityBranchTypeId;
    public static bool IsSignalCapture(GraphNodeDefinition definition) => definition.TypeId == SignalCaptureTypeId;
    public static bool IsLogical(GraphNodeDefinition definition) =>
        IsCompare(definition) || IsAnd(definition) || IsOr(definition) || IsSignalToNumber(definition) ||
        IsLoopClock(definition) || IsQualityBranch(definition) || IsSignalCapture(definition);

    public static void ValidateNodeParameters(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        if (!IsLogical(definition)) return;
        if (IsLoopClock(definition))
        {
            var ids = new[] { "length", "count", "bounded" };
            if (definition.Parameters.Count != ids.Length ||
                definition.Parameters.Any(parameter => !ids.Contains(parameter.Id) || parameter.Type != "float") ||
                nodeParameters?.Any(parameter => !ids.Contains(parameter.Id) || parameter.Type != "float") == true)
                throw new InvalidDataException("Loop Clock has an invalid parameter schema.");
            foreach (var parameter in definition.Parameters)
            {
                var value = nodeParameters?.SingleOrDefault(item => item.Id == parameter.Id)?.Value ?? parameter.DefaultValue;
                if (!NodeParameter.IsValidValue("float", value) || !value.TryGetDouble(out var number) ||
                    !double.IsFinite(number) || number < 0 ||
                    parameter.Id == "count" && (number > 9007199254740991d || Math.Truncate(number) != number) ||
                    parameter.Id == "bounded" && number != 0 && number != 1)
                    throw new InvalidDataException($"Loop Clock has an invalid '{parameter.Id}' value.");
            }
            return;
        }

        var expectedParameters = new List<(string Id, string Type, int Minimum, int Maximum)>();
        if (IsQualityBranch(definition))
        {
            expectedParameters.Add(("comparison", "enum:4a94e882-39e8-4efb-92c7-180000000041", 0, 5));
            expectedParameters.Add(("tier", "enum:4a94e882-39e8-4efb-92c7-180000000040", 0, 4));
        }
        if (IsCompare(definition))
        {
            expectedParameters.Add((OperationParameterId, $"enum:{CompareOperatorTypeId:D}", 0, 5));
            expectedParameters.Add((ModeParameterId, $"enum:{CompareModeTypeId:D}", 0, 1));
        }
        else if (IsAnd(definition))
            expectedParameters.Add((ModeParameterId, $"enum:{SignalAndModeTypeId:D}", 0, 1));
        else if (IsSignalToNumber(definition))
            expectedParameters.Add((ModeParameterId, $"enum:{SignalToNumberModeTypeId:D}", 0, 2));

        if (nodeParameters is not null &&
            nodeParameters.Any(parameter => expectedParameters.All(expected => expected.Id != parameter.Id)))
            throw new InvalidDataException($"Logical node '{definition.Name}' has an unknown parameter.");
        if (definition.Parameters.Count != expectedParameters.Count ||
            definition.Parameters.Any(parameter => expectedParameters.All(expected =>
                expected.Id != parameter.Id || expected.Type != parameter.Type)))
            throw new InvalidDataException($"Logical node '{definition.Name}' has an invalid parameter schema.");

        foreach (var (id, type, minimum, maximum) in expectedParameters)
        {
            var parameter = definition.Parameters.SingleOrDefault(item => item.Id == id && item.Type == type)
                ?? throw new InvalidDataException($"Logical node '{definition.Name}' has an invalid parameter definition.");
            var stored = nodeParameters?.SingleOrDefault(item => item.Id == id);
            if (stored is null) continue;
            if (stored.Type != type || !NodeParameter.IsValidValue(type, stored.Value) ||
                !stored.Value.TryGetInt32(out var value) || value < minimum || value > maximum)
                throw new InvalidDataException($"Logical node '{definition.Name}' has an invalid '{id}' value.");
        }

    }

    public static uint Configuration(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters)
    {
        ValidateNodeParameters(definition, nodeParameters);
        if (IsQualityBranch(definition))
            return (uint)ParameterValue(definition, nodeParameters, "comparison") |
                ((uint)ParameterValue(definition, nodeParameters, "tier") << 8);
        if (IsCompare(definition))
        {
            var operation = ParameterValue(definition, nodeParameters, OperationParameterId);
            var mode = ParameterValue(definition, nodeParameters, ModeParameterId);
            return (uint)operation | ((uint)mode << 8);
        }
        return IsAnd(definition) || IsSignalToNumber(definition)
            ? (uint)ParameterValue(definition, nodeParameters, ModeParameterId)
            : 0;
    }

    public static uint Configuration(GraphNodeSnapshot node, GraphNodeDefinition definition) =>
        Configuration(definition, node.Parameters);

    private static int ParameterValue(GraphNodeDefinition definition,
        IReadOnlyList<NodeParameterSnapshot>? nodeParameters, string id)
    {
        var parameterDefinition = definition.Parameters.Single(item => item.Id == id);
        var stored = nodeParameters?.SingleOrDefault(item => item.Id == id);
        var value = stored?.Value ?? parameterDefinition.DefaultValue;
        if ((stored is not null && stored.Type != parameterDefinition.Type) ||
            !NodeParameter.IsValidValue(parameterDefinition.Type, value) ||
            !value.TryGetInt32(out var number))
            throw new InvalidDataException($"Logical node '{definition.Name}' has an invalid '{id}' value.");
        return number;
    }
}
