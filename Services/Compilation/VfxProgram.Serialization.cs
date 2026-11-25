using System.Buffers.Binary;
using System.Text;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public sealed partial class VfxProgram
{
    internal byte[] ToPackageBinary() => new VfxProgram(
        _instructions, _typedInstructions, _customInstructions, _curveInstructions, 11, _gameIoPorts,
        _enumValueInstructions, _enumSelectInstructions, _branchGuards,
        _logicalInstructions, _logicalSignalRoutes, _valueTypes).ToBinary();

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
                if (_version >= 8)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(span[(_version >= 11 ? 44 : 112)..],
                        instruction.BranchGuardId);
                }
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
}
