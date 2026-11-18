#pragma once

#include "liberafx/VfxRuntime.h"
#include "liberafx/math/VectorMath.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>

namespace liberafx {
namespace vfx_runtime_detail {

constexpr auto legacyHeaderSize = std::size_t{12};
constexpr auto legacyInstructionSize = std::size_t{44};
constexpr auto typedHeaderSize = std::size_t{16};
constexpr auto typedInstructionSize = std::size_t{96};
constexpr auto customInstructionSize = std::size_t{256};
constexpr auto curveKeySize = std::size_t{40};
constexpr auto maxNodes = std::uint32_t{10000};
constexpr auto maxCurveKeysPerInstruction = std::uint32_t{10000};
constexpr auto maxCurveKeysTotal = std::uint32_t{100000};
constexpr auto maxSignalRoutes = std::size_t{1000000};
constexpr auto maxLogicalQueueEvents = std::size_t{1000000};
constexpr auto noInput = std::uint32_t{0xffffffffu};
constexpr auto customParameterOpcode = std::uint8_t{40};
constexpr auto customSignalOpcode = std::uint8_t{41};
constexpr auto curveOpcode = std::uint8_t{42};
constexpr auto enumValueOpcode = std::uint8_t{43};
constexpr auto enumSelectOpcode = std::uint8_t{44};
constexpr auto branchTimeOpcode = std::uint8_t{45};
constexpr auto logicalEventSignalOpcode = std::uint8_t{47};
constexpr auto currentPhaseOpcode = std::uint8_t{55};
constexpr auto phaseTimeOpcode = std::uint8_t{56};

constexpr auto isEnumSourceOpcode(std::uint8_t opcode) noexcept -> bool
{
    return opcode == enumValueOpcode || opcode == currentPhaseOpcode;
}

inline auto branchGuard(const std::uint8_t* current, std::uint16_t version) noexcept
    -> std::uint32_t;
constexpr auto int32Opcode = std::uint8_t{48};
constexpr auto int32AddOpcode = std::uint8_t{49};
constexpr auto int32SubtractOpcode = std::uint8_t{50};
constexpr auto int32MultiplyOpcode = std::uint8_t{51};
constexpr auto int32DivideOpcode = std::uint8_t{52};
constexpr auto floatToInt32Opcode = std::uint8_t{53};
constexpr auto int32ToFloatOpcode = std::uint8_t{54};
constexpr auto customMaxPorts = std::uint8_t{4};
constexpr auto enumBranchHeaderSize = std::size_t{16};
constexpr auto enumValueRecordSize = std::size_t{24};
constexpr auto enumSelectRecordSize = std::size_t{68};
constexpr auto enumCaseRecordSize = std::size_t{40};
constexpr auto enumGuardRecordSize = std::size_t{40};
constexpr auto logicalEventHeaderSize = std::size_t{16};
constexpr auto valueTypeHeaderSize = std::size_t{8};
constexpr auto valueTypeRecordSize = std::size_t{8};
constexpr auto logicalInstructionRecordSize = std::size_t{32};
constexpr auto logicalSignalRouteRecordSize = std::size_t{12};
constexpr auto customWorkAlignment = std::size_t{16};
constexpr auto maxCustomStateAlignment = std::size_t{64};
constexpr auto logicalCompareTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8, 0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x01};
constexpr auto logicalAndTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8, 0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x02};
constexpr auto logicalOrTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8, 0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x03};
constexpr auto logicalSignalToNumberTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8, 0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x04};
constexpr auto logicalLoopClockTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8, 0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x05};
constexpr auto logicalQualityBranchTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8, 0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x06};
constexpr auto logicalSignalCaptureTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8, 0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x07};
constexpr auto valueTypeFloat = std::uint8_t{1};
constexpr auto valueTypeInt32 = std::uint8_t{2};
constexpr auto valueTypeVector2 = std::uint8_t{3};
constexpr auto valueTypeVector3 = std::uint8_t{4};
constexpr auto valueTypeVector4 = std::uint8_t{5};
constexpr auto valueTypeColor = std::uint8_t{6};
constexpr auto valueTypeEnum = std::uint8_t{7};
inline std::atomic<std::uint64_t> nextProgramGeneration{1};
inline std::atomic<std::uint64_t> nextInstanceGeneration{1};
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559,
              "LIBERA FX requires IEEE-754 binary64.");

inline auto validStateAlignment(std::size_t alignment) noexcept -> bool;
inline auto instruction(const std::uint8_t* bytes, std::uint16_t version,
                        std::size_t index) noexcept -> const std::uint8_t*;

inline auto read16(const std::uint8_t* bytes) noexcept -> std::uint16_t
{
    return static_cast<std::uint16_t>(bytes[0] | (std::uint16_t{bytes[1]} << 8));
}

inline auto read32(const std::uint8_t* bytes) noexcept -> std::uint32_t
{
    return std::uint32_t{bytes[0]} | (std::uint32_t{bytes[1]} << 8) |
           (std::uint32_t{bytes[2]} << 16) | (std::uint32_t{bytes[3]} << 24);
}

inline auto branchGuard(const std::uint8_t* current, std::uint16_t version) noexcept
    -> std::uint32_t
{
    auto opcode = current[16];
    auto customLayout = opcode == customParameterOpcode || opcode == customSignalOpcode ||
                        opcode == logicalEventSignalOpcode;
    return read32(current + (version >= 11 && customLayout ? 44 : 112));
}

inline auto read64(const std::uint8_t* bytes) noexcept -> std::uint64_t
{
    auto value = std::uint64_t{0};
    for (auto index = 0u; index < 8; ++index)
    {
        value |= std::uint64_t{bytes[index]} << (index * 8);
    }
    return value;
}

