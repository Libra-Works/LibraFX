#include "LogicalNodes.h"
#include <liberafx/PhasePrimitives.h>

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
constexpr auto loopClockTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x05};
constexpr auto qualityBranchTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x06};
constexpr auto signalCaptureTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x07};

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

struct alignas(16) SignalValueState final {
    std::array<double, 4> value{};
};

auto updateSignalValue(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (invocation.state == nullptr || invocation.stateBytes < sizeof(SignalValueState) ||
        invocation.outputs == nullptr || invocation.outputWidth < 1 || invocation.outputWidth > 4)
        return Result::InvalidArgument;
    auto state = static_cast<SignalValueState*>(invocation.state);
    std::copy_n(state->value.data(), invocation.outputWidth, invocation.outputs);
    invocation.emittedBranches = 0;
    return Result::Ok;
}

auto onQualitySignal(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (invocation.quality == nullptr || invocation.signalInputSlot != 0 ||
        (invocation.configuration & 0xffff0000u) != 0 || invocation.outputWidth != 1)
        return Result::InvalidArgument;
    auto result = updateSignalValue(invocation);
    if (result != Result::Ok) return result;
    auto selected = false;
    result = compareHostQuality(*invocation.quality,
        static_cast<QualityComparison>(invocation.configuration & 0xffu),
        static_cast<HostQualityTier>((invocation.configuration >> 8u) & 0xffu), selected);
    if (result != Result::Ok) return result;
    auto state = static_cast<SignalValueState*>(invocation.state);
    state->value[0] = invocation.outputs[0] = selected ? 1 : 0;
    invocation.emittedBranches = selected ? 1 : 2;
    return Result::Ok;
}

auto onCaptureSignal(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (invocation.inputs == nullptr || invocation.inputCount != 1 ||
        invocation.inputWidths == nullptr || invocation.inputWidths[0] != 4 ||
        invocation.signalInputSlot != 0 || invocation.outputWidth != 4 || invocation.configuration != 0)
        return Result::InvalidArgument;
    auto result = updateSignalValue(invocation);
    if (result != Result::Ok) return result;
    for (auto lane = 0u; lane < 4; ++lane)
        if (!std::isfinite(invocation.inputs[lane])) return Result::NonFinite;
    auto state = static_cast<SignalValueState*>(invocation.state);
    std::copy_n(invocation.inputs, 4, state->value.data());
    std::copy_n(invocation.inputs, 4, invocation.outputs);
    invocation.captureSignalValue = true;
    invocation.emittedBranches = 1;
    return Result::Ok;
}

struct LoopValues final {
    LoopClock clock{};
    FutureBoundaryArm arm{};
    LoopSample sample{};
    double time = 0;
    bool initialized = false;
};

struct alignas(16) LoopState final {
    LoopValues value{};
    LoopValues pending{};
    bool pendingCommit = false;
};

