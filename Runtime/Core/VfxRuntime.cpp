#include "liberafx/VfxRuntime.h"
#include "liberafx/math/VectorMath.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>

namespace liberafx {
namespace {

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

auto branchGuard(const std::uint8_t* current, std::uint16_t version) noexcept -> std::uint32_t;
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
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x01};
constexpr auto logicalAndTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x02};
constexpr auto logicalOrTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x03};
constexpr auto logicalSignalToNumberTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x04};
constexpr auto logicalLoopClockTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x05};
constexpr auto logicalQualityBranchTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x06};
constexpr auto logicalSignalCaptureTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x07};
constexpr auto valueTypeFloat = std::uint8_t{1};
constexpr auto valueTypeInt32 = std::uint8_t{2};
constexpr auto valueTypeVector2 = std::uint8_t{3};
constexpr auto valueTypeVector3 = std::uint8_t{4};
constexpr auto valueTypeVector4 = std::uint8_t{5};
constexpr auto valueTypeColor = std::uint8_t{6};
constexpr auto valueTypeEnum = std::uint8_t{7};
std::atomic<std::uint64_t> nextProgramGeneration{1};
std::atomic<std::uint64_t> nextInstanceGeneration{1};
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559,
              "LIBERA FX requires IEEE-754 binary64.");

auto validStateAlignment(std::size_t alignment) noexcept -> bool;
auto instruction(const std::uint8_t* bytes, std::uint16_t version,
                 std::size_t index) noexcept -> const std::uint8_t*;

auto read16(const std::uint8_t* bytes) noexcept -> std::uint16_t
{
    return static_cast<std::uint16_t>(bytes[0] | (std::uint16_t{bytes[1]} << 8));
}

auto read32(const std::uint8_t* bytes) noexcept -> std::uint32_t
{
    return std::uint32_t{bytes[0]} | (std::uint32_t{bytes[1]} << 8) |
           (std::uint32_t{bytes[2]} << 16) | (std::uint32_t{bytes[3]} << 24);
}

auto branchGuard(const std::uint8_t* current, std::uint16_t version) noexcept -> std::uint32_t
{
    auto opcode = current[16];
    auto customLayout = opcode == customParameterOpcode || opcode == customSignalOpcode ||
        opcode == logicalEventSignalOpcode;
    return read32(current + (version >= 11 && customLayout ? 44 : 112));
}

auto read64(const std::uint8_t* bytes) noexcept -> std::uint64_t
{
    auto value = std::uint64_t{0};
    for (auto index = 0u; index < 8; ++index)
        value |= std::uint64_t{bytes[index]} << (index * 8);
    return value;
}

auto readI32(const std::uint8_t* bytes) noexcept -> std::int32_t
{
    auto bits = read32(bytes);
    auto value = std::int32_t{};
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

auto readDouble(const std::uint8_t* bytes) noexcept -> double
{
    auto bits = read64(bytes);
    auto value = double{};
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

void write64(std::uint8_t* bytes, std::uint64_t value) noexcept
{
    for (auto index = 0u; index < 8; ++index)
        bytes[index] = static_cast<std::uint8_t>(value >> (index * 8));
}

void writeDouble(std::uint8_t* bytes, double value) noexcept
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    write64(bytes, bits);
}

auto instruction(const std::uint8_t* bytes, std::uint16_t version,
                 std::size_t index) noexcept -> const std::uint8_t*;

auto validUtf8(const std::uint8_t* text, std::size_t length) noexcept -> bool
{
    for (auto index = std::size_t{}; index < length;)
    {
        auto first = text[index++];
        auto codePoint = std::uint32_t{};
        auto continuationCount = 0u;
        if (first < 0x80) codePoint = first;
        else if (first >= 0xc2 && first <= 0xdf)
        {
            codePoint = first & 0x1f;
            continuationCount = 1;
        }
        else if (first >= 0xe0 && first <= 0xef)
        {
            codePoint = first & 0x0f;
            continuationCount = 2;
        }
        else if (first >= 0xf0 && first <= 0xf4)
        {
            codePoint = first & 0x07;
            continuationCount = 3;
        }
        else return false;
        if (continuationCount > length - index) return false;
        for (auto count = 0u; count < continuationCount; ++count)
        {
            auto next = text[index++];
            if ((next & 0xc0) != 0x80) return false;
            codePoint = (codePoint << 6) | (next & 0x3f);
        }
        if ((continuationCount == 1 && codePoint < 0x80) ||
            (continuationCount == 2 && codePoint < 0x800) ||
            (continuationCount == 3 && codePoint < 0x10000) ||
            codePoint > 0x10ffff || (codePoint >= 0xd800 && codePoint <= 0xdfff) ||
            codePoint <= 0x1f || (codePoint >= 0x7f && codePoint <= 0x9f))
            return false;
    }
    return true;
}

auto validGameIoMetadata(const std::uint8_t* data, std::size_t size,
                         std::size_t offset, std::uint32_t instructionCount,
                         std::uint16_t version, std::size_t& metadataEnd) noexcept -> bool
{
    constexpr auto headerSize = std::size_t{8};
    constexpr auto legacyRecordSize = std::size_t{24};
    constexpr auto recordSize = std::size_t{72};
    auto currentRecordSize = version == 6 ? legacyRecordSize : recordSize;
    if (offset > size || size - offset < headerSize ||
        std::memcmp(data + offset, "LFIO", 4) != 0 || read16(data + offset + 6) != 0)
        return false;
    auto portCount = read16(data + offset + 4);
    if (portCount == 0 || portCount > 256) return false;
    auto cursor = offset + headerSize;
    for (auto port = std::uint16_t{}; port < portCount; ++port)
    {
        if (cursor > size || size - cursor < currentRecordSize) return false;
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
            (kind == 1 || kind == 3 ? width != 0 || isColor != 0 || bindingCount != 1 :
                (matrix ? kind == 1 || kind == 3 || isColor != 0 :
                    width < 1 || width > 4 || (isColor && width != 4)) ||
                (kind == 4 && !matrix)))
            return false;
        auto nameOffset = cursor + currentRecordSize;
        if (nameOffset > size || size - nameOffset < std::size_t{nameLength} + 1 ||
            data[nameOffset + nameLength] != 0 || data[nameOffset] == ' ' ||
            data[nameOffset + nameLength - 1] == ' ' ||
            !validUtf8(data + nameOffset, nameLength))
            return false;
        for (auto row = 0u; row < bindingCount; ++row)
        {
            auto nodeId = version == 6 ? record : record + row * 16;
            auto emptyId = true;
            for (auto byte = 0u; byte < 16; ++byte) emptyId = emptyId && nodeId[byte] == 0;
            if (emptyId) return false;
            auto foundNode = false;
            for (auto index = 0u; index < instructionCount; ++index)
            {
                auto current = instruction(data, 3, index);
                if (std::memcmp(current, nodeId, 16) != 0) continue;
                auto rowWidth = matrix ? std::uint8_t{4} : width;
                if ((kind == 1 || kind == 3) ?
                        (current[16] != customSignalOpcode &&
                         (current[16] != logicalEventSignalOpcode || current[19] == 0)) :
                    current[17] != rowWidth || ((current[20] & 1) != 0) != (isColor != 0))
                    return false;
                foundNode = true;
                break;
            }
            if (!foundNode) return false;
            for (auto earlierRow = 0u; earlierRow < row; ++earlierRow)
                if (std::memcmp(nodeId, record + earlierRow * 16, 16) == 0) return false;
        }
        for (auto previous = offset + headerSize; previous < cursor;)
        {
            auto previousRecord = data + previous;
            auto previousKind = version == 6 ? previousRecord[16] : previousRecord[64];
            auto previousBindingCount = version == 6 ? std::uint8_t{1} : previousRecord[67];
            auto previousNameLength = read16(previousRecord + (version == 6 ? 20 : 68));
            auto duplicateName = previousNameLength == nameLength &&
                std::memcmp(data + previous + currentRecordSize, data + nameOffset, nameLength) == 0;
            if (duplicateName && kind != 4 && previousKind != 4) return false;
            for (auto row = 0u; row < bindingCount; ++row)
            for (auto previousRow = 0u; previousRow < previousBindingCount; ++previousRow)
                if (std::memcmp(version == 6 ? record : record + row * 16,
                    version == 6 ? previousRecord : previousRecord + previousRow * 16, 16) == 0 &&
                    kind != 2 && previousKind != 2)
                return false;
            previous += currentRecordSize + previousNameLength + 1;
        }
        cursor = nameOffset + nameLength + 1;
    }
    metadataEnd = cursor;
    return version >= 9 ? cursor <= size : cursor == size;
}

auto validEnumBranchMetadata(const std::uint8_t* data, std::size_t size,
                             std::size_t offset, std::uint16_t version, std::uint32_t instructionCount,
                             std::size_t& metadataEnd, std::size_t& selectRecordsOffset,
                             std::size_t& guardRecordsOffset, std::size_t& guardValuesOffset,
                             std::uint16_t& enumValueCount, std::uint16_t& enumSelectCount,
                             std::uint16_t& guardCount) noexcept -> bool
{
    if (offset > size || size - offset < enumBranchHeaderSize ||
        std::memcmp(data + offset, "LFEB", 4) != 0 || read16(data + offset + 4) != 1)
        return false;
    guardCount = read16(data + offset + 6);
    enumValueCount = read16(data + offset + 8);
    enumSelectCount = read16(data + offset + 10);
    auto declaredSize = read32(data + offset + 12);
    if (declaredSize < enumBranchHeaderSize || declaredSize > size - offset || guardCount > 255)
        return false;
    metadataEnd = offset + declaredSize;
    auto valuesOffset = offset + enumBranchHeaderSize;
    if (std::size_t{enumValueCount} > (declaredSize - enumBranchHeaderSize) / enumValueRecordSize)
        return false;
    selectRecordsOffset = valuesOffset + std::size_t{enumValueCount} * enumValueRecordSize;
    if (std::size_t{enumSelectCount} > (metadataEnd - selectRecordsOffset) / enumSelectRecordSize)
        return false;
    guardRecordsOffset = selectRecordsOffset + std::size_t{enumSelectCount} * enumSelectRecordSize;
    if (std::size_t{guardCount} > (metadataEnd - guardRecordsOffset) / enumGuardRecordSize)
        return false;
    guardValuesOffset = guardRecordsOffset + std::size_t{guardCount} * enumGuardRecordSize;
    auto caseCursor = guardValuesOffset;
    for (auto index = 0u; index < enumValueCount; ++index)
    {
        auto record = data + valuesOffset + std::size_t{index} * enumValueRecordSize;
        auto nodeIndex = read32(record);
        if (nodeIndex >= instructionCount) return false;
        auto current = instruction(data, 3, nodeIndex);
        if (!isEnumSourceOpcode(current[16]) || std::memcmp(current + 96, record + 4, 16) != 0)
            return false;
        auto sourceIndex = read32(current + 24);
        if (sourceIndex != noInput)
        {
            if (sourceIndex >= nodeIndex) return false;
            auto source = instruction(data, 3, sourceIndex);
            if (!isEnumSourceOpcode(source[16]) || std::memcmp(source + 96, record + 4, 16) != 0)
                return false;
        }
        for (auto prior = 0u; prior < index; ++prior)
            if (read32(data + valuesOffset + std::size_t{prior} * enumValueRecordSize) == nodeIndex)
                return false;
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
             (defaultSource >= nodeIndex || instruction(data, 3, defaultSource)[17] != outputWidth)) ||
            outputWidth < 1 || outputWidth > 4 || caseCount > 32 ||
            offset + casesOffset != caseCursor ||
            std::size_t{caseCount} > (metadataEnd - caseCursor) / enumCaseRecordSize)
            return false;
        auto current = instruction(data, 3, nodeIndex);
        auto selector = instruction(data, 3, selectorIndex);
        if (current[16] != enumSelectOpcode || current[17] != outputWidth ||
            read32(current + 24) != selectorIndex || !isEnumSourceOpcode(selector[16]) ||
            std::memcmp(current + 96, record + 12, 16) != 0 ||
            std::memcmp(selector + 96, record + 12, 16) != 0)
            return false;
        for (auto prior = 0u; prior < index; ++prior)
            if (read32(data + selectRecordsOffset + std::size_t{prior} * enumSelectRecordSize) == nodeIndex)
                return false;
        for (auto lane = 0u; lane < outputWidth; ++lane)
            if (!std::isfinite(readDouble(record + 36 + lane * 8))) return false;
        for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
        {
            auto branch = data + caseCursor + std::size_t{caseIndex} * enumCaseRecordSize;
            auto source = read32(branch + 4);
            for (auto lane = 0u; lane < outputWidth; ++lane)
                if (!std::isfinite(readDouble(branch + 8 + lane * 8))) return false;
            if (source != noInput &&
                (source >= nodeIndex || instruction(data, 3, source)[17] != outputWidth))
                return false;
            for (auto prior = 0u; prior < caseIndex; ++prior)
                if (read32(data + caseCursor + std::size_t{prior} * enumCaseRecordSize) == read32(branch))
                    return false;
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
        if (selectorIndex >= instructionCount ||
            (parent != noInput && parent >= index) || flags > 1 ||
            offset + guardCaseListOffset != guardDataCursor ||
            std::size_t{caseCount} > (metadataEnd - guardDataCursor) / 4 ||
            offset + excludedOffset != guardDataCursor + std::size_t{caseCount} * 4 ||
            std::size_t{excludedCount} >
                (metadataEnd - (offset + excludedOffset)) / 4)
            return false;
        auto selector = instruction(data, 3, selectorIndex);
        if (!isEnumSourceOpcode(selector[16]) || std::memcmp(selector + 96, record + 4, 16) != 0)
            return false;
        for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
            for (auto prior = 0u; prior < caseIndex; ++prior)
                if (read32(data + offset + guardCaseListOffset + std::size_t{caseIndex} * 4) ==
                    read32(data + offset + guardCaseListOffset + std::size_t{prior} * 4))
                    return false;
        for (auto caseIndex = 0u; caseIndex < excludedCount; ++caseIndex)
            for (auto prior = 0u; prior < caseIndex; ++prior)
                if (read32(data + offset + excludedOffset + std::size_t{caseIndex} * 4) ==
                    read32(data + offset + excludedOffset + std::size_t{prior} * 4))
                    return false;
        guardDataCursor = offset + excludedOffset + std::size_t{excludedCount} * 4;
        auto ancestor = read32(record + 20);
        auto depth = 1u;
        while (ancestor != noInput)
        {
            if (++depth > 64 || ancestor >= index) return false;
            ancestor = read32(data + guardRecordsOffset +
                std::size_t{ancestor} * enumGuardRecordSize + 20);
        }
    }
    if (guardDataCursor != metadataEnd) return false;
    auto observedEnumValues = std::uint16_t{0};
    auto observedEnumSelects = std::uint16_t{0};
    for (auto index = 0u; index < instructionCount; ++index)
    {
        auto current = instruction(data, 3, index);
        auto opcode = current[16];
        if (isEnumSourceOpcode(opcode)) ++observedEnumValues;
        if (opcode == enumSelectOpcode) ++observedEnumSelects;
        auto guardId = branchGuard(current, version);
        if (guardId != noInput && guardId >= guardCount) return false;
        if (opcode == branchTimeOpcode && guardId == noInput) return false;
    }
    return observedEnumValues == enumValueCount && observedEnumSelects == enumSelectCount;
}

auto findLogicalDescriptor(const std::uint8_t* typeId, std::uint32_t abiVersion,
                           bool& registeredType) noexcept -> const LogicalNodeDescriptor*
{
    registeredType = false;
    auto count = std::size_t{0};
    auto descriptors = logicalNodes(count);
    for (auto index = std::size_t{0}; index < count; ++index)
    {
        if (std::memcmp(descriptors[index].typeId, typeId, 16) != 0) continue;
        registeredType = true;
        if (descriptors[index].abiVersion == abiVersion) return &descriptors[index];
    }
    return nullptr;
}

auto validLogicalDescriptor(const LogicalNodeDescriptor& descriptor) noexcept -> bool
{
    if (descriptor.abiVersion == 0 || descriptor.inputCount > customMaxPorts ||
        descriptor.parameterCount > customMaxPorts || descriptor.signalInputCount > customMaxPorts ||
        descriptor.outputWidth < 1 || descriptor.outputWidth > 4 ||
        descriptor.signalBranchCount > customMaxPorts || descriptor.update == nullptr ||
        !validStateAlignment(descriptor.stateAlignment) ||
        descriptor.stateBytes > (std::numeric_limits<std::size_t>::max() -
            descriptor.stateAlignment + 1) ||
        (descriptor.signalInputCount != 0 && descriptor.onSignal == nullptr))
        return false;
    for (auto input = 0u; input < descriptor.inputCount; ++input)
        if (descriptor.inputWidths[input] < 1 || descriptor.inputWidths[input] > 4)
            return false;
    return true;
}

auto validLogicalConfiguration(const std::uint8_t* typeId, std::uint32_t configuration) noexcept -> bool
{
    if (std::memcmp(typeId, logicalCompareTypeId.data(), 16) == 0)
    {
        auto operation = configuration & 0xffu;
        auto mode = (configuration >> 8u) & 0xffu;
        return (configuration & 0xffff0000u) == 0 && operation <= 5 && mode <= 1;
    }
    if (std::memcmp(typeId, logicalAndTypeId.data(), 16) == 0)
        return (configuration & 0xffffff00u) == 0 && (configuration & 0xffu) <= 1;
    if (std::memcmp(typeId, logicalOrTypeId.data(), 16) == 0)
        return configuration == 0;
    if (std::memcmp(typeId, logicalSignalToNumberTypeId.data(), 16) == 0)
        return (configuration & 0xffffff00u) == 0 && (configuration & 0xffu) <= 2;
    if (std::memcmp(typeId, logicalLoopClockTypeId.data(), 16) == 0)
        return configuration == 0;
    if (std::memcmp(typeId, logicalQualityBranchTypeId.data(), 16) == 0)
        return (configuration & 0xffff0000u) == 0 && (configuration & 0xffu) <= 5 &&
            ((configuration >> 8u) & 0xffu) <= 4;
    if (std::memcmp(typeId, logicalSignalCaptureTypeId.data(), 16) == 0)
        return configuration == 0;
    return false;
}