inline auto readI32(const std::uint8_t* bytes) noexcept -> std::int32_t
{
    auto bits = read32(bytes);
    auto value = std::int32_t{};
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

inline auto readDouble(const std::uint8_t* bytes) noexcept -> double
{
    auto bits = read64(bytes);
    auto value = double{};
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

inline void write64(std::uint8_t* bytes, std::uint64_t value) noexcept
{
    for (auto index = 0u; index < 8; ++index)
    {
        bytes[index] = static_cast<std::uint8_t>(value >> (index * 8));
    }
}

inline void writeDouble(std::uint8_t* bytes, double value) noexcept
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    write64(bytes, bits);
}

inline auto instruction(const std::uint8_t* bytes, std::uint16_t version,
                        std::size_t index) noexcept -> const std::uint8_t*;

inline auto validUtf8(const std::uint8_t* text, std::size_t length) noexcept -> bool
{
    for (auto index = std::size_t{}; index < length;)
    {
        auto first = text[index++];
        auto codePoint = std::uint32_t{};
        auto continuationCount = 0u;
        if (first < 0x80)
        {
            codePoint = first;
        } else if (first >= 0xc2 && first <= 0xdf)
        {
            codePoint = first & 0x1f;
            continuationCount = 1;
        } else if (first >= 0xe0 && first <= 0xef)
        {
            codePoint = first & 0x0f;
            continuationCount = 2;
        } else if (first >= 0xf0 && first <= 0xf4)
        {
            codePoint = first & 0x07;
            continuationCount = 3;
        } else
        {
            return false;
        }
        if (continuationCount > length - index)
        {
            return false;
        }
        for (auto count = 0u; count < continuationCount; ++count)
        {
            auto next = text[index++];
            if ((next & 0xc0) != 0x80)
            {
                return false;
            }
            codePoint = (codePoint << 6) | (next & 0x3f);
        }
        if ((continuationCount == 1 && codePoint < 0x80) ||
            (continuationCount == 2 && codePoint < 0x800) ||
            (continuationCount == 3 && codePoint < 0x10000) || codePoint > 0x10ffff ||
            (codePoint >= 0xd800 && codePoint <= 0xdfff) || codePoint <= 0x1f ||
            (codePoint >= 0x7f && codePoint <= 0x9f))
        {
            return false;
        }
    }
    return true;
}

inline auto validGameIoMetadata(const std::uint8_t* data, std::size_t size, std::size_t offset,
                                std::uint32_t instructionCount, std::uint16_t version,
                                std::size_t& metadataEnd) noexcept -> bool
{
    constexpr auto headerSize = std::size_t{8};
    constexpr auto legacyRecordSize = std::size_t{24};
    constexpr auto recordSize = std::size_t{72};
    auto currentRecordSize = version == 6 ? legacyRecordSize : recordSize;
    if (offset > size || size - offset < headerSize || std::memcmp(data + offset, "LFIO", 4) != 0 ||
        read16(data + offset + 6) != 0)
    {
        return false;
    }
    auto portCount = read16(data + offset + 4);
    if (portCount == 0 || portCount > 256)
    {
        return false;
    }
    auto cursor = offset + headerSize;
    for (auto port = std::uint16_t{}; port < portCount; ++port)
    {
        if (cursor > size || size - cursor < currentRecordSize)
        {
            return false;
        }
        auto record = data + cursor;
        auto kind = version == 6 ? record[16] : record[64];
        auto width = version == 6 ? record[17] : record[65];
        auto isColor = version == 6 ? record[18] : record[66];
        auto bindingCount = version == 6 ? std::uint8_t{1} : record[67];
        auto nameLength = read16(record + (version == 6 ? 20 : 68));
        auto matrix = width == 16;
        if (kind > (version == 6 ? 3 : 4) || isColor > 1 || nameLength == 0 || nameLength > 128 ||
            (version == 6 && (record[19] != 0 || read16(record + 22) != 0)) ||
            (version >= 7 && (read16(record + 70) != 0 || bindingCount != (matrix ? 4 : 1))) ||
            (kind == 1 || kind == 3
                 ? width != 0 || isColor != 0 || bindingCount != 1
                 : (matrix ? kind == 1 || kind == 3 || isColor != 0
                           : width < 1 || width > 4 || (isColor && width != 4)) ||
                       (kind == 4 && !matrix)))
        {
            return false;
        }
        auto nameOffset = cursor + currentRecordSize;
        if (nameOffset > size || size - nameOffset < std::size_t{nameLength} + 1 ||
            data[nameOffset + nameLength] != 0 || data[nameOffset] == ' ' ||
            data[nameOffset + nameLength - 1] == ' ' || !validUtf8(data + nameOffset, nameLength))
        {
            return false;
        }
        for (auto row = 0u; row < bindingCount; ++row)
        {
            auto nodeId = version == 6 ? record : record + row * 16;
            auto emptyId = true;
            for (auto byte = 0u; byte < 16; ++byte)
            {
                emptyId = emptyId && nodeId[byte] == 0;
            }
            if (emptyId)
            {
                return false;
            }
            auto foundNode = false;
            for (auto index = 0u; index < instructionCount; ++index)
            {
                auto current = instruction(data, 3, index);
                if (std::memcmp(current, nodeId, 16) != 0)
                {
                    continue;
                }
                auto rowWidth = matrix ? std::uint8_t{4} : width;
                if ((kind == 1 || kind == 3)
                        ? (current[16] != customSignalOpcode &&
                           (current[16] != logicalEventSignalOpcode || current[19] == 0))
                        : current[17] != rowWidth || ((current[20] & 1) != 0) != (isColor != 0))
                {
                    return false;
                }
                foundNode = true;
                break;
            }
            if (!foundNode)
            {
                return false;
            }
            for (auto earlierRow = 0u; earlierRow < row; ++earlierRow)
            {
                if (std::memcmp(nodeId, record + earlierRow * 16, 16) == 0)
                {
                    return false;
                }
            }
        }
        for (auto previous = offset + headerSize; previous < cursor;)
        {
            auto previousRecord = data + previous;
            auto previousKind = version == 6 ? previousRecord[16] : previousRecord[64];
            auto previousBindingCount = version == 6 ? std::uint8_t{1} : previousRecord[67];
            auto previousNameLength = read16(previousRecord + (version == 6 ? 20 : 68));
            auto duplicateName =
                previousNameLength == nameLength && std::memcmp(data + previous + currentRecordSize,
                                                                data + nameOffset, nameLength) == 0;
            if (duplicateName && kind != 4 && previousKind != 4)
            {
                return false;
            }
            for (auto row = 0u; row < bindingCount; ++row)
            {
                for (auto previousRow = 0u; previousRow < previousBindingCount; ++previousRow)
                {
                    if (std::memcmp(version == 6 ? record : record + row * 16,
                                    version == 6 ? previousRecord
                                                 : previousRecord + previousRow * 16,
                                    16) == 0 &&
                        kind != 2 && previousKind != 2)
                    {
                        return false;
                    }
                }
            }
            previous += currentRecordSize + previousNameLength + 1;
        }
        cursor = nameOffset + nameLength + 1;
    }
    metadataEnd = cursor;
    return version >= 9 ? cursor <= size : cursor == size;
}

inline auto validEnumBranchMetadata(const std::uint8_t* data, std::size_t size, std::size_t offset,
                                    std::uint16_t version, std::uint32_t instructionCount,
                                    std::size_t& metadataEnd, std::size_t& selectRecordsOffset,
                                    std::size_t& guardRecordsOffset, std::size_t& guardValuesOffset,
                                    std::uint16_t& enumValueCount, std::uint16_t& enumSelectCount,
                                    std::uint16_t& guardCount) noexcept -> bool
{
    if (offset > size || size - offset < enumBranchHeaderSize ||
        std::memcmp(data + offset, "LFEB", 4) != 0 || read16(data + offset + 4) != 1)
    {
        return false;
    }
    guardCount = read16(data + offset + 6);
    enumValueCount = read16(data + offset + 8);
    enumSelectCount = read16(data + offset + 10);
    auto declaredSize = read32(data + offset + 12);
    if (declaredSize < enumBranchHeaderSize || declaredSize > size - offset || guardCount > 255)
    {
        return false;
    }
    metadataEnd = offset + declaredSize;
    auto valuesOffset = offset + enumBranchHeaderSize;
    if (std::size_t{enumValueCount} > (declaredSize - enumBranchHeaderSize) / enumValueRecordSize)
    {
        return false;
    }
    selectRecordsOffset = valuesOffset + std::size_t{enumValueCount} * enumValueRecordSize;
    if (std::size_t{enumSelectCount} > (metadataEnd - selectRecordsOffset) / enumSelectRecordSize)
    {
        return false;
    }
    guardRecordsOffset = selectRecordsOffset + std::size_t{enumSelectCount} * enumSelectRecordSize;
    if (std::size_t{guardCount} > (metadataEnd - guardRecordsOffset) / enumGuardRecordSize)
    {
        return false;
    }
    guardValuesOffset = guardRecordsOffset + std::size_t{guardCount} * enumGuardRecordSize;
    auto caseCursor = guardValuesOffset;
    for (auto index = 0u; index < enumValueCount; ++index)
    {
        auto record = data + valuesOffset + std::size_t{index} * enumValueRecordSize;
        auto nodeIndex = read32(record);
        if (nodeIndex >= instructionCount)
        {
            return false;
        }
        auto current = instruction(data, 3, nodeIndex);
        if (!isEnumSourceOpcode(current[16]) || std::memcmp(current + 96, record + 4, 16) != 0)
        {
            return false;
        }
        auto sourceIndex = read32(current + 24);
        if (sourceIndex != noInput)
        {
            if (sourceIndex >= nodeIndex)
            {
                return false;
            }
            auto source = instruction(data, 3, sourceIndex);
            if (!isEnumSourceOpcode(source[16]) || std::memcmp(source + 96, record + 4, 16) != 0)
            {
                return false;
            }
        }
        for (auto prior = 0u; prior < index; ++prior)
        {
            if (read32(data + valuesOffset + std::size_t{prior} * enumValueRecordSize) == nodeIndex)
            {
                return false;
            }
        }
    }
    for (auto index = 0u; index < enumSelectCount; ++index)
    {
        auto record = data + selectRecordsOffset + std::size_t{index} * enumSelectRecordSize;
        auto nodeIndex = read32(record);
        auto selectorIndex = read32(record + 4);
        auto defaultSource = read32(record + 8);
        auto outputWidth = record[28];
        auto caseCount = record[29];
        auto casesOffset = read32(record + 32);
        if (nodeIndex >= instructionCount || selectorIndex >= nodeIndex ||
            (defaultSource != noInput &&
             (defaultSource >= nodeIndex ||
              instruction(data, 3, defaultSource)[17] != outputWidth)) ||
            outputWidth < 1 || outputWidth > 4 || caseCount > 32 ||
            offset + casesOffset != caseCursor ||
            std::size_t{caseCount} > (metadataEnd - caseCursor) / enumCaseRecordSize)
        {
            return false;
        }
        auto current = instruction(data, 3, nodeIndex);
        auto selector = instruction(data, 3, selectorIndex);
        if (current[16] != enumSelectOpcode || current[17] != outputWidth ||
            read32(current + 24) != selectorIndex || !isEnumSourceOpcode(selector[16]) ||
            std::memcmp(current + 96, record + 12, 16) != 0 ||
            std::memcmp(selector + 96, record + 12, 16) != 0)
        {
            return false;
        }
        for (auto prior = 0u; prior < index; ++prior)
        {
            if (read32(data + selectRecordsOffset + std::size_t{prior} * enumSelectRecordSize) ==
                nodeIndex)
            {
                return false;
            }
        }
        for (auto lane = 0u; lane < outputWidth; ++lane)
        {
            if (!std::isfinite(readDouble(record + 36 + lane * 8)))
            {
                return false;
            }
        }
        for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
        {
            auto branch = data + caseCursor + std::size_t{caseIndex} * enumCaseRecordSize;
            auto source = read32(branch + 4);
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                if (!std::isfinite(readDouble(branch + 8 + lane * 8)))
                {
                    return false;
                }
            }
            if (source != noInput &&
                (source >= nodeIndex || instruction(data, 3, source)[17] != outputWidth))
            {
                return false;
            }
            for (auto prior = 0u; prior < caseIndex; ++prior)
            {
                if (read32(data + caseCursor + std::size_t{prior} * enumCaseRecordSize) ==
                    read32(branch))
                {
                    return false;
                }
            }
        }
        caseCursor += std::size_t{caseCount} * enumCaseRecordSize;
    }
    auto guardDataCursor = caseCursor;
    for (auto index = 0u; index < guardCount; ++index)
    {
        auto record = data + guardRecordsOffset + std::size_t{index} * enumGuardRecordSize;
        auto selectorIndex = read32(record);
        auto parent = read32(record + 20);
        auto flags = read32(record + 24);
        auto caseCount = read16(record + 28);
        auto excludedCount = read16(record + 30);
        auto guardCaseListOffset = read32(record + 32);
        auto excludedOffset = read32(record + 36);
        if (selectorIndex >= instructionCount || (parent != noInput && parent >= index) ||
            flags > 1 || offset + guardCaseListOffset != guardDataCursor ||
            std::size_t{caseCount} > (metadataEnd - guardDataCursor) / 4 ||
            offset + excludedOffset != guardDataCursor + std::size_t{caseCount} * 4 ||
            std::size_t{excludedCount} > (metadataEnd - (offset + excludedOffset)) / 4)
        {
            return false;
        }
        auto selector = instruction(data, 3, selectorIndex);
        if (!isEnumSourceOpcode(selector[16]) || std::memcmp(selector + 96, record + 4, 16) != 0)
        {
            return false;
        }
        for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
        {
            for (auto prior = 0u; prior < caseIndex; ++prior)
            {
                if (read32(data + offset + guardCaseListOffset + std::size_t{caseIndex} * 4) ==
                    read32(data + offset + guardCaseListOffset + std::size_t{prior} * 4))
                {
                    return false;
                }
            }
        }
        for (auto caseIndex = 0u; caseIndex < excludedCount; ++caseIndex)
        {
            for (auto prior = 0u; prior < caseIndex; ++prior)
            {
                if (read32(data + offset + excludedOffset + std::size_t{caseIndex} * 4) ==
                    read32(data + offset + excludedOffset + std::size_t{prior} * 4))
                {
                    return false;
                }
            }
        }
        guardDataCursor = offset + excludedOffset + std::size_t{excludedCount} * 4;
        auto ancestor = read32(record + 20);
        auto depth = 1u;
        while (ancestor != noInput)
        {
            if (++depth > 64 || ancestor >= index)
            {
                return false;
            }
            ancestor = read32(data + guardRecordsOffset +
                              std::size_t{ancestor} * enumGuardRecordSize + 20);
        }
    }
    if (guardDataCursor != metadataEnd)
    {
        return false;
    }
    auto observedEnumValues = std::uint16_t{0};
    auto observedEnumSelects = std::uint16_t{0};
    for (auto index = 0u; index < instructionCount; ++index)
    {
        auto current = instruction(data, 3, index);
        auto opcode = current[16];
        if (isEnumSourceOpcode(opcode))
        {
            ++observedEnumValues;
        }
        if (opcode == enumSelectOpcode)
        {
            ++observedEnumSelects;
        }
        auto guardId = branchGuard(current, version);
        if (guardId != noInput && guardId >= guardCount)
        {
            return false;
        }
        if (opcode == branchTimeOpcode && guardId == noInput)
        {
            return false;
        }
    }
    return observedEnumValues == enumValueCount && observedEnumSelects == enumSelectCount;
}

