#include "LbVfxRuntimeInternal.h"

namespace liberafx {
using namespace vfx_runtime_detail;

auto Program::load(const void* bytes, std::size_t size, Program& out,
                   const ProfilingHooks& profiling) noexcept -> Result
{
    if (!profiling.valid())
    {
        return Result::InvalidArgument;
    }
    auto profileScope = ProfileScope(profiling, ProfileEvent::Load);
    if (bytes == nullptr || size < legacyHeaderSize)
    {
        return Result::InvalidArgument;
    }
    auto data = static_cast<const std::uint8_t*>(bytes);
    if (std::memcmp(data, "LFXB", 4) != 0)
    {
        return Result::InvalidBinary;
    }
    auto version = read16(data + 4);
    if (version >= 4 && version <= 11)
    {
        return load(bytes, size, nullptr, 0, nullptr, 0, out, profiling);
    }
    if (version != 1 && version != 2)
    {
        return Result::UnsupportedBinary;
    }
    if (data[6] != 1)
    {
        return Result::UnsupportedBinary;
    }
    if (data[7] != 0)
    {
        return Result::InvalidBinary;
    }
    auto count = read32(data + 8);
    auto headerSize = version == 1 ? legacyHeaderSize : typedHeaderSize;
    auto instructionSize = version == 1 ? legacyInstructionSize : typedInstructionSize;
    if (count > maxNodes || size < headerSize)
    {
        return Result::InvalidBinary;
    }
    if (version == 2 && (read16(data + 12) != typedInstructionSize || read16(data + 14) != 0))
    {
        return Result::UnsupportedBinary;
    }
    if (size != headerSize + std::size_t{count} * instructionSize)
    {
        return Result::InvalidBinary;
    }
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, version, index);
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte)
        {
            emptyId = emptyId && current[byte] == 0;
        }
        if (emptyId)
        {
            return Result::InvalidBinary;
        }
        for (auto other = std::uint32_t{0}; other < index; ++other)
        {
            if (std::memcmp(current, instruction(data, version, other), 16) == 0)
            {
                return Result::InvalidBinary;
            }
        }
        if (version == 1)
        {
            auto opcode = current[16];
            auto a = read32(current + 20);
            auto b = read32(current + 24);
            if (opcode < 1 || opcode > 6)
            {
                return Result::UnsupportedBinary;
            }
            if (current[17] != 0 || current[18] != 0 || current[19] != 0 || !validIndex(a, index) ||
                !validIndex(b, index) || !std::isfinite(readDouble(current + 28)) ||
                !std::isfinite(readDouble(current + 36)) || (opcode == 1 && b != noInput) ||
                (opcode == 2 && (a != noInput || b != noInput)))
            {
                return Result::InvalidBinary;
            }
        } else
        {
            if (current[16] == 0 || current[16] > 39)
            {
                return Result::UnsupportedBinary;
            }
            if (!validTypedInstruction(current, index))
            {
                return Result::InvalidBinary;
            }
            auto inputA = read32(current + 24);
            auto inputB = read32(current + 28);
            auto validSource = [&](auto sourceIndex, auto sourceWidth, auto selectedLane,
                                   auto colorFlag) noexcept {
                if (sourceIndex == noInput)
                {
                    return sourceWidth == 0 && selectedLane == 0 && colorFlag == 0;
                }
                auto source = instruction(data, 2, sourceIndex);
                auto sourceIsBreak = source[16] >= 26 && source[16] <= 28;
                if (sourceIsBreak)
                {
                    return sourceWidth == 1 && selectedLane < source[17] && colorFlag == 0;
                }
                return sourceWidth == source[17] && selectedLane == 0 &&
                       ((source[20] & 1) != 0) == (colorFlag != 0);
            };
            if (!validSource(inputA, current[18], current[21], current[20] & 2) ||
                !validSource(inputB, current[19], current[22], current[20] & 4))
            {
                return Result::InvalidBinary;
            }
        }
    }
    out.bytes_ = data;
    out.nodeCount_ = count;
    out.version_ = version;
    out.bindings_ = nullptr;
    out.stateBytes_ = 0;
    out.signalRoutesOffset_ = 0;
    out.signalStackOffset_ = 0;
    out.signalStackCapacity_ = 0;
    out.gameIoMetadataOffset_ = 0;
    out.valueTypeMetadataOffset_ = 0;
    out.gameIoPortCount_ = 0;
    out.enumBranchMetadataOffset_ = 0;
    out.enumSelectRecordsOffset_ = 0;
    out.enumGuardRecordsOffset_ = 0;
    out.enumGuardValuesOffset_ = 0;
    out.branchClockOffset_ = 0;
    out.enumValueCount_ = 0;
    out.enumSelectCount_ = 0;
    out.branchGuardCount_ = 0;
    out.logicalEventMetadataOffset_ = 0;
    out.logicalInstructionRecordsOffset_ = 0;
    out.logicalSignalRoutesOffset_ = 0;
    out.logicalEventMaskOffset_ = 0;
    out.logicalDirtyOffset_ = 0;
    out.logicalQueueOffset_ = 0;
    out.signalPayloadQueueOffset_ = 0;
    out.logicalQueueCapacity_ = 0;
    out.logicalInstructionCount_ = 0;
    out.logicalSignalRouteCount_ = 0;
    out.generation_ = nextProgramGeneration.fetch_add(1, std::memory_order_relaxed);
    return Result::Ok;
}