auto validLogicalEventMetadata(const std::uint8_t* data, std::size_t size,
                               std::size_t offset, std::uint32_t instructionCount,
                               std::size_t& metadataEnd, std::size_t& instructionRecordsOffset,
                               std::size_t& signalRoutesOffset, std::uint16_t& logicalInstructionCount,
                               std::uint32_t& signalRouteCount,
                               std::size_t& logicalQueueCapacity) noexcept -> bool
{
    if (offset > size || size - offset < logicalEventHeaderSize ||
        std::memcmp(data + offset, "LFLE", 4) != 0 || read16(data + offset + 4) != 1)
        return false;
    logicalInstructionCount = read16(data + offset + 6);
    signalRouteCount = read32(data + offset + 8);
    auto declaredSize = read32(data + offset + 12);
    if (declaredSize < logicalEventHeaderSize || declaredSize > size - offset ||
        signalRouteCount > maxSignalRoutes ||
        std::size_t{logicalInstructionCount} >
            (declaredSize - logicalEventHeaderSize) / logicalInstructionRecordSize)
        return false;
    auto routesBytesOffset = logicalEventHeaderSize +
        std::size_t{logicalInstructionCount} * logicalInstructionRecordSize;
    if (std::size_t{signalRouteCount} >
        (declaredSize - routesBytesOffset) / logicalSignalRouteRecordSize ||
        routesBytesOffset + std::size_t{signalRouteCount} * logicalSignalRouteRecordSize != declaredSize)
        return false;
    metadataEnd = offset + declaredSize;
    instructionRecordsOffset = offset + logicalEventHeaderSize;
    signalRoutesOffset = instructionRecordsOffset +
        std::size_t{logicalInstructionCount} * logicalInstructionRecordSize;

    auto signalInputCounts = std::array<std::uint8_t, maxNodes>{};
    signalInputCounts.fill(0xff);
    auto observedLogicalInstructions = std::uint16_t{0};
    for (auto index = std::uint32_t{0}; index < instructionCount; ++index)
        observedLogicalInstructions += instruction(data, 3, index)[16] == logicalEventSignalOpcode ? 1 : 0;
    if (observedLogicalInstructions != logicalInstructionCount) return false;

    for (auto index = 0u; index < logicalInstructionCount; ++index)
    {
        auto record = data + instructionRecordsOffset + std::size_t{index} * logicalInstructionRecordSize;
        auto nodeIndex = read32(record);
        auto abiVersion = read32(record + 20);
        auto configuration = read32(record + 24);
        if (nodeIndex >= instructionCount || signalInputCounts[nodeIndex] != 0xff)
            return false;
        auto current = instruction(data, 3, nodeIndex);
        if (current[16] != logicalEventSignalOpcode ||
            std::memcmp(current + 24, record + 4, 16) != 0 ||
            read32(current + 40) != abiVersion ||
            current[18] != record[28] || current[19] != record[31] ||
            current[17] != record[30] || record[28] > customMaxPorts ||
            record[29] > customMaxPorts || record[30] < 1 || record[30] > 4 ||
            record[31] > customMaxPorts || current[20] != 0)
            return false;
        auto registeredType = false;
        auto descriptor = findLogicalDescriptor(record + 4, abiVersion, registeredType);
        auto expectedParameterCount = std::memcmp(record + 4, logicalCompareTypeId.data(), 16) == 0 ||
            std::memcmp(record + 4, logicalQualityBranchTypeId.data(), 16) == 0 ? 2 :
            std::memcmp(record + 4, logicalOrTypeId.data(), 16) == 0 ||
                std::memcmp(record + 4, logicalLoopClockTypeId.data(), 16) == 0 ||
                std::memcmp(record + 4, logicalSignalCaptureTypeId.data(), 16) == 0 ? 0 :
            std::memcmp(record + 4, logicalAndTypeId.data(), 16) == 0 ||
                std::memcmp(record + 4, logicalSignalToNumberTypeId.data(), 16) == 0 ? 1 : 0xff;
        if (descriptor == nullptr || !validLogicalDescriptor(*descriptor) ||
            descriptor->inputCount != record[28] || descriptor->parameterCount != expectedParameterCount ||
            descriptor->signalInputCount != record[29] || descriptor->outputWidth != record[30] ||
            descriptor->signalBranchCount != record[31] ||
            !validLogicalConfiguration(record + 4, configuration))
            return false;
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte) emptyId = emptyId && record[4 + byte] == 0;
        if (emptyId) return false;
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
                return false;
            if (sourceIndex == noInput && (sourceWidth != 0 || selectedLane != 0 || isColor != 0))
                return false;
        }
        for (auto prior = 0u; prior < index; ++prior)
            if (read32(data + instructionRecordsOffset +
                std::size_t{prior} * logicalInstructionRecordSize) == nodeIndex)
                return false;
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
            return false;
        auto source = instruction(data, 3, sourceIndex);
        auto target = instruction(data, 3, targetIndex);
        if ((source[16] != customSignalOpcode && source[16] != logicalEventSignalOpcode) ||
            branch >= source[19] ||
            (target[16] != customSignalOpcode && target[16] != logicalEventSignalOpcode))
            return false;
        if (target[16] == logicalEventSignalOpcode)
        {
            if (signalInputCounts[targetIndex] == 0xff || inputSlot >= signalInputCounts[targetIndex])
                return false;
        }
        else if (inputSlot != 0) return false;
        if (hasPrevious && (sourceIndex < previousSource ||
            (sourceIndex == previousSource && branch < previousBranch)))
            return false;
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
        if (opcode != customSignalOpcode && opcode != logicalEventSignalOpcode) continue;
        auto branchCount = source[19];
        auto maximumBranchEvents = std::uint64_t{0};
        auto allBranchEvents = std::uint64_t{0};
        auto branchEventCounts = std::array<std::uint64_t, customMaxPorts>{};
        while (routeCursor != 0)
        {
            auto route = data + signalRoutesOffset +
                (routeCursor - 1) * logicalSignalRouteRecordSize;
            auto routeSource = read32(route);
            if (routeSource < nodeIndex) break;
            if (routeSource != nodeIndex) return false;
            auto branch = route[4];
            auto targetIndex = read32(route + 8);
            if (branch >= branchCount || eventCounts[targetIndex] >
                maxLogicalQueueEvents - branchEventCounts[branch])
                return false;
            branchEventCounts[branch] += eventCounts[targetIndex];
            --routeCursor;
        }
        for (auto branch = 0u; branch < branchCount; ++branch)
        {
            auto branchEvents = branchEventCounts[branch];
            maximumBranchEvents = std::max(maximumBranchEvents, branchEvents);
            if (branchEvents > maxLogicalQueueEvents - allBranchEvents) return false;
            allBranchEvents += branchEvents;
        }
        auto childEvents = opcode == customSignalOpcode ? maximumBranchEvents : allBranchEvents;
        if (childEvents >= maxLogicalQueueEvents) return false;
        eventCounts[nodeIndex] = childEvents + 1;
        if (opcode == customSignalOpcode)
            maximumManualEvents = std::max(maximumManualEvents, eventCounts[nodeIndex]);
        if (opcode == logicalEventSignalOpcode &&
            std::memcmp(source + 24, logicalCompareTypeId.data(), 16) == 0)
            generatedEvents += allBranchEvents;
        if (generatedEvents > maxLogicalQueueEvents) return false;
    }
    if (routeCursor != 0) return false;
    auto requiredQueueCapacity = std::max(maximumManualEvents, generatedEvents);
    if (requiredQueueCapacity == 0) requiredQueueCapacity = 1;
    if (requiredQueueCapacity > maxLogicalQueueEvents) return false;
    logicalQueueCapacity = static_cast<std::size_t>(requiredQueueCapacity);
    return true;
}

auto instruction(const std::uint8_t* bytes, std::uint16_t version, std::size_t index) noexcept -> const std::uint8_t*
{
    auto stride = version == 1 ? legacyInstructionSize :
        version == 2 ? typedInstructionSize : customInstructionSize;
    return bytes + (version == 1 ? legacyHeaderSize : typedHeaderSize) + index * stride;
}

auto validStateAlignment(std::size_t alignment) noexcept -> bool
{
    return alignment >= customWorkAlignment && alignment <= maxCustomStateAlignment &&
           (alignment & (alignment - 1)) == 0;
}

auto alignUp(std::size_t value, std::size_t alignment, std::size_t& out) noexcept -> bool
{
    auto padding = alignment - 1;
    if (value > std::numeric_limits<std::size_t>::max() - padding) return false;
    out = (value + padding) & ~padding;
    return true;
}

auto isAligned(const void* pointer, std::size_t alignment) noexcept -> bool
{
    return pointer != nullptr &&
           reinterpret_cast<std::uintptr_t>(pointer) % alignment == 0;
}

auto stateOffset(const CustomNodeBinding* bindings, std::size_t nodeIndex) noexcept -> std::size_t
{
    return bindings[nodeIndex].stateOffset;
}

// Only the Core-owned, explicitly enumerated arithmetic and matrix primitives
// may be re-evaluated after Signal dispatch. A host registry can override a
// builtin UUID, so matching the UUID alone must never establish purity.
auto isLatePureBuiltin(const CustomNodeDescriptor* descriptor) noexcept -> bool
{
    if (descriptor == nullptr || descriptor->abiVersion != 1 ||
        descriptor->execution != CustomExecution::Parameter ||
        descriptor->stateBytes != 0 || descriptor->context != nullptr)
        return false;
    auto count = std::size_t{0};
    auto builtins = builtinNodes(count);
    auto isBuiltin = false;
    for (auto index = std::size_t{0}; index < count; ++index)
        if (descriptor == &builtins[index]) { isBuiltin = true; break; }
    if (!isBuiltin) return false;
    constexpr auto mathPrefix = std::array<std::uint8_t, 15>{
        0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33,
        0x8d, 0x44, 0, 0, 0, 0, 0};
    constexpr auto matrixPrefix = std::array<std::uint8_t, 15>{
        0xb0, 0xaf, 0x61, 0xd2, 0x29, 0xf6, 0x4c, 0xc9,
        0xa7, 0x1e, 0, 0, 0, 0, 0};
    auto id = descriptor->typeId[15];
    return (std::memcmp(descriptor->typeId, mathPrefix.data(), mathPrefix.size()) == 0 &&
            id >= 1 && id <= 47) ||
        (std::memcmp(descriptor->typeId, matrixPrefix.data(), matrixPrefix.size()) == 0 &&
            ((id >= 1 && id <= 13) || (id >= 15 && id <= 18)));
}

auto validIndex(std::uint32_t input, std::uint32_t index) noexcept -> bool
{
    return input == noInput || input < index;
}

auto typedInputWidth(std::uint32_t input, std::uint8_t width) noexcept -> bool
{
    return input == noInput ? width == 0 : width >= 1 && width <= 4;
}

auto validInt32(double value) noexcept -> bool
{
    return std::isfinite(value) && std::trunc(value) == value &&
        value >= std::numeric_limits<std::int32_t>::min() &&
        value <= std::numeric_limits<std::int32_t>::max();
}

auto int32FromDouble(double value, std::int32_t& result) noexcept -> Result
{
    if (!std::isfinite(value)) return Result::NonFinite;
    if (!validInt32(value)) return Result::InvalidValueType;
    result = static_cast<std::int32_t>(value);
    return Result::Ok;
}

auto requiredInputWidths(std::uint8_t opcode, std::uint8_t outputWidth,
                         std::uint8_t& inputAWidth, std::uint8_t& inputBWidth,
                         bool& requiresA, bool& requiresB) noexcept -> bool
{
    requiresA = false;
    requiresB = false;
    if (opcode == int32Opcode)
        return outputWidth == 1 && inputAWidth <= 1 && inputBWidth == 0;
    if (opcode >= int32AddOpcode && opcode <= int32DivideOpcode)
        return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 1;
    if (opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode)
    {
        requiresA = true;
        return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 0;
    }
    if (opcode == 1)
    {
        return outputWidth == 1 && inputAWidth <= 1 && inputBWidth == 0;
    }
    if (opcode == 2) return outputWidth == 1 && inputAWidth == 0 && inputBWidth == 0;
    if (opcode >= 3 && opcode <= 6)
    {
        return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 1;
    }
    if (opcode >= 7 && opcode <= 10)
    {
        auto expected = opcode == 7 ? 2 : opcode == 8 ? 3 : 4;
        return outputWidth == expected && (inputAWidth == 0 || inputAWidth == expected) && inputBWidth == 0;
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
        auto expectedOutput = opcode == 29 || opcode == 34 ? 3 :
                              opcode == 30 || opcode == 32 ? 4 : 2;
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

auto validTypedInstruction(const std::uint8_t* current, std::uint32_t index) noexcept -> bool
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
        if (inputA == noInput) shapeAWidth = 1;
        if (inputB == noInput) shapeBWidth = 1;
    }
    if (opcode >= 11 && opcode <= 22)
    {
        if (inputA == noInput) shapeAWidth = outputWidth;
        if (inputB == noInput) shapeBWidth = outputWidth;
    }
    auto requiresA = false;
    auto requiresB = false;
    if (outputWidth < 1 || outputWidth > 4 ||
        !typedInputWidth(inputA, inputAWidth) || !typedInputWidth(inputB, inputBWidth) ||
        !validIndex(inputA, index) || !validIndex(inputB, index) ||
        !requiredInputWidths(opcode, outputWidth, shapeAWidth, shapeBWidth, requiresA, requiresB) ||
        (requiresA && inputA == noInput) || (requiresB && inputB == noInput) ||
        (typeFlags & 0xf8) != 0 || current[23] != 0 ||
        inputASourceComponent > 3 || inputBSourceComponent > 3 ||
        (inputA == noInput && inputASourceComponent != 0) ||
        (inputB == noInput && inputBSourceComponent != 0))
        return false;
    auto outputIsColor = (typeFlags & 1) != 0;
    auto inputAIsColor = (typeFlags & 2) != 0;
    auto inputBIsColor = (typeFlags & 4) != 0;
    auto expectedInputAColor = opcode == 38 || (opcode == 10 && inputA != noInput);
    if (outputIsColor != (opcode == 10 || opcode == 39) ||
        inputAIsColor != expectedInputAColor || inputBIsColor ||
        (inputA == noInput && inputAIsColor) || (inputB == noInput && inputBIsColor))
        return false;
    for (auto lane = 0u; lane < 4; ++lane)
        if (!std::isfinite(readDouble(current + 32 + lane * 8)) ||
            !std::isfinite(readDouble(current + 64 + lane * 8)))
            return false;
    if (opcode >= 35 && opcode <= 37)
        for (auto lane = 0u; lane < outputWidth; ++lane)
        {
            auto channel = readDouble(current + 32 + lane * 8);
            if (channel < 0 || channel >= 4 || std::floor(channel) != channel) return false;
        }
    if (opcode == 10)
        for (auto lane = 0u; lane < 4; ++lane)
        {
            auto channel = readDouble(current + 32 + lane * 8);
            if (channel < 0 || (lane == 3 && channel > 1)) return false;
        }
    return true;
}

auto valueTypeWidth(std::uint8_t type) noexcept -> std::uint8_t
{
    switch (type)
    {
    case valueTypeFloat:
    case valueTypeInt32:
    case valueTypeEnum: return 1;
    case valueTypeVector2: return 2;
    case valueTypeVector3: return 3;
    case valueTypeVector4:
    case valueTypeColor: return 4;
    default: return 0;
    }
}

auto typeForShape(std::uint8_t width, bool color) noexcept -> std::uint8_t
{
    if (color) return width == 4 ? valueTypeColor : 0;
    switch (width)
    {
    case 1: return valueTypeFloat;
    case 2: return valueTypeVector2;
    case 3: return valueTypeVector3;
    case 4: return valueTypeVector4;
    default: return 0;
    }
}

auto valueTypeRecord(const std::uint8_t* data, std::size_t offset,
                     std::uint32_t index) noexcept -> const std::uint8_t*
{
    return data + offset + valueTypeHeaderSize + std::size_t{index} * valueTypeRecordSize;
}

auto validValueTypeMetadata(const std::uint8_t* data, std::size_t binarySize,
                            std::size_t offset, std::uint32_t instructionCount) noexcept -> bool
{
    if (offset > binarySize || binarySize - offset < valueTypeHeaderSize ||
        std::memcmp(data + offset, "LFTY", 4) != 0 ||
        read16(data + offset + 4) != 1 || read16(data + offset + 6) != valueTypeRecordSize ||
        std::size_t{instructionCount} >
            (binarySize - offset - valueTypeHeaderSize) / valueTypeRecordSize ||
        offset + valueTypeHeaderSize + std::size_t{instructionCount} * valueTypeRecordSize != binarySize)
        return false;

    auto expectedInputs = [&](std::uint8_t opcode, std::uint8_t outputType,
                              std::uint8_t outputWidth, std::uint8_t& count,
                              std::array<std::uint8_t, 4>& types) noexcept -> bool {
        count = 0;
        types = {};
        auto set = [&](std::uint8_t input, std::uint8_t type) noexcept {
            if (input >= types.size() || type == 0) return false;
            types[input] = type;
            count = static_cast<std::uint8_t>(input + 1);
            return true;
        };
        if (opcode == 1 || (opcode >= 7 && opcode <= 10) || opcode == int32Opcode)
            return set(0, outputType);
        if (opcode == 2 || opcode == curveOpcode || opcode == branchTimeOpcode ||
            opcode == currentPhaseOpcode || opcode == phaseTimeOpcode)
            return true;
        if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
            (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))
            return set(0, outputType) && set(1, outputType);
        if (opcode >= 23 && opcode <= 25)
            return set(0, valueTypeFloat);
        if (opcode >= 26 && opcode <= 28)
            return set(0, outputType);
        if (opcode >= 29 && opcode <= 34)
        {
            auto inputType = opcode <= 30 ? valueTypeVector2 :
                opcode <= 32 ? valueTypeVector3 : valueTypeVector4;
            return set(0, inputType);
        }
        if (opcode >= 35 && opcode <= 37)
            return set(0, valueTypeVector4);
        if (opcode == 38) return set(0, valueTypeColor);
        if (opcode == 39) return set(0, valueTypeVector4);
        if (opcode == enumValueOpcode) return set(0, valueTypeEnum);
        if (opcode == enumSelectOpcode) return set(0, valueTypeEnum);
        if (opcode == floatToInt32Opcode) return set(0, valueTypeFloat);
        if (opcode == int32ToFloatOpcode) return set(0, valueTypeInt32);
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
            return false;
        auto isCustom = opcode == customParameterOpcode || opcode == customSignalOpcode;
        auto isLogical = opcode == logicalEventSignalOpcode;
        if (isCustom)
        {
            auto expectedOutput = opcode == customSignalOpcode ? 0 :
                typeForShape(outputWidth, (current[20] & 1) != 0);
            if (outputType != expectedOutput || inputCount != current[18]) return false;
        }
        else if (isLogical)
        {
            if (outputType != typeForShape(outputWidth, false) || inputCount != current[18]) return false;
        }
        else if (opcode == enumSelectOpcode)
        {
            if (valueTypeWidth(outputType) != outputWidth ||
                outputType == valueTypeEnum || outputType == 0 || inputCount != 1 ||
                record[2] != valueTypeEnum)
                return false;
        }
        else
        {
            std::uint8_t requiredCount = 0;
            auto requiredTypes = std::array<std::uint8_t, 4>{};
            if (!expectedInputs(opcode, outputType, outputWidth, requiredCount, requiredTypes) ||
                inputCount != requiredCount)
                return false;
            auto requiredOutput = opcode == 1 || opcode == 2 || (opcode >= 3 && opcode <= 6) ||
                opcode == curveOpcode || opcode == branchTimeOpcode || opcode == phaseTimeOpcode ||
                opcode == int32ToFloatOpcode ? valueTypeFloat :
                opcode == int32Opcode || (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                opcode == floatToInt32Opcode ? valueTypeInt32 :
                isEnumSourceOpcode(opcode) ? valueTypeEnum :
                opcode == 10 ? valueTypeColor :
                opcode == 7 || opcode == 11 || opcode == 12 || opcode == 13 || opcode == 14 ||
                    opcode == 26 || opcode == 33 || opcode == 35 ? valueTypeVector2 :
                opcode == 8 || (opcode >= 15 && opcode <= 18) || opcode == 27 ||
                    opcode == 29 || opcode == 34 || opcode == 36 ? valueTypeVector3 :
                opcode == 9 || (opcode >= 19 && opcode <= 22) || opcode == 28 ||
                    opcode == 30 || opcode == 32 || opcode == 37 || opcode == 38 ? valueTypeVector4 :
                opcode == 39 ? valueTypeColor :
                opcode == 23 ? valueTypeVector2 :
                opcode == 24 ? valueTypeVector3 :
                opcode == 25 ? valueTypeVector4 :
                opcode == 31 ? valueTypeVector2 :
                opcode == 40 ? outputType : 0;
            if (outputType != requiredOutput) return false;
            if (valueTypeWidth(outputType) != outputWidth ||
                (((current[20] & 1) != 0) != (outputType == valueTypeColor)))
                return false;
            for (auto input = 0u; input < requiredCount; ++input)
                if (record[2 + input] != requiredTypes[input]) return false;
        }

        for (auto input = 0u; input < inputCount; ++input)
        {
            auto type = record[2 + input];
            auto width = valueTypeWidth(type);
            if (width == 0) return false;
            if (isCustom || isLogical)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                auto sourceWidth = inputRecord[4];
                auto color = inputRecord[6] != 0;
                if (type != typeForShape(sourceWidth == 0 ? width : sourceWidth, color) ||
                    (isLogical && (type == valueTypeColor || type == valueTypeEnum)))
                    return false;
                if (sourceIndex != noInput)
                {
                    if (sourceIndex >= index) return false;
                    auto sourceOpcode = instruction(data, 10, sourceIndex)[16];
                    auto sourceType = sourceOpcode >= 26 && sourceOpcode <= 28 ? valueTypeFloat :
                        valueTypeRecord(data, offset, sourceIndex)[0];
                    if (sourceType != type) return false;
                }
                auto defaults = current + 80 + input * 32;
                for (auto lane = 0u; lane < width; ++lane)
                {
                    auto value = readDouble(defaults + lane * 8);
                    if (!std::isfinite(value) || (type == valueTypeInt32 && !validInt32(value)))
                        return false;
                }
            }
            else
            {
                auto sourceIndex = input == 0 ? read32(current + 24) :
                    input == 1 ? read32(current + 28) : noInput;
                auto sourceComponent = input == 0 ? current[21] :
                    input == 1 ? current[22] : 0;
                if (sourceIndex != noInput)
                {
                    if (sourceIndex >= index) return false;
                    auto source = instruction(data, 10, sourceIndex);
                    auto sourceType = valueTypeRecord(data, offset, sourceIndex)[0];
                    if (source[16] >= 26 && source[16] <= 28) sourceType = valueTypeFloat;
                    if (sourceType != type) return false;
                }
                auto defaults = current + (input == 0 ? 32 : 64);
                for (auto lane = 0u; lane < width; ++lane)
                {
                    auto value = readDouble(defaults + lane * 8);
                    if (!std::isfinite(value) || (type == valueTypeInt32 && !validInt32(value)))
                        return false;
                }
                if (type == valueTypeEnum && sourceIndex != noInput)
                {
                    auto source = instruction(data, 10, sourceIndex);
                    if (!isEnumSourceOpcode(source[16]) ||
                        std::memcmp(current + 96, source + 96, 16) != 0)
                        return false;
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
            if (record[2 + input] != 0) return false;
    }
    return true;
}

auto validEnumSelectValueTypes(const std::uint8_t* data, std::size_t valueTypeOffset,
                               std::size_t enumMetadataOffset,
                               std::size_t selectRecordsOffset, std::uint16_t selectCount,
                               std::uint32_t instructionCount) noexcept -> bool
{
    for (auto index = 0u; index < selectCount; ++index)
    {
        auto record = data + selectRecordsOffset + std::size_t{index} * enumSelectRecordSize;
        auto nodeIndex = read32(record);
        if (nodeIndex >= instructionCount) return false;
        auto outputType = valueTypeRecord(data, valueTypeOffset, nodeIndex)[0];
        auto defaultSource = read32(record + 8);
        if (defaultSource != noInput &&
            (defaultSource >= nodeIndex ||
             valueTypeRecord(data, valueTypeOffset, defaultSource)[0] != outputType))
            return false;
        auto outputWidth = record[28];
        if (outputType == valueTypeInt32)
            for (auto lane = 0u; lane < outputWidth; ++lane)
                if (!validInt32(readDouble(record + 36 + lane * 8))) return false;

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
                return false;
            if (outputType == valueTypeInt32)
                for (auto lane = 0u; lane < outputWidth; ++lane)
                    if (!validInt32(readDouble(branch + 8 + lane * 8))) return false;
        }
    }
    return true;
}

auto validCurveInstruction(const std::uint8_t* current, std::uint16_t version) noexcept -> bool
{
    if (current[16] != curveOpcode || current[17] != 1 || current[18] != 0 ||
        current[19] != 0 || current[20] != 0 || current[21] != 0 || current[22] != 0 ||
        current[23] != 0 || read32(current + 24) != noInput || read32(current + 28) != noInput)
        return false;
    for (auto lane = 0u; lane < 4; ++lane)
        if (readDouble(current + 32 + lane * 8) != 0 ||
            readDouble(current + 64 + lane * 8) != 0)
            return false;
    for (auto byte = typedInstructionSize; byte < 224; ++byte)
        if (!(version >= 8 && byte >= 112 && byte < 116) && current[byte] != 0) return false;
    for (auto byte = 232u; byte < customInstructionSize; ++byte)
        if (current[byte] != 0) return false;
    return true;
}

auto validCurveKey(const std::uint8_t* key) noexcept -> bool
{
    auto time = readDouble(key);
    auto value = readDouble(key + 8);
    auto inSlope = readDouble(key + 16);
    auto outSlope = readDouble(key + 24);
    if (!std::isfinite(time) || std::abs(time) > 1'000'000 ||
        !std::isfinite(value) || std::abs(value) > 1'000'000 ||
        !std::isfinite(inSlope) || std::abs(inSlope) > 1'000'000 ||
        !std::isfinite(outSlope) || std::abs(outSlope) > 1'000'000 ||
        key[32] > 2)
        return false;
    for (auto byte = 33u; byte < curveKeySize; ++byte)
        if (key[byte] != 0) return false;
    return true;
}

auto evaluateCurve(const std::uint8_t* keys, std::uint32_t count, double time) noexcept -> double
{
    if (count == 0) return 0;
    auto keyAt = [&](std::uint32_t index) noexcept { return keys + std::size_t{index} * curveKeySize; };
    auto first = keyAt(0);
    auto last = keyAt(count - 1);
    if (time <= readDouble(first)) return readDouble(first + 8);
    if (time >= readDouble(last)) return readDouble(last + 8);
    auto low = 0u;
    auto high = count - 1;
    while (high - low > 1)
    {
        auto middle = low + (high - low) / 2;
        if (time <= readDouble(keyAt(middle))) high = middle;
        else low = middle;
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
    case 0: return leftValue;
    case 1: return leftValue + (rightValue - leftValue) * t;
    case 2:
        return (2 * t * t * t - 3 * t * t + 1) * leftValue +
            (t * t * t - 2 * t * t + t) * span * readDouble(left + 24) +
            (-2 * t * t * t + 3 * t * t) * rightValue +
            (t * t * t - t * t) * span * readDouble(right + 16);
    default: return 0;
    }
}

auto rangesOverlap(const void* first, std::size_t firstSize, const void* second,
                   std::size_t secondSize) noexcept -> bool
{
    if (firstSize == 0 || secondSize == 0) return false;
    auto firstStart = reinterpret_cast<std::uintptr_t>(first);
    auto secondStart = reinterpret_cast<std::uintptr_t>(second);
    if (firstSize > std::numeric_limits<std::uintptr_t>::max() - firstStart ||
        secondSize > std::numeric_limits<std::uintptr_t>::max() - secondStart)
        return true;
    return firstStart < secondStart + secondSize && secondStart < firstStart + firstSize;
}

}