inline auto findLogicalDescriptor(const std::uint8_t* typeId, std::uint32_t abiVersion,
                                  bool& registeredType) noexcept -> const LogicalNodeDescriptor*
{
    registeredType = false;
    auto count = std::size_t{0};
    auto descriptors = logicalNodes(count);
    for (auto index = std::size_t{0}; index < count; ++index)
    {
        if (std::memcmp(descriptors[index].typeId, typeId, 16) != 0)
        {
            continue;
        }
        registeredType = true;
        if (descriptors[index].abiVersion == abiVersion)
        {
            return &descriptors[index];
        }
    }
    return nullptr;
}

inline auto validLogicalDescriptor(const LogicalNodeDescriptor& descriptor) noexcept -> bool
{
    if (descriptor.abiVersion == 0 || descriptor.inputCount > customMaxPorts ||
        descriptor.parameterCount > customMaxPorts ||
        descriptor.signalInputCount > customMaxPorts || descriptor.outputWidth < 1 ||
        descriptor.outputWidth > 4 || descriptor.signalBranchCount > customMaxPorts ||
        descriptor.update == nullptr || !validStateAlignment(descriptor.stateAlignment) ||
        descriptor.stateBytes >
            (std::numeric_limits<std::size_t>::max() - descriptor.stateAlignment + 1) ||
        (descriptor.signalInputCount != 0 && descriptor.onSignal == nullptr))
    {
        return false;
    }
    for (auto input = 0u; input < descriptor.inputCount; ++input)
    {
        if (descriptor.inputWidths[input] < 1 || descriptor.inputWidths[input] > 4)
        {
            return false;
        }
    }
    return true;
}