auto Program::load(const void* bytes, std::size_t size, const CustomNodeDescriptor* registry,
                   std::size_t registryCount, CustomNodeBinding* bindings, std::size_t bindingCount,
                   Program& out, const ProfilingHooks& profiling) noexcept -> Result
{
    if (!profiling.valid())
    {
        return Result::InvalidArgument;
    }
    if (bytes == nullptr || size < legacyHeaderSize)
    {
        return Result::InvalidArgument;
    }
    auto data = static_cast<const std::uint8_t*>(bytes);
    if (std::memcmp(data, "LFXB", 4) != 0)
    {
        return Result::InvalidBinary;
    }
    auto version = read16(data + 4);
    if (version < 3 || version > 11)
    {
        return load(bytes, size, out, profiling);
    }
    out.enumBranchMetadataOffset_ = 0;
    out.enumSelectRecordsOffset_ = 0;
    out.enumGuardRecordsOffset_ = 0;
    out.enumGuardValuesOffset_ = 0;
    out.branchClockOffset_ = 0;
    out.enumValueCount_ = 0;
    out.enumSelectCount_ = 0;
    out.branchGuardCount_ = 0;
    out.logicalEventMetadataOffset_ = 0;
    out.logicalInstructionRecordsOffset_ = 0;
    out.logicalSignalRoutesOffset_ = 0;
    out.logicalEventMaskOffset_ = 0;
    out.logicalDirtyOffset_ = 0;
    out.logicalQueueOffset_ = 0;
    out.logicalQueueCapacity_ = 0;
    out.logicalInstructionCount_ = 0;
    out.logicalSignalRouteCount_ = 0;
    out.gameIoMetadataOffset_ = 0;
    out.valueTypeMetadataOffset_ = 0;
    out.signalPayloadQueueOffset_ = 0;
    out.gameIoPortCount_ = 0;
    auto profileScope = ProfileScope(profiling, ProfileEvent::Load);
    if (size < typedHeaderSize)
    {
        return Result::InvalidBinary;
    }
    if (data[6] != 1)
    {
        return Result::UnsupportedBinary;
    }
    if (data[7] != 0 || read16(data + 12) != customInstructionSize || read16(data + 14) != 0)
    {
        return Result::InvalidBinary;
    }
    auto count = read32(data + 8);
    if (count > maxNodes)
    {
        return Result::InvalidBinary;
    }
    auto binarySize = size;
    auto valueTypeMetadataOffset = std::size_t{0};
    if (version >= 10)
    {
        auto valueTypeMetadataSize = valueTypeHeaderSize + std::size_t{count} * valueTypeRecordSize;
        if (binarySize < valueTypeMetadataSize)
        {
            return Result::InvalidBinary;
        }
        valueTypeMetadataOffset = binarySize - valueTypeMetadataSize;
        if (std::memcmp(data + valueTypeMetadataOffset, "LFTY", 4) != 0 ||
            read16(data + valueTypeMetadataOffset + 4) != 1 ||
            read16(data + valueTypeMetadataOffset + 6) != valueTypeRecordSize)
        {
            return Result::InvalidBinary;
        }
        size = valueTypeMetadataOffset;
    }
    auto instructionBytes = typedHeaderSize + std::size_t{count} * customInstructionSize;
    if (size < instructionBytes || (version == 3 && size != instructionBytes))
    {
        return Result::InvalidBinary;
    }
    auto hasCustomNodes = false;
    for (auto index = 0u; index < count; ++index)
    {
        auto opcode = instruction(data, version, index)[16];
        hasCustomNodes = hasCustomNodes || opcode == customParameterOpcode ||
                         opcode == customSignalOpcode || opcode == logicalEventSignalOpcode;
    }
    if ((hasCustomNodes && (bindings == nullptr || bindingCount < count ||
                            !isAligned(bindings, alignof(CustomNodeBinding)))) ||
        (registryCount != 0 && !isAligned(registry, alignof(CustomNodeDescriptor))) ||
        (registryCount != 0 && registry == nullptr))
    {
        return Result::InvalidArgument;
    }
    for (auto index = std::size_t{0}; index < registryCount; ++index)
    {
        const auto& descriptor = registry[index];
        if (descriptor.abiVersion == 0 || descriptor.function == nullptr ||
            descriptor.inputCount > customMaxPorts ||
            !validStateAlignment(descriptor.stateAlignment) ||
            descriptor.stateBytes >
                (std::numeric_limits<std::size_t>::max() - descriptor.stateAlignment + 1))
        {
            return Result::InvalidArgument;
        }
        if ((descriptor.execution == CustomExecution::Parameter &&
             (descriptor.outputWidth < 1 || descriptor.outputWidth > 4 ||
              descriptor.branchCount != 0)) ||
            (descriptor.execution == CustomExecution::Signal &&
             (descriptor.outputWidth != 0 || descriptor.outputIsColor ||
              descriptor.branchCount > customMaxPorts)) ||
            (descriptor.execution != CustomExecution::Parameter &&
             descriptor.execution != CustomExecution::Signal) ||
            (descriptor.outputIsColor && descriptor.outputWidth != 4))
        {
            return Result::InvalidArgument;
        }
        for (auto input = 0u; input < descriptor.inputCount; ++input)
        {
            if (descriptor.inputWidths[input] < 1 || descriptor.inputWidths[input] > 4 ||
                descriptor.inputIsColor[input] > 1 ||
                (descriptor.inputIsColor[input] != 0 && descriptor.inputWidths[input] != 4))
            {
                return Result::InvalidArgument;
            }
        }
        for (auto other = std::size_t{0}; other < index; ++other)
        {
            if (std::memcmp(descriptor.typeId, registry[other].typeId, 16) == 0 &&
                descriptor.abiVersion == registry[other].abiVersion)
            {
                return Result::InvalidArgument;
            }
        }
    }
    auto logicalDescriptorCount = std::size_t{0};
    auto logicalDescriptors = logicalNodes(logicalDescriptorCount);
    for (auto index = std::size_t{0}; index < logicalDescriptorCount; ++index)
    {
        if (!validLogicalDescriptor(logicalDescriptors[index]))
        {
            return Result::InvalidArgument;
        }
        for (auto other = std::size_t{0}; other < index; ++other)
        {
            if (std::memcmp(logicalDescriptors[index].typeId, logicalDescriptors[other].typeId,
                            16) == 0)
            {
                return Result::InvalidArgument;
            }
        }
    }

    auto resolveDescriptor = [&](const std::uint8_t* typeId, std::uint32_t abiVersion,
                                 bool& registeredType) noexcept -> const CustomNodeDescriptor* {
        registeredType = false;
        for (auto index = std::size_t{0}; index < registryCount; ++index)
        {
            if (std::memcmp(registry[index].typeId, typeId, 16) == 0)
            {
                registeredType = true;
                if (registry[index].abiVersion == abiVersion)
                {
                    return &registry[index];
                }
            }
        }
        if (registeredType)
        {
            return nullptr;
        }
        auto builtinCount = std::size_t{0};
        auto builtins = builtinNodes(builtinCount);
        for (auto index = std::size_t{0}; index < builtinCount; ++index)
        {
            if (std::memcmp(builtins[index].typeId, typeId, 16) == 0)
            {
                registeredType = true;
                if (builtins[index].abiVersion == abiVersion)
                {
                    return &builtins[index];
                }
            }
        }
        return nullptr;
    };
    auto valuesSize = std::size_t{count} * 4 * sizeof(double);
    auto totalWorkBytes = valuesSize;
    auto needsSignalPayloads = false;
    auto curveDataBytes = std::size_t{0};
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, 3, index);
        if (current[16] != curveOpcode)
        {
            continue;
        }
        auto keyCount = read32(current + 228);
        if (keyCount > maxCurveKeysPerInstruction ||
            std::size_t{keyCount} > (size - instructionBytes - curveDataBytes) / curveKeySize)
        {
            return Result::InvalidBinary;
        }
        curveDataBytes += std::size_t{keyCount} * curveKeySize;
    }
    auto expectedCurveOffset = std::size_t{0};
    auto totalCurveKeys = std::uint32_t{0};
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, 3, index);
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte)
        {
            emptyId = emptyId && current[byte] == 0;
        }
        if (emptyId)
        {
            return Result::InvalidBinary;
        }
        for (auto other = std::uint32_t{0}; other < index; ++other)
        {
            if (std::memcmp(current, instruction(data, 3, other), 16) == 0)
            {
                return Result::InvalidBinary;
            }
        }
        auto opcode = current[16];
        if (opcode == curveOpcode)
        {
            if (version < 4 || !validCurveInstruction(current, version))
            {
                return Result::InvalidBinary;
            }
            auto curveOffset = read32(current + 224);
            auto curveKeyCount = read32(current + 228);
            if (curveOffset != expectedCurveOffset || curveKeyCount > maxCurveKeysPerInstruction ||
                curveKeyCount > maxCurveKeysTotal - totalCurveKeys ||
                std::size_t{curveKeyCount} > (curveDataBytes - expectedCurveOffset) / curveKeySize)
            {
                return Result::InvalidBinary;
            }
            auto keys = data + instructionBytes + curveOffset;
            auto previousTime = -std::numeric_limits<double>::infinity();
            for (auto keyIndex = 0u; keyIndex < curveKeyCount; ++keyIndex)
            {
                auto key = keys + std::size_t{keyIndex} * curveKeySize;
                auto keyTime = readDouble(key);
                if (!validCurveKey(key) || keyTime <= previousTime)
                {
                    return Result::InvalidBinary;
                }
                previousTime = keyTime;
            }
            auto addedBytes = std::size_t{curveKeyCount} * curveKeySize;
            expectedCurveOffset += addedBytes;
            totalCurveKeys += curveKeyCount;
            continue;
        }
        if ((opcode >= 1 && opcode <= 39) ||
            (version >= 10 && opcode >= int32Opcode && opcode <= int32ToFloatOpcode))
        {
            if (!validTypedInstruction(current, index))
            {
                return Result::InvalidBinary;
            }
            auto inputA = read32(current + 24);
            auto inputB = read32(current + 28);
            auto validSource = [&](auto sourceIndex, auto sourceWidth, auto selectedLane,
                                   auto colorFlag) noexcept {
                if (sourceIndex == noInput)
                {
                    return sourceWidth == 0 && selectedLane == 0 && colorFlag == 0;
                }
                auto source = instruction(data, 3, sourceIndex);
                auto sourceIsBreak = source[16] >= 26 && source[16] <= 28;
                return sourceIsBreak
                           ? sourceWidth == 1 && selectedLane < source[17] && colorFlag == 0
                           : sourceWidth == source[17] && selectedLane == 0 &&
                                 ((source[20] & 1) != 0) == (colorFlag != 0);
            };
            if (!validSource(inputA, current[18], current[21], current[20] & 2) ||
                !validSource(inputB, current[19], current[22], current[20] & 4))
            {
                return Result::InvalidBinary;
            }
            for (auto byte = typedInstructionSize; byte < customInstructionSize; ++byte)
            {
                if (!(version >= 8 && byte >= 112 && byte < 116) && current[byte] != 0)
                {
                    return Result::InvalidBinary;
                }
            }
            continue;
        }
        if (opcode == currentPhaseOpcode || opcode == phaseTimeOpcode)
        {
            if (version < 11 || current[17] != 1 || current[18] != 0 || current[19] != 0 ||
                current[20] != 0 || current[21] != 0 || current[22] != 0 || current[23] != 0 ||
                read32(current + 24) != noInput || read32(current + 28) != noInput)
            {
                return Result::InvalidBinary;
            }
            constexpr auto phaseEnum = std::array<std::uint8_t, 16>{
                0x4a, 0x94, 0xe8, 0x82, 0x39, 0xe8, 0x4e, 0xfb, 0x92, 0xc7, 0x18, 0, 0, 0, 0, 0x30};
            for (auto byte = 32u; byte < customInstructionSize; ++byte)
            {
                if (byte >= 112 && byte < 116)
                {
                    continue;
                }
                auto expected = opcode == currentPhaseOpcode && byte >= 96 && byte < 112
                                    ? phaseEnum[byte - 96]
                                    : std::uint8_t{};
                if (current[byte] != expected)
                {
                    return Result::InvalidBinary;
                }
            }
            continue;
        }
        if (opcode == enumValueOpcode || opcode == enumSelectOpcode || opcode == branchTimeOpcode)
        {
            if (version < 8 || current[18] > 1 || current[19] != 0 || current[20] != 0 ||
                current[21] != 0 || current[22] != 0 || current[23] != 0 ||
                read32(current + 28) != noInput || !validIndex(read32(current + 24), index))
            {
                return Result::InvalidBinary;
            }
            auto inputA = read32(current + 24);
            if (opcode == enumValueOpcode)
            {
                if (current[17] != 1 || (inputA == noInput ? current[18] != 0 : current[18] != 1) ||
                    !std::isfinite(readDouble(current + 32)) ||
                    std::trunc(readDouble(current + 32)) != readDouble(current + 32) ||
                    readDouble(current + 32) < std::numeric_limits<std::int32_t>::min() ||
                    readDouble(current + 32) > std::numeric_limits<std::int32_t>::max())
                {
                    return Result::InvalidBinary;
                }
            } else if (opcode == enumSelectOpcode)
            {
                if (current[17] < 1 || current[17] > 4 || current[18] != 1 || inputA == noInput ||
                    !isEnumSourceOpcode(instruction(data, 3, inputA)[16]))
                {
                    return Result::InvalidBinary;
                }
            } else if (current[17] != 1 || current[18] != 0 || inputA != noInput ||
                       read32(current + 112) == noInput)
            {
                return Result::InvalidBinary;
            }
            auto typeIsEmpty = true;
            for (auto byte = 0u; byte < 16; ++byte)
            {
                typeIsEmpty = typeIsEmpty && current[96 + byte] == 0;
            }
            if ((opcode == branchTimeOpcode) != typeIsEmpty)
            {
                return Result::InvalidBinary;
            }
            for (auto byte = 116u; byte < customInstructionSize; ++byte)
            {
                if (current[byte] != 0)
                {
                    return Result::InvalidBinary;
                }
            }
            continue;
        }
        if (opcode == logicalEventSignalOpcode)
        {
            if (version < 9 || current[18] > customMaxPorts || current[19] > customMaxPorts ||
                current[20] != 0 || current[21] != 0 || current[22] != 0 || current[23] != 0 ||
                (version < 11 && read32(current + 44) != 0) || read32(current + 40) == 0)
            {
                return Result::InvalidBinary;
            }
            auto registeredType = false;
            auto descriptor =
                findLogicalDescriptor(current + 24, read32(current + 40), registeredType);
            if (descriptor == nullptr)
            {
                return registeredType ? Result::UnsupportedBinary : Result::UnknownCustomNode;
            }
            if (!validLogicalDescriptor(*descriptor) || descriptor->inputCount != current[18] ||
                descriptor->outputWidth != current[17] ||
                descriptor->signalBranchCount != current[19])
            {
                return Result::UnsupportedBinary;
            }
            if (std::memcmp(current + 24, logicalSignalCaptureTypeId.data(), 16) == 0)
            {
                if (version < 11)
                {
                    return Result::UnsupportedBinary;
                }
                needsSignalPayloads = true;
            }
            auto alignedStateOffset = std::size_t{0};
            if (!alignUp(totalWorkBytes, descriptor->stateAlignment, alignedStateOffset) ||
                descriptor->stateBytes >
                    std::numeric_limits<std::size_t>::max() - alignedStateOffset)
            {
                return Result::InvalidBinary;
            }
            totalWorkBytes = alignedStateOffset + descriptor->stateBytes;
            for (auto input = 0u; input < customMaxPorts; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                auto sourceWidth = inputRecord[4];
                auto selectedLane = inputRecord[5];
                if (input < descriptor->inputCount)
                {
                    if (!validIndex(sourceIndex, index) || inputRecord[6] != 0 ||
                        inputRecord[7] != 0 ||
                        (sourceIndex == noInput ? sourceWidth != 0 || selectedLane != 0
                                                : sourceWidth != descriptor->inputWidths[input]))
                    {
                        return Result::InvalidBinary;
                    }
                    if (sourceIndex != noInput)
                    {
                        auto source = instruction(data, 3, sourceIndex);
                        auto sourceWidthActual = source[16] == customParameterOpcode ||
                                                         source[16] == logicalEventSignalOpcode
                                                     ? source[17]
                                                 : source[16] >= 26 && source[16] <= 28
                                                     ? 1
                                                     : source[17];
                        if (source[16] == customSignalOpcode || sourceWidth != sourceWidthActual ||
                            selectedLane != 0)
                        {
                            return Result::InvalidBinary;
                        }
                    }
                } else if (sourceIndex != noInput || sourceWidth != 0 || selectedLane != 0 ||
                           inputRecord[6] != 0 || inputRecord[7] != 0)
                {
                    return Result::InvalidBinary;
                }
                for (auto lane = 0u; lane < 4; ++lane)
                {
                    if (!std::isfinite(readDouble(current + 80 + input * 32 + lane * 8)))
                    {
                        return Result::InvalidBinary;
                    }
                }
            }
            for (auto branch = 0u; branch < customMaxPorts; ++branch)
            {
                auto target = read32(current + 208 + branch * 4);
                if (branch < current[19])
                {
                    if (target != noInput &&
                        (target <= index || target >= count ||
                         (instruction(data, 3, target)[16] != customSignalOpcode &&
                          instruction(data, 3, target)[16] != logicalEventSignalOpcode)))
                    {
                        return Result::InvalidBinary;
                    }
                } else if (target != noInput)
                {
                    return Result::InvalidBinary;
                }
            }
            if (version < 11)
            {
                for (auto byte = 116u; byte < 208; ++byte)
                {
                    if (current[byte] != 0)
                    {
                        return Result::InvalidBinary;
                    }
                }
            }
            if (version < 5)
            {
                for (auto byte = 224u; byte < customInstructionSize; ++byte)
                {
                    if (current[byte] != 0)
                    {
                        return Result::InvalidBinary;
                    }
                }
            }
            continue;
        }
        if (opcode != customParameterOpcode && opcode != customSignalOpcode)
        {
            return Result::UnsupportedBinary;
        }
        auto inputCount = current[18];
        auto branchCount = current[19];
        auto outputWidth = current[17];
        if (inputCount > customMaxPorts || (current[20] & 0xfe) != 0 || current[21] != 0 ||
            current[22] != 0 || current[23] != 0 || (version < 11 && read32(current + 44) != 0) ||
            read32(current + 40) == 0 ||
            (opcode == customParameterOpcode &&
             (outputWidth < 1 || outputWidth > 4 || branchCount != 0)) ||
            (opcode == customSignalOpcode &&
             (outputWidth != 0 || branchCount > customMaxPorts || current[20] != 0)))
        {
            return Result::InvalidBinary;
        }
        if (version < 5 || opcode != customSignalOpcode)
        {
            for (auto byte = 224u; byte < customInstructionSize; ++byte)
            {
                if (current[byte] != 0)
                {
                    return Result::InvalidBinary;
                }
            }
        }
        auto emptyTypeId = true;
        for (auto byte = 0u; byte < 16; ++byte)
        {
            emptyTypeId = emptyTypeId && current[24 + byte] == 0;
        }
        if (emptyTypeId)
        {
            return Result::InvalidBinary;
        }
        auto registeredType = false;
        auto descriptor = resolveDescriptor(current + 24, read32(current + 40), registeredType);
        if (descriptor == nullptr)
        {
            return registeredType ? Result::UnsupportedBinary : Result::UnknownCustomNode;
        }
        if (descriptor->inputCount != inputCount ||
            descriptor->execution != (opcode == customParameterOpcode ? CustomExecution::Parameter
                                                                      : CustomExecution::Signal) ||
            descriptor->outputWidth != outputWidth || descriptor->branchCount != branchCount ||
            descriptor->outputIsColor != ((current[20] & 1) != 0))
        {
            return Result::UnsupportedBinary;
        }
        auto alignedStateOffset = std::size_t{0};
        if (!validStateAlignment(descriptor->stateAlignment) ||
            !alignUp(totalWorkBytes, descriptor->stateAlignment, alignedStateOffset) ||
            descriptor->stateBytes > std::numeric_limits<std::size_t>::max() - alignedStateOffset)
        {
            return Result::InvalidBinary;
        }
        totalWorkBytes = alignedStateOffset + descriptor->stateBytes;

        for (auto input = 0u; input < customMaxPorts; ++input)
        {
            auto inputRecord = current + 48 + input * 8;
            auto sourceIndex = read32(inputRecord);
            auto sourceWidth = inputRecord[4];
            auto selectedLane = inputRecord[5];
            if (input < inputCount)
            {
                if (!validIndex(sourceIndex, index) || inputRecord[6] > 1 || inputRecord[7] != 0 ||
                    inputRecord[6] != descriptor->inputIsColor[input] ||
                    (sourceIndex == noInput ? sourceWidth != 0 || selectedLane != 0
                                            : sourceWidth != descriptor->inputWidths[input]))
                {
                    return Result::InvalidBinary;
                }
                if (sourceIndex != noInput)
                {
                    auto source = instruction(data, 3, sourceIndex);
                    auto sourceWidthActual = source[16] == customParameterOpcode    ? source[17]
                                             : source[16] >= 26 && source[16] <= 28 ? 1
                                                                                    : source[17];
                    auto sourceColor = source[16] == customParameterOpcode   ? (source[20] & 1) != 0
                                       : source[16] >= 1 && source[16] <= 39 ? (source[20] & 1) != 0
                                                                             : false;
                    if (source[16] == customSignalOpcode || sourceWidth != sourceWidthActual ||
                        (sourceColor != (inputRecord[6] != 0)) ||
                        (source[16] >= 26 && source[16] <= 28 ? selectedLane >= source[17]
                                                              : selectedLane != 0))
                    {
                        return Result::InvalidBinary;
                    }
                }
            } else if (sourceIndex != noInput || sourceWidth != 0 || selectedLane != 0 ||
                       inputRecord[6] != 0 || inputRecord[7] != 0)
            {
                return Result::InvalidBinary;
            }
            for (auto lane = 0u; lane < 4; ++lane)
            {
                auto component = readDouble(current + 80 + input * 32 + lane * 8);
                if (!std::isfinite(component) ||
                    (input < inputCount && descriptor->inputIsColor[input] != 0 && lane < 4 &&
                     (component < 0 || (lane == 3 && component > 1))))
                {
                    return Result::InvalidBinary;
                }
            }
        }
        for (auto branch = 0u; branch < customMaxPorts; ++branch)
        {
            auto target = read32(current + 208 + branch * 4);
            if (branch < branchCount)
            {
                if (target != noInput &&
                    (target <= index || target >= count ||
                     (instruction(data, 3, target)[16] != customSignalOpcode &&
                      instruction(data, 3, target)[16] != logicalEventSignalOpcode)))
                {
                    return Result::InvalidBinary;
                }
            } else if (target != noInput)
            {
                return Result::InvalidBinary;
            }
        }
    }
    if (expectedCurveOffset != curveDataBytes)
    {
        return Result::InvalidBinary;
    }
    auto routeDataOffset = instructionBytes + expectedCurveOffset;
    auto routeBytes = std::size_t{0};
    if (version >= 5)
    {
        for (auto index = std::uint32_t{0}; index < count; ++index)
        {
            auto current = instruction(data, version, index);
            if (current[16] != customSignalOpcode && current[16] != logicalEventSignalOpcode)
            {
                continue;
            }
            for (auto branch = 0u; branch < customMaxPorts; ++branch)
            {
                auto offset = read32(current + 224 + branch * 8);
                auto extraCount = read32(current + 228 + branch * 8);
                if (extraCount == 0)
                {
                    if (offset != 0)
                    {
                        return Result::InvalidBinary;
                    }
                    continue;
                }
                if (branch >= current[19] || read32(current + 208 + branch * 4) == noInput ||
                    offset != routeBytes || extraCount > maxSignalRoutes - routeBytes / 4 ||
                    routeBytes > size - routeDataOffset ||
                    extraCount > (size - routeDataOffset - routeBytes) / 4)
                {
                    return Result::InvalidBinary;
                }
                for (auto entry = 0u; entry < extraCount; ++entry)
                {
                    auto target =
                        read32(data + routeDataOffset + routeBytes + std::size_t{entry} * 4);
                    if (target <= index || target >= count ||
                        (instruction(data, version, target)[16] != customSignalOpcode &&
                         instruction(data, version, target)[16] != logicalEventSignalOpcode))
                    {
                        return Result::InvalidBinary;
                    }
                }
                routeBytes += std::size_t{extraCount} * 4;
            }
        }
        if (version >= 8)
        {
            std::size_t enumMetadataEnd = 0;
            std::size_t enumSelectRecordsOffset = 0;
            std::size_t enumGuardRecordsOffset = 0;
            std::size_t enumGuardValuesOffset = 0;
            std::uint16_t enumValueCount = 0;
            std::uint16_t enumSelectCount = 0;
            std::uint16_t enumGuardCount = 0;
            auto enumMetadataOffset = routeDataOffset + routeBytes;
            if (!validEnumBranchMetadata(data, size, enumMetadataOffset, version, count,
                                         enumMetadataEnd, enumSelectRecordsOffset,
                                         enumGuardRecordsOffset, enumGuardValuesOffset,
                                         enumValueCount, enumSelectCount, enumGuardCount))
            {
                return Result::InvalidBinary;
            }
            out.enumBranchMetadataOffset_ = enumMetadataOffset;
            out.enumSelectRecordsOffset_ = enumSelectRecordsOffset;
            out.enumGuardRecordsOffset_ = enumGuardRecordsOffset;
            out.enumGuardValuesOffset_ = enumGuardValuesOffset;
            out.enumValueCount_ = enumValueCount;
            out.enumSelectCount_ = enumSelectCount;
            out.branchGuardCount_ = enumGuardCount;
            auto metadataCursor = enumMetadataEnd;
            if (version >= 9)
            {
                if (metadataCursor < size && size - metadataCursor >= 4 &&
                    std::memcmp(data + metadataCursor, "LFIO", 4) == 0)
                {
                    auto gameIoEnd = std::size_t{0};
                    if (!validGameIoMetadata(data, size, metadataCursor, count, version, gameIoEnd))
                    {
                        return Result::InvalidBinary;
                    }
                    out.gameIoMetadataOffset_ = metadataCursor;
                    out.gameIoPortCount_ = read16(data + metadataCursor + 4);
                    metadataCursor = gameIoEnd;
                }
                std::size_t logicalMetadataEnd = 0;
                std::size_t logicalRecordsOffset = 0;
                std::size_t logicalRoutesOffset = 0;
                std::uint16_t logicalInstructionCount = 0;
                std::uint32_t logicalRouteCount = 0;
                std::size_t validatedQueueCapacity = 0;
                if (!validLogicalEventMetadata(data, size, metadataCursor, count,
                                               logicalMetadataEnd, logicalRecordsOffset,
                                               logicalRoutesOffset, logicalInstructionCount,
                                               logicalRouteCount, validatedQueueCapacity) ||
                    logicalMetadataEnd != size)
                {
                    return Result::InvalidBinary;
                }
                out.logicalEventMetadataOffset_ = metadataCursor;
                out.logicalInstructionRecordsOffset_ = logicalRecordsOffset;
                out.logicalSignalRoutesOffset_ = logicalRoutesOffset;
                out.logicalInstructionCount_ = logicalInstructionCount;
                out.logicalSignalRouteCount_ = logicalRouteCount;
                out.logicalQueueCapacity_ = validatedQueueCapacity;
                auto routeCursor = std::uint32_t{0};
                for (auto sourceIndex = 0u; sourceIndex < count; ++sourceIndex)
                {
                    auto current = instruction(data, 3, sourceIndex);
                    if (current[16] != customSignalOpcode &&
                        current[16] != logicalEventSignalOpcode)
                    {
                        continue;
                    }
                    for (auto branch = 0u; branch < current[19]; ++branch)
                    {
                        auto primaryTarget = read32(current + 208 + branch * 4);
                        auto extraOffset = read32(current + 224 + branch * 8);
                        auto extraCount = read32(current + 228 + branch * 8);
                        if ((primaryTarget == noInput && extraCount != 0) ||
                            (primaryTarget != noInput &&
                             (routeCursor >= logicalRouteCount ||
                              read32(data + logicalRoutesOffset +
                                     std::size_t{routeCursor} * logicalSignalRouteRecordSize) !=
                                  sourceIndex ||
                              data[logicalRoutesOffset +
                                   std::size_t{routeCursor} * logicalSignalRouteRecordSize + 4] !=
                                  branch ||
                              read32(data + logicalRoutesOffset +
                                     std::size_t{routeCursor} * logicalSignalRouteRecordSize + 8) !=
                                  primaryTarget)))
                        {
                            return Result::InvalidBinary;
                        }
                        if (primaryTarget != noInput)
                        {
                            ++routeCursor;
                        }
                        for (auto extra = 0u; extra < extraCount; ++extra)
                        {
                            auto target = read32(data + routeDataOffset + extraOffset +
                                                 std::size_t{extra} * sizeof(std::uint32_t));
                            auto route = data + logicalRoutesOffset +
                                         std::size_t{routeCursor} * logicalSignalRouteRecordSize;
                            if (routeCursor >= logicalRouteCount || read32(route) != sourceIndex ||
                                route[4] != branch || read32(route + 8) != target)
                            {
                                return Result::InvalidBinary;
                            }
                            ++routeCursor;
                        }
                    }
                }
                if (routeCursor != logicalRouteCount)
                {
                    return Result::InvalidBinary;
                }
            } else
            {
                if (enumMetadataEnd < size)
                {
                    auto gameIoEnd = std::size_t{0};
                    if (!validGameIoMetadata(data, size, enumMetadataEnd, count, version,
                                             gameIoEnd))
                    {
                        return Result::InvalidBinary;
                    }
                    out.gameIoMetadataOffset_ = enumMetadataEnd;
                    out.gameIoPortCount_ = read16(data + enumMetadataEnd + 4);
                }
            }
        } else
        {
            if (version == 5 && routeBytes != size - routeDataOffset)
            {
                return Result::InvalidBinary;
            }
            if (version >= 6)
            {
                auto gameIoEnd = std::size_t{0};
                auto gameIoOffset = routeDataOffset + routeBytes;
                if (!validGameIoMetadata(data, size, gameIoOffset, count, version, gameIoEnd))
                {
                    return Result::InvalidBinary;
                }
                out.gameIoMetadataOffset_ = gameIoOffset;
                out.gameIoPortCount_ = read16(data + gameIoOffset + 4);
            }
        }
    }
    if (version < 5 && routeDataOffset != size)
    {
        return Result::InvalidBinary;
    }
    if (version >= 10 &&
        (!validValueTypeMetadata(data, binarySize, valueTypeMetadataOffset, count) ||
         !validEnumSelectValueTypes(data, valueTypeMetadataOffset, out.enumBranchMetadataOffset_,
                                    out.enumSelectRecordsOffset_, out.enumSelectCount_, count)))
    {
        return Result::InvalidBinary;
    }
    auto stackOffset = totalWorkBytes;
    auto logicalQueueCapacity = std::size_t{0};
    if (version >= 9)
    {
        logicalQueueCapacity = out.logicalQueueCapacity_;
        if (logicalQueueCapacity >
                std::numeric_limits<std::size_t>::max() / sizeof(LogicalQueuedSignal) ||
            !alignUp(totalWorkBytes, alignof(LogicalQueuedSignal), stackOffset) ||
            logicalQueueCapacity * sizeof(LogicalQueuedSignal) >
                std::numeric_limits<std::size_t>::max() - stackOffset)
        {
            return Result::InvalidBinary;
        }
        totalWorkBytes = stackOffset + logicalQueueCapacity * sizeof(LogicalQueuedSignal);
        if (needsSignalPayloads)
        {
            auto payloadOffset = std::size_t{};
            if (!alignUp(totalWorkBytes, alignof(SignalPayload), payloadOffset) ||
                logicalQueueCapacity > (std::numeric_limits<std::size_t>::max() - payloadOffset) /
                                           sizeof(SignalPayload))
            {
                return Result::InvalidBinary;
            }
            out.signalPayloadQueueOffset_ = payloadOffset;
            totalWorkBytes = payloadOffset + logicalQueueCapacity * sizeof(SignalPayload);
        }
    } else if (routeBytes != 0)
    {
        if (!alignUp(totalWorkBytes, alignof(double), stackOffset) ||
            routeBytes > std::numeric_limits<std::size_t>::max() - stackOffset)
        {
            return Result::InvalidBinary;
        }
        if (!alignUp(stackOffset + routeBytes, alignof(double), totalWorkBytes))
        {
            return Result::InvalidBinary;
        }
    }
    auto branchClockOffset = std::size_t{0};
    if (out.branchGuardCount_ != 0)
    {
        if (!alignUp(totalWorkBytes, alignof(double), branchClockOffset) ||
            std::size_t{out.branchGuardCount_} >
                (std::numeric_limits<std::size_t>::max() - branchClockOffset) / 24)
        {
            return Result::InvalidBinary;
        }
        totalWorkBytes = branchClockOffset + std::size_t{out.branchGuardCount_} * 24;
    }
    auto logicalEventMaskOffset = std::size_t{0};
    auto logicalDirtyOffset = std::size_t{0};
    if (version >= 9)
    {
        if (!alignUp(totalWorkBytes, alignof(std::uint64_t), logicalEventMaskOffset) ||
            std::size_t{count} >
                (std::numeric_limits<std::size_t>::max() - logicalEventMaskOffset) /
                    sizeof(std::uint64_t))
        {
            return Result::InvalidBinary;
        }
        totalWorkBytes = logicalEventMaskOffset + std::size_t{count} * sizeof(std::uint64_t);
        logicalDirtyOffset = totalWorkBytes;
        if (std::size_t{count} > std::numeric_limits<std::size_t>::max() - totalWorkBytes)
        {
            return Result::InvalidBinary;
        }
        totalWorkBytes += count;
    }
    auto boundStateOffset = valuesSize;
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, 3, index);
        auto descriptor = static_cast<const CustomNodeDescriptor*>(nullptr);
        auto logicalDescriptor = static_cast<const LogicalNodeDescriptor*>(nullptr);
        if (current[16] == customParameterOpcode || current[16] == customSignalOpcode)
        {
            auto registeredType = false;
            descriptor = resolveDescriptor(current + 24, read32(current + 40), registeredType);
        } else if (current[16] == logicalEventSignalOpcode)
        {
            auto registeredType = false;
            logicalDescriptor =
                findLogicalDescriptor(current + 24, read32(current + 40), registeredType);
        }
        auto stateAlignment = descriptor != nullptr          ? descriptor->stateAlignment
                              : logicalDescriptor != nullptr ? logicalDescriptor->stateAlignment
                                                             : 0;
        auto stateSize = descriptor != nullptr          ? descriptor->stateBytes
                         : logicalDescriptor != nullptr ? logicalDescriptor->stateBytes
                                                        : 0;
        if (stateAlignment != 0)
        {
            auto alignedStateOffset = std::size_t{0};
            if (!alignUp(boundStateOffset, stateAlignment, alignedStateOffset))
            {
                return Result::InvalidBinary;
            }
            boundStateOffset = alignedStateOffset;
        }
        if (bindings != nullptr)
        {
            bindings[index].descriptor = descriptor;
            bindings[index].stateOffset = boundStateOffset;
            bindings[index].logicalDescriptor = logicalDescriptor;
        }
        if (stateSize != 0)
        {
            boundStateOffset += stateSize;
        }
    }
    out.bytes_ = data;
    out.nodeCount_ = count;
    out.version_ = version;
    out.bindings_ = bindings;
    out.stateBytes_ = totalWorkBytes - valuesSize;
    out.signalRoutesOffset_ = routeDataOffset;
    out.signalStackOffset_ = stackOffset;
    out.signalStackCapacity_ = version >= 9 ? logicalQueueCapacity : routeBytes / 4;
    out.gameIoPortCount_ =
        version >= 6
            ? (out.gameIoMetadataOffset_ != 0 ? read16(data + out.gameIoMetadataOffset_ + 4) : 0)
            : 0;
    out.valueTypeMetadataOffset_ = valueTypeMetadataOffset;
    out.branchClockOffset_ = branchClockOffset;
    out.logicalEventMaskOffset_ = logicalEventMaskOffset;
    out.logicalDirtyOffset_ = logicalDirtyOffset;
    out.logicalQueueOffset_ = version >= 9 ? stackOffset : 0;
    out.logicalQueueCapacity_ = version >= 9 ? logicalQueueCapacity : 0;
    out.generation_ = nextProgramGeneration.fetch_add(1, std::memory_order_relaxed);
    return Result::Ok;
}