auto Program::load(const void* bytes, std::size_t size, Program& out,
                   const ProfilingHooks& profiling) noexcept -> Result
{
    if (!profiling.valid()) return Result::InvalidArgument;
    auto profileScope = ProfileScope(profiling, ProfileEvent::Load);
    if (bytes == nullptr || size < legacyHeaderSize) return Result::InvalidArgument;
    auto data = static_cast<const std::uint8_t*>(bytes);
    if (std::memcmp(data, "LFXB", 4) != 0) return Result::InvalidBinary;
    auto version = read16(data + 4);
    if (version >= 4 && version <= 11)
        return load(bytes, size, nullptr, 0, nullptr, 0, out, profiling);
    if (version != 1 && version != 2) return Result::UnsupportedBinary;
    if (data[6] != 1) return Result::UnsupportedBinary;
    if (data[7] != 0) return Result::InvalidBinary;
    auto count = read32(data + 8);
    auto headerSize = version == 1 ? legacyHeaderSize : typedHeaderSize;
    auto instructionSize = version == 1 ? legacyInstructionSize : typedInstructionSize;
    if (count > maxNodes || size < headerSize) return Result::InvalidBinary;
    if (version == 2 &&
        (read16(data + 12) != typedInstructionSize || read16(data + 14) != 0))
        return Result::UnsupportedBinary;
    if (size != headerSize + std::size_t{count} * instructionSize) return Result::InvalidBinary;
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, version, index);
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte) emptyId = emptyId && current[byte] == 0;
        if (emptyId) return Result::InvalidBinary;
        for (auto other = std::uint32_t{0}; other < index; ++other)
            if (std::memcmp(current, instruction(data, version, other), 16) == 0) return Result::InvalidBinary;
        if (version == 1)
        {
            auto opcode = current[16];
            auto a = read32(current + 20);
            auto b = read32(current + 24);
            if (opcode < 1 || opcode > 6) return Result::UnsupportedBinary;
            if (current[17] != 0 || current[18] != 0 || current[19] != 0 ||
                !validIndex(a, index) || !validIndex(b, index) ||
                !std::isfinite(readDouble(current + 28)) || !std::isfinite(readDouble(current + 36)) ||
                (opcode == 1 && b != noInput) || (opcode == 2 && (a != noInput || b != noInput)))
                return Result::InvalidBinary;
        }
        else
        {
            if (current[16] == 0 || current[16] > 39) return Result::UnsupportedBinary;
            if (!validTypedInstruction(current, index)) return Result::InvalidBinary;
            auto inputA = read32(current + 24);
            auto inputB = read32(current + 28);
            auto validSource = [&](auto sourceIndex, auto sourceWidth, auto selectedLane, auto colorFlag) noexcept {
                if (sourceIndex == noInput) return sourceWidth == 0 && selectedLane == 0 && colorFlag == 0;
                auto source = instruction(data, 2, sourceIndex);
                auto sourceIsBreak = source[16] >= 26 && source[16] <= 28;
                if (sourceIsBreak)
                    return sourceWidth == 1 && selectedLane < source[17] && colorFlag == 0;
                return sourceWidth == source[17] && selectedLane == 0 &&
                       ((source[20] & 1) != 0) == (colorFlag != 0);
            };
            if (!validSource(inputA, current[18], current[21], current[20] & 2) ||
                !validSource(inputB, current[19], current[22], current[20] & 4))
                return Result::InvalidBinary;
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
                   std::size_t registryCount, CustomNodeBinding* bindings,
                   std::size_t bindingCount, Program& out, const ProfilingHooks& profiling) noexcept -> Result
{
    if (!profiling.valid()) return Result::InvalidArgument;
    if (bytes == nullptr || size < legacyHeaderSize) return Result::InvalidArgument;
    auto data = static_cast<const std::uint8_t*>(bytes);
    if (std::memcmp(data, "LFXB", 4) != 0) return Result::InvalidBinary;
    auto version = read16(data + 4);
    if (version < 3 || version > 11) return load(bytes, size, out, profiling);
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
    if (size < typedHeaderSize) return Result::InvalidBinary;
    if (data[6] != 1) return Result::UnsupportedBinary;
    if (data[7] != 0 || read16(data + 12) != customInstructionSize || read16(data + 14) != 0)
        return Result::InvalidBinary;
    auto count = read32(data + 8);
    if (count > maxNodes) return Result::InvalidBinary;
    auto binarySize = size;
    auto valueTypeMetadataOffset = std::size_t{0};
    if (version >= 10)
    {
        auto valueTypeMetadataSize = valueTypeHeaderSize +
            std::size_t{count} * valueTypeRecordSize;
        if (binarySize < valueTypeMetadataSize) return Result::InvalidBinary;
        valueTypeMetadataOffset = binarySize - valueTypeMetadataSize;
        if (std::memcmp(data + valueTypeMetadataOffset, "LFTY", 4) != 0 ||
            read16(data + valueTypeMetadataOffset + 4) != 1 ||
            read16(data + valueTypeMetadataOffset + 6) != valueTypeRecordSize)
            return Result::InvalidBinary;
        size = valueTypeMetadataOffset;
    }
    auto instructionBytes = typedHeaderSize + std::size_t{count} * customInstructionSize;
    if (size < instructionBytes ||
        (version == 3 && size != instructionBytes))
        return Result::InvalidBinary;
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
        return Result::InvalidArgument;
    for (auto index = std::size_t{0}; index < registryCount; ++index)
    {
        const auto& descriptor = registry[index];
        if (descriptor.abiVersion == 0 || descriptor.function == nullptr ||
            descriptor.inputCount > customMaxPorts || !validStateAlignment(descriptor.stateAlignment) ||
            descriptor.stateBytes > (std::numeric_limits<std::size_t>::max() - descriptor.stateAlignment + 1))
            return Result::InvalidArgument;
        if ((descriptor.execution == CustomExecution::Parameter &&
             (descriptor.outputWidth < 1 || descriptor.outputWidth > 4 || descriptor.branchCount != 0)) ||
            (descriptor.execution == CustomExecution::Signal &&
             (descriptor.outputWidth != 0 || descriptor.outputIsColor ||
              descriptor.branchCount > customMaxPorts)) ||
            (descriptor.execution != CustomExecution::Parameter &&
             descriptor.execution != CustomExecution::Signal) ||
            (descriptor.outputIsColor && descriptor.outputWidth != 4))
            return Result::InvalidArgument;
        for (auto input = 0u; input < descriptor.inputCount; ++input)
            if (descriptor.inputWidths[input] < 1 || descriptor.inputWidths[input] > 4 ||
                descriptor.inputIsColor[input] > 1 ||
                (descriptor.inputIsColor[input] != 0 && descriptor.inputWidths[input] != 4))
                return Result::InvalidArgument;
        for (auto other = std::size_t{0}; other < index; ++other)
            if (std::memcmp(descriptor.typeId, registry[other].typeId, 16) == 0 &&
                descriptor.abiVersion == registry[other].abiVersion)
                return Result::InvalidArgument;
    }
    auto logicalDescriptorCount = std::size_t{0};
    auto logicalDescriptors = logicalNodes(logicalDescriptorCount);
    for (auto index = std::size_t{0}; index < logicalDescriptorCount; ++index)
    {
        if (!validLogicalDescriptor(logicalDescriptors[index])) return Result::InvalidArgument;
        for (auto other = std::size_t{0}; other < index; ++other)
            if (std::memcmp(logicalDescriptors[index].typeId, logicalDescriptors[other].typeId, 16) == 0)
                return Result::InvalidArgument;
    }

    auto resolveDescriptor = [&](const std::uint8_t* typeId, std::uint32_t abiVersion,
                                 bool& registeredType) noexcept -> const CustomNodeDescriptor* {
        registeredType = false;
        for (auto index = std::size_t{0}; index < registryCount; ++index)
            if (std::memcmp(registry[index].typeId, typeId, 16) == 0)
            {
                registeredType = true;
                if (registry[index].abiVersion == abiVersion) return &registry[index];
            }
        if (registeredType) return nullptr;
        auto builtinCount = std::size_t{0};
        auto builtins = builtinNodes(builtinCount);
        for (auto index = std::size_t{0}; index < builtinCount; ++index)
            if (std::memcmp(builtins[index].typeId, typeId, 16) == 0)
            {
                registeredType = true;
                if (builtins[index].abiVersion == abiVersion) return &builtins[index];
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
        if (current[16] != curveOpcode) continue;
        auto keyCount = read32(current + 228);
        if (keyCount > maxCurveKeysPerInstruction ||
            std::size_t{keyCount} > (size - instructionBytes - curveDataBytes) / curveKeySize)
            return Result::InvalidBinary;
        curveDataBytes += std::size_t{keyCount} * curveKeySize;
    }
    auto expectedCurveOffset = std::size_t{0};
    auto totalCurveKeys = std::uint32_t{0};
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, 3, index);
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte) emptyId = emptyId && current[byte] == 0;
        if (emptyId) return Result::InvalidBinary;
        for (auto other = std::uint32_t{0}; other < index; ++other)
            if (std::memcmp(current, instruction(data, 3, other), 16) == 0) return Result::InvalidBinary;
        auto opcode = current[16];
        if (opcode == curveOpcode)
        {
            if (version < 4 || !validCurveInstruction(current, version)) return Result::InvalidBinary;
            auto curveOffset = read32(current + 224);
            auto curveKeyCount = read32(current + 228);
            if (curveOffset != expectedCurveOffset || curveKeyCount > maxCurveKeysPerInstruction ||
                curveKeyCount > maxCurveKeysTotal - totalCurveKeys ||
                std::size_t{curveKeyCount} > (curveDataBytes - expectedCurveOffset) / curveKeySize)
                return Result::InvalidBinary;
            auto keys = data + instructionBytes + curveOffset;
            auto previousTime = -std::numeric_limits<double>::infinity();
            for (auto keyIndex = 0u; keyIndex < curveKeyCount; ++keyIndex)
            {
                auto key = keys + std::size_t{keyIndex} * curveKeySize;
                auto keyTime = readDouble(key);
                if (!validCurveKey(key) || keyTime <= previousTime)
                    return Result::InvalidBinary;
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
            if (!validTypedInstruction(current, index)) return Result::InvalidBinary;
            auto inputA = read32(current + 24);
            auto inputB = read32(current + 28);
            auto validSource = [&](auto sourceIndex, auto sourceWidth, auto selectedLane, auto colorFlag) noexcept {
                if (sourceIndex == noInput) return sourceWidth == 0 && selectedLane == 0 && colorFlag == 0;
                auto source = instruction(data, 3, sourceIndex);
                auto sourceIsBreak = source[16] >= 26 && source[16] <= 28;
                return sourceIsBreak ? sourceWidth == 1 && selectedLane < source[17] && colorFlag == 0 :
                    sourceWidth == source[17] && selectedLane == 0 &&
                    ((source[20] & 1) != 0) == (colorFlag != 0);
            };
            if (!validSource(inputA, current[18], current[21], current[20] & 2) ||
                !validSource(inputB, current[19], current[22], current[20] & 4))
                return Result::InvalidBinary;
            for (auto byte = typedInstructionSize; byte < customInstructionSize; ++byte)
                if (!(version >= 8 && byte >= 112 && byte < 116) && current[byte] != 0)
                    return Result::InvalidBinary;
            continue;
        }
        if (opcode == currentPhaseOpcode || opcode == phaseTimeOpcode)
        {
            if (version < 11 || current[17] != 1 || current[18] != 0 || current[19] != 0 ||
                current[20] != 0 || current[21] != 0 || current[22] != 0 || current[23] != 0 ||
                read32(current + 24) != noInput || read32(current + 28) != noInput)
                return Result::InvalidBinary;
            constexpr auto phaseEnum = std::array<std::uint8_t, 16>{
                0x4a, 0x94, 0xe8, 0x82, 0x39, 0xe8, 0x4e, 0xfb,
                0x92, 0xc7, 0x18, 0, 0, 0, 0, 0x30};
            for (auto byte = 32u; byte < customInstructionSize; ++byte)
            {
                if (byte >= 112 && byte < 116) continue;
                auto expected = opcode == currentPhaseOpcode && byte >= 96 && byte < 112
                    ? phaseEnum[byte - 96] : std::uint8_t{};
                if (current[byte] != expected) return Result::InvalidBinary;
            }
            continue;
        }
        if (opcode == enumValueOpcode || opcode == enumSelectOpcode || opcode == branchTimeOpcode)
        {
            if (version < 8 || current[18] > 1 || current[19] != 0 || current[20] != 0 ||
                current[21] != 0 || current[22] != 0 || current[23] != 0 ||
                read32(current + 28) != noInput ||
                !validIndex(read32(current + 24), index))
                return Result::InvalidBinary;
            auto inputA = read32(current + 24);
            if (opcode == enumValueOpcode)
            {
                if (current[17] != 1 || (inputA == noInput ? current[18] != 0 : current[18] != 1) ||
                    !std::isfinite(readDouble(current + 32)) ||
                    std::trunc(readDouble(current + 32)) != readDouble(current + 32) ||
                    readDouble(current + 32) < std::numeric_limits<std::int32_t>::min() ||
                    readDouble(current + 32) > std::numeric_limits<std::int32_t>::max())
                    return Result::InvalidBinary;
            }
            else if (opcode == enumSelectOpcode)
            {
                if (current[17] < 1 || current[17] > 4 || current[18] != 1 ||
                    inputA == noInput || !isEnumSourceOpcode(instruction(data, 3, inputA)[16]))
                    return Result::InvalidBinary;
            }
            else if (current[17] != 1 || current[18] != 0 || inputA != noInput ||
                     read32(current + 112) == noInput)
                return Result::InvalidBinary;
            auto typeIsEmpty = true;
            for (auto byte = 0u; byte < 16; ++byte)
                typeIsEmpty = typeIsEmpty && current[96 + byte] == 0;
            if ((opcode == branchTimeOpcode) != typeIsEmpty) return Result::InvalidBinary;
            for (auto byte = 116u; byte < customInstructionSize; ++byte)
                if (current[byte] != 0) return Result::InvalidBinary;
            continue;
        }
        if (opcode == logicalEventSignalOpcode)
        {
            if (version < 9 || current[18] > customMaxPorts || current[19] > customMaxPorts ||
                current[20] != 0 || current[21] != 0 || current[22] != 0 || current[23] != 0 ||
                (version < 11 && read32(current + 44) != 0) || read32(current + 40) == 0)
                return Result::InvalidBinary;
            auto registeredType = false;
            auto descriptor = findLogicalDescriptor(current + 24, read32(current + 40), registeredType);
            if (descriptor == nullptr)
                return registeredType ? Result::UnsupportedBinary : Result::UnknownCustomNode;
            if (!validLogicalDescriptor(*descriptor) || descriptor->inputCount != current[18] ||
                descriptor->outputWidth != current[17] || descriptor->signalBranchCount != current[19])
                return Result::UnsupportedBinary;
            if (std::memcmp(current + 24, logicalSignalCaptureTypeId.data(), 16) == 0)
            {
                if (version < 11) return Result::UnsupportedBinary;
                needsSignalPayloads = true;
            }
            auto alignedStateOffset = std::size_t{0};
            if (!alignUp(totalWorkBytes, descriptor->stateAlignment, alignedStateOffset) ||
                descriptor->stateBytes > std::numeric_limits<std::size_t>::max() - alignedStateOffset)
                return Result::InvalidBinary;
            totalWorkBytes = alignedStateOffset + descriptor->stateBytes;
            for (auto input = 0u; input < customMaxPorts; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                auto sourceWidth = inputRecord[4];
                auto selectedLane = inputRecord[5];
                if (input < descriptor->inputCount)
                {
                    if (!validIndex(sourceIndex, index) || inputRecord[6] != 0 || inputRecord[7] != 0 ||
                        (sourceIndex == noInput ? sourceWidth != 0 || selectedLane != 0 :
                            sourceWidth != descriptor->inputWidths[input]))
                        return Result::InvalidBinary;
                    if (sourceIndex != noInput)
                    {
                        auto source = instruction(data, 3, sourceIndex);
                        auto sourceWidthActual = source[16] == customParameterOpcode ||
                            source[16] == logicalEventSignalOpcode ? source[17] :
                            source[16] >= 26 && source[16] <= 28 ? 1 : source[17];
                        if (source[16] == customSignalOpcode ||
                            sourceWidth != sourceWidthActual || selectedLane != 0)
                            return Result::InvalidBinary;
                    }
                }
                else if (sourceIndex != noInput || sourceWidth != 0 || selectedLane != 0 ||
                         inputRecord[6] != 0 || inputRecord[7] != 0)
                    return Result::InvalidBinary;
                for (auto lane = 0u; lane < 4; ++lane)
                    if (!std::isfinite(readDouble(current + 80 + input * 32 + lane * 8)))
                        return Result::InvalidBinary;
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
                        return Result::InvalidBinary;
                }
                else if (target != noInput) return Result::InvalidBinary;
            }
            if (version < 11)
                for (auto byte = 116u; byte < 208; ++byte)
                    if (current[byte] != 0) return Result::InvalidBinary;
            if (version < 5)
                for (auto byte = 224u; byte < customInstructionSize; ++byte)
                    if (current[byte] != 0) return Result::InvalidBinary;
            continue;
        }
        if (opcode != customParameterOpcode && opcode != customSignalOpcode)
            return Result::UnsupportedBinary;
        auto inputCount = current[18];
        auto branchCount = current[19];
        auto outputWidth = current[17];
        if (inputCount > customMaxPorts || (current[20] & 0xfe) != 0 || current[21] != 0 ||
            current[22] != 0 || current[23] != 0 || (version < 11 && read32(current + 44) != 0) ||
            read32(current + 40) == 0 ||
            (opcode == customParameterOpcode && (outputWidth < 1 || outputWidth > 4 || branchCount != 0)) ||
            (opcode == customSignalOpcode && (outputWidth != 0 || branchCount > customMaxPorts ||
                                               current[20] != 0)))
            return Result::InvalidBinary;
        if (version < 5 || opcode != customSignalOpcode)
            for (auto byte = 224u; byte < customInstructionSize; ++byte)
                if (current[byte] != 0) return Result::InvalidBinary;
        auto emptyTypeId = true;
        for (auto byte = 0u; byte < 16; ++byte) emptyTypeId = emptyTypeId && current[24 + byte] == 0;
        if (emptyTypeId) return Result::InvalidBinary;
        auto registeredType = false;
        auto descriptor = resolveDescriptor(current + 24, read32(current + 40), registeredType);
        if (descriptor == nullptr)
            return registeredType ? Result::UnsupportedBinary : Result::UnknownCustomNode;
        if (descriptor->inputCount != inputCount ||
            descriptor->execution != (opcode == customParameterOpcode ?
                CustomExecution::Parameter : CustomExecution::Signal) ||
            descriptor->outputWidth != outputWidth || descriptor->branchCount != branchCount ||
            descriptor->outputIsColor != ((current[20] & 1) != 0))
            return Result::UnsupportedBinary;
        auto alignedStateOffset = std::size_t{0};
        if (!validStateAlignment(descriptor->stateAlignment) ||
            !alignUp(totalWorkBytes, descriptor->stateAlignment, alignedStateOffset) ||
            descriptor->stateBytes > std::numeric_limits<std::size_t>::max() - alignedStateOffset)
            return Result::InvalidBinary;
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
                    (sourceIndex == noInput ? sourceWidth != 0 || selectedLane != 0 :
                     sourceWidth != descriptor->inputWidths[input]))
                    return Result::InvalidBinary;
                if (sourceIndex != noInput)
                {
                    auto source = instruction(data, 3, sourceIndex);
                    auto sourceWidthActual = source[16] == customParameterOpcode ? source[17] :
                        source[16] >= 26 && source[16] <= 28 ? 1 : source[17];
                    auto sourceColor = source[16] == customParameterOpcode ? (source[20] & 1) != 0 :
                        source[16] >= 1 && source[16] <= 39 ? (source[20] & 1) != 0 : false;
                    if (source[16] == customSignalOpcode || sourceWidth != sourceWidthActual ||
                        (sourceColor != (inputRecord[6] != 0)) ||
                        (source[16] >= 26 && source[16] <= 28 ?
                            selectedLane >= source[17] : selectedLane != 0))
                        return Result::InvalidBinary;
                }
            }
            else if (sourceIndex != noInput || sourceWidth != 0 || selectedLane != 0 ||
                     inputRecord[6] != 0 || inputRecord[7] != 0)
                return Result::InvalidBinary;
            for (auto lane = 0u; lane < 4; ++lane)
            {
                auto component = readDouble(current + 80 + input * 32 + lane * 8);
                if (!std::isfinite(component) ||
                    (input < inputCount && descriptor->inputIsColor[input] != 0 &&
                     lane < 4 && (component < 0 || (lane == 3 && component > 1))))
                    return Result::InvalidBinary;
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
                    return Result::InvalidBinary;
            }
            else if (target != noInput) return Result::InvalidBinary;
        }
    }
    if (expectedCurveOffset != curveDataBytes) return Result::InvalidBinary;
    auto routeDataOffset = instructionBytes + expectedCurveOffset;
    auto routeBytes = std::size_t{0};
    if (version >= 5)
    {
        for (auto index = std::uint32_t{0}; index < count; ++index)
        {
            auto current = instruction(data, version, index);
            if (current[16] != customSignalOpcode && current[16] != logicalEventSignalOpcode) continue;
            for (auto branch = 0u; branch < customMaxPorts; ++branch)
            {
                auto offset = read32(current + 224 + branch * 8);
                auto extraCount = read32(current + 228 + branch * 8);
                if (extraCount == 0)
                {
                    if (offset != 0) return Result::InvalidBinary;
                    continue;
                }
                if (branch >= current[19] || read32(current + 208 + branch * 4) == noInput ||
                        offset != routeBytes || extraCount > maxSignalRoutes - routeBytes / 4 ||
                        routeBytes > size - routeDataOffset ||
                        extraCount > (size - routeDataOffset - routeBytes) / 4)
                    return Result::InvalidBinary;
                for (auto entry = 0u; entry < extraCount; ++entry)
                {
                    auto target = read32(data + routeDataOffset + routeBytes + std::size_t{entry} * 4);
                    if (target <= index || target >= count ||
                        (instruction(data, version, target)[16] != customSignalOpcode &&
                         instruction(data, version, target)[16] != logicalEventSignalOpcode))
                        return Result::InvalidBinary;
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
            if (!validEnumBranchMetadata(data, size, enumMetadataOffset, version, count, enumMetadataEnd,
                    enumSelectRecordsOffset, enumGuardRecordsOffset, enumGuardValuesOffset,
                    enumValueCount, enumSelectCount, enumGuardCount))
                return Result::InvalidBinary;
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
                        return Result::InvalidBinary;
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
                        logicalMetadataEnd, logicalRecordsOffset, logicalRoutesOffset,
                        logicalInstructionCount, logicalRouteCount, validatedQueueCapacity) ||
                    logicalMetadataEnd != size)
                    return Result::InvalidBinary;
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
                        current[16] != logicalEventSignalOpcode) continue;
                    for (auto branch = 0u; branch < current[19]; ++branch)
                    {
                        auto primaryTarget = read32(current + 208 + branch * 4);
                        auto extraOffset = read32(current + 224 + branch * 8);
                        auto extraCount = read32(current + 228 + branch * 8);
                        if ((primaryTarget == noInput && extraCount != 0) ||
                            (primaryTarget != noInput &&
                                (routeCursor >= logicalRouteCount ||
                                 read32(data + logicalRoutesOffset +
                                     std::size_t{routeCursor} * logicalSignalRouteRecordSize) != sourceIndex ||
                                 data[logicalRoutesOffset +
                                     std::size_t{routeCursor} * logicalSignalRouteRecordSize + 4] != branch ||
                                 read32(data + logicalRoutesOffset +
                                     std::size_t{routeCursor} * logicalSignalRouteRecordSize + 8) != primaryTarget)))
                            return Result::InvalidBinary;
                        if (primaryTarget != noInput) ++routeCursor;
                        for (auto extra = 0u; extra < extraCount; ++extra)
                        {
                            auto target = read32(data + routeDataOffset + extraOffset +
                                std::size_t{extra} * sizeof(std::uint32_t));
                            auto route = data + logicalRoutesOffset +
                                std::size_t{routeCursor} * logicalSignalRouteRecordSize;
                            if (routeCursor >= logicalRouteCount || read32(route) != sourceIndex ||
                                route[4] != branch || read32(route + 8) != target)
                                return Result::InvalidBinary;
                            ++routeCursor;
                        }
                    }
                }
                if (routeCursor != logicalRouteCount)
                    return Result::InvalidBinary;
            }
            else
            {
                if (enumMetadataEnd < size)
                {
                    auto gameIoEnd = std::size_t{0};
                    if (!validGameIoMetadata(data, size, enumMetadataEnd, count, version, gameIoEnd))
                        return Result::InvalidBinary;
                    out.gameIoMetadataOffset_ = enumMetadataEnd;
                    out.gameIoPortCount_ = read16(data + enumMetadataEnd + 4);
                }
            }
        }
        else
        {
            if (version == 5 && routeBytes != size - routeDataOffset) return Result::InvalidBinary;
            if (version >= 6)
            {
                auto gameIoEnd = std::size_t{0};
                auto gameIoOffset = routeDataOffset + routeBytes;
                if (!validGameIoMetadata(data, size, gameIoOffset, count, version, gameIoEnd))
                    return Result::InvalidBinary;
                out.gameIoMetadataOffset_ = gameIoOffset;
                out.gameIoPortCount_ = read16(data + gameIoOffset + 4);
            }
        }
    }
    if (version < 5 && routeDataOffset != size)
        return Result::InvalidBinary;
    if (version >= 10 &&
        (!validValueTypeMetadata(data, binarySize, valueTypeMetadataOffset, count) ||
         !validEnumSelectValueTypes(data, valueTypeMetadataOffset, out.enumBranchMetadataOffset_,
             out.enumSelectRecordsOffset_, out.enumSelectCount_, count)))
        return Result::InvalidBinary;
    auto stackOffset = totalWorkBytes;
    auto logicalQueueCapacity = std::size_t{0};
    if (version >= 9)
    {
        logicalQueueCapacity = out.logicalQueueCapacity_;
        if (logicalQueueCapacity > std::numeric_limits<std::size_t>::max() /
                sizeof(LogicalQueuedSignal) ||
            !alignUp(totalWorkBytes, alignof(LogicalQueuedSignal), stackOffset) ||
            logicalQueueCapacity * sizeof(LogicalQueuedSignal) >
                std::numeric_limits<std::size_t>::max() - stackOffset)
            return Result::InvalidBinary;
        totalWorkBytes = stackOffset + logicalQueueCapacity * sizeof(LogicalQueuedSignal);
        if (needsSignalPayloads)
        {
            auto payloadOffset = std::size_t{};
            if (!alignUp(totalWorkBytes, alignof(SignalPayload), payloadOffset) ||
                logicalQueueCapacity > (std::numeric_limits<std::size_t>::max() - payloadOffset) /
                    sizeof(SignalPayload))
                return Result::InvalidBinary;
            out.signalPayloadQueueOffset_ = payloadOffset;
            totalWorkBytes = payloadOffset + logicalQueueCapacity * sizeof(SignalPayload);
        }
    }
    else if (routeBytes != 0)
    {
        if (!alignUp(totalWorkBytes, alignof(double), stackOffset) ||
            routeBytes > std::numeric_limits<std::size_t>::max() - stackOffset)
            return Result::InvalidBinary;
        if (!alignUp(stackOffset + routeBytes, alignof(double), totalWorkBytes))
            return Result::InvalidBinary;
    }
    auto branchClockOffset = std::size_t{0};
    if (out.branchGuardCount_ != 0)
    {
        if (!alignUp(totalWorkBytes, alignof(double), branchClockOffset) ||
            std::size_t{out.branchGuardCount_} >
                (std::numeric_limits<std::size_t>::max() - branchClockOffset) / 24)
            return Result::InvalidBinary;
        totalWorkBytes = branchClockOffset + std::size_t{out.branchGuardCount_} * 24;
    }
    auto logicalEventMaskOffset = std::size_t{0};
    auto logicalDirtyOffset = std::size_t{0};
    if (version >= 9)
    {
        if (!alignUp(totalWorkBytes, alignof(std::uint64_t), logicalEventMaskOffset) ||
            std::size_t{count} > (std::numeric_limits<std::size_t>::max() - logicalEventMaskOffset) /
                sizeof(std::uint64_t))
            return Result::InvalidBinary;
        totalWorkBytes = logicalEventMaskOffset + std::size_t{count} * sizeof(std::uint64_t);
        logicalDirtyOffset = totalWorkBytes;
        if (std::size_t{count} > std::numeric_limits<std::size_t>::max() - totalWorkBytes)
            return Result::InvalidBinary;
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
        }
        else if (current[16] == logicalEventSignalOpcode)
        {
            auto registeredType = false;
            logicalDescriptor = findLogicalDescriptor(current + 24, read32(current + 40), registeredType);
        }
        auto stateAlignment = descriptor != nullptr ? descriptor->stateAlignment :
            logicalDescriptor != nullptr ? logicalDescriptor->stateAlignment : 0;
        auto stateSize = descriptor != nullptr ? descriptor->stateBytes :
            logicalDescriptor != nullptr ? logicalDescriptor->stateBytes : 0;
        if (stateAlignment != 0)
        {
            auto alignedStateOffset = std::size_t{0};
            if (!alignUp(boundStateOffset, stateAlignment, alignedStateOffset))
                return Result::InvalidBinary;
            boundStateOffset = alignedStateOffset;
        }
        if (bindings != nullptr)
        {
            bindings[index].descriptor = descriptor;
            bindings[index].stateOffset = boundStateOffset;
            bindings[index].logicalDescriptor = logicalDescriptor;
        }
        if (stateSize != 0) boundStateOffset += stateSize;
    }
    out.bytes_ = data;
    out.nodeCount_ = count;
    out.version_ = version;
    out.bindings_ = bindings;
    out.stateBytes_ = totalWorkBytes - valuesSize;
    out.signalRoutesOffset_ = routeDataOffset;
    out.signalStackOffset_ = stackOffset;
    out.signalStackCapacity_ = version >= 9 ? logicalQueueCapacity : routeBytes / 4;
    out.gameIoPortCount_ = version >= 6 ?
        (out.gameIoMetadataOffset_ != 0 ? read16(data + out.gameIoMetadataOffset_ + 4) : 0) : 0;
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
    if (index >= gameIoPortCount_ || gameIoMetadataOffset_ == 0) return false;
    auto cursor = gameIoMetadataOffset_ + 8;
    auto recordSize = version_ == 6 ? std::size_t{24} : std::size_t{72};
    for (auto current = std::size_t{}; current < index; ++current)
        cursor += recordSize + read16(bytes_ + cursor + (version_ == 6 ? 20 : 68)) + 1;
    auto record = bytes_ + cursor;
    auto nameLength = read16(record + (version_ == 6 ? 20 : 68));
    out.name = reinterpret_cast<const char*>(record + recordSize);
    std::memcpy(out.nodeId, record, 16);
    out.kind = version_ == 6 ? record[16] : record[64];
    out.width = version_ == 6 ? record[17] : record[65];
    out.isColor = version_ == 6 ? record[18] : record[66];
    out.nodeCount = version_ == 6 ? 1 : record[67];
    if (version_ >= 7)
        for (auto row = 1u; row < out.nodeCount; ++row)
            std::memcpy(out.additionalNodeIds[row - 1], record + row * 16, 16);
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
        for (auto index = std::size_t{0}; index < nodeCount_; ++index)
        {
            if (bindings_[index].descriptor != nullptr)
                alignment = std::max(alignment, bindings_[index].descriptor->stateAlignment);
            if (bindings_[index].logicalDescriptor != nullptr)
                alignment = std::max(alignment, bindings_[index].logicalDescriptor->stateAlignment);
        }
    return alignment;
}