inline auto validLogicalConfiguration(const std::uint8_t* typeId,
                                      std::uint32_t configuration) noexcept -> bool
{
    if (std::memcmp(typeId, logicalCompareTypeId.data(), 16) == 0)
    {
        auto operation = configuration & 0xffu;
        auto mode = (configuration >> 8u) & 0xffu;
        return (configuration & 0xffff0000u) == 0 && operation <= 5 && mode <= 1;
    }
    if (std::memcmp(typeId, logicalAndTypeId.data(), 16) == 0)
    {
        return (configuration & 0xffffff00u) == 0 && (configuration & 0xffu) <= 1;
    }
    if (std::memcmp(typeId, logicalOrTypeId.data(), 16) == 0)
    {
        return configuration == 0;
    }
    if (std::memcmp(typeId, logicalSignalToNumberTypeId.data(), 16) == 0)
    {
        return (configuration & 0xffffff00u) == 0 && (configuration & 0xffu) <= 2;
    }
    if (std::memcmp(typeId, logicalLoopClockTypeId.data(), 16) == 0)
    {
        return configuration == 0;
    }
    if (std::memcmp(typeId, logicalQualityBranchTypeId.data(), 16) == 0)
    {
        return (configuration & 0xffff0000u) == 0 && (configuration & 0xffu) <= 5 &&
               ((configuration >> 8u) & 0xffu) <= 4;
    }
    if (std::memcmp(typeId, logicalSignalCaptureTypeId.data(), 16) == 0)
    {
        return configuration == 0;
    }
    return false;
}

inline auto
validLogicalEventMetadata(const std::uint8_t* data, std::size_t size, std::size_t offset,
                          std::uint32_t instructionCount, std::size_t& metadataEnd,
                          std::size_t& instructionRecordsOffset, std::size_t& signalRoutesOffset,
                          std::uint16_t& logicalInstructionCount, std::uint32_t& signalRouteCount,
                          std::size_t& logicalQueueCapacity) noexcept -> bool
{
    if (offset > size || size - offset < logicalEventHeaderSize ||
        std::memcmp(data + offset, "LFLE", 4) != 0 || read16(data + offset + 4) != 1)
    {
        return false;
    }
    logicalInstructionCount = read16(data + offset + 6);
    signalRouteCount = read32(data + offset + 8);
    auto declaredSize = read32(data + offset + 12);
    if (declaredSize < logicalEventHeaderSize || declaredSize > size - offset ||
        signalRouteCount > maxSignalRoutes ||
        std::size_t{logicalInstructionCount} >
            (declaredSize - logicalEventHeaderSize) / logicalInstructionRecordSize)
    {
        return false;
    }
    auto routesBytesOffset = logicalEventHeaderSize +
                             std::size_t{logicalInstructionCount} * logicalInstructionRecordSize;
    if (std::size_t{signalRouteCount} >
            (declaredSize - routesBytesOffset) / logicalSignalRouteRecordSize ||
        routesBytesOffset + std::size_t{signalRouteCount} * logicalSignalRouteRecordSize !=
            declaredSize)
    {
        return false;
    }
    metadataEnd = offset + declaredSize;
    instructionRecordsOffset = offset + logicalEventHeaderSize;
    signalRoutesOffset = instructionRecordsOffset +
                         std::size_t{logicalInstructionCount} * logicalInstructionRecordSize;

    auto signalInputCounts = std::array<std::uint8_t, maxNodes>{};
    signalInputCounts.fill(0xff);
    auto observedLogicalInstructions = std::uint16_t{0};
    for (auto index = std::uint32_t{0}; index < instructionCount; ++index)
    {
        observedLogicalInstructions +=
            instruction(data, 3, index)[16] == logicalEventSignalOpcode ? 1 : 0;
    }
    if (observedLogicalInstructions != logicalInstructionCount)
    {
        return false;
    }

    for (auto index = 0u; index < logicalInstructionCount; ++index)
    {
        auto record =
            data + instructionRecordsOffset + std::size_t{index} * logicalInstructionRecordSize;
        auto nodeIndex = read32(record);
        auto abiVersion = read32(record + 20);
        auto configuration = read32(record + 24);
        if (nodeIndex >= instructionCount || signalInputCounts[nodeIndex] != 0xff)
        {
            return false;
        }
        auto current = instruction(data, 3, nodeIndex);
        if (current[16] != logicalEventSignalOpcode ||
            std::memcmp(current + 24, record + 4, 16) != 0 || read32(current + 40) != abiVersion ||
            current[18] != record[28] || current[19] != record[31] || current[17] != record[30] ||
            record[28] > customMaxPorts || record[29] > customMaxPorts || record[30] < 1 ||
            record[30] > 4 || record[31] > customMaxPorts || current[20] != 0)
        {
            return false;
        }
        auto registeredType = false;
        auto descriptor = findLogicalDescriptor(record + 4, abiVersion, registeredType);
        auto expectedParameterCount =
            std::memcmp(record + 4, logicalCompareTypeId.data(), 16) == 0 ||
                    std::memcmp(record + 4, logicalQualityBranchTypeId.data(), 16) == 0
                ? 2
            : std::memcmp(record + 4, logicalOrTypeId.data(), 16) == 0 ||
                    std::memcmp(record + 4, logicalLoopClockTypeId.data(), 16) == 0 ||
                    std::memcmp(record + 4, logicalSignalCaptureTypeId.data(), 16) == 0
                ? 0
            : std::memcmp(record + 4, logicalAndTypeId.data(), 16) == 0 ||
                    std::memcmp(record + 4, logicalSignalToNumberTypeId.data(), 16) == 0
                ? 1
                : 0xff;
        if (descriptor == nullptr || !validLogicalDescriptor(*descriptor) ||
            descriptor->inputCount != record[28] ||
            descriptor->parameterCount != expectedParameterCount ||
            descriptor->signalInputCount != record[29] || descriptor->outputWidth != record[30] ||
            descriptor->signalBranchCount != record[31] ||
            !validLogicalConfiguration(record + 4, configuration))
        {
            return false;
        }
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte)
        {
            emptyId = emptyId && record[4 + byte] == 0;
        }
        if (emptyId)
        {
            return false;
        }
        signalInputCounts[nodeIndex] = record[29];
        for (auto input = 0u; input < descriptor->inputCount; ++input)
        {
            auto inputRecord = current + 48 + input * 8;
            auto sourceIndex = read32(inputRecord);
            auto sourceWidth = inputRecord[4];
            auto selectedLane = inputRecord[5];
            auto isColor = inputRecord[6];
            if (sourceIndex != noInput &&
                (sourceIndex >= nodeIndex || sourceWidth != descriptor->inputWidths[input] ||
                 selectedLane != 0 || isColor != 0 ||
                 instruction(data, 3, sourceIndex)[17] != descriptor->inputWidths[input]))
            {
                return false;
            }
            if (sourceIndex == noInput && (sourceWidth != 0 || selectedLane != 0 || isColor != 0))
            {
                return false;
            }
        }
        for (auto prior = 0u; prior < index; ++prior)
        {
            if (read32(data + instructionRecordsOffset +
                       std::size_t{prior} * logicalInstructionRecordSize) == nodeIndex)
            {
                return false;
            }
        }
    }

    auto previousSource = std::uint32_t{0};
    auto previousBranch = std::uint8_t{0};
    auto hasPrevious = false;
    for (auto index = 0u; index < signalRouteCount; ++index)
    {
        auto record = data + signalRoutesOffset + std::size_t{index} * logicalSignalRouteRecordSize;
        auto sourceIndex = read32(record);
        auto branch = record[4];
        auto inputSlot = record[5];
        auto targetIndex = read32(record + 8);
        if (read16(record + 6) != 0 || sourceIndex >= instructionCount ||
            targetIndex >= instructionCount || targetIndex <= sourceIndex)
        {
            return false;
        }
        auto source = instruction(data, 3, sourceIndex);
        auto target = instruction(data, 3, targetIndex);
        if ((source[16] != customSignalOpcode && source[16] != logicalEventSignalOpcode) ||
            branch >= source[19] ||
            (target[16] != customSignalOpcode && target[16] != logicalEventSignalOpcode))
        {
            return false;
        }
        if (target[16] == logicalEventSignalOpcode)
        {
            if (signalInputCounts[targetIndex] == 0xff ||
                inputSlot >= signalInputCounts[targetIndex])
            {
                return false;
            }
        } else if (inputSlot != 0)
        {
            return false;
        }
        if (hasPrevious && (sourceIndex < previousSource ||
                            (sourceIndex == previousSource && branch < previousBranch)))
        {
            return false;
        }
        previousSource = sourceIndex;
        previousBranch = branch;
        hasPrevious = true;
    }
    auto eventCounts = std::array<std::uint64_t, maxNodes>{};
    auto maximumManualEvents = std::uint64_t{0};
    auto generatedEvents = std::uint64_t{0};
    auto routeCursor = std::size_t{signalRouteCount};
    for (auto sourceIndex = instructionCount; sourceIndex > 0; --sourceIndex)
    {
        auto nodeIndex = sourceIndex - 1;
        auto source = instruction(data, 3, nodeIndex);
        auto opcode = source[16];
        if (opcode != customSignalOpcode && opcode != logicalEventSignalOpcode)
        {
            continue;
        }
        auto branchCount = source[19];
        auto maximumBranchEvents = std::uint64_t{0};
        auto allBranchEvents = std::uint64_t{0};
        auto branchEventCounts = std::array<std::uint64_t, customMaxPorts>{};
        while (routeCursor != 0)
        {
            auto route =
                data + signalRoutesOffset + (routeCursor - 1) * logicalSignalRouteRecordSize;
            auto routeSource = read32(route);
            if (routeSource < nodeIndex)
            {
                break;
            }
            if (routeSource != nodeIndex)
            {
                return false;
            }
            auto branch = route[4];
            auto targetIndex = read32(route + 8);
            if (branch >= branchCount ||
                eventCounts[targetIndex] > maxLogicalQueueEvents - branchEventCounts[branch])
            {
                return false;
            }
            branchEventCounts[branch] += eventCounts[targetIndex];
            --routeCursor;
        }
        for (auto branch = 0u; branch < branchCount; ++branch)
        {
            auto branchEvents = branchEventCounts[branch];
            maximumBranchEvents = std::max(maximumBranchEvents, branchEvents);
            if (branchEvents > maxLogicalQueueEvents - allBranchEvents)
            {
                return false;
            }
            allBranchEvents += branchEvents;
        }
        auto childEvents = opcode == customSignalOpcode ? maximumBranchEvents : allBranchEvents;
        if (childEvents >= maxLogicalQueueEvents)
        {
            return false;
        }
        eventCounts[nodeIndex] = childEvents + 1;
        if (opcode == customSignalOpcode)
        {
            maximumManualEvents = std::max(maximumManualEvents, eventCounts[nodeIndex]);
        }
        if (opcode == logicalEventSignalOpcode &&
            std::memcmp(source + 24, logicalCompareTypeId.data(), 16) == 0)
        {
            generatedEvents += allBranchEvents;
        }
        if (generatedEvents > maxLogicalQueueEvents)
        {
            return false;
        }
    }
    if (routeCursor != 0)
    {
        return false;
    }
    auto requiredQueueCapacity = std::max(maximumManualEvents, generatedEvents);
    if (requiredQueueCapacity == 0)
    {
        requiredQueueCapacity = 1;
    }
    if (requiredQueueCapacity > maxLogicalQueueEvents)
    {
        return false;
    }
    logicalQueueCapacity = static_cast<std::size_t>(requiredQueueCapacity);
    return true;
}