auto Program::gameIoPort(std::size_t index, ProgramGameIoPort& out) const noexcept -> bool
{
    out = {};
    if (index >= gameIoPortCount_ || gameIoMetadataOffset_ == 0)
    {
        return false;
    }
    auto cursor = gameIoMetadataOffset_ + 8;
    auto recordSize = version_ == 6 ? std::size_t{24} : std::size_t{72};
    for (auto current = std::size_t{}; current < index; ++current)
    {
        cursor += recordSize + read16(bytes_ + cursor + (version_ == 6 ? 20 : 68)) + 1;
    }
    auto record = bytes_ + cursor;
    auto nameLength = read16(record + (version_ == 6 ? 20 : 68));
    out.name = reinterpret_cast<const char*>(record + recordSize);
    std::memcpy(out.nodeId, record, 16);
    out.kind = version_ == 6 ? record[16] : record[64];
    out.width = version_ == 6 ? record[17] : record[65];
    out.isColor = version_ == 6 ? record[18] : record[66];
    out.nodeCount = version_ == 6 ? 1 : record[67];
    if (version_ >= 7)
    {
        for (auto row = 1u; row < out.nodeCount; ++row)
        {
            std::memcpy(out.additionalNodeIds[row - 1], record + row * 16, 16);
        }
    }
    return nameLength != 0;
}

