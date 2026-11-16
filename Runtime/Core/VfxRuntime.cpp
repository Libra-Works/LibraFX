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
constexpr auto noInput = std::uint32_t{0xffffffffu};
constexpr auto customParameterOpcode = std::uint8_t{40};
constexpr auto customSignalOpcode = std::uint8_t{41};
constexpr auto curveOpcode = std::uint8_t{42};
constexpr auto customMaxPorts = std::uint8_t{4};
constexpr auto customWorkAlignment = std::size_t{16};
constexpr auto maxCustomStateAlignment = std::size_t{64};
std::atomic<std::uint64_t> nextProgramGeneration{1};
std::atomic<std::uint64_t> nextInstanceGeneration{1};
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559,
              "LIBERA FX requires IEEE-754 binary64.");

auto read16(const std::uint8_t* bytes) noexcept -> std::uint16_t
{
    return static_cast<std::uint16_t>(bytes[0] | (std::uint16_t{bytes[1]} << 8));
}

auto read32(const std::uint8_t* bytes) noexcept -> std::uint32_t
{
    return std::uint32_t{bytes[0]} | (std::uint32_t{bytes[1]} << 8) |
           (std::uint32_t{bytes[2]} << 16) | (std::uint32_t{bytes[3]} << 24);
}

auto readDouble(const std::uint8_t* bytes) noexcept -> double
{
    auto bits = std::uint64_t{0};
    for (auto index = 0u; index < 8; ++index)
        bits |= std::uint64_t{bytes[index]} << (index * 8);
    auto value = double{};
    std::memcpy(&value, &bits, sizeof(value));
    return value;
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

auto validIndex(std::uint32_t input, std::uint32_t index) noexcept -> bool
{
    return input == noInput || input < index;
}

auto typedInputWidth(std::uint32_t input, std::uint8_t width) noexcept -> bool
{
    return input == noInput ? width == 0 : width >= 1 && width <= 4;
}

auto requiredInputWidths(std::uint8_t opcode, std::uint8_t outputWidth,
                         std::uint8_t& inputAWidth, std::uint8_t& inputBWidth,
                         bool& requiresA, bool& requiresB) noexcept -> bool
{
    requiresA = false;
    requiresB = false;
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

auto validCurveInstruction(const std::uint8_t* current) noexcept -> bool
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
        if (current[byte] != 0) return false;
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
    if (version == 4 || version == 5) return load(bytes, size, nullptr, 0, nullptr, 0, out, profiling);
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
    if (version != 3 && version != 4 && version != 5) return load(bytes, size, out, profiling);
    auto profileScope = ProfileScope(profiling, ProfileEvent::Load);
    if (size < typedHeaderSize) return Result::InvalidBinary;
    if (data[6] != 1) return Result::UnsupportedBinary;
    if (data[7] != 0 || read16(data + 12) != customInstructionSize || read16(data + 14) != 0)
        return Result::InvalidBinary;
    auto count = read32(data + 8);
    auto instructionBytes = typedHeaderSize + std::size_t{count} * customInstructionSize;
    if (count > maxNodes || size < instructionBytes ||
        (version == 3 && size != instructionBytes))
        return Result::InvalidBinary;
    auto hasCustomNodes = false;
    for (auto index = 0u; index < count; ++index)
    {
        auto opcode = instruction(data, version, index)[16];
        hasCustomNodes = hasCustomNodes || opcode == customParameterOpcode || opcode == customSignalOpcode;
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
              descriptor.branchCount < 1 || descriptor.branchCount > customMaxPorts)) ||
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
    auto curveDataBytes = size - instructionBytes;
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
            if (version < 4 || !validCurveInstruction(current)) return Result::InvalidBinary;
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
        if (opcode >= 1 && opcode <= 39)
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
                if (current[byte] != 0) return Result::InvalidBinary;
            continue;
        }
        if (opcode != customParameterOpcode && opcode != customSignalOpcode)
            return Result::UnsupportedBinary;
        auto inputCount = current[18];
        auto branchCount = current[19];
        auto outputWidth = current[17];
        if (inputCount > customMaxPorts || (current[20] & 0xfe) != 0 || current[21] != 0 ||
            current[22] != 0 || current[23] != 0 || read32(current + 44) != 0 ||
            read32(current + 40) == 0 ||
            (opcode == customParameterOpcode && (outputWidth < 1 || outputWidth > 4 || branchCount != 0)) ||
            (opcode == customSignalOpcode && (outputWidth != 0 || branchCount < 1 || branchCount > customMaxPorts ||
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
                     instruction(data, 3, target)[16] != customSignalOpcode))
                    return Result::InvalidBinary;
            }
            else if (target != noInput) return Result::InvalidBinary;
        }
    }
    if (version == 4 && expectedCurveOffset != curveDataBytes) return Result::InvalidBinary;
    auto routeDataOffset = instructionBytes + expectedCurveOffset;
    auto routeBytes = std::size_t{0};
    if (version == 5)
    {
        for (auto index = std::uint32_t{0}; index < count; ++index)
        {
            auto current = instruction(data, version, index);
            if (current[16] != customSignalOpcode) continue;
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
                    extraCount > (size - routeDataOffset - routeBytes) / 4)
                    return Result::InvalidBinary;
                for (auto entry = 0u; entry < extraCount; ++entry)
                {
                    auto target = read32(data + routeDataOffset + routeBytes + std::size_t{entry} * 4);
                    if (target <= index || target >= count ||
                        instruction(data, version, target)[16] != customSignalOpcode)
                        return Result::InvalidBinary;
                }
                routeBytes += std::size_t{extraCount} * 4;
            }
        }
        if (routeBytes != size - routeDataOffset) return Result::InvalidBinary;
    }
    auto stackOffset = totalWorkBytes;
    if (routeBytes != 0)
    {
        if (!alignUp(totalWorkBytes, alignof(double), stackOffset) ||
            routeBytes > std::numeric_limits<std::size_t>::max() - stackOffset)
            return Result::InvalidBinary;
        if (!alignUp(stackOffset + routeBytes, alignof(double), totalWorkBytes))
            return Result::InvalidBinary;
    }
    auto boundStateOffset = valuesSize;
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, 3, index);
        auto descriptor = static_cast<const CustomNodeDescriptor*>(nullptr);
        if (current[16] == customParameterOpcode || current[16] == customSignalOpcode)
        {
            auto registeredType = false;
            descriptor = resolveDescriptor(current + 24, read32(current + 40), registeredType);
        }
        if (descriptor != nullptr)
        {
            auto alignedStateOffset = std::size_t{0};
            if (!alignUp(boundStateOffset, descriptor->stateAlignment, alignedStateOffset))
                return Result::InvalidBinary;
            boundStateOffset = alignedStateOffset;
        }
        if (bindings != nullptr)
        {
            bindings[index].descriptor = descriptor;
            bindings[index].stateOffset = boundStateOffset;
        }
        if (descriptor != nullptr) boundStateOffset += descriptor->stateBytes;
    }
    out.bytes_ = data;
    out.nodeCount_ = count;
    out.version_ = version;
    out.bindings_ = bindings;
    out.stateBytes_ = totalWorkBytes - valuesSize;
    out.signalRoutesOffset_ = routeDataOffset;
    out.signalStackOffset_ = stackOffset;
    out.signalStackCapacity_ = routeBytes / 4;
    out.generation_ = nextProgramGeneration.fetch_add(1, std::memory_order_relaxed);
    return Result::Ok;
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
            if (bindings_[index].descriptor != nullptr)
                alignment = std::max(alignment, bindings_[index].descriptor->stateAlignment);
    return alignment;
}

