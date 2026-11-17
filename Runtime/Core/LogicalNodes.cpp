#include "LogicalNodes.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace liberafx {
namespace {

constexpr auto compareTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x01};
constexpr auto andTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x02};
constexpr auto orTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x03};
constexpr auto signalToNumberTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x04};

constexpr auto compareAbiVersion = std::uint32_t{1};
constexpr auto gateAbiVersion = std::uint32_t{1};
constexpr auto signalToNumberAbiVersion = std::uint32_t{1};
constexpr auto sameUpdateMode = std::uint8_t{0};
constexpr auto accumulateMode = std::uint8_t{1};
constexpr auto pulseMode = std::uint8_t{0};
constexpr auto latchMode = std::uint8_t{1};
constexpr auto counterMode = std::uint8_t{2};
constexpr auto maximumExactCounter = std::uint64_t{9007199254740991};

struct alignas(16) CompareState final {
    bool previousCondition = false;
    bool pendingCondition = false;
    bool pendingCommit = false;
};

struct alignas(16) GateState final {
    std::uint64_t accumulatedMask = 0;
    std::uint64_t sameUpdateMask = 0;
    std::uint64_t updateTick = 0;
    std::uint64_t emittedTick = 0;
    std::uint64_t pendingUpdateTick = 0;
    bool hasUpdateTick = false;
    bool hasEmittedTick = false;
    bool pendingUpdate = false;
    bool pendingSameUpdateReset = false;
};

struct alignas(16) SignalToNumberState final {
    std::uint64_t count = 0;
    bool latched = false;
};

auto isValidInvocation(const LogicalNodeInvocation& invocation, std::size_t stateBytes) noexcept -> bool
{
    return invocation.state != nullptr && invocation.stateBytes >= stateBytes &&
        invocation.outputs != nullptr && invocation.outputWidth == 1;
}

auto compareOperation(double left, double right, std::uint8_t operation) noexcept -> bool
{
    switch (operation)
    {
    case 0: return left < right;
    case 1: return left <= right;
    case 2: return left == right;
    case 3: return left != right;
    case 4: return left >= right;
    case 5: return left > right;
    default: return false;
    }
}

auto updateCompare(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (!isValidInvocation(invocation, sizeof(CompareState)) ||
        invocation.inputs == nullptr || invocation.inputCount != 2 ||
        invocation.inputWidths == nullptr || invocation.inputWidths[0] != 1 ||
        invocation.inputWidths[1] != 1)
        return Result::InvalidArgument;

    auto operation = static_cast<std::uint8_t>(invocation.configuration & 0xffu);
    auto mode = static_cast<std::uint8_t>((invocation.configuration >> 8u) & 0xffu);
    if ((invocation.configuration & 0xffff0000u) != 0 || operation > 5 || mode > 1)
        return Result::InvalidValueType;

    auto* state = static_cast<CompareState*>(invocation.state);
    auto isTrue = compareOperation(invocation.inputs[0], invocation.inputs[4], operation);
    auto shouldEmit = mode == 0 ? isTrue && !state->previousCondition : isTrue;
    state->pendingCondition = isTrue;
    state->pendingCommit = true;
    invocation.outputs[0] = isTrue ? 1.0 : 0.0;
    invocation.emittedBranches = shouldEmit ? std::uint64_t{1} : std::uint64_t{0};
    return Result::Ok;
}

auto commitCompare(LogicalNodeInvocation& invocation) noexcept -> void
{
    if (!isValidInvocation(invocation, sizeof(CompareState))) return;
    auto* state = static_cast<CompareState*>(invocation.state);
    if (!state->pendingCommit) return;
    state->previousCondition = state->pendingCondition;
    state->pendingCommit = false;
}

auto updateAnd(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (!isValidInvocation(invocation, sizeof(GateState)))
        return Result::InvalidArgument;
    if (invocation.configuration > accumulateMode) return Result::InvalidValueType;
    auto* state = static_cast<GateState*>(invocation.state);
    if (!state->hasUpdateTick || state->updateTick != invocation.eventTick)
    {
        state->pendingSameUpdateReset = true;
        state->pendingUpdateTick = invocation.eventTick;
        state->pendingUpdate = true;
    }
    invocation.outputs[0] = 0;
    invocation.emittedBranches = 0;
    return Result::Ok;
}

auto commitAnd(LogicalNodeInvocation& invocation) noexcept -> void
{
    if (!isValidInvocation(invocation, sizeof(GateState))) return;
    auto* state = static_cast<GateState*>(invocation.state);
    if (!state->pendingUpdate) return;
    if (state->pendingSameUpdateReset) state->sameUpdateMask = 0;
    state->updateTick = state->pendingUpdateTick;
    state->hasUpdateTick = true;
    state->pendingUpdate = false;
    state->pendingSameUpdateReset = false;
}

auto updateOr(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (!isValidInvocation(invocation, sizeof(GateState)) || invocation.configuration != 0)
        return Result::InvalidArgument;
    invocation.outputs[0] = 0;
    invocation.emittedBranches = 0;
    return Result::Ok;
}