auto sampleLoop(LogicalNodeInvocation& invocation, bool reset) noexcept -> Result
{
    if (invocation.state == nullptr || invocation.stateBytes < sizeof(LoopState) ||
        invocation.inputs == nullptr || invocation.inputCount != 4 ||
        invocation.inputWidths == nullptr || invocation.outputs == nullptr ||
        invocation.outputWidth != 4 || invocation.configuration != 0)
        return Result::InvalidArgument;
    for (auto index = 0u; index < 4; ++index)
        if (invocation.inputWidths[index] != 1 || !std::isfinite(invocation.inputs[index * 4]))
            return Result::InvalidArgument;
    auto time = invocation.inputs[0] == -1 ? invocation.time : invocation.inputs[0];
    auto length = invocation.inputs[4];
    auto count = invocation.inputs[8];
    auto bounded = invocation.inputs[12];
    if (!std::isfinite(time) || time < 0 || length < 0 || count < 0 ||
        count > static_cast<double>(maximumExactCounter) || std::trunc(count) != count ||
        (bounded != 0 && bounded != 1))
        return Result::InvalidArgument;
    auto state = static_cast<LoopState*>(invocation.state);
    auto next = state->value;
    if (reset || !next.initialized || time < next.time)
    {
        auto result = next.clock.reset(reset ? time : 0, length, bounded != 0,
            static_cast<std::uint64_t>(count));
        if (result != Result::Ok) return result;
        next.arm.clear();
        next.sample = {};
        next.initialized = true;
    }
    auto sample = LoopSample{};
    auto result = next.clock.advance(time, sample);
    if (result != Result::Ok) return result;
    if (sample.boundaryCount > maximumExactCounter || sample.loopIndex > maximumExactCounter)
        return Result::CounterOverflow;
    invocation.emittedBranches = (sample.boundariesCrossed != 0 ? std::uint64_t{1} : 0) |
        (sample.complete && !next.sample.complete ? std::uint64_t{2} : 0) |
        (next.arm.consume(sample) ? std::uint64_t{4} : 0);
    next.sample = sample;
    next.time = time;
    invocation.outputs[0] = sample.loopTime;
    invocation.outputs[1] = static_cast<double>(sample.loopIndex);
    invocation.outputs[2] = static_cast<double>(sample.boundaryCount);
    invocation.outputs[3] = sample.complete ? 1 : 0;
    state->pending = next;
    state->pendingCommit = true;
    return Result::Ok;
}

auto updateLoop(LogicalNodeInvocation& invocation) noexcept -> Result
{
    return sampleLoop(invocation, false);
}

auto commitLoop(LogicalNodeInvocation& invocation) noexcept -> void
{
    if (invocation.state == nullptr || invocation.stateBytes < sizeof(LoopState)) return;
    auto state = static_cast<LoopState*>(invocation.state);
    if (!state->pendingCommit) return;
    state->value = state->pending;
    state->pendingCommit = false;
}

auto onLoopSignal(LogicalNodeInvocation& invocation) noexcept -> Result
{
    if (invocation.signalInputSlot > 1) return Result::InvalidArgument;
    auto result = sampleLoop(invocation, invocation.signalInputSlot == 1);
    if (result != Result::Ok) return result;
    auto state = static_cast<LoopState*>(invocation.state);
    if (invocation.signalInputSlot == 0 && !state->pending.sample.complete)
        state->pending.arm.arm(state->pending.sample);
    commitLoop(invocation);
    return Result::Ok;
}

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

const auto descriptors = std::array<LogicalNodeDescriptor, 7>{
    makeDescriptor(compareTypeId, compareAbiVersion, 2, {1, 1, 0, 0}, 2, 0, 1, 1,
        sizeof(CompareState), alignof(CompareState), updateCompare, nullptr, commitCompare),
    makeDescriptor(andTypeId, gateAbiVersion, 0, {}, 1, 3, 1, 1,
        sizeof(GateState), alignof(GateState), updateAnd, onAndSignal, commitAnd),
    makeDescriptor(orTypeId, gateAbiVersion, 0, {}, 0, 3, 1, 1,
        sizeof(GateState), alignof(GateState), updateOr, onOrSignal),
    makeDescriptor(signalToNumberTypeId, signalToNumberAbiVersion, 0, {}, 1, 2, 1, 0,
        sizeof(SignalToNumberState), alignof(SignalToNumberState),
        updateSignalToNumber, onSignalToNumber),
    makeDescriptor(loopClockTypeId, 1, 4, {1, 1, 1, 1}, 0, 2, 4, 3,
        sizeof(LoopState), alignof(LoopState), updateLoop, onLoopSignal, commitLoop),
    makeDescriptor(qualityBranchTypeId, 1, 0, {}, 2, 1, 1, 2,
        sizeof(SignalValueState), alignof(SignalValueState), updateSignalValue, onQualitySignal),
    makeDescriptor(signalCaptureTypeId, 1, 1, {4, 0, 0, 0}, 0, 1, 4, 1,
        sizeof(SignalValueState), alignof(SignalValueState), updateSignalValue, onCaptureSignal)
};

}

auto logicalNodes(std::size_t& count) noexcept -> const LogicalNodeDescriptor*
{
    count = descriptors.size();
    return descriptors.data();
}

}
