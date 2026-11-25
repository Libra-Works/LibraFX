using System.Buffers.Binary;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public sealed partial class VfxProgram
{
    // Internal kernel instruction; it is emitted by the compiler, not exposed
    // as an authoring node. Keep the editable graph and live-debug code intact.
    internal static readonly Guid CpuLifetimeColorType = new("7c7e1000-2521-4bdb-b8a0-210000000049");
    private static Guid CpuType(int suffix) => new($"7c7e1000-2521-4bdb-b8a0-2100000000{suffix:x2}");
    private static Guid MathType(int suffix) => new($"74305ac1-91b2-4c33-8d44-0000000000{suffix:x2}");

    internal static byte[] OptimizeCpuKernelBinary(byte[] bytes)
    {
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("LFXB"u8))
            throw new InvalidDataException("Invalid CPU stage bytecode header.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (version is not (3 or 11)) return bytes;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (count < 0 || count > MaxInstructions || bytes.Length < 16 + count * 256)
            throw new InvalidDataException("Invalid CPU stage length.");
        if (version == 3 && bytes.Length != 16 + count * 256)
            throw new InvalidDataException("Invalid V3 CPU stage length.");
        var valueTypes = Array.Empty<VfxNodeValueTypes>();
        var logicalRoutes = Array.Empty<VfxLogicalSignalRoute>();
        if (version == 11)
        {
            var typeOffset = bytes.Length - ValueTypeHeaderSize - count * ValueTypeRecordSize;
            if (typeOffset < 16 + count * 256 || !bytes.AsSpan(typeOffset, 4).SequenceEqual("LFTY"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(typeOffset + 4)) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(typeOffset + 6)) != ValueTypeRecordSize)
                throw new InvalidDataException("Invalid CPU value-type metadata.");
            var enumOffset = 16 + count * 256;
            if (typeOffset - enumOffset < EnumBranchHeaderSize + LogicalEventHeaderSize ||
                !bytes.AsSpan(enumOffset, 4).SequenceEqual("LFEB"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(enumOffset + 12)) != EnumBranchHeaderSize)
                return bytes;
            var logicalOffset = enumOffset + EnumBranchHeaderSize;
            if (!bytes.AsSpan(logicalOffset, 4).SequenceEqual("LFLE"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(logicalOffset + 6)) != 0)
                return bytes;
            var routeCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(logicalOffset + 8));
            if (routeCount > MaxSignalRoutes ||
                logicalOffset + LogicalEventHeaderSize + (long)routeCount * 12 != typeOffset)
                return bytes;
            logicalRoutes = new VfxLogicalSignalRoute[routeCount];
            for (var index = 0; index < logicalRoutes.Length; index++)
            {
                var route = bytes.AsSpan(logicalOffset + LogicalEventHeaderSize + index * 12, 12);
                logicalRoutes[index] = new(BinaryPrimitives.ReadInt32LittleEndian(route), route[4],
                    BinaryPrimitives.ReadInt32LittleEndian(route[8..]), route[5]);
            }
            valueTypes = new VfxNodeValueTypes[count];
            for (var index = 0; index < count; index++)
            {
                var record = bytes.AsSpan(typeOffset + ValueTypeHeaderSize + index * ValueTypeRecordSize, ValueTypeRecordSize);
                if (record[1] > 4) throw new InvalidDataException("Invalid CPU value-type input count.");
                valueTypes[index] = new((GraphValueType)record[0],
                    record.Slice(2, record[1]).ToArray().Select(value => (GraphValueType)value).ToArray());
            }
        }
        var typed = new VfxTypedInstruction[count];
        var customs = new List<VfxCustomInstruction>();
        for (var index = 0; index < count; index++)
        {
            var record = bytes.AsSpan(16 + index * 256, 256);
            var id = new Guid(record[..16], bigEndian: true);
            var opcode = record[16];
            double[] Values(int offset)
            {
                var values = Enumerable.Range(0, 4).Select(lane =>
                    BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(16 + index * 256 + offset + lane * 8))).ToArray();
                if (values.Any(value => !double.IsFinite(value))) throw new InvalidDataException("Nonfinite CPU constant.");
                return values;
            }
            int Source(int offset)
            {
                var source = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16 + index * 256 + offset));
                if (source < -1 || source >= index) throw new InvalidDataException("Invalid CPU numeric dependency.");
                return source;
            }
            if (opcode is 40 or 41)
            {
                if (record[18] > 4 || record[19] > 4) throw new InvalidDataException("Invalid CPU custom port count.");
                var inputs = new VfxCustomInput[record[18]];
                for (var slot = 0; slot < inputs.Length; slot++)
                    inputs[slot] = new(Source(48 + slot * 8), record[52 + slot * 8], record[53 + slot * 8],
                        record[54 + slot * 8] != 0, Values(80 + slot * 32));
                var targets = new int[4];
                for (var branch = 0; branch < 4; branch++)
                {
                    targets[branch] = BinaryPrimitives.ReadInt32LittleEndian(record[(208 + branch * 4)..]);
                    if (targets[branch] < -1 || targets[branch] >= count)
                        throw new InvalidDataException("Invalid CPU Signal destination.");
                }
                customs.Add(new(id, opcode, record[17], (record[20] & 1) != 0,
                    new Guid(record.Slice(24, 16), bigEndian: true), BinaryPrimitives.ReadInt32LittleEndian(record[40..]),
                    record[19], inputs, targets, [[], [], [], []]));
                typed[index] = new(id, (VfxOpcode)opcode, record[17], 0, 0, record[20], 0, 0, -1, -1, new double[4], new double[4]);
            }
            else
            {
                if (opcode is < 1 or > 39) return bytes;
                typed[index] = new(id, (VfxOpcode)opcode, record[17], record[18], record[19], record[20], record[21], record[22],
                    Source(24), Source(28), Values(32), Values(64));
            }
        }
        var legacy = typed.Select(node => new VfxInstruction(node.NodeId, node.Opcode, node.InputA, node.InputB,
            node.DefaultA[0], node.DefaultB[0])).ToArray();
        var program = new VfxProgram(legacy, typed, customs.ToArray(), [], 3,
            logicalSignalRoutes: logicalRoutes, valueTypes: valueTypes);
        // Preserve noncanonical or extension-bearing records byte for byte.
        var canonical = version == 11 ? program.ToPackageBinary() : program.ToBinary();
        if (!canonical.AsSpan().SequenceEqual(bytes)) return bytes;
        var optimized = program.OptimizeCpuKernel();
        return ReferenceEquals(program, optimized) ? bytes :
            version == 11 ? optimized.ToPackageBinary() : optimized.ToBinary();
    }

    internal VfxProgram OptimizeCpuKernel()
    {
        // V3 has no guards, metadata tails or Signal fanout to relocate. Unknown
        // callbacks can observe evaluation order, so they are explicit barriers.
        if (_version != 3 || _curveInstructions.Length != 0 || _gameIoPorts.Length != 0) return this;
        var knownCpu = new[] { 5, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x42, 0x43, 0x44 }.Select(CpuType).ToHashSet();
        if (_customInstructions.Any(node => !knownCpu.Contains(node.TypeId) &&
                node.TypeId != MathType(20) && node.TypeId != MathType(47))) return this;
        var indices = _typedInstructions.Select((node, index) => (node.NodeId, index))
            .ToDictionary(item => item.NodeId, item => item.index);
        var customByIndex = _customInstructions.ToDictionary(node => indices[node.NodeId]);
        var uses = new int[_typedInstructions.Length];
        void Use(int index) { if (index >= 0) uses[index]++; }
        for (var index = 0; index < _typedInstructions.Length; index++)
            if (customByIndex.TryGetValue(index, out var custom))
                foreach (var input in custom.Inputs) Use(input.SourceIndex);
            else { Use(_typedInstructions[index].InputA); Use(_typedInstructions[index].InputB); }
        bool Single(int index) => index >= 0 && uses[index] == 1;
        bool Constant(VfxCustomInput input, double value) => input.SourceIndex < 0 && input.Defaults[0] == value;
        bool Custom(int index, Guid type) => Single(index) && customByIndex.TryGetValue(index, out var node) &&
            node.TypeId == type && node.Opcode == 40 && node.AbiVersion == 1 && node.OutputWidth == 1 && !node.OutputIsColor &&
            node.BranchCount == 0 && node.Inputs.Length == (type == MathType(20) ? 3 : 0);
        var removed = new HashSet<int>();
        var replacements = new Dictionary<int, VfxCustomInstruction>();
        foreach (var (index, compose) in customByIndex)
        {
            if (compose.TypeId != MathType(47) || compose.Opcode != 40 || compose.AbiVersion != 1 || compose.OutputWidth != 4 ||
                compose.OutputIsColor || compose.BranchCount != 0 || compose.Inputs.Length != 4 ||
                compose.Inputs.Take(3).Any(input => input.SourceIndex >= 0) ||
                compose.Inputs.Any(input => input.IsColor || input.SourceComponent != 0 ||
                    input.SourceWidth != (input.SourceIndex < 0 ? 0 : 1))) continue;
            var subtractIndex = compose.Inputs[3].SourceIndex;
            if (!Single(subtractIndex)) continue;
            var subtract = _typedInstructions[subtractIndex];
            if (subtract.Opcode != VfxOpcode.Subtract || subtract.OutputWidth != 1 ||
                subtract.InputA >= 0 || subtract.DefaultA[0] != 1 || subtract.InputAWidth != 0 ||
                subtract.InputBWidth != 1 || subtract.TypeFlags != 0 ||
                subtract.InputASourceComponent != 0 || subtract.InputBSourceComponent != 0) continue;
            var clampIndex = subtract.InputB;
            if (!Custom(clampIndex, MathType(20))) continue;
            var clamp = customByIndex[clampIndex];
            if (!Constant(clamp.Inputs[1], 0) || !Constant(clamp.Inputs[2], 1) ||
                clamp.Inputs.Any(input => input.IsColor || input.SourceComponent != 0 ||
                    input.SourceWidth != (input.SourceIndex < 0 ? 0 : 1))) continue;
            var divideIndex = clamp.Inputs[0].SourceIndex;
            if (!Single(divideIndex)) continue;
            var divide = _typedInstructions[divideIndex];
            if (divide.Opcode != VfxOpcode.Divide || divide.OutputWidth != 1 ||
                divide.InputAWidth != 1 || divide.InputBWidth != 1 || divide.TypeFlags != 0 ||
                divide.InputASourceComponent != 0 || divide.InputBSourceComponent != 0 ||
                !Custom(divide.InputA, CpuType(0x15)) || !Custom(divide.InputB, CpuType(0x14))) continue;
            foreach (var dead in new[] { subtractIndex, clampIndex, divideIndex, divide.InputA, divide.InputB }) removed.Add(dead);
            replacements[index] = compose with { TypeId = CpuLifetimeColorType, Inputs = compose.Inputs.Take(3).ToArray() };
        }
        if (removed.Count == 0) return this;
        var remap = new int[_typedInstructions.Length];
        var next = 0;
        for (var index = 0; index < remap.Length; index++) remap[index] = removed.Contains(index) ? -1 : next++;
        int Map(int index) => index < 0 ? index : remap[index] >= 0 ? remap[index] :
            throw new InvalidDataException("CPU kernel optimization removed a referenced instruction.");
        var typed = _typedInstructions.Where((_, index) => !removed.Contains(index)).Select(item =>
            item with { InputA = Map(item.InputA), InputB = Map(item.InputB) }).ToArray();
        var legacy = _instructions.Where((_, index) => !removed.Contains(index)).Select(item =>
            item with { InputA = Map(item.InputA), InputB = Map(item.InputB) }).ToArray();
        var customs = customByIndex.Where(pair => !removed.Contains(pair.Key)).Select(pair =>
        {
            var item = replacements.GetValueOrDefault(pair.Key, pair.Value);
            return item with { Inputs = item.Inputs.Select(input => input with { SourceIndex = Map(input.SourceIndex) }).ToArray(),
                BranchTargets = item.BranchTargets.Select(Map).ToArray(),
                BranchAdditionalTargets = item.BranchAdditionalTargets.Select(branch => branch.Select(Map).ToArray()).ToArray() };
        }).ToArray();
        var valueTypes = _valueTypes.Where((_, index) => !removed.Contains(index)).ToArray();
        if (valueTypes.Length != 0)
        {
            foreach (var (index, replacement) in replacements)
            {
                var mappedIndex = remap[index];
                valueTypes[mappedIndex] = valueTypes[mappedIndex] with
                {
                    InputTypes = valueTypes[mappedIndex].InputTypes.Take(replacement.Inputs.Length).ToArray()
                };
            }
        }
        var logicalRoutes = _logicalSignalRoutes.Select(route => route with
        {
            SourceIndex = Map(route.SourceIndex),
            TargetIndex = Map(route.TargetIndex)
        }).ToArray();
        return new VfxProgram(legacy, typed, customs, [], 3,
            logicalSignalRoutes: logicalRoutes, valueTypes: valueTypes);
    }
}