inline auto instruction(const std::uint8_t* bytes, std::uint16_t version,
                        std::size_t index) noexcept -> const std::uint8_t*
{
    auto stride = version == 1   ? legacyInstructionSize
                  : version == 2 ? typedInstructionSize
                                 : customInstructionSize;
    return bytes + (version == 1 ? legacyHeaderSize : typedHeaderSize) + index * stride;
}

inline auto validStateAlignment(std::size_t alignment) noexcept -> bool
{
    return alignment >= customWorkAlignment && alignment <= maxCustomStateAlignment &&
           (alignment & (alignment - 1)) == 0;
}

inline auto alignUp(std::size_t value, std::size_t alignment, std::size_t& out) noexcept -> bool
{
    auto padding = alignment - 1;
    if (value > std::numeric_limits<std::size_t>::max() - padding)
    {
        return false;
    }
    out = (value + padding) & ~padding;
    return true;
}

inline auto isAligned(const void* pointer, std::size_t alignment) noexcept -> bool
{
    return pointer != nullptr && reinterpret_cast<std::uintptr_t>(pointer) % alignment == 0;
}

inline auto stateOffset(const CustomNodeBinding* bindings, std::size_t nodeIndex) noexcept
    -> std::size_t
{
    return bindings[nodeIndex].stateOffset;
}

// Only the Core-owned, explicitly enumerated arithmetic and matrix primitives
// may be re-evaluated after Signal dispatch. A host registry can override a
// builtin UUID, so matching the UUID alone must never establish purity.
inline auto isLatePureBuiltin(const CustomNodeDescriptor* descriptor) noexcept -> bool
{
    if (descriptor == nullptr || descriptor->abiVersion != 1 ||
        descriptor->execution != CustomExecution::Parameter || descriptor->stateBytes != 0 ||
        descriptor->context != nullptr)
    {
        return false;
    }
    auto count = std::size_t{0};
    auto builtins = builtinNodes(count);
    auto isBuiltin = false;
    for (auto index = std::size_t{0}; index < count; ++index)
    {
        if (descriptor == &builtins[index])
        {
            isBuiltin = true;
            break;
        }
    }
    if (!isBuiltin)
    {
        return false;
    }
    constexpr auto mathPrefix = std::array<std::uint8_t, 15>{
        0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33, 0x8d, 0x44, 0, 0, 0, 0, 0};
    constexpr auto matrixPrefix = std::array<std::uint8_t, 15>{
        0xb0, 0xaf, 0x61, 0xd2, 0x29, 0xf6, 0x4c, 0xc9, 0xa7, 0x1e, 0, 0, 0, 0, 0};
    auto id = descriptor->typeId[15];
    return (std::memcmp(descriptor->typeId, mathPrefix.data(), mathPrefix.size()) == 0 && id >= 1 &&
            id <= 47) ||
           (std::memcmp(descriptor->typeId, matrixPrefix.data(), matrixPrefix.size()) == 0 &&
            ((id >= 1 && id <= 13) || (id >= 15 && id <= 18)));
}

inline auto validIndex(std::uint32_t input, std::uint32_t index) noexcept -> bool
{
    return input == noInput || input < index;
}

inline auto typedInputWidth(std::uint32_t input, std::uint8_t width) noexcept -> bool
{
    return input == noInput ? width == 0 : width >= 1 && width <= 4;
}