auto Program::parameterValueCount() const noexcept -> std::size_t
{
    return std::size_t{nodeCount_} * (version_ == 1 ? 2 : version_ == 2 ? 8 : 16);
}

auto Program::valueType(std::size_t instructionIndex, ValueType& out) const noexcept -> Result
{
    if (bytes_ == nullptr || instructionIndex >= nodeCount_) return Result::InvalidArgument;
    if (version_ >= 10)
    {
        auto type = valueTypeRecord(bytes_, valueTypeMetadataOffset_,
            static_cast<std::uint32_t>(instructionIndex))[0];
        if (type > valueTypeEnum) return Result::InvalidBinary;
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
        if (type == 0) return Result::InvalidValueType;
        out = static_cast<ValueType>(type);
        return Result::Ok;
    }
    if (version_ == 1)
    {
        if (opcode < 1 || opcode > 6) return Result::InvalidValueType;
        out = ValueType::Float;
        return Result::Ok;
    }
    auto type = typeForShape(current[17], (current[20] & 1) != 0);
    if (type == 0) return Result::InvalidValueType;
    out = static_cast<ValueType>(type);
    return Result::Ok;
}

auto Program::defaultsAlignment() const noexcept -> std::size_t
{
    return alignof(double);
}

auto Instance::initialize(const Program& program, double* work, std::size_t workCount) noexcept -> Result
{
    return initialize(program, work, workCount, nullptr, 0);
}