auto Program::parameterValueCount() const noexcept -> std::size_t
{
    return std::size_t{nodeCount_} * (version_ == 1 ? 2 : version_ == 2 ? 8 : 16);
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
    if (dispatching_) return Result::Busy;
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
            else if (program.version_ == 2 || current[16] <= 39 || current[16] == curveOpcode)
                for (auto lane = 0u; lane < 8; ++lane)
                    defaults[index * (program.version_ >= 3 ? 16 : 8) + lane] =
                        readDouble(current + (lane < 4 ? 32 + lane * 8 : 64 + (lane - 4) * 8));
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
                    if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
                        (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
                    else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
                    else if (opcode >= 35 && opcode <= 37) laneCount = width;
                }
                else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
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
            if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
                (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
            else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
            else if (opcode >= 35 && opcode <= 37) laneCount = width;
        }
        else if (handle.inputSlot == 1 &&
                 ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))) laneCount = width;
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
            if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
                (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
            else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
            else if (opcode >= 35 && opcode <= 37) laneCount = width;
        }
        else if (handle.inputSlot == 1 &&
                 ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))) laneCount = width;
        if (handle.inputSlot > 1 || handle.component >= laneCount) return Result::InvalidHandle;
        if (program_->version_ == 1)
            defaults_[handle.nodeIndex * 2 + handle.inputSlot] = value;
        else
            defaults_[handle.nodeIndex * (program_->version_ >= 3 ? 16 : 8) +
                handle.inputSlot * 4 + handle.component] = value;
    }
    return Result::Ok;
}