inline auto validInt32(double value) noexcept -> bool
{
    return std::isfinite(value) && std::trunc(value) == value &&
           value >= std::numeric_limits<std::int32_t>::min() &&
           value <= std::numeric_limits<std::int32_t>::max();
}

inline auto int32FromDouble(double value, std::int32_t& result) noexcept -> Result
{
    if (!std::isfinite(value))
    {
        return Result::NonFinite;
    }
    if (!validInt32(value))
    {
        return Result::InvalidValueType;
    }
    result = static_cast<std::int32_t>(value);
    return Result::Ok;
}

inline auto requiredInputWidths(std::uint8_t opcode, std::uint8_t outputWidth,
                                std::uint8_t& inputAWidth, std::uint8_t& inputBWidth,
                                bool& requiresA, bool& requiresB) noexcept -> bool
{
    requiresA = false;
    requiresB = false;
    if (opcode == int32Opcode)
    {
        return outputWidth == 1 && inputAWidth <= 1 && inputBWidth == 0;
    }
    if (opcode >= int32AddOpcode && opcode <= int32DivideOpcode)
    {
        return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 1;
    }
    if (opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode)
    {
        requiresA = true;
        return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 0;
    }
    if (opcode == 1)
    {
        return outputWidth == 1 && inputAWidth <= 1 && inputBWidth == 0;
    }
    if (opcode == 2)
    {
        return outputWidth == 1 && inputAWidth == 0 && inputBWidth == 0;
    }
    if (opcode >= 3 && opcode <= 6)
    {
        return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 1;
    }
    if (opcode >= 7 && opcode <= 10)
    {
        auto expected = opcode == 7 ? 2 : opcode == 8 ? 3 : 4;
        return outputWidth == expected && (inputAWidth == 0 || inputAWidth == expected) &&
               inputBWidth == 0;
    }
    if (opcode >= 11 && opcode <= 22)
    {
        auto expected = opcode <= 14 ? 2 : opcode <= 18 ? 3 : 4;
        return outputWidth == expected && inputAWidth == expected && inputBWidth == expected;
    }
    if (opcode >= 23 && opcode <= 25)
    {
        requiresA = true;
        auto expected = opcode == 23 ? 2 : opcode == 24 ? 3 : 4;
        return outputWidth == expected && inputAWidth == 1 && inputBWidth == 0;
    }
    if (opcode >= 26 && opcode <= 28)
    {
        requiresA = true;
        auto expected = opcode == 26 ? 2 : opcode == 27 ? 3 : 4;
        return outputWidth == expected && inputAWidth == expected && inputBWidth == 0;
    }
    if (opcode >= 29 && opcode <= 34)
    {
        requiresA = true;
        auto expectedInput = opcode <= 30 ? 2 : opcode <= 32 ? 3 : 4;
        auto expectedOutput = opcode == 29 || opcode == 34   ? 3
                              : opcode == 30 || opcode == 32 ? 4
                                                             : 2;
        return outputWidth == expectedOutput && inputAWidth == expectedInput && inputBWidth == 0;
    }
    if (opcode >= 35 && opcode <= 37)
    {
        requiresA = true;
        auto expected = opcode == 35 ? 2 : opcode == 36 ? 3 : 4;
        return outputWidth == expected && inputAWidth == 4 && inputBWidth == 0;
    }
    if (opcode == 38 || opcode == 39)
    {
        requiresA = true;
        return outputWidth == 4 && inputAWidth == 4 && inputBWidth == 0;
    }
    return false;
}

inline auto validTypedInstruction(const std::uint8_t* current, std::uint32_t index) noexcept -> bool
{
    auto opcode = current[16];
    auto outputWidth = current[17];
    auto inputAWidth = current[18];
    auto inputBWidth = current[19];
    auto inputA = read32(current + 24);
    auto inputB = read32(current + 28);
    auto typeFlags = current[20];
    auto inputASourceComponent = current[21];
    auto inputBSourceComponent = current[22];
    auto shapeAWidth = inputA == noInput ? std::uint8_t{0} : inputAWidth;
    auto shapeBWidth = inputB == noInput ? std::uint8_t{0} : inputBWidth;
    if (opcode >= 3 && opcode <= 6)
    {
        if (inputA == noInput)
        {
            shapeAWidth = 1;
        }
        if (inputB == noInput)
        {
            shapeBWidth = 1;
        }
    }
    if (opcode >= 11 && opcode <= 22)
    {
        if (inputA == noInput)
        {
            shapeAWidth = outputWidth;
        }
        if (inputB == noInput)
        {
            shapeBWidth = outputWidth;
        }
    }
    auto requiresA = false;
    auto requiresB = false;
    if (outputWidth < 1 || outputWidth > 4 || !typedInputWidth(inputA, inputAWidth) ||
        !typedInputWidth(inputB, inputBWidth) || !validIndex(inputA, index) ||
        !validIndex(inputB, index) ||
        !requiredInputWidths(opcode, outputWidth, shapeAWidth, shapeBWidth, requiresA, requiresB) ||
        (requiresA && inputA == noInput) || (requiresB && inputB == noInput) ||
        (typeFlags & 0xf8) != 0 || current[23] != 0 || inputASourceComponent > 3 ||
        inputBSourceComponent > 3 || (inputA == noInput && inputASourceComponent != 0) ||
        (inputB == noInput && inputBSourceComponent != 0))
    {
        return false;
    }
    auto outputIsColor = (typeFlags & 1) != 0;
    auto inputAIsColor = (typeFlags & 2) != 0;
    auto inputBIsColor = (typeFlags & 4) != 0;
    auto expectedInputAColor = opcode == 38 || (opcode == 10 && inputA != noInput);
    if (outputIsColor != (opcode == 10 || opcode == 39) || inputAIsColor != expectedInputAColor ||
        inputBIsColor || (inputA == noInput && inputAIsColor) ||
        (inputB == noInput && inputBIsColor))
    {
        return false;
    }
    for (auto lane = 0u; lane < 4; ++lane)
    {
        if (!std::isfinite(readDouble(current + 32 + lane * 8)) ||
            !std::isfinite(readDouble(current + 64 + lane * 8)))
        {
            return false;
        }
    }
    if (opcode >= 35 && opcode <= 37)
    {
        for (auto lane = 0u; lane < outputWidth; ++lane)
        {
            auto channel = readDouble(current + 32 + lane * 8);
            if (channel < 0 || channel >= 4 || std::floor(channel) != channel)
            {
                return false;
            }
        }
    }
    if (opcode == 10)
    {
        for (auto lane = 0u; lane < 4; ++lane)
        {
            auto channel = readDouble(current + 32 + lane * 8);
            if (channel < 0 || (lane == 3 && channel > 1))
            {
                return false;
            }
        }
    }
    return true;
}

inline auto valueTypeWidth(std::uint8_t type) noexcept -> std::uint8_t
{
    switch (type)
    {
    case valueTypeFloat:
    case valueTypeInt32:
    case valueTypeEnum:
        return 1;
    case valueTypeVector2:
        return 2;
    case valueTypeVector3:
        return 3;
    case valueTypeVector4:
    case valueTypeColor:
        return 4;
    default:
        return 0;
    }
}

inline auto typeForShape(std::uint8_t width, bool color) noexcept -> std::uint8_t
{
    if (color)
    {
        return width == 4 ? valueTypeColor : 0;
    }
    switch (width)
    {
    case 1:
        return valueTypeFloat;
    case 2:
        return valueTypeVector2;
    case 3:
        return valueTypeVector3;
    case 4:
        return valueTypeVector4;
    default:
        return 0;
    }
}

inline auto valueTypeRecord(const std::uint8_t* data, std::size_t offset,
                            std::uint32_t index) noexcept -> const std::uint8_t*
{
    return data + offset + valueTypeHeaderSize + std::size_t{index} * valueTypeRecordSize;
}