auto Instance::initialize(const Program& program, double* work, std::size_t workCount,
                          double* defaults, std::size_t defaultsCount) noexcept -> Result
{
    if (dispatching_ || debugging_.beforeNode != nullptr) return Result::Busy;
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Initialize);
    auto workBytes = program.workBytes();
    auto workRequiredCount = workBytes / sizeof(double) +
        (workBytes % sizeof(double) == 0 ? 0 : 1);
    auto defaultsRequiredCount = program.parameterValueCount();
    if (program.bytes_ == nullptr || (workRequiredCount > 0 && work == nullptr))
        return Result::InvalidArgument;
    if (workCount < workRequiredCount || (defaults != nullptr && defaultsCount < defaultsRequiredCount))
        return Result::InsufficientWork;
    if (defaults == nullptr && defaultsCount != 0) return Result::InvalidArgument;
    if (workRequiredCount != 0 && !isAligned(work, program.workAlignment()))
        return Result::InvalidArgument;
    if (defaults != nullptr && !isAligned(defaults, program.defaultsAlignment()))
        return Result::InvalidArgument;
    if (defaults != nullptr &&
        rangesOverlap(work, workRequiredCount * sizeof(double), defaults, defaultsRequiredCount * sizeof(double)))
        return Result::InvalidArgument;
    if (defaults != nullptr)
        for (auto index = std::uint32_t{0}; index < program.nodeCount_; ++index)
        {
            auto current = instruction(program.bytes_, program.version_, index);
            if (program.version_ == 1)
            {
                defaults[index * 2] = readDouble(current + 28);
                defaults[index * 2 + 1] = readDouble(current + 36);
            }
            else if (program.version_ == 2 || current[16] <= 39 || current[16] == curveOpcode ||
                     (current[16] >= int32Opcode && current[16] <= int32ToFloatOpcode))
                for (auto lane = 0u; lane < 8; ++lane)
                    defaults[index * (program.version_ >= 3 ? 16 : 8) + lane] =
                        readDouble(current + (lane < 4 ? 32 + lane * 8 : 64 + (lane - 4) * 8));
            else if (current[16] == enumValueOpcode || current[16] == enumSelectOpcode)
            {
                for (auto lane = 0u; lane < 4; ++lane)
                {
                    defaults[index * 16 + lane] = readDouble(current + 32 + lane * 8);
                    defaults[index * 16 + 4 + lane] = readDouble(current + 64 + lane * 8);
                }
            }
            else
                for (auto input = 0u; input < customMaxPorts; ++input)
                    for (auto lane = 0u; lane < 4; ++lane)
                        defaults[index * 16 + input * 4 + lane] =
                            readDouble(current + 80 + input * 32 + lane * 8);
        }
    cleanupStates();
    program_ = &program;
    work_ = work;
    defaults_ = defaults;
    stateWork_ = work == nullptr ? nullptr : reinterpret_cast<double*>(
        reinterpret_cast<std::uint8_t*>(work) +
        std::size_t{program.nodeCount_} * (program.version_ == 1 ? sizeof(double) : 4 * sizeof(double)));
    if (program.version_ >= 3 && program.stateBytes_ != 0)
        std::memset(stateWork_, 0, program.stateBytes_);
    initializedStateCount_ = 0;
    updateSerial_ = 0;
    evaluationSerial_ = 0;
    pendingEvaluationSerial_ = 0;
    if (program.version_ >= 3 && program.bindings_ != nullptr)
        for (auto index = std::size_t{0}; index < program.nodeCount_; ++index)
        {
            auto descriptor = program.bindings_[index].descriptor;
            if (descriptor == nullptr) continue;
            auto state = reinterpret_cast<std::uint8_t*>(work) + stateOffset(program.bindings_, index);
            if (descriptor->initialize != nullptr)
            {
                auto status = descriptor->initialize(state, descriptor->stateBytes, descriptor->context);
                if (status != Result::Ok)
                {
                    if (descriptor->cleanup != nullptr)
                        descriptor->cleanup(state, descriptor->stateBytes, descriptor->context);
                    cleanupStates();
                    program_ = nullptr;
                    work_ = nullptr;
                    defaults_ = nullptr;
                    stateWork_ = nullptr;
                    result_ = Result::NotReady;
                    phase_.store(Phase::Idle, std::memory_order_release);
                    return status;
                }
            }
            initializedStateCount_ = index + 1;
        }
    generation_ = nextInstanceGeneration.fetch_add(1, std::memory_order_relaxed);
    deferredSignals_ = nullptr;
    deferredCapacity_ = 0;
    deferredCount_ = 0;
    logicalQueueHead_ = 0;
    logicalQueueCount_ = 0;
    generatedQueueSeeded_ = false;
    generatedSignalsPending_ = false;
    forceRequestsEnabled_ = false;
    forceScope_ = 0;
    currentPhase_ = pendingPhase_ = 0;
    phaseStartTime_ = pendingPhaseTime_ = 0;
    quality_ = {};
    pendingTimeContext_ = false;
    evaluatedTimeContext_ = false;
    result_ = Result::NotReady;
    phase_.store(Phase::Idle, std::memory_order_release);
    return Result::Ok;
}

Instance::~Instance()
{
    cleanupStates();
}

auto Instance::setProfiling(const ProfilingHooks& profiling) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!profiling.valid()) return Result::InvalidArgument;
    profiling_ = profiling;
    return Result::Ok;
}

auto Instance::setDebugHooks(const ExecutionDebugHooks& hooks) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (hooks.beforeNode != nullptr && program_ == nullptr) return Result::NotReady;
    debugging_ = hooks;
    return Result::Ok;
}

auto Instance::beforeDebugNode(std::uint32_t index) noexcept -> Result
{
    if (debugging_.beforeNode == nullptr) return Result::Ok;
    auto current = instruction(program_->bytes_, program_->version_, index);
    auto opcode = current[16];
    auto values = std::array<DebugInputValue, 2>{};
    auto count = opcode == 2 || opcode == curveOpcode || opcode == branchTimeOpcode ||
        opcode == currentPhaseOpcode || opcode == phaseTimeOpcode ? std::size_t{} :
        ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
            (opcode >= int32AddOpcode && opcode <= int32DivideOpcode)) ? std::size_t{2} : std::size_t{1};
    for (auto slot = std::size_t{}; slot < count; ++slot)
    {
        auto& value = values[slot];
        value.slot = static_cast<std::uint8_t>(slot);
        value.kind = DebugValueKind::Numeric;
        if (program_->version_ == 1)
        {
            auto source = read32(current + 20 + slot * 4);
            value.width = 1;
            value.connected = source != noInput;
            if (value.connected)
                std::memcpy(value.sourceNodeId, instruction(program_->bytes_, 1, source), 16);
            value.components[0] = value.connected ? work_[source] :
                defaults_ != nullptr ? defaults_[index * 2 + slot] : readDouble(current + 28 + slot * 8);
            continue;
        }
        auto source = read32(current + 24 + slot * 4);
        value.connected = source != noInput;
        if (value.connected)
            std::memcpy(value.sourceNodeId, instruction(program_->bytes_, program_->version_, source), 16);
        value.width = current[17];
        if (opcode == enumValueOpcode) value.kind = DebugValueKind::Int32;
        if (opcode >= 23 && opcode <= 25) value.width = 1;
        if (opcode >= 29 && opcode <= 34) value.width = opcode <= 30 ? 2 : opcode <= 32 ? 3 : 4;
        if (opcode >= 35 && opcode <= 39) value.width = 4;
        if (value.width == 4 && (opcode == 10 || opcode == 38 || (current[20] & (2u << slot)) != 0))
            value.kind = DebugValueKind::Color;
        if (program_->version_ >= 10)
        {
            auto type = valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_, index)[2 + slot];
            value.width = valueTypeWidth(type);
            value.kind = type == valueTypeInt32 || type == valueTypeEnum ? DebugValueKind::Int32 :
                type == valueTypeColor ? DebugValueKind::Color : DebugValueKind::Numeric;
        }
        for (auto lane = 0u; lane < value.width; ++lane)
            value.components[lane] = value.connected ? work_[source * 4 + current[21 + slot] + lane] :
                defaults_ != nullptr ? defaults_[index * (program_->version_ >= 3 ? 16 : 8) + slot * 4 + lane] :
                readDouble(current + 32 + slot * 32 + lane * 8);
    }
    return debugging_.beforeNode(debugging_.context, {current, values.data(), count, time_});
}

#include "ExecutionDebugCapture.inl"

void Instance::cleanupStates() noexcept
{
    if (program_ == nullptr || program_->version_ < 3 || work_ == nullptr) return;
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Cleanup);
    for (auto index = initializedStateCount_; index > 0; --index)
    {
        auto nodeIndex = index - 1;
        auto descriptor = program_->bindings_[nodeIndex].descriptor;
        if (descriptor == nullptr || descriptor->cleanup == nullptr) continue;
        auto state = reinterpret_cast<std::uint8_t*>(work_) +
            stateOffset(program_->bindings_, nodeIndex);
        descriptor->cleanup(state, descriptor->stateBytes, descriptor->context);
    }
    initializedStateCount_ = 0;
}

auto Instance::bindValue(const std::uint8_t nodeId[16], ValueHandle& out) const noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Bind, noProfileNode, profiling_.accessors);
    if (program_ == nullptr || nodeId == nullptr) return Result::InvalidArgument;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
        if (std::memcmp(instruction(program_->bytes_, program_->version_, index), nodeId, 16) == 0)
        {
            auto node = instruction(program_->bytes_, program_->version_, index);
            auto width = program_->version_ == 1 ? 1 : node[17];
            if (program_->version_ >= 3 && node[16] == customSignalOpcode)
                return Result::InvalidValueType;
            out = {program_, generation_, program_->generation_, index, static_cast<std::uint8_t>(width)};
            return Result::Ok;
        }
    return Result::NodeNotFound;
}

auto Instance::bindParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                             std::uint8_t component, ParameterHandle& out) const noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Bind, noProfileNode, profiling_.accessors);
    if (program_ == nullptr || nodeId == nullptr) return Result::InvalidArgument;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
        if (std::memcmp(instruction(program_->bytes_, program_->version_, index), nodeId, 16) == 0)
        {
            auto current = instruction(program_->bytes_, program_->version_, index);
            auto laneCount = std::uint8_t{0};
            if (program_->version_ >= 3 &&
                (current[16] == customParameterOpcode || current[16] == customSignalOpcode))
                laneCount = inputSlot < current[18] ?
                    program_->bindings_[index].descriptor->inputWidths[inputSlot] : 0;
            else
            {
                auto opcode = current[16];
                auto width = program_->version_ == 1 ? 1u : current[17];
                if (program_->version_ == 1)
                    laneCount = (inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                        (inputSlot == 1 && opcode >= 3 && opcode <= 6) ? 1 : 0;
                else if (inputSlot == 0)
                {
                    if (opcode == enumValueOpcode) laneCount = 1;
                    if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
                        (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
                    else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
                    else if (opcode >= 35 && opcode <= 37) laneCount = width;
                    else if (opcode == int32Opcode ||
                             (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                             opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) laneCount = 1;
                }
                else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
                else if (inputSlot == 1 && opcode >= int32AddOpcode && opcode <= int32DivideOpcode)
                    laneCount = 1;
            }
            if (component >= laneCount) return Result::InvalidValueType;
            out = {program_, generation_, program_->generation_, index, inputSlot, component};
            return Result::Ok;
        }
    return Result::NodeNotFound;
}

auto Instance::bindSignal(const std::uint8_t nodeId[16], SignalHandle& out) const noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Bind, noProfileNode, profiling_.accessors);
    if (program_ == nullptr || program_->version_ < 3 || nodeId == nullptr)
        return Result::InvalidArgument;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
        if (std::memcmp(instruction(program_->bytes_, program_->version_, index), nodeId, 16) == 0)
        {
            if (instruction(program_->bytes_, program_->version_, index)[16] != customSignalOpcode)
                return Result::InvalidValueType;
            out = {program_, generation_, program_->generation_, index};
            return Result::Ok;
        }
    return Result::NodeNotFound;
}

auto Instance::value(const ValueHandle& handle, double out[4]) const noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::ReadValue, handle.nodeIndex, profiling_.accessors);
    if (out == nullptr) return Result::InvalidArgument;
    if (program_ == nullptr || handle.programIdentity != program_ || handle.generation != generation_ ||
        handle.programGeneration != program_->generation_ ||
        handle.nodeIndex >= program_->nodeCount_)
        return Result::InvalidHandle;
    if (handle.componentCount == 0 || handle.componentCount > 4)
        return Result::InvalidHandle;
    auto node = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    if ((program_->version_ >= 3 && node[16] == customSignalOpcode) ||
        handle.componentCount != (program_->version_ == 1 ? 1 : node[17]))
        return Result::InvalidHandle;
    auto status = result();
    if (status != Result::Ok) return status;
    auto base = handle.nodeIndex * (program_->version_ == 1 ? 1 : 4);
    for (auto lane = 0u; lane < handle.componentCount; ++lane) out[lane] = work_[base + lane];
    for (auto lane = handle.componentCount; lane < 4; ++lane) out[lane] = 0;
    return Result::Ok;
}

auto Instance::valueInt32(const ValueHandle& handle, std::int32_t& out) const noexcept -> Result
{
    auto values = std::array<double, 4>{};
    auto status = value(handle, values.data());
    if (status != Result::Ok) return status;
    auto type = ValueType::None;
    status = program_->valueType(handle.nodeIndex, type);
    if (status != Result::Ok) return status;
    if (type != ValueType::Int32 || handle.componentCount != 1)
        return Result::InvalidValueType;
    return int32FromDouble(values[0], out);
}

auto Instance::parameter(const ParameterHandle& handle, double& out) const noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::ReadParameter, handle.nodeIndex, profiling_.accessors);
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (program_ == nullptr || handle.programIdentity != program_ || handle.generation != generation_ ||
        handle.programGeneration != program_->generation_ || handle.nodeIndex >= program_->nodeCount_)
        return Result::InvalidHandle;
    auto node = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    auto valid = false;
    if (program_->version_ >= 3 &&
        (node[16] == customParameterOpcode || node[16] == customSignalOpcode))
    {
        auto descriptor = program_->bindings_[handle.nodeIndex].descriptor;
        valid = descriptor != nullptr && handle.inputSlot < node[18] &&
            handle.component < descriptor->inputWidths[handle.inputSlot];
    }
    else
    {
        auto opcode = node[16];
        auto width = program_->version_ == 1 ? 1u : node[17];
        auto laneCount = 0u;
        if (program_->version_ == 1)
        {
            if ((handle.inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                (handle.inputSlot == 1 && opcode >= 3 && opcode <= 6)) laneCount = 1;
        }
        else if (handle.inputSlot == 0)
        {
            if (opcode == enumValueOpcode) laneCount = 1;
            if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
                (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
            else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
            else if (opcode >= 35 && opcode <= 37) laneCount = width;
            else if (opcode == int32Opcode ||
                     (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                     opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) laneCount = 1;
        }
        else if (handle.inputSlot == 1 &&
                 ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
                  (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))) laneCount = width;
        valid = handle.inputSlot <= 1 && handle.component < laneCount;
    }
    if (!valid) return Result::InvalidHandle;
    if (program_->version_ == 1)
        out = defaults_ == nullptr ?
            readDouble(node + (handle.inputSlot == 0 ? 28 : 36)) :
            defaults_[handle.nodeIndex * 2 + handle.inputSlot];
    else if (program_->version_ >= 3 &&
             (node[16] == customParameterOpcode || node[16] == customSignalOpcode))
        out = defaults_ == nullptr ?
            readDouble(node + 80 + handle.inputSlot * 32 + handle.component * 8) :
            defaults_[handle.nodeIndex * 16 + handle.inputSlot * 4 + handle.component];
    else
        out = defaults_ == nullptr ?
            readDouble(node + (handle.inputSlot == 0 ? 32 : 64) + handle.component * 8) :
            defaults_[handle.nodeIndex * (program_->version_ >= 3 ? 16 : 8) +
                handle.inputSlot * 4 + handle.component];
    return Result::Ok;
}

auto Instance::setParameter(const ParameterHandle& handle, double value) noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::WriteParameter, handle.nodeIndex, profiling_.accessors);
    if (dispatching_) return Result::Busy;
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (program_ == nullptr || defaults_ == nullptr || handle.programIdentity != program_ ||
        handle.generation != generation_ || handle.programGeneration != program_->generation_ ||
        handle.nodeIndex >= program_->nodeCount_)
        return Result::InvalidHandle;
    if (!std::isfinite(value)) return Result::NonFinite;
    auto node = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    if (program_->version_ >= 10 && handle.inputSlot < 4 &&
        valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_, handle.nodeIndex)
                [2 + handle.inputSlot] == valueTypeInt32 &&
        !validInt32(value))
        return Result::InvalidValueType;
    if (node[16] == enumValueOpcode &&
        (handle.inputSlot != 0 || handle.component != 0 || std::trunc(value) != value ||
         value < std::numeric_limits<std::int32_t>::min() ||
         value > std::numeric_limits<std::int32_t>::max()))
        return Result::InvalidValueType;
    if (program_->version_ >= 3 &&
        (node[16] == customParameterOpcode || node[16] == customSignalOpcode))
    {
        auto descriptor = program_->bindings_[handle.nodeIndex].descriptor;
        if (descriptor == nullptr || handle.inputSlot >= node[18] ||
            handle.component >= descriptor->inputWidths[handle.inputSlot])
            return Result::InvalidHandle;
        if (descriptor->inputIsColor[handle.inputSlot] != 0 &&
            (value < 0 || (handle.component == 3 && value > 1)))
            return Result::InvalidValueType;
        defaults_[handle.nodeIndex * 16 + handle.inputSlot * 4 + handle.component] = value;
    }
    else
    {
        auto opcode = node[16];
        auto width = program_->version_ == 1 ? 1u : node[17];
        auto laneCount = 0u;
        if (program_->version_ == 1)
        {
            if ((handle.inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                (handle.inputSlot == 1 && opcode >= 3 && opcode <= 6)) laneCount = 1;
        }
        else if (handle.inputSlot == 0)
        {
            if (opcode == enumValueOpcode) laneCount = 1;
            if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
                (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
            else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
            else if (opcode >= 35 && opcode <= 37) laneCount = width;
            else if (opcode == int32Opcode ||
                     (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                     opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) laneCount = 1;
        }
        else if (handle.inputSlot == 1 &&
                 ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
                  (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))) laneCount = width;
        if (handle.inputSlot > 1 || handle.component >= laneCount) return Result::InvalidHandle;
        if (program_->version_ == 1)
            defaults_[handle.nodeIndex * 2 + handle.inputSlot] = value;
        else
            defaults_[handle.nodeIndex * (program_->version_ >= 3 ? 16 : 8) +
                handle.inputSlot * 4 + handle.component] = value;
    }
    return Result::Ok;
}

auto Instance::setParameterInt32(const ParameterHandle& handle, std::int32_t value) noexcept -> Result
{
    auto currentValue = 0.0;
    auto status = parameter(handle, currentValue);
    if (status != Result::Ok) return status;
    if (program_ == nullptr || program_->version_ < 10 || handle.inputSlot >= 4 ||
        valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_, handle.nodeIndex)
            [2 + handle.inputSlot] != valueTypeInt32)
        return Result::InvalidValueType;
    return setParameter(handle, static_cast<double>(value));
}

auto Instance::setHostQuality(const HostQualitySnapshot& snapshot) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running ||
        generatedSignalsPending_ || logicalQueueCount_ != 0)
        return Result::Busy;
    if (!program_) return Result::InvalidArgument;
    if (static_cast<unsigned>(snapshot.availability) >
            static_cast<unsigned>(HostQualityAvailability::Unsupported) ||
        (snapshot.availability == HostQualityAvailability::Known &&
            (static_cast<unsigned>(snapshot.tier) > static_cast<unsigned>(HostQualityTier::Cinematic) ||
             static_cast<unsigned>(snapshot.source) > static_cast<unsigned>(HostQualitySource::PlatformPolicy))))
        return Result::InvalidValueType;
    quality_ = snapshot;
    return Result::Ok;
}

auto Instance::setTimeContext(double effectTime, double frameDelta) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!program_ || !std::isfinite(effectTime) || !std::isfinite(frameDelta) ||
        effectTime < 0 || frameDelta < 0) return Result::InvalidArgument;
    pendingEffectTime_ = effectTime;
    pendingFrameDelta_ = frameDelta;
    pendingTimeContext_ = true;
    return Result::Ok;
}

auto Instance::clearTimeContext() noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!program_) return Result::InvalidArgument;
    pendingTimeContext_ = false;
    return Result::Ok;
}

auto Instance::updateAsync(double time, Enqueue enqueue, void* queueContext) noexcept -> Result
{
    if (dispatching_) return Result::Busy;
    if (program_ == nullptr || enqueue == nullptr || !std::isfinite(time)) return Result::InvalidArgument;
    if (program_->version_ >= 9 && generatedSignalsPending_) return Result::Busy;
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Schedule);
    auto phase = phase_.load(std::memory_order_acquire);
    for (;;)
    {
        if (phase == Phase::Running) return Result::Busy;
        if (phase_.compare_exchange_weak(phase, Phase::Running, std::memory_order_acq_rel)) break;
    }
    time_ = time;
    evaluatedTimeContext_ = pendingTimeContext_;
    effectTime_ = pendingEffectTime_;
    frameDelta_ = pendingFrameDelta_;
    pendingTimeContext_ = false;
    if (!enqueue(queueContext, &Instance::run, this))
    {
        pendingTimeContext_ = evaluatedTimeContext_;
        phase_.store(Phase::Idle, std::memory_order_release);
        return Result::QueueRejected;
    }
    return Result::Ok;
}

