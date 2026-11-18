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

public sealed partial class VfxProgram
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
}