auto onAndSignal(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (!isValidInvocation(invocation, sizeof(GateState)) || invocation.signalInputSlot > 2 ||
        invocation.configuration > accumulateMode)
        return Result::InvalidArgument;

    auto* state = static_cast<GateState*>(invocation.state);
    invocation.emittedBranches = 0;
    if (invocation.signalInputSlot == 2)
    {
        state->accumulatedMask = 0;
        state->sameUpdateMask = 0;
        invocation.outputs[0] = 0;
        return Result::Ok;
    }

    if (state->hasEmittedTick && state->emittedTick == invocation.eventTick)
        return Result::Ok;

    auto slotMask = std::uint64_t{1} << invocation.signalInputSlot;
    auto& arrivedMask = invocation.configuration == sameUpdateMode
        ? state->sameUpdateMask : state->accumulatedMask;
    arrivedMask |= slotMask;
    if ((arrivedMask & std::uint64_t{0x3}) != std::uint64_t{0x3})
        return Result::Ok;

    arrivedMask = 0;
    state->emittedTick = invocation.eventTick;
    state->hasEmittedTick = true;
    invocation.outputs[0] = 1;
    invocation.emittedBranches = 1;
    return Result::Ok;
}

auto onOrSignal(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (!isValidInvocation(invocation, sizeof(GateState)) || invocation.signalInputSlot > 2 ||
        invocation.configuration != 0)
        return Result::InvalidArgument;

    auto* state = static_cast<GateState*>(invocation.state);
    invocation.emittedBranches = 0;
    if (invocation.signalInputSlot == 2)
    {
        invocation.outputs[0] = 0;
        return Result::Ok;
    }
    if (state->hasEmittedTick && state->emittedTick == invocation.eventTick)
        return Result::Ok;

    state->emittedTick = invocation.eventTick;
    state->hasEmittedTick = true;
    invocation.outputs[0] = 1;
    invocation.emittedBranches = 1;
    return Result::Ok;
}

auto updateSignalToNumber(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (!isValidInvocation(invocation, sizeof(SignalToNumberState)) ||
        invocation.configuration > counterMode)
        return Result::InvalidArgument;

    auto* state = static_cast<SignalToNumberState*>(invocation.state);
    invocation.outputs[0] = invocation.configuration == latchMode && state->latched ? 1.0 :
        invocation.configuration == counterMode ? static_cast<double>(state->count) : 0.0;
    invocation.emittedBranches = 0;
    return Result::Ok;
}

auto onSignalToNumber(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (!isValidInvocation(invocation, sizeof(SignalToNumberState)) ||
        invocation.signalInputSlot > 1 || invocation.configuration > counterMode)
        return Result::InvalidArgument;

    auto* state = static_cast<SignalToNumberState*>(invocation.state);
    invocation.emittedBranches = 0;
    if (invocation.signalInputSlot == 1)
    {
        state->count = 0;
        state->latched = false;
        invocation.outputs[0] = 0;
        return Result::Ok;
    }

    switch (invocation.configuration)
    {
    case pulseMode:
        invocation.outputs[0] = 1;
        return Result::Ok;
    case latchMode:
        state->latched = true;
        invocation.outputs[0] = 1;
        return Result::Ok;
    case counterMode:
        if (state->count >= maximumExactCounter)
            return Result::CounterOverflow;
        ++state->count;
        invocation.outputs[0] = static_cast<double>(state->count);
        return Result::Ok;
    default:
        return Result::InvalidValueType;
    }
}

auto makeDescriptor(const std::array<std::uint8_t, 16>& typeId, std::uint32_t abiVersion,
                    std::uint8_t inputCount, std::array<std::uint8_t, 4> inputWidths,
                    std::uint8_t parameterCount, std::uint8_t signalInputCount,
                    std::uint8_t outputWidth, std::uint8_t signalBranchCount,
                    std::size_t stateBytes, std::size_t stateAlignment,
                    LogicalNodeUpdate update, LogicalNodeSignal onSignal,
                    LogicalNodeCommit commit = nullptr) noexcept -> LogicalNodeDescriptor
{
    auto descriptor = LogicalNodeDescriptor{};
    std::memcpy(descriptor.typeId, typeId.data(), typeId.size());
    descriptor.abiVersion = abiVersion;
    descriptor.inputCount = inputCount;
    std::copy(inputWidths.begin(), inputWidths.end(), descriptor.inputWidths);
    descriptor.parameterCount = parameterCount;
    descriptor.signalInputCount = signalInputCount;
    descriptor.outputWidth = outputWidth;
    descriptor.signalBranchCount = signalBranchCount;
    descriptor.stateBytes = stateBytes;
    descriptor.stateAlignment = stateAlignment;
    descriptor.update = update;
    descriptor.onSignal = onSignal;
    descriptor.commit = commit;
    return descriptor;
}

const auto descriptors = std::array<LogicalNodeDescriptor, 4>{
    makeDescriptor(compareTypeId, compareAbiVersion, 2, {1, 1, 0, 0}, 2, 0, 1, 1,
        sizeof(CompareState), alignof(CompareState), updateCompare, nullptr, commitCompare),
    makeDescriptor(andTypeId, gateAbiVersion, 0, {}, 1, 3, 1, 1,
        sizeof(GateState), alignof(GateState), updateAnd, onAndSignal, commitAnd),
    makeDescriptor(orTypeId, gateAbiVersion, 0, {}, 0, 3, 1, 1,
        sizeof(GateState), alignof(GateState), updateOr, onOrSignal),
    makeDescriptor(signalToNumberTypeId, signalToNumberAbiVersion, 0, {}, 1, 2, 1, 0,
        sizeof(SignalToNumberState), alignof(SignalToNumberState),
        updateSignalToNumber, onSignalToNumber)
};

}

auto logicalNodes(std::size_t& count) noexcept -> const LogicalNodeDescriptor*
{
    count = descriptors.size();
    return descriptors.data();
}

}