auto Instance::setParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                            std::uint8_t component, double value) noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::WriteParameter, noProfileNode, profiling_.accessors);
    if (dispatching_) return Result::Busy;
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (program_ == nullptr || defaults_ == nullptr || nodeId == nullptr ||
        inputSlot > (program_->version_ >= 3 ? 3 : 1))
        return Result::InvalidArgument;
    if (!std::isfinite(value)) return Result::NonFinite;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        if (std::memcmp(current, nodeId, 16) != 0) continue;
        auto opcode = current[16];
        if (program_->version_ >= 10 &&
            valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_, index)
                [2 + inputSlot] == valueTypeInt32 &&
            !validInt32(value))
            return Result::InvalidValueType;
        if (program_->version_ >= 3 &&
            (opcode == customParameterOpcode || opcode == customSignalOpcode))
        {
            if (inputSlot >= current[18] || component >= 4) return Result::InvalidValueType;
            auto descriptor = program_->bindings_[index].descriptor;
            if (descriptor == nullptr || component >= descriptor->inputWidths[inputSlot])
                return Result::InvalidValueType;
            if (descriptor->inputIsColor[inputSlot] != 0 &&
                (value < 0 || (component == 3 && value > 1)))
                return Result::InvalidValueType;
            defaults_[index * 16 + inputSlot * 4 + component] = value;
            return Result::Ok;
        }
        auto width = program_->version_ == 1 ? 1u : current[17];
        auto laneCount = 0u;
        if (program_->version_ == 1)
        {
            if ((inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                (inputSlot == 1 && opcode >= 3 && opcode <= 6))
                laneCount = 1;
        }
        else if (inputSlot == 0)
        {
            if (opcode == 1 || (opcode >= 7 && opcode <= 10)) laneCount = width;
            else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
            else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
            else if (opcode >= 35 && opcode <= 37) laneCount = width;
            else if (opcode == int32Opcode ||
                     (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                     opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) laneCount = 1;
        }
        else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))
            laneCount = width;
        else if (inputSlot == 1 && opcode >= int32AddOpcode && opcode <= int32DivideOpcode)
            laneCount = 1;
        if (component >= laneCount) return Result::InvalidValueType;
        if (opcode >= 26 && opcode <= 28)
        {
            auto inputWidth = current[18];
            if (value < 0 || value >= inputWidth || std::floor(value) != value)
                return Result::InvalidValueType;
        }
        if (opcode >= 35 && opcode <= 37 &&
            (value < 0 || value >= 4 || std::floor(value) != value))
            return Result::InvalidValueType;
        if (opcode == 10 && (value < 0 || (component == 3 && value > 1)))
            return Result::InvalidValueType;
        if (program_->version_ == 1)
            defaults_[index * 2 + inputSlot] = value;
        else if (program_->version_ >= 3)
            defaults_[index * 16 + inputSlot * 4 + component] = value;
        else
            defaults_[index * 8 + inputSlot * 4 + component] = value;
        return Result::Ok;
    }
    return Result::NodeNotFound;
}

auto Instance::result() const noexcept -> Result
{
    return phase_.load(std::memory_order_acquire) == Phase::Complete ? result_ : Result::NotReady;
}

auto Instance::setSignalObserver(SignalObserver observer, void* context) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
        return Result::Busy;
    if (observer != nullptr && context == nullptr) return Result::InvalidArgument;
    signalObserver_ = observer;
    signalObserverContext_ = context;
    return Result::Ok;
}

auto Instance::enableForceRequests() noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!program_) return Result::NotReady;
    forceRequestsEnabled_ = true;
    return Result::Ok;
}

auto Instance::pendingForceRequest(std::uint8_t& scope) const noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    scope = forceScope_;
    return Result::Ok;
}

auto Instance::value(const std::uint8_t nodeId[16], double& out) const noexcept -> Result
{
    auto components = std::array<double, 4>{};
    auto count = std::uint8_t{};
    auto status = valueComponents(nodeId, components.data(), count);
    if (status == Result::Ok)
    {
        if (count != 1) return Result::InvalidValueType;
        out = components[0];
    }
    return status;
}

auto Instance::valueComponents(const std::uint8_t nodeId[16], double out[4],
                              std::uint8_t& componentCount) const noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::ReadValue, noProfileNode, profiling_.accessors);
    if (nodeId == nullptr || out == nullptr) return Result::InvalidArgument;
    auto status = result();
    if (status != Result::Ok) return status;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        if (std::memcmp(current, nodeId, 16) == 0)
        {
            auto width = program_->version_ == 1 ? 1u : current[17];
            if (program_->version_ >= 3 && current[16] == customSignalOpcode)
                return Result::InvalidValueType;
            for (auto lane = 0u; lane < width; ++lane)
                out[lane] = work_[index * (program_->version_ == 1 ? 1u : 4u) + lane];
            for (auto lane = width; lane < 4; ++lane) out[lane] = 0;
            componentCount = static_cast<std::uint8_t>(width);
            return Result::Ok;
        }
    }
    return Result::NodeNotFound;
}

auto Instance::setUnconnectedParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                                       std::uint8_t component, double value) noexcept -> Result
{
    if (program_ == nullptr || inputSlot > (program_->version_ >= 3 ? 3 : 1))
        return Result::InvalidArgument;
    auto handle = ParameterHandle{};
    auto status = bindParameter(nodeId, inputSlot, component, handle);
    if (status != Result::Ok) return status;
    auto current = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    auto offset = program_->version_ == 1 ? 20 + inputSlot * 4 :
        program_->version_ >= 3 &&
            (current[16] == customParameterOpcode || current[16] == customSignalOpcode) ?
            48 + inputSlot * 8 : 24 + inputSlot * 4;
    if (read32(current + offset) != noInput) return Result::InvalidValueType;
    return setParameter(handle, value);
}

auto Instance::dispatchSignal(const std::uint8_t entryNodeId[16], double time,
                              std::size_t maxInstructions) noexcept -> Result
{
    auto entry = SignalHandle{};
    auto status = bindSignal(entryNodeId, entry);
    if (status != Result::Ok) return status;
    return dispatchSignal(entry, time, maxInstructions);
}

auto Instance::dispatchSignal(const SignalHandle& entry, double time,
                              std::size_t maxInstructions) noexcept -> Result
{
    return dispatchSignalInternal(entry, time, maxInstructions, -1);
}

auto Instance::setDeferredSignalStorage(DeferredSignal* storage, std::size_t capacity) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!program_ || (capacity != 0 && !storage) || (capacity == 0 && storage) ||
        capacity > std::numeric_limits<std::size_t>::max() / sizeof(DeferredSignal))
        return Result::InvalidArgument;
    if (deferredCount_) return Result::Busy;
    auto bytes = capacity * sizeof(DeferredSignal);
    if (capacity && (!isAligned(storage, alignof(DeferredSignal)) ||
        rangesOverlap(storage, bytes, work_, program_->workBytes()) ||
        (defaults_ && rangesOverlap(storage, bytes, defaults_, program_->parameterValueCount() * sizeof(double)))))
        return Result::InvalidArgument;
    deferredSignals_ = storage;
    deferredCapacity_ = capacity;
    return Result::Ok;
}

auto Instance::nextDeferredSignal(double& time, bool& hasSignal) const noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!program_) return Result::InvalidArgument;
    hasSignal = false;
    auto earliest = std::numeric_limits<double>::infinity();
    for (auto index = std::size_t{0}; index < deferredCount_; ++index)
    {
        const auto& item = deferredSignals_[index];
        auto due = item.time;
        if (item.usesBranchTime)
        {
            if (item.branchGuardId >= program_->branchGuardCount_)
                return Result::InvalidBinary;
            if (!isBranchActive(item.branchGuardId))
                due = std::numeric_limits<double>::max();
            else
            {
                auto clock = reinterpret_cast<const std::uint8_t*>(work_) + program_->branchClockOffset_ +
                    std::size_t{item.branchGuardId} * 24;
                auto localTime = readDouble(clock);
                due = time_ + std::max(0.0, item.time - localTime);
            }
        }
        if (due < earliest)
        {
            earliest = due;
            hasSignal = true;
        }
    }
    if (hasSignal) time = earliest;
    return Result::Ok;
}

auto Instance::clearDeferredSignals() noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!program_) return Result::InvalidArgument;
    deferredCount_ = 0;
    return Result::Ok;
}

auto Instance::dispatchDeferredSignal(double time, std::size_t maxInstructions) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (!program_ || !std::isfinite(time) || !maxInstructions) return Result::InvalidArgument;
    if (!deferredCount_ || time_ != time || result() != Result::Ok)
        return Result::NotReady;
    auto selected = deferredCount_;
    for (auto index = std::size_t{0}; index < deferredCount_; ++index)
    {
        const auto& candidate = deferredSignals_[index];
        if (!candidate.usesBranchTime)
        {
            if (candidate.time == time) { selected = index; break; }
            continue;
        }
        if (candidate.branchGuardId >= program_->branchGuardCount_ ||
            !isBranchActive(candidate.branchGuardId)) continue;
        auto clock = reinterpret_cast<const std::uint8_t*>(work_) + program_->branchClockOffset_ +
            std::size_t{candidate.branchGuardId} * 24;
        if (readDouble(clock) >= candidate.time) { selected = index; break; }
    }
    if (selected == deferredCount_) return Result::NotReady;
    auto item = deferredSignals_[selected];
    for (auto index = selected + 1; index < deferredCount_; ++index)
        deferredSignals_[index - 1] = deferredSignals_[index];
    --deferredCount_;
    auto entry = SignalHandle{program_, generation_, program_->generation_, item.nodeIndex};
    return dispatchSignalInternal(entry, time, maxInstructions, item.branch);
}

auto Instance::dispatchGeneratedSignals(std::size_t maxInstructions) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (program_ == nullptr || maxInstructions == 0) return Result::InvalidArgument;
    if (program_->version_ < 9) return Result::Ok;
    auto status = result();
    if (status != Result::Ok) return status;
    if (!generatedSignalsPending_ && logicalQueueCount_ == 0)
        return refreshDirtyPure();
    return drainLogicalSignals(maxInstructions, true);
}

auto Instance::dispatchSignalV9(const SignalHandle& entry, double time,
                                std::size_t maxInstructions, int resumeBranch) noexcept -> Result
{
    if (logicalQueueCount_ != 0 || generatedSignalsPending_ || generatedQueueSeeded_)
        return Result::NotReady;
    if (program_->logicalQueueCapacity_ == 0) return Result::InvalidBinary;
    auto queue = reinterpret_cast<LogicalQueuedSignal*>(
        reinterpret_cast<std::uint8_t*>(work_) + program_->logicalQueueOffset_);
    auto payloadQueue = program_->signalPayloadQueueOffset_ == 0 ? nullptr :
        reinterpret_cast<SignalPayload*>(reinterpret_cast<std::uint8_t*>(work_) +
            program_->signalPayloadQueueOffset_);
    if (resumeBranch >= 0)
        queue[0] = {entry.nodeIndex, static_cast<std::uint8_t>(resumeBranch), 2, 0};
    else
        queue[0] = {entry.nodeIndex, 0, 1, 0};
    if (payloadQueue != nullptr) payloadQueue[0] = {};
    logicalQueueHead_ = 0;
    logicalQueueCount_ = 1;
    time_ = time;
    return drainLogicalSignals(maxInstructions, false);
}

auto Instance::drainLogicalSignals(std::size_t maxInstructions, bool seedGenerated) noexcept -> Result
{
    if (dispatching_) return Result::Busy;
    if (program_ == nullptr || program_->version_ < 9 || maxInstructions == 0)
        return Result::InvalidArgument;
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    auto status = result();
    if (status != Result::Ok) return status;
    auto queue = reinterpret_cast<LogicalQueuedSignal*>(
        reinterpret_cast<std::uint8_t*>(work_) + program_->logicalQueueOffset_);
    auto payloadQueue = program_->signalPayloadQueueOffset_ == 0 ? nullptr :
        reinterpret_cast<SignalPayload*>(reinterpret_cast<std::uint8_t*>(work_) +
            program_->signalPayloadQueueOffset_);
    auto activePayload = SignalPayload{};
    auto enqueue = [&](std::uint32_t nodeIndex, std::uint8_t inputSlot, std::uint8_t isRoot,
                       std::uint32_t sourceIndex) noexcept -> Result {
        if (logicalQueueCount_ >= program_->logicalQueueCapacity_) return Result::InstructionBudgetExceeded;
        auto tail = (logicalQueueHead_ + logicalQueueCount_) % program_->logicalQueueCapacity_;
        queue[tail] = {nodeIndex, inputSlot, isRoot, static_cast<std::uint16_t>(sourceIndex + 1)};
        if (payloadQueue != nullptr) payloadQueue[tail] = activePayload;
        ++logicalQueueCount_;
        return Result::Ok;
    };
    auto routeLowerBound = [&](std::uint32_t sourceIndex, std::uint8_t branch) noexcept {
        auto first = std::size_t{0};
        auto last = std::size_t{program_->logicalSignalRouteCount_};
        while (first < last)
        {
            auto middle = first + (last - first) / 2;
            auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                middle * logicalSignalRouteRecordSize;
            auto currentSource = read32(record);
            auto currentBranch = record[4];
            if (currentSource < sourceIndex ||
                (currentSource == sourceIndex && currentBranch < branch))
                first = middle + 1;
            else last = middle;
        }
        return first;
    };
    auto routeCount = [&](std::uint32_t sourceIndex, std::uint8_t branch) noexcept {
        auto count = std::size_t{0};
        auto routeIndex = routeLowerBound(sourceIndex, branch);
        while (routeIndex < program_->logicalSignalRouteCount_)
        {
            auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                routeIndex * logicalSignalRouteRecordSize;
            if (read32(record) != sourceIndex || record[4] != branch) break;
            ++routeIndex;
            ++count;
        }
        return count;
    };
    auto appendRoutes = [&](std::uint32_t sourceIndex, std::uint8_t branch) noexcept -> Result {
        auto routeIndex = routeLowerBound(sourceIndex, branch);
        while (routeIndex < program_->logicalSignalRouteCount_)
        {
            auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                routeIndex * logicalSignalRouteRecordSize;
            if (read32(record) != sourceIndex || record[4] != branch) break;
            auto result = enqueue(read32(record + 8), record[5], 0, sourceIndex);
            if (result != Result::Ok) return result;
            ++routeIndex;
        }
        return Result::Ok;
    };

    if (seedGenerated && generatedSignalsPending_ && !generatedQueueSeeded_)
    {
        std::size_t requiredEvents = 0;
        auto eventMasks = reinterpret_cast<const std::uint8_t*>(work_) + program_->logicalEventMaskOffset_;
        for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_; ++logicalIndex)
        {
            auto record = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                std::size_t{logicalIndex} * logicalInstructionRecordSize;
            auto nodeIndex = read32(record);
            auto branches = read64(eventMasks + std::size_t{nodeIndex} * sizeof(std::uint64_t));
            for (auto branch = 0u; branch < record[31]; ++branch)
                if ((branches & (std::uint64_t{1} << branch)) != 0)
                {
                    auto routes = routeCount(nodeIndex, static_cast<std::uint8_t>(branch));
                    if (routes > program_->logicalQueueCapacity_ - requiredEvents)
                        return Result::InstructionBudgetExceeded;
                    requiredEvents += routes;
                }
        }
        if (requiredEvents > program_->logicalQueueCapacity_ - logicalQueueCount_)
            return Result::InstructionBudgetExceeded;
        for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_; ++logicalIndex)
        {
            auto record = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                std::size_t{logicalIndex} * logicalInstructionRecordSize;
            auto nodeIndex = read32(record);
            auto branches = read64(eventMasks + std::size_t{nodeIndex} * sizeof(std::uint64_t));
            for (auto branch = 0u; branch < record[31]; ++branch)
                if ((branches & (std::uint64_t{1} << branch)) != 0)
                {
                    auto enqueueStatus = appendRoutes(nodeIndex, static_cast<std::uint8_t>(branch));
                    if (enqueueStatus != Result::Ok) return enqueueStatus;
                }
        }
        std::memset(const_cast<std::uint8_t*>(eventMasks), 0,
            std::size_t{program_->nodeCount_} * sizeof(std::uint64_t));
        generatedQueueSeeded_ = true;
    }

    struct DispatchScope final {
        bool& active;
        ~DispatchScope() { active = false; }
    };
    auto scope = DispatchScope{dispatching_};
    dispatching_ = true;
    auto visited = std::size_t{0};
    while (logicalQueueCount_ != 0)
    {
        if (visited >= maxInstructions) return Result::InstructionBudgetExceeded;
        auto event = queue[logicalQueueHead_];
        activePayload = payloadQueue == nullptr ? SignalPayload{} : payloadQueue[logicalQueueHead_];
        for (auto fieldIndex = std::size_t{}; fieldIndex < activePayload.size(); ++fieldIndex)
        {
            auto field = activePayload.field(fieldIndex);
            auto sourceIndex = 0u;
            for (; sourceIndex < program_->nodeCount_; ++sourceIndex)
                if (std::memcmp(instruction(program_->bytes_, 3, sourceIndex), field->id.data(), 16) == 0)
                    break;
            if (sourceIndex == program_->nodeCount_ || field->kind != SignalValueKind::Vector4)
                return Result::InvalidBinary;
            std::copy_n(field->components, 4, work_ + sourceIndex * 4);
            (reinterpret_cast<std::uint8_t*>(work_) + program_->logicalDirtyOffset_)[sourceIndex] = 1;
        }
        status = refreshDirtyPure();
        if (status != Result::Ok) return status;
        auto current = instruction(program_->bytes_, 3, event.nodeIndex);
        if (event.isRoot == 2)
        {
            auto branch = event.inputSlot;
            auto fanout = routeCount(event.nodeIndex, branch);
            if (logicalQueueCount_ - 1 + fanout > program_->logicalQueueCapacity_)
                return Result::InstructionBudgetExceeded;
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            status = appendRoutes(event.nodeIndex, branch);
            if (status != Result::Ok) return status;
            continue;
        }
        ++visited;
        auto guardId = branchGuard(current, program_->version_);
        if (guardId != noInput && !isBranchActive(guardId))
        {
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            continue;
        }
        if (event.isRoot == 0 && current[16] != customSignalOpcode &&
            current[16] != logicalEventSignalOpcode)
            return Result::InvalidBinary;
        if (event.isRoot == 1 && current[16] != customSignalOpcode)
            return Result::InvalidValueType;
        auto maxFanout = std::size_t{0};
        auto logicalDescriptor = program_->bindings_[event.nodeIndex].logicalDescriptor;
        auto customDescriptor = program_->bindings_[event.nodeIndex].descriptor;
        if (current[16] == logicalEventSignalOpcode)
        {
            if (logicalDescriptor == nullptr || logicalDescriptor->onSignal == nullptr ||
                event.inputSlot >= logicalDescriptor->signalInputCount)
                return Result::InvalidValueType;
            for (auto branch = 0u; branch < logicalDescriptor->signalBranchCount; ++branch)
                maxFanout += routeCount(event.nodeIndex, static_cast<std::uint8_t>(branch));
        }
        else
        {
            if (customDescriptor == nullptr || customDescriptor->execution != CustomExecution::Signal)
                return Result::UnknownCustomNode;
            for (auto branch = 0u; branch < customDescriptor->branchCount; ++branch)
                maxFanout = std::max(maxFanout, routeCount(event.nodeIndex, static_cast<std::uint8_t>(branch)));
        }
        if (logicalQueueCount_ - 1 + maxFanout > program_->logicalQueueCapacity_)
            return Result::InstructionBudgetExceeded;

        alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
        auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
        alignas(customWorkAlignment) auto constants = std::array<double, customMaxPorts * 4>{};
        auto inputCount = current[18];
        auto outputWidth = current[17];
        auto state = reinterpret_cast<std::uint8_t*>(work_) +
            stateOffset(program_->bindings_, event.nodeIndex);
        if (current[16] == logicalEventSignalOpcode)
        {
            for (auto input = 0u; input < inputCount; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = logicalDescriptor->inputWidths[input];
                for (auto lane = 0u; lane < inputWidths[input]; ++lane)
                {
                    constants[input * 4 + lane] = defaults_ == nullptr ?
                        readDouble(current + 80 + input * 32 + lane * 8) :
                        defaults_[event.nodeIndex * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput ? constants[input * 4 + lane] :
                        work_[sourceIndex * 4 + inputRecord[5] + lane];
                }
            }
            auto invocation = LogicalNodeInvocation{
                inputs.data(), inputCount, inputWidths.data(), work_ + event.nodeIndex * 4, outputWidth,
                state, logicalDescriptor->stateBytes,
                0,
                evaluationSerial_, event.inputSlot, 0, time_, &quality_};
            auto record = static_cast<const std::uint8_t*>(nullptr);
            for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_; ++logicalIndex)
            {
                auto candidate = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                    std::size_t{logicalIndex} * logicalInstructionRecordSize;
                if (read32(candidate) == event.nodeIndex) { record = candidate; break; }
            }
            if (record == nullptr) return Result::InvalidBinary;
            invocation.configuration = read32(record + 24);
            status = debugResolvedInputs(event.nodeIndex, time_, inputs.data(), inputWidths.data(), nullptr,
                event.reserved == 0 ? nullptr : instruction(program_->bytes_, 3, event.reserved - 1));
            if (status != Result::Ok) return status;
            if (signalObserver_ != nullptr)
            {
                status = signalObserver_(signalObserverContext_, current);
                if (status != Result::Ok) return status;
            }
            status = logicalDescriptor->onSignal(invocation);
            if (status != Result::Ok) return status;
            auto validBranches = logicalDescriptor->signalBranchCount == 0 ? std::uint64_t{0} :
                (std::uint64_t{1} << logicalDescriptor->signalBranchCount) - 1;
            if ((invocation.emittedBranches & ~validBranches) != 0)
                return Result::InvalidValueType;
            for (auto lane = 0u; lane < outputWidth; ++lane)
                if (!std::isfinite(work_[event.nodeIndex * 4 + lane])) return Result::NonFinite;
            if (invocation.captureSignalValue)
            {
                if (payloadQueue == nullptr || outputWidth != 4) return Result::UnsupportedBinary;
                auto field = SignalFieldValue{};
                std::copy_n(current, 16, field.id.data());
                field.kind = SignalValueKind::Vector4;
                std::copy_n(work_ + event.nodeIndex * 4, 4, field.components);
                status = activePayload.set(field);
                if (status != Result::Ok) return status;
            }
            auto dirty = reinterpret_cast<std::uint8_t*>(work_) + program_->logicalDirtyOffset_;
            dirty[event.nodeIndex] = 1;
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            for (auto branch = 0u; branch < logicalDescriptor->signalBranchCount; ++branch)
                if ((invocation.emittedBranches & (std::uint64_t{1} << branch)) != 0)
                {
                    status = appendRoutes(event.nodeIndex, static_cast<std::uint8_t>(branch));
                    if (status != Result::Ok) return status;
                }
        }
        else
        {
            for (auto input = 0u; input < inputCount; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = customDescriptor->inputWidths[input];
                for (auto lane = 0u; lane < inputWidths[input]; ++lane)
                {
                    constants[input * 4 + lane] = defaults_ == nullptr ?
                        readDouble(current + 80 + input * 32 + lane * 8) :
                        defaults_[event.nodeIndex * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput ? constants[input * 4 + lane] :
                        work_[sourceIndex * 4 + inputRecord[5] + lane];
                }
            }
            auto invocation = CustomNodeInvocation{
                inputs.data(), inputCount, inputWidths.data(), nullptr, 0,
                constants.data(), std::size_t{inputCount} * 4, state, customDescriptor->stateBytes,
                time_, 0xff, customDescriptor->context, effectTime_, frameDelta_, evaluatedTimeContext_};
            invocation.signalPayload = &activePayload;
            status = debugResolvedInputs(event.nodeIndex, time_, inputs.data(), inputWidths.data(),
                customDescriptor->inputIsColor,
                event.reserved == 0 ? nullptr : instruction(program_->bytes_, 3, event.reserved - 1));
            if (status != Result::Ok) return status;
            if (signalObserver_ != nullptr)
            {
                status = signalObserver_(signalObserverContext_, current);
                if (status != Result::Ok) return status;
            }
            status = customDescriptor->function(invocation);
            if (status != Result::Ok) return status;
            if (invocation.deferSignal && activePayload.size() != 0)
                return Result::UnsupportedBinary;
            if (invocation.enterPhase != 0)
            {
                if (invocation.enterPhase > 3) return Result::InvalidArgument;
                pendingPhase_ = invocation.enterPhase;
                pendingPhaseTime_ = time_;
            }
            if (invocation.forceScope != 0)
            {
                if (!forceRequestsEnabled_) return Result::UnsupportedBinary;
                if (invocation.forceScope > 2) return Result::InvalidArgument;
                forceScope_ = invocation.forceScope;
                deferredCount_ = logicalQueueCount_ = 0;
                generatedQueueSeeded_ = generatedSignalsPending_ = false;
                return Result::Ok;
            }
            if (current[19] == 0)
            {
                --logicalQueueCount_;
                logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
                continue;
            }
            if (invocation.deferSignal)
            {
                auto usesBranchTime = guardId != noInput;
                auto baseTime = time_;
                if (usesBranchTime)
                {
                    if (guardId >= program_->branchGuardCount_ || guardId >= 0xff)
                        return Result::UnsupportedBinary;
                    auto clock = reinterpret_cast<const std::uint8_t*>(work_) +
                        program_->branchClockOffset_ + std::size_t{guardId} * 24;
                    baseTime = readDouble(clock);
                }
                auto due = baseTime + invocation.delaySeconds;
                if (!std::isfinite(invocation.delaySeconds) || invocation.delaySeconds < 0 ||
                    !std::isfinite(due))
                    return Result::InvalidArgument;
                if (!deferredSignals_) return Result::NotReady;
                if (deferredCount_ == deferredCapacity_) return Result::QueueRejected;
                if (invocation.selectedBranch >= current[19]) return Result::InvalidValueType;
                deferredSignals_[deferredCount_++] = {
                    due, event.nodeIndex, invocation.selectedBranch,
                    usesBranchTime ? static_cast<std::uint8_t>(guardId) : std::uint8_t{0xff},
                    usesBranchTime};
                --logicalQueueCount_;
                logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
                continue;
            }
            if (invocation.selectedBranch >= current[19]) return Result::InvalidValueType;
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            status = appendRoutes(event.nodeIndex, invocation.selectedBranch);
            if (status != Result::Ok) return status;
        }
    }
    status = refreshDirtyPure();
    if (status != Result::Ok) return status;
    if (generatedQueueSeeded_)
    {
        generatedQueueSeeded_ = false;
        generatedSignalsPending_ = false;
    }
    return Result::Ok;
}