auto Instance::updateAsync(double time, Enqueue enqueue, void* queueContext) noexcept -> Result
{
    if (dispatching_) return Result::Busy;
    if (program_ == nullptr || enqueue == nullptr || !std::isfinite(time)) return Result::InvalidArgument;
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Schedule);
    auto phase = phase_.load(std::memory_order_acquire);
    for (;;)
    {
        if (phase == Phase::Running) return Result::Busy;
        if (phase_.compare_exchange_weak(phase, Phase::Running, std::memory_order_acq_rel)) break;
    }
    time_ = time;
    if (!enqueue(queueContext, &Instance::run, this))
    {
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
        }
        else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))
            laneCount = width;
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
    if (instruction(program_->bytes_, 3, index)[16] != customSignalOpcode)
        return Result::InvalidValueType;
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Signal);

    struct DispatchScope final {
        bool& active;
        ~DispatchScope() { active = false; }
    };
    auto scope = DispatchScope{dispatching_};
    dispatching_ = true;
    auto visited = std::size_t{0};
    auto pending = std::size_t{0};
    auto stack = reinterpret_cast<std::uint8_t*>(work_) + program_->signalStackOffset_;
    while (index != noInput || pending != 0)
    {
        if (index == noInput)
        {
            std::memcpy(&index, stack + --pending * sizeof(index), sizeof(index));
            continue;
        }
        if (++visited > maxInstructions) return Result::InstructionBudgetExceeded;
        auto current = instruction(program_->bytes_, 3, index);
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
        for (auto lane = 0u; lane < current[17]; ++lane) work_[index * 4 + lane] = 0;
        auto invocation = CustomNodeInvocation{
            inputs.data(), current[18], inputWidths.data(), nullptr, 0,
            constants.data(), std::size_t{current[18]} * 4,
            state, descriptor->stateBytes, time, 0xff, descriptor->context};
        status = descriptor->function(invocation);
        if (status != Result::Ok) return status;
        if (invocation.selectedBranch >= current[19]) return Result::InvalidValueType;
        if (program_->version_ >= 5)
        {
            auto offset = read32(current + 224 + invocation.selectedBranch * 8);
            auto extraCount = read32(current + 228 + invocation.selectedBranch * 8);
            if (extraCount > program_->signalStackCapacity_ - pending) return Result::InvalidBinary;
            for (auto extra = extraCount; extra > 0; --extra)
            {
                auto target = read32(program_->bytes_ + program_->signalRoutesOffset_ +
                    offset + std::size_t{extra - 1} * 4);
                std::memcpy(stack + pending++ * sizeof(target), &target, sizeof(target));
            }
        }
        index = read32(current + 208 + invocation.selectedBranch * 4);
    }
    return Result::Ok;
}

void Instance::run(void* context) noexcept
{
    auto instance = static_cast<Instance*>(context);
    instance->result_ = instance->evaluate();
    instance->phase_.store(Phase::Complete, std::memory_order_release);
}

auto Instance::evaluate() noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Evaluate);
    if (program_->version_ == 1)
    {
        for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
        {
            auto current = instruction(program_->bytes_, 1, index);
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
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        auto opcode = current[16];
        auto nodeScope = ProfileScope(profiling_, ProfileEvent::Node, index,
            profiling_.nodes && opcode != customSignalOpcode);
        if (program_->version_ >= 3 &&
            (opcode == customParameterOpcode || opcode == customSignalOpcode))
        {
            if (opcode == customSignalOpcode) continue;
            auto descriptor = program_->bindings_[index].descriptor;
            if (descriptor == nullptr) return Result::UnknownCustomNode;
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
                state, descriptor->stateBytes, time_, 0xff, descriptor->context};
            auto status = descriptor->function(invocation);
            if (status != Result::Ok) return status;
            for (auto lane = 0u; lane < current[17]; ++lane)
                if (!std::isfinite(work_[index * 4 + lane])) return Result::NonFinite;
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