auto Program::workBytes() const noexcept -> std::size_t
{
    auto values = std::size_t{nodeCount_} * (version_ == 1 ? sizeof(double) : 4 * sizeof(double));
    return values + (version_ >= 3 ? stateBytes_ : 0);
}

auto Program::workAlignment() const noexcept -> std::size_t
{
    auto alignment = version_ >= 3 ? customWorkAlignment : alignof(double);
    if (version_ >= 3 && bindings_ != nullptr)
    {
        for (auto index = std::size_t{0}; index < nodeCount_; ++index)
        {
            if (bindings_[index].descriptor != nullptr)
            {
                alignment = std::max(alignment, bindings_[index].descriptor->stateAlignment);
            }
            if (bindings_[index].logicalDescriptor != nullptr)
            {
                alignment = std::max(alignment, bindings_[index].logicalDescriptor->stateAlignment);
            }
        }
    }
    return alignment;
}

auto Program::parameterValueCount() const noexcept -> std::size_t
{
    return std::size_t{nodeCount_} * (version_ == 1 ? 2 : version_ == 2 ? 8 : 16);
}

auto Program::valueType(std::size_t instructionIndex, ValueType& out) const noexcept -> Result
{
    if (bytes_ == nullptr || instructionIndex >= nodeCount_)
    {
        return Result::InvalidArgument;
    }
    if (version_ >= 10)
    {
        auto type = valueTypeRecord(bytes_, valueTypeMetadataOffset_,
                                    static_cast<std::uint32_t>(instructionIndex))[0];
        if (type > valueTypeEnum)
        {
            return Result::InvalidBinary;
        }
        out = static_cast<ValueType>(type);
        return Result::Ok;
    }

    auto current = instruction(bytes_, version_, static_cast<std::uint32_t>(instructionIndex));
    auto opcode = current[16];
    if (isEnumSourceOpcode(opcode))
    {
        out = ValueType::Enum;
        return Result::Ok;
    }
    if (opcode == customSignalOpcode)
    {
        out = ValueType::None;
        return Result::Ok;
    }
    if (opcode == customParameterOpcode && bindings_ != nullptr &&
        bindings_[instructionIndex].descriptor != nullptr)
    {
        auto descriptor = bindings_[instructionIndex].descriptor;
        auto type = typeForShape(descriptor->outputWidth, descriptor->outputIsColor);
        if (type == 0)
        {
            return Result::InvalidValueType;
        }
        out = static_cast<ValueType>(type);
        return Result::Ok;
    }
    if (version_ == 1)
    {
        if (opcode < 1 || opcode > 6)
        {
            return Result::InvalidValueType;
        }
        out = ValueType::Float;
        return Result::Ok;
    }
    auto type = typeForShape(current[17], (current[20] & 1) != 0);
    if (type == 0)
    {
        return Result::InvalidValueType;
    }
    out = static_cast<ValueType>(type);
    return Result::Ok;
}

auto Program::defaultsAlignment() const noexcept -> std::size_t { return alignof(double); }

} // namespace liberafx