auto Instance::refreshDirtyPure() noexcept -> Result
{
    if (program_ == nullptr || program_->version_ < 9) return Result::Ok;
    auto dirtyBytes = reinterpret_cast<std::uint8_t*>(work_) + program_->logicalDirtyOffset_;
    auto dirty = std::array<bool, maxNodes>{};
    auto anyDirty = false;
    for (auto index = 0u; index < program_->nodeCount_; ++index)
    {
        dirty[index] = dirtyBytes[index] != 0;
        anyDirty = anyDirty || dirty[index];
    }
    if (!anyDirty) return Result::Ok;

    auto sourceIsDirty = [&](std::uint32_t sourceIndex) noexcept {
        return sourceIndex != noInput && sourceIndex < program_->nodeCount_ && dirty[sourceIndex];
    };
    for (auto index = 0u; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, 3, index);
        auto opcode = current[16];
        if (dirty[index] || opcode == customSignalOpcode)
            continue;
        auto guardId = branchGuard(current, program_->version_);
        if (guardId != noInput && !isBranchActive(guardId)) continue;
        auto depends = false;
        if (opcode == customParameterOpcode || opcode == logicalEventSignalOpcode)
        {
            for (auto input = 0u; input < current[18]; ++input)
                depends = depends || sourceIsDirty(read32(current + 48 + input * 8));
            if (depends && opcode == logicalEventSignalOpcode)
            {
                if (std::memcmp(current + 24, logicalSignalCaptureTypeId.data(), 16) != 0)
                    return Result::UnsupportedBinary;
                continue;
            }
            if (depends && !isLatePureBuiltin(program_->bindings_[index].descriptor))
                return Result::UnsupportedBinary;
        }
        else if (opcode == enumValueOpcode)
            depends = sourceIsDirty(read32(current + 24));
        else if (opcode == enumSelectOpcode)
        {
            for (auto selectIndex = 0u; selectIndex < program_->enumSelectCount_; ++selectIndex)
            {
                auto record = program_->bytes_ + program_->enumSelectRecordsOffset_ +
                    std::size_t{selectIndex} * enumSelectRecordSize;
                if (read32(record) != index) continue;
                depends = sourceIsDirty(read32(record + 4)) || sourceIsDirty(read32(record + 8));
                auto caseCount = record[29];
                auto caseOffset = read32(record + 32);
                for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
                    depends = depends || sourceIsDirty(read32(program_->bytes_ +
                        program_->enumBranchMetadataOffset_ + caseOffset +
                        std::size_t{caseIndex} * enumCaseRecordSize + 4));
                break;
            }
        }
        else if ((opcode >= 1 && opcode <= 39) || opcode == curveOpcode || opcode == branchTimeOpcode)
            depends = sourceIsDirty(read32(current + 24)) || sourceIsDirty(read32(current + 28));
        if (depends) dirty[index] = true;
    }
    for (auto index = 0u; index < program_->nodeCount_; ++index)
        if (dirty[index]) dirtyBytes[index] = 1;
    auto status = evaluate(true, dirty.data());
    if (status != Result::Ok) return status;
    std::memset(dirtyBytes, 0, program_->nodeCount_);
    return Result::Ok;
}

auto Instance::dispatchSignalInternal(const SignalHandle& entry, double time,
                                      std::size_t maxInstructions, int resumeBranch) noexcept -> Result
{
    if (dispatching_) return Result::Busy;
    if (program_ == nullptr || program_->version_ < 3 || !std::isfinite(time) || maxInstructions == 0)
        return Result::InvalidArgument;
    if (entry.programIdentity != program_ || entry.generation != generation_ ||
        entry.programGeneration != program_->generation_ || entry.nodeIndex >= program_->nodeCount_)
        return Result::InvalidHandle;
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    auto status = result();
    if (status != Result::Ok) return status;
    auto index = entry.nodeIndex;
    auto entryInstruction = instruction(program_->bytes_, 3, index);
    if (entryInstruction[16] != customSignalOpcode)
        return Result::InvalidValueType;
    if (program_->version_ >= 8 && !isBranchActive(branchGuard(entryInstruction, program_->version_)))
        return resumeBranch >= 0 ? Result::NotReady : Result::Ok;
    if (program_->version_ >= 9)
        return dispatchSignalV9(entry, time, maxInstructions, resumeBranch);
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Signal);

    struct DispatchScope final {
        bool& active;
        ~DispatchScope() { active = false; }
    };
    auto scope = DispatchScope{dispatching_};
    dispatching_ = true;
    auto visited = std::size_t{0};
    auto pending = std::size_t{0};
    auto predecessor = noInput;
    auto stack = reinterpret_cast<std::uint8_t*>(work_) + program_->signalStackOffset_;
    auto selectBranch = [&](const std::uint8_t* current, unsigned branch) noexcept -> Result {
        if (branch >= current[19]) return Result::InvalidValueType;
        if (program_->version_ >= 5)
        {
            auto offset = read32(current + 224 + branch * 8);
            auto extraCount = read32(current + 228 + branch * 8);
            if (extraCount > program_->signalStackCapacity_ - pending) return Result::InvalidBinary;
            for (auto extra = extraCount; extra > 0; --extra)
            {
                auto target = read32(program_->bytes_ + program_->signalRoutesOffset_ +
                    offset + std::size_t{extra - 1} * 4);
                auto packed = target | ((index + 1) << 16);
                std::memcpy(stack + pending++ * sizeof(packed), &packed, sizeof(packed));
            }
        }
        predecessor = index;
        index = read32(current + 208 + branch * 4);
        return Result::Ok;
    };
    if (resumeBranch >= 0)
    {
        status = selectBranch(instruction(program_->bytes_, 3, index), static_cast<unsigned>(resumeBranch));
        if (status != Result::Ok) return status;
    }
    while (index != noInput || pending != 0)
    {
        if (index == noInput)
        {
            auto packed = std::uint32_t{};
            std::memcpy(&packed, stack + --pending * sizeof(packed), sizeof(packed));
            index = packed & 0xffff;
            predecessor = (packed >> 16) - 1;
            continue;
        }
        if (++visited > maxInstructions) return Result::InstructionBudgetExceeded;
        auto current = instruction(program_->bytes_, 3, index);
        if (program_->version_ >= 8 && !isBranchActive(branchGuard(current, program_->version_)))
        {
            index = noInput;
            continue;
        }
        auto nodeScope = ProfileScope(profiling_, ProfileEvent::Node, index, profiling_.nodes);
        auto descriptor = program_->bindings_[index].descriptor;
        if (descriptor == nullptr || descriptor->execution != CustomExecution::Signal)
            return Result::UnknownCustomNode;
        alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
        auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
        alignas(customWorkAlignment) auto constants = std::array<double, customMaxPorts * 4>{};
        for (auto input = 0u; input < current[18]; ++input)
        {
            auto inputRecord = current + 48 + input * 8;
            auto sourceIndex = read32(inputRecord);
            inputWidths[input] = descriptor->inputWidths[input];
            for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane)
            {
                constants[input * 4 + lane] = defaults_ == nullptr ?
                    readDouble(current + 80 + input * 32 + lane * 8) :
                    defaults_[index * 16 + input * 4 + lane];
                inputs[input * 4 + lane] = sourceIndex == noInput ? constants[input * 4 + lane] :
                    work_[sourceIndex * 4 + inputRecord[5] + lane];
            }
        }
        auto state = reinterpret_cast<std::uint8_t*>(work_) +
            stateOffset(program_->bindings_, index);
        status = debugResolvedInputs(index, time, inputs.data(), inputWidths.data(), descriptor->inputIsColor,
            predecessor == noInput ? nullptr : instruction(program_->bytes_, 3, predecessor));
        if (status != Result::Ok) return status;
        if (signalObserver_ != nullptr)
        {
            status = signalObserver_(signalObserverContext_, current);
            if (status != Result::Ok) return status;
        }
        for (auto lane = 0u; lane < current[17]; ++lane) work_[index * 4 + lane] = 0;
        auto invocation = CustomNodeInvocation{
            inputs.data(), current[18], inputWidths.data(), nullptr, 0,
            constants.data(), std::size_t{current[18]} * 4,
            state, descriptor->stateBytes, time, 0xff, descriptor->context,
            effectTime_, frameDelta_, evaluatedTimeContext_};
        status = descriptor->function(invocation);
        if (status != Result::Ok) return status;
        if (invocation.enterPhase != 0)
        {
            if (invocation.enterPhase > 3) return Result::InvalidArgument;
            pendingPhase_ = invocation.enterPhase;
            pendingPhaseTime_ = time;
        }
        if (invocation.forceScope != 0)
        {
            if (!forceRequestsEnabled_) return Result::UnsupportedBinary;
            if (invocation.forceScope > 2) return Result::InvalidArgument;
            forceScope_ = invocation.forceScope;
            deferredCount_ = 0;
            return Result::Ok;
        }
        if (current[19] == 0)
        {
            index = noInput;
            continue;
        }
        if (invocation.selectedBranch >= current[19]) return Result::InvalidValueType;
        if (invocation.deferSignal)
        {
            auto guardId = program_->version_ >= 8 ? branchGuard(current, program_->version_) : noInput;
            auto usesBranchTime = guardId != noInput;
            auto baseTime = time;
            if (usesBranchTime)
            {
                if (guardId >= program_->branchGuardCount_ || guardId >= 0xff)
                    return Result::UnsupportedBinary;
                auto clock = reinterpret_cast<const std::uint8_t*>(work_) +
                    program_->branchClockOffset_ + std::size_t{guardId} * 24;
                baseTime = readDouble(clock);
            }
            auto due = baseTime + invocation.delaySeconds;
            if (!std::isfinite(invocation.delaySeconds) || invocation.delaySeconds < 0 || !std::isfinite(due))
                return Result::InvalidArgument;
            if (!deferredSignals_) return Result::NotReady;
            if (deferredCount_ == deferredCapacity_) return Result::QueueRejected;
            deferredSignals_[deferredCount_++] = {
                due, index, invocation.selectedBranch,
                usesBranchTime ? static_cast<std::uint8_t>(guardId) : std::uint8_t{0xff},
                usesBranchTime};
            index = noInput;
            continue;
        }
        status = selectBranch(current, invocation.selectedBranch);
        if (status != Result::Ok) return status;
    }
    return Result::Ok;
}

void Instance::run(void* context) noexcept
{
    auto instance = static_cast<Instance*>(context);
    if (instance->pendingPhase_ != 0)
    {
        instance->currentPhase_ = instance->pendingPhase_ - 1;
        instance->phaseStartTime_ = instance->pendingPhaseTime_;
        instance->pendingPhase_ = 0;
    }
    instance->result_ = instance->evaluate();
    if (instance->program_->version_ >= 9)
    {
        if (instance->result_ == Result::Ok)
        {
            for (auto logicalIndex = 0u;
                 logicalIndex < instance->program_->logicalInstructionCount_; ++logicalIndex)
            {
                auto record = instance->program_->bytes_ + instance->program_->logicalInstructionRecordsOffset_ +
                    std::size_t{logicalIndex} * logicalInstructionRecordSize;
                auto nodeIndex = read32(record);
                auto current = instruction(instance->program_->bytes_, 3, nodeIndex);
                if (!instance->isBranchActive(branchGuard(current, instance->program_->version_)))
                    continue;
                auto binding = instance->program_->bindings_[nodeIndex];
                if (binding.logicalDescriptor == nullptr || binding.logicalDescriptor->commit == nullptr)
                    continue;
                auto state = reinterpret_cast<std::uint8_t*>(instance->work_) +
                    stateOffset(instance->program_->bindings_, nodeIndex);
                auto invocation = LogicalNodeInvocation{
                    nullptr, 0, nullptr, instance->work_ + nodeIndex * 4,
                    binding.logicalDescriptor->outputWidth, state, binding.logicalDescriptor->stateBytes,
                    read32(record + 24), instance->pendingEvaluationSerial_, 0xff, 0, instance->time_};
                binding.logicalDescriptor->commit(invocation);
            }
            instance->evaluationSerial_ = instance->pendingEvaluationSerial_;
            instance->generatedSignalsPending_ = instance->program_->logicalInstructionCount_ != 0;
        }
        else
        {
            auto eventMasks = reinterpret_cast<std::uint8_t*>(instance->work_) +
                instance->program_->logicalEventMaskOffset_;
            std::memset(eventMasks, 0, std::size_t{instance->program_->nodeCount_} * sizeof(std::uint64_t));
        }
    }
    instance->phase_.store(Phase::Complete, std::memory_order_release);
}

auto Instance::isBranchActive(std::uint32_t guardId) const noexcept -> bool
{
    if (program_ == nullptr || program_->version_ < 8 || guardId == noInput) return true;
    auto chain = std::array<std::uint32_t, 64>{};
    auto depth = std::size_t{0};
    auto currentGuard = guardId;
    while (currentGuard != noInput)
    {
        if (depth == chain.size() || currentGuard >= program_->branchGuardCount_) return false;
        chain[depth++] = currentGuard;
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
            std::size_t{currentGuard} * enumGuardRecordSize;
        currentGuard = read32(record + 20);
    }
    while (depth != 0)
    {
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
            std::size_t{chain[--depth]} * enumGuardRecordSize;
        auto selector = read32(record);
        auto selected = work_[selector * 4];
        auto isInteger = std::isfinite(selected) && std::trunc(selected) == selected &&
            selected >= std::numeric_limits<std::int32_t>::min() &&
            selected <= std::numeric_limits<std::int32_t>::max();
        auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
        auto matched = false;
        auto caseCount = read16(record + 28);
        auto valuesOffset = read32(record + 32);
        for (auto index = 0u; index < caseCount; ++index)
            matched = matched || (isInteger &&
                readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ + valuesOffset +
                    std::size_t{index} * 4) == selectedValue);
        if ((read32(record + 24) & 1u) != 0)
        {
            auto excluded = false;
            auto excludedCount = read16(record + 30);
            auto excludedOffset = read32(record + 36);
            for (auto index = 0u; index < excludedCount; ++index)
                excluded = excluded || (isInteger &&
                    readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ + excludedOffset +
                        std::size_t{index} * 4) == selectedValue);
            matched = matched || !excluded;
        }
        if (!matched) return false;
    }
    return true;
}