inline auto validValueTypeMetadata(const std::uint8_t* data, std::size_t binarySize,
                                   std::size_t offset, std::uint32_t instructionCount) noexcept
    -> bool
{
    if (offset > binarySize || binarySize - offset < valueTypeHeaderSize ||
        std::memcmp(data + offset, "LFTY", 4) != 0 || read16(data + offset + 4) != 1 ||
        read16(data + offset + 6) != valueTypeRecordSize ||
        std::size_t{instructionCount} >
            (binarySize - offset - valueTypeHeaderSize) / valueTypeRecordSize ||
        offset + valueTypeHeaderSize + std::size_t{instructionCount} * valueTypeRecordSize !=
            binarySize)
    {
        return false;
    }

    auto expectedInputs = [&](std::uint8_t opcode, std::uint8_t outputType,
                              std::uint8_t outputWidth, std::uint8_t& count,
                              std::array<std::uint8_t, 4>& types) noexcept -> bool {
        count = 0;
        types = {};
        auto set = [&](std::uint8_t input, std::uint8_t type) noexcept {
            if (input >= types.size() || type == 0)
            {
                return false;
            }
            types[input] = type;
            count = static_cast<std::uint8_t>(input + 1);
            return true;
        };
        if (opcode == 1 || (opcode >= 7 && opcode <= 10) || opcode == int32Opcode)
        {
            return set(0, outputType);
        }
        if (opcode == 2 || opcode == curveOpcode || opcode == branchTimeOpcode ||
            opcode == currentPhaseOpcode || opcode == phaseTimeOpcode)
        {
            return true;
        }
        if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
            (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))
        {
            return set(0, outputType) && set(1, outputType);
        }
        if (opcode >= 23 && opcode <= 25)
        {
            return set(0, valueTypeFloat);
        }
        if (opcode >= 26 && opcode <= 28)
        {
            return set(0, outputType);
        }
        if (opcode >= 29 && opcode <= 34)
        {
            auto inputType = opcode <= 30   ? valueTypeVector2
                             : opcode <= 32 ? valueTypeVector3
                                            : valueTypeVector4;
            return set(0, inputType);
        }
        if (opcode >= 35 && opcode <= 37)
        {
            return set(0, valueTypeVector4);
        }
        if (opcode == 38)
        {
            return set(0, valueTypeColor);
        }
        if (opcode == 39)
        {
            return set(0, valueTypeVector4);
        }
        if (opcode == enumValueOpcode)
        {
            return set(0, valueTypeEnum);
        }
        if (opcode == enumSelectOpcode)
        {
            return set(0, valueTypeEnum);
        }
        if (opcode == floatToInt32Opcode)
        {
            return set(0, valueTypeFloat);
        }
        if (opcode == int32ToFloatOpcode)
        {
            return set(0, valueTypeInt32);
        }
        return opcode == customParameterOpcode || opcode == customSignalOpcode ||
               opcode == logicalEventSignalOpcode;
    };

    for (auto index = 0u; index < instructionCount; ++index)
    {
        auto current = instruction(data, 10, index);
        auto record = valueTypeRecord(data, offset, index);
        auto opcode = current[16];
        auto outputType = record[0];
        auto inputCount = record[1];
        auto outputWidth = current[17];
        if (inputCount > 4 || record[6] != 0 || record[7] != 0)
        {
            return false;
        }
        auto isCustom = opcode == customParameterOpcode || opcode == customSignalOpcode;
        auto isLogical = opcode == logicalEventSignalOpcode;
        if (isCustom)
        {
            auto expectedOutput = opcode == customSignalOpcode
                                      ? 0
                                      : typeForShape(outputWidth, (current[20] & 1) != 0);
            if (outputType != expectedOutput || inputCount != current[18])
            {
                return false;
            }
        } else if (isLogical)
        {
            if (outputType != typeForShape(outputWidth, false) || inputCount != current[18])
            {
                return false;
            }
        } else if (opcode == enumSelectOpcode)
        {
            if (valueTypeWidth(outputType) != outputWidth || outputType == valueTypeEnum ||
                outputType == 0 || inputCount != 1 || record[2] != valueTypeEnum)
            {
                return false;
            }
        } else
        {
            std::uint8_t requiredCount = 0;
            auto requiredTypes = std::array<std::uint8_t, 4>{};
            if (!expectedInputs(opcode, outputType, outputWidth, requiredCount, requiredTypes) ||
                inputCount != requiredCount)
            {
                return false;
            }
            auto requiredOutput =
                opcode == 1 || opcode == 2 || (opcode >= 3 && opcode <= 6) ||
                        opcode == curveOpcode || opcode == branchTimeOpcode ||
                        opcode == phaseTimeOpcode || opcode == int32ToFloatOpcode
                    ? valueTypeFloat
                : opcode == int32Opcode ||
                        (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                        opcode == floatToInt32Opcode
                    ? valueTypeInt32
                : isEnumSourceOpcode(opcode) ? valueTypeEnum
                : opcode == 10               ? valueTypeColor
                : opcode == 7 || opcode == 11 || opcode == 12 || opcode == 13 || opcode == 14 ||
                        opcode == 26 || opcode == 33 || opcode == 35
                    ? valueTypeVector2
                : opcode == 8 || (opcode >= 15 && opcode <= 18) || opcode == 27 || opcode == 29 ||
                        opcode == 34 || opcode == 36
                    ? valueTypeVector3
                : opcode == 9 || (opcode >= 19 && opcode <= 22) || opcode == 28 || opcode == 30 ||
                        opcode == 32 || opcode == 37 || opcode == 38
                    ? valueTypeVector4
                : opcode == 39 ? valueTypeColor
                : opcode == 23 ? valueTypeVector2
                : opcode == 24 ? valueTypeVector3
                : opcode == 25 ? valueTypeVector4
                : opcode == 31 ? valueTypeVector2
                : opcode == 40 ? outputType
                               : 0;
            if (outputType != requiredOutput)
            {
                return false;
            }
            if (valueTypeWidth(outputType) != outputWidth ||
                (((current[20] & 1) != 0) != (outputType == valueTypeColor)))
            {
                return false;
            }
            for (auto input = 0u; input < requiredCount; ++input)
            {
                if (record[2 + input] != requiredTypes[input])
                {
                    return false;
                }
            }
        }

        for (auto input = 0u; input < inputCount; ++input)
        {
            auto type = record[2 + input];
            auto width = valueTypeWidth(type);
            if (width == 0)
            {
                return false;
            }
            if (isCustom || isLogical)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                auto sourceWidth = inputRecord[4];
                auto color = inputRecord[6] != 0;
                if (type != typeForShape(sourceWidth == 0 ? width : sourceWidth, color) ||
                    (isLogical && (type == valueTypeColor || type == valueTypeEnum)))
                {
                    return false;
                }
                if (sourceIndex != noInput)
                {
                    if (sourceIndex >= index)
                    {
                        return false;
                    }
                    auto sourceOpcode = instruction(data, 10, sourceIndex)[16];
                    auto sourceType = sourceOpcode >= 26 && sourceOpcode <= 28
                                          ? valueTypeFloat
                                          : valueTypeRecord(data, offset, sourceIndex)[0];
                    if (sourceType != type)
                    {
                        return false;
                    }
                }
                auto defaults = current + 80 + input * 32;
                for (auto lane = 0u; lane < width; ++lane)
                {
                    auto value = readDouble(defaults + lane * 8);
                    if (!std::isfinite(value) || (type == valueTypeInt32 && !validInt32(value)))
                    {
                        return false;
                    }
                }
            } else
            {
                auto sourceIndex = input == 0   ? read32(current + 24)
                                   : input == 1 ? read32(current + 28)
                                                : noInput;
                auto sourceComponent = input == 0 ? current[21] : input == 1 ? current[22] : 0;
                if (sourceIndex != noInput)
                {
                    if (sourceIndex >= index)
                    {
                        return false;
                    }
                    auto source = instruction(data, 10, sourceIndex);
                    auto sourceType = valueTypeRecord(data, offset, sourceIndex)[0];
                    if (source[16] >= 26 && source[16] <= 28)
                    {
                        sourceType = valueTypeFloat;
                    }
                    if (sourceType != type)
                    {
                        return false;
                    }
                }
                auto defaults = current + (input == 0 ? 32 : 64);
                for (auto lane = 0u; lane < width; ++lane)
                {
                    auto value = readDouble(defaults + lane * 8);
                    if (!std::isfinite(value) || (type == valueTypeInt32 && !validInt32(value)))
                    {
                        return false;
                    }
                }
                if (type == valueTypeEnum && sourceIndex != noInput)
                {
                    auto source = instruction(data, 10, sourceIndex);
                    if (!isEnumSourceOpcode(source[16]) ||
                        std::memcmp(current + 96, source + 96, 16) != 0)
                    {
                        return false;
                    }
                }
                if (sourceIndex == noInput && input == 0 && current[16] == floatToInt32Opcode)
                {
                    // This conversion is intentionally connection-only; it must not
                    // turn an implicit Float default into an Int32 value.
                    return false;
                }
                (void)sourceComponent;
            }
        }
        for (auto input = inputCount; input < 4; ++input)
        {
            if (record[2 + input] != 0)
            {
                return false;
            }
        }
    }
    return true;
}