auto Instance::updateBranchClocks(std::uint32_t guardId, bool& active) noexcept -> Result
{
    active = true;
    if (program_ == nullptr || program_->version_ < 8 || guardId == noInput) return Result::Ok;
    auto chain = std::array<std::uint32_t, 64>{};
    auto depth = std::size_t{0};
    auto currentGuard = guardId;
    while (currentGuard != noInput)
    {
        if (depth == chain.size() || currentGuard >= program_->branchGuardCount_)
            return Result::InvalidBinary;
        chain[depth++] = currentGuard;
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
            std::size_t{currentGuard} * enumGuardRecordSize;
        currentGuard = read32(record + 20);
    }
    auto globalTime = evaluatedTimeContext_ ? effectTime_ : time_;
    while (depth != 0)
    {
        auto guardIndex = chain[--depth];
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
            std::size_t{guardIndex} * enumGuardRecordSize;
        auto selector = read32(record);
        auto selected = work_[selector * 4];
        auto isInteger = std::isfinite(selected) && std::trunc(selected) == selected &&
            selected >= std::numeric_limits<std::int32_t>::min() &&
            selected <= std::numeric_limits<std::int32_t>::max();
        auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
        auto matched = false;
        auto caseCount = read16(record + 28);
        auto valuesOffset = read32(record + 32);
        for (auto index = 0u; index < caseCount; ++index)
            matched = matched || (isInteger &&
                readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ + valuesOffset +
                    std::size_t{index} * 4) == selectedValue);
        if ((read32(record + 24) & 1u) != 0)
        {
            auto excluded = false;
            auto excludedCount = read16(record + 30);
            auto excludedOffset = read32(record + 36);
            for (auto index = 0u; index < excludedCount; ++index)
                excluded = excluded || (isInteger &&
                    readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ + excludedOffset +
                        std::size_t{index} * 4) == selectedValue);
            matched = matched || !excluded;
        }
        active = active && matched;
        auto clock = reinterpret_cast<std::uint8_t*>(work_) + program_->branchClockOffset_ +
            std::size_t{guardIndex} * 24;
        auto localTime = readDouble(clock);
        auto lastGlobalTime = readDouble(clock + 8);
        auto lastSerial = read64(clock + 16);
        if (lastSerial != updateSerial_)
        {
            auto delta = evaluatedTimeContext_ ? frameDelta_ :
                (globalTime >= lastGlobalTime ? globalTime - lastGlobalTime : 0.0);
            if (lastSerial == 0) delta = 0;
            if (active && delta > 0) localTime += delta;
            if (!std::isfinite(localTime)) return Result::NonFinite;
            writeDouble(clock, localTime);
            writeDouble(clock + 8, globalTime);
            write64(clock + 16, updateSerial_);
        }
    }
    return Result::Ok;
}

auto Instance::evaluate(bool pureRefresh, const bool* dirty) noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Evaluate);
    if (pureRefresh && dirty == nullptr) return Result::InvalidArgument;
    if (program_->version_ == 1)
    {
        if (pureRefresh) return Result::InvalidArgument;
        for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
        {
            auto current = instruction(program_->bytes_, 1, index);
            auto debugStatus = beforeDebugNode(index);
            if (debugStatus != Result::Ok) return debugStatus;
            auto nodeScope = ProfileScope(profiling_, ProfileEvent::Node, index, profiling_.nodes);
            auto aIndex = read32(current + 20);
            auto bIndex = read32(current + 24);
            auto a = aIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 28) : defaults_[index * 2]) : work_[aIndex];
            auto b = bIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 36) : defaults_[index * 2 + 1]) : work_[bIndex];
            auto value = double{};
            switch (current[16])
            {
            case 1: value = a; break;
            case 2: value = time_; break;
            case 3: value = a + b; break;
            case 4: value = a - b; break;
            case 5: value = a * b; break;
            case 6:
                if (b == 0) return Result::DivideByZero;
                value = a / b;
                break;
            default: return Result::UnsupportedBinary;
            }
            if (!std::isfinite(value)) return Result::NonFinite;
            work_[index] = value;
        }
        return Result::Ok;
    }
    if (!pureRefresh && ++updateSerial_ == 0)
    {
        updateSerial_ = 1;
        for (auto index = 0u; index < program_->branchGuardCount_; ++index)
        {
            auto clock = reinterpret_cast<std::uint8_t*>(work_) + program_->branchClockOffset_ +
                std::size_t{index} * 24;
            write64(clock + 16, 0);
        }
    }
    if (!pureRefresh && program_->version_ >= 9)
    {
        pendingEvaluationSerial_ = evaluationSerial_ + 1;
        auto eventMasks = reinterpret_cast<std::uint8_t*>(work_) + program_->logicalEventMaskOffset_;
        std::memset(eventMasks, 0, std::size_t{program_->nodeCount_} * sizeof(std::uint64_t));
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        auto opcode = current[16];
        if (pureRefresh)
        {
            if (!dirty[index] || opcode == customSignalOpcode ||
                opcode == logicalEventSignalOpcode)
                continue;
        }
        auto guardId = program_->version_ >= 8 ? branchGuard(current, program_->version_) : noInput;
        if (guardId != noInput)
        {
            if (pureRefresh)
            {
                if (!isBranchActive(guardId)) continue;
            }
            else
            {
                auto active = false;
                auto clockStatus = updateBranchClocks(guardId, active);
                if (clockStatus != Result::Ok) return clockStatus;
                if (!active) continue;
            }
        }
        if (opcode != customParameterOpcode && opcode != customSignalOpcode &&
            opcode != logicalEventSignalOpcode && opcode != enumSelectOpcode)
        {
            auto debugStatus = beforeDebugNode(index);
            if (debugStatus != Result::Ok) return debugStatus;
        }
        auto nodeScope = ProfileScope(profiling_, ProfileEvent::Node, index,
            profiling_.nodes && opcode != customSignalOpcode);
        if (program_->version_ >= 3 &&
            (opcode == customParameterOpcode || opcode == customSignalOpcode))
        {
            if (opcode == customSignalOpcode) continue;
            auto descriptor = program_->bindings_[index].descriptor;
            if (descriptor == nullptr) return Result::UnknownCustomNode;
            if (pureRefresh && !isLatePureBuiltin(descriptor)) return Result::UnsupportedBinary;
            alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
            auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
            alignas(customWorkAlignment) auto constants = std::array<double, customMaxPorts * 4>{};
            for (auto input = 0u; input < current[18]; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = descriptor->inputWidths[input];
                for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane)
                {
                    constants[input * 4 + lane] = defaults_ == nullptr ?
                        readDouble(current + 80 + input * 32 + lane * 8) :
                        defaults_[index * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput ? constants[input * 4 + lane] :
                        work_[sourceIndex * 4 + inputRecord[5] + lane];
                }
            }
            auto state = reinterpret_cast<std::uint8_t*>(work_) +
                stateOffset(program_->bindings_, index);
            auto invocation = CustomNodeInvocation{
                inputs.data(), current[18], inputWidths.data(), work_ + index * 4, current[17],
                constants.data(), std::size_t{current[18]} * 4,
                state, descriptor->stateBytes, time_, 0xff, descriptor->context,
                effectTime_, frameDelta_, evaluatedTimeContext_};
            auto debugStatus = debugResolvedInputs(index, time_, inputs.data(), inputWidths.data(),
                descriptor->inputIsColor);
            if (debugStatus != Result::Ok) return debugStatus;
            auto status = descriptor->function(invocation);
            if (status != Result::Ok) return status;
            for (auto lane = 0u; lane < current[17]; ++lane)
                if (!std::isfinite(work_[index * 4 + lane])) return Result::NonFinite;
            continue;
        }
        if (opcode == logicalEventSignalOpcode)
        {
            if (pureRefresh) continue;
            auto descriptor = program_->bindings_[index].logicalDescriptor;
            if (descriptor == nullptr || descriptor->update == nullptr) return Result::UnknownCustomNode;
            auto record = static_cast<const std::uint8_t*>(nullptr);
            for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_; ++logicalIndex)
            {
                auto candidate = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                    std::size_t{logicalIndex} * logicalInstructionRecordSize;
                if (read32(candidate) == index) { record = candidate; break; }
            }
            if (record == nullptr) return Result::InvalidBinary;
            alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
            auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
            for (auto input = 0u; input < current[18]; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = descriptor->inputWidths[input];
                for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane)
                {
                    auto defaultValue = defaults_ == nullptr ?
                        readDouble(current + 80 + input * 32 + lane * 8) :
                        defaults_[index * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput ? defaultValue :
                        work_[sourceIndex * 4 + inputRecord[5] + lane];
                }
            }
            auto state = reinterpret_cast<std::uint8_t*>(work_) +
                stateOffset(program_->bindings_, index);
            auto invocation = LogicalNodeInvocation{
                inputs.data(), current[18], inputWidths.data(), work_ + index * 4, current[17],
                state, descriptor->stateBytes, read32(record + 24), pendingEvaluationSerial_,
                0xff, 0, time_};
            auto debugStatus = debugResolvedInputs(index, time_, inputs.data(), inputWidths.data());
            if (debugStatus != Result::Ok) return debugStatus;
            auto status = descriptor->update(invocation);
            if (status != Result::Ok) return status;
            auto validBranches = descriptor->signalBranchCount == 0 ? std::uint64_t{0} :
                (std::uint64_t{1} << descriptor->signalBranchCount) - 1;
            if ((invocation.emittedBranches & ~validBranches) != 0)
                return Result::InvalidValueType;
            for (auto lane = 0u; lane < descriptor->outputWidth; ++lane)
                if (!std::isfinite(work_[index * 4 + lane])) return Result::NonFinite;
            auto eventMasks = reinterpret_cast<std::uint8_t*>(work_) + program_->logicalEventMaskOffset_;
            write64(eventMasks + std::size_t{index} * sizeof(std::uint64_t), invocation.emittedBranches);
            continue;
        }
        if (opcode == currentPhaseOpcode || opcode == phaseTimeOpcode)
        {
            if (time_ < phaseStartTime_) return Result::InvalidArgument;
            work_[index * 4] = opcode == currentPhaseOpcode
                ? static_cast<double>(currentPhase_) : time_ - phaseStartTime_;
            continue;
        }
        if (opcode == enumValueOpcode)
        {
            auto sourceIndex = read32(current + 24);
            auto enumValue = sourceIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 32) : defaults_[index * 16]) :
                work_[sourceIndex * 4 + current[21]];
            if (!std::isfinite(enumValue) || std::trunc(enumValue) != enumValue ||
                enumValue < std::numeric_limits<std::int32_t>::min() ||
                enumValue > std::numeric_limits<std::int32_t>::max())
                return Result::InvalidValueType;
            work_[index * 4] = enumValue;
            continue;
        }
        if (opcode == enumSelectOpcode)
        {
            auto selectRecord = static_cast<const std::uint8_t*>(nullptr);
            for (auto selectIndex = 0u; selectIndex < program_->enumSelectCount_; ++selectIndex)
            {
                auto candidate = program_->bytes_ + program_->enumSelectRecordsOffset_ +
                    std::size_t{selectIndex} * enumSelectRecordSize;
                if (read32(candidate) == index)
                {
                    selectRecord = candidate;
                    break;
                }
            }
            if (selectRecord == nullptr) return Result::InvalidBinary;
            auto selector = read32(selectRecord + 4);
            auto selected = work_[selector * 4];
            auto isInteger = std::isfinite(selected) && std::trunc(selected) == selected &&
                selected >= std::numeric_limits<std::int32_t>::min() &&
                selected <= std::numeric_limits<std::int32_t>::max();
            auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
            auto caseCount = selectRecord[29];
            auto casesOffset = read32(selectRecord + 32);
            auto selectedCase = static_cast<const std::uint8_t*>(nullptr);
            for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
            {
                auto candidate = program_->bytes_ + program_->enumBranchMetadataOffset_ + casesOffset +
                    std::size_t{caseIndex} * enumCaseRecordSize;
                if (isInteger && readI32(candidate) == selectedValue)
                {
                    selectedCase = candidate;
                    break;
                }
            }
            auto sourceIndex = noInput;
            auto defaultValues = static_cast<const std::uint8_t*>(nullptr);
            if (selectedCase != nullptr)
            {
                sourceIndex = read32(selectedCase + 4);
                defaultValues = selectedCase + 8;
                if (sourceIndex == noInput && read32(selectRecord + 8) != noInput)
                    sourceIndex = read32(selectRecord + 8);
            }
            else
            {
                sourceIndex = read32(selectRecord + 8);
                defaultValues = selectRecord + 36;
            }
            auto debugStatus = debugEnumSelect(index, selector, sourceIndex, defaultValues);
            if (debugStatus != Result::Ok) return debugStatus;
            for (auto lane = 0u; lane < current[17]; ++lane)
                work_[index * 4 + lane] = sourceIndex == noInput ?
                    readDouble(defaultValues + lane * 8) :
                    work_[sourceIndex * 4 + lane];
            continue;
        }
        if (opcode == branchTimeOpcode)
        {
            auto clockIndex = read32(current + 112);
            if (clockIndex >= program_->branchGuardCount_) return Result::InvalidBinary;
            auto clock = reinterpret_cast<std::uint8_t*>(work_) + program_->branchClockOffset_ +
                std::size_t{clockIndex} * 24;
            work_[index * 4] = readDouble(clock);
            continue;
        }
        if (opcode == curveOpcode)
        {
            auto instructionBytes = typedHeaderSize +
                std::size_t{program_->nodeCount_} * customInstructionSize;
            auto curveKeys = program_->bytes_ + instructionBytes + read32(current + 224);
            work_[index * 4] = evaluateCurve(curveKeys, read32(current + 228), time_);
            if (!std::isfinite(work_[index * 4])) return Result::NonFinite;
            continue;
        }
        auto outputWidth = current[17];
        auto aIndex = read32(current + 24);
        auto bIndex = read32(current + 28);
        auto base = index * 4;
        auto valueA = [&](auto lane) noexcept {
            return aIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 32 + lane * 8) :
                    defaults_[index * (program_->version_ >= 3 ? 16 : 8) + lane]) :
                work_[aIndex * 4 + current[21] + lane];
        };
        auto valueB = [&](auto lane) noexcept {
            return bIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 64 + lane * 8) :
                    defaults_[index * (program_->version_ >= 3 ? 16 : 8) + 4 + lane]) :
                work_[bIndex * 4 + current[22] + lane];
        };
        if (opcode >= int32Opcode && opcode <= int32ToFloatOpcode)
        {
            auto a = valueA(0);
            if (opcode == floatToInt32Opcode)
            {
                if (!std::isfinite(a)) return Result::NonFinite;
                auto truncated = std::trunc(a);
                if (truncated < std::numeric_limits<std::int32_t>::min() ||
                    truncated > std::numeric_limits<std::int32_t>::max())
                    return Result::ArithmeticOverflow;
                work_[base] = truncated;
                continue;
            }
            auto integerA = std::int32_t{};
            auto status = int32FromDouble(a, integerA);
            if (status != Result::Ok) return status;
            if (opcode == int32Opcode)
            {
                work_[base] = integerA;
                continue;
            }
            if (opcode == int32ToFloatOpcode)
            {
                work_[base] = static_cast<double>(integerA);
                continue;
            }
            auto integerB = std::int32_t{};
            status = int32FromDouble(valueB(0), integerB);
            if (status != Result::Ok) return status;
            if (opcode == int32DivideOpcode)
            {
                if (integerB == 0) return Result::DivideByZero;
                if (integerA == std::numeric_limits<std::int32_t>::min() && integerB == -1)
                    return Result::ArithmeticOverflow;
                work_[base] = integerA / integerB;
                continue;
            }
            auto result = std::int64_t{};
            if (opcode == int32AddOpcode)
                result = std::int64_t{integerA} + integerB;
            else if (opcode == int32SubtractOpcode)
                result = std::int64_t{integerA} - integerB;
            else if (opcode == int32MultiplyOpcode)
                result = std::int64_t{integerA} * integerB;
            else return Result::UnsupportedBinary;
            if (result < std::numeric_limits<std::int32_t>::min() ||
                result > std::numeric_limits<std::int32_t>::max())
                return Result::ArithmeticOverflow;
            work_[base] = static_cast<double>(result);
            continue;
        }
        if (opcode == 1 || opcode == 7 || opcode == 8 || opcode == 9 || opcode == 10)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane) work_[base + lane] = valueA(lane);
        }
        else if (opcode == 2)
        {
            work_[base] = time_;
        }
        else if (opcode >= 3 && opcode <= 22)
        {
            if (outputWidth > 1)
            {
                auto aValues = std::array<double, 4>{};
                auto bValues = std::array<double, 4>{};
                auto parameterStride = program_->version_ >= 3 ? 16u : 8u;
                auto a = aIndex != noInput ? work_ + aIndex * 4 + current[21] :
                    defaults_ != nullptr ? defaults_ + index * parameterStride : nullptr;
                auto b = bIndex != noInput ? work_ + bIndex * 4 + current[22] :
                    defaults_ != nullptr ? defaults_ + index * parameterStride + 4 : nullptr;
                if (a == nullptr)
                {
                    for (auto lane = 0u; lane < outputWidth; ++lane) aValues[lane] = valueA(lane);
                    a = aValues.data();
                }
                if (b == nullptr)
                {
                    for (auto lane = 0u; lane < outputWidth; ++lane) bValues[lane] = valueB(lane);
                    b = bValues.data();
                }
                auto status = math::arithmetic(work_ + base, a, b, outputWidth,
                    static_cast<math::Arithmetic>((opcode - 3) % 4));
                if (status == math::ArithmeticResult::DivideByZero) return Result::DivideByZero;
                if (status == math::ArithmeticResult::NonFinite) return Result::NonFinite;
                if (status != math::ArithmeticResult::Ok) return Result::InvalidArgument;
                continue;
            }
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                auto a = valueA(lane);
                auto b = valueB(lane);
                auto value = double{};
                switch ((opcode - 3) % 4)
                {
                case 0: value = a + b; break;
                case 1: value = a - b; break;
                case 2: value = a * b; break;
                case 3:
                    if (b == 0) return Result::DivideByZero;
                    value = a / b;
                    break;
                }
                if (!std::isfinite(value)) return Result::NonFinite;
                work_[base + lane] = value;
            }
        }
        else if (opcode >= 23 && opcode <= 25)
        {
            auto value = valueA(0);
            for (auto lane = 0u; lane < outputWidth; ++lane) work_[base + lane] = value;
        }
        else if (opcode >= 26 && opcode <= 28)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
                work_[base + lane] = valueA(lane);
        }
        else if (opcode >= 29 && opcode <= 34)
        {
            auto copied = opcode == 29 || opcode == 30 ? 2u : opcode == 31 || opcode == 32 ? 3u : 4u;
            for (auto lane = 0u; lane < outputWidth; ++lane)
                work_[base + lane] = lane < copied ? valueA(lane) :
                    (defaults_ == nullptr ? readDouble(current + 32) :
                        defaults_[index * (program_->version_ >= 3 ? 16 : 8)]);
        }
        else if (opcode >= 35 && opcode <= 37)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
                work_[base + lane] = valueA(static_cast<std::uint32_t>(
                    defaults_ == nullptr ? readDouble(current + 32 + lane * 8) :
                        defaults_[index * (program_->version_ >= 3 ? 16 : 8) + lane]));
        }
        else if (opcode == 38 || opcode == 39)
        {
            for (auto lane = 0u; lane < 4; ++lane) work_[base + lane] = valueA(lane);
        }
        else return Result::UnsupportedBinary;
        for (auto lane = 0u; lane < outputWidth; ++lane)
            if (!std::isfinite(work_[base + lane])) return Result::NonFinite;
    }
    return Result::Ok;
}

}