inline auto validEnumSelectValueTypes(const std::uint8_t* data, std::size_t valueTypeOffset,
                                      std::size_t enumMetadataOffset,
                                      std::size_t selectRecordsOffset, std::uint16_t selectCount,
                                      std::uint32_t instructionCount) noexcept -> bool
{
    for (auto index = 0u; index < selectCount; ++index)
    {
        auto record = data + selectRecordsOffset + std::size_t{index} * enumSelectRecordSize;
        auto nodeIndex = read32(record);
        if (nodeIndex >= instructionCount)
        {
            return false;
        }
        auto outputType = valueTypeRecord(data, valueTypeOffset, nodeIndex)[0];
        auto defaultSource = read32(record + 8);
        if (defaultSource != noInput &&
            (defaultSource >= nodeIndex ||
             valueTypeRecord(data, valueTypeOffset, defaultSource)[0] != outputType))
        {
            return false;
        }
        auto outputWidth = record[28];
        if (outputType == valueTypeInt32)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                if (!validInt32(readDouble(record + 36 + lane * 8)))
                {
                    return false;
                }
            }
        }

        auto caseCount = record[29];
        auto casesOffset = read32(record + 32);
        for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
        {
            auto branch = data + enumMetadataOffset + casesOffset +
                          std::size_t{caseIndex} * enumCaseRecordSize;
            auto source = read32(branch + 4);
            if (source != noInput &&
                (source >= nodeIndex ||
                 valueTypeRecord(data, valueTypeOffset, source)[0] != outputType))
            {
                return false;
            }
            if (outputType == valueTypeInt32)
            {
                for (auto lane = 0u; lane < outputWidth; ++lane)
                {
                    if (!validInt32(readDouble(branch + 8 + lane * 8)))
                    {
                        return false;
                    }
                }
            }
        }
    }
    return true;
}

inline auto validCurveInstruction(const std::uint8_t* current, std::uint16_t version) noexcept
    -> bool
{
    if (current[16] != curveOpcode || current[17] != 1 || current[18] != 0 || current[19] != 0 ||
        current[20] != 0 || current[21] != 0 || current[22] != 0 || current[23] != 0 ||
        read32(current + 24) != noInput || read32(current + 28) != noInput)
    {
        return false;
    }
    for (auto lane = 0u; lane < 4; ++lane)
    {
        if (readDouble(current + 32 + lane * 8) != 0 || readDouble(current + 64 + lane * 8) != 0)
        {
            return false;
        }
    }
    for (auto byte = typedInstructionSize; byte < 224; ++byte)
    {
        if (!(version >= 8 && byte >= 112 && byte < 116) && current[byte] != 0)
        {
            return false;
        }
    }
    for (auto byte = 232u; byte < customInstructionSize; ++byte)
    {
        if (current[byte] != 0)
        {
            return false;
        }
    }
    return true;
}

inline auto validCurveKey(const std::uint8_t* key) noexcept -> bool
{
    auto time = readDouble(key);
    auto value = readDouble(key + 8);
    auto inSlope = readDouble(key + 16);
    auto outSlope = readDouble(key + 24);
    if (!std::isfinite(time) || std::abs(time) > 1'000'000 || !std::isfinite(value) ||
        std::abs(value) > 1'000'000 || !std::isfinite(inSlope) || std::abs(inSlope) > 1'000'000 ||
        !std::isfinite(outSlope) || std::abs(outSlope) > 1'000'000 || key[32] > 2)
    {
        return false;
    }
    for (auto byte = 33u; byte < curveKeySize; ++byte)
    {
        if (key[byte] != 0)
        {
            return false;
        }
    }
    return true;
}

inline auto evaluateCurve(const std::uint8_t* keys, std::uint32_t count, double time) noexcept
    -> double
{
    if (count == 0)
    {
        return 0;
    }
    auto keyAt = [&](std::uint32_t index) noexcept {
        return keys + std::size_t{index} * curveKeySize;
    };
    auto first = keyAt(0);
    auto last = keyAt(count - 1);
    if (time <= readDouble(first))
    {
        return readDouble(first + 8);
    }
    if (time >= readDouble(last))
    {
        return readDouble(last + 8);
    }
    auto low = 0u;
    auto high = count - 1;
    while (high - low > 1)
    {
        auto middle = low + (high - low) / 2;
        if (time <= readDouble(keyAt(middle)))
        {
            high = middle;
        } else
        {
            low = middle;
        }
    }
    auto left = keyAt(low);
    auto right = keyAt(high);
    auto leftTime = readDouble(left);
    auto rightTime = readDouble(right);
    auto leftValue = readDouble(left + 8);
    auto rightValue = readDouble(right + 8);
    auto span = rightTime - leftTime;
    auto t = (time - leftTime) / span;
    switch (left[32])
    {
    case 0:
        return leftValue;
    case 1:
        return leftValue + (rightValue - leftValue) * t;
    case 2:
        return (2 * t * t * t - 3 * t * t + 1) * leftValue +
               (t * t * t - 2 * t * t + t) * span * readDouble(left + 24) +
               (-2 * t * t * t + 3 * t * t) * rightValue +
               (t * t * t - t * t) * span * readDouble(right + 16);
    default:
        return 0;
    }
}

inline auto rangesOverlap(const void* first, std::size_t firstSize, const void* second,
                          std::size_t secondSize) noexcept -> bool
{
    if (firstSize == 0 || secondSize == 0)
    {
        return false;
    }
    auto firstStart = reinterpret_cast<std::uintptr_t>(first);
    auto secondStart = reinterpret_cast<std::uintptr_t>(second);
    if (firstSize > std::numeric_limits<std::uintptr_t>::max() - firstStart ||
        secondSize > std::numeric_limits<std::uintptr_t>::max() - secondStart)
    {
        return true;
    }
    return firstStart < secondStart + secondSize && secondStart < firstStart + firstSize;
}

} // namespace vfx_runtime_detail

} // namespace liberafx
