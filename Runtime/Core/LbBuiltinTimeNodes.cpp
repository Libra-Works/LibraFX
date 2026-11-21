#include "LbBuiltinNodesInternal.h"
#include "liberafx/LbVfxRuntime.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>

namespace liberafx::builtin_time_detail
{
template <unsigned kind> auto evaluateHostTime(CustomNodeInvocation& call) noexcept -> Result
{
    if (!call.hasTimeContext)
    {
        return Result::NotReady;
    }
    call.outputs[0] = kind == 0 ? call.effectTime : kind == 1 ? call.frameDelta :
        kind == 2 ? call.realEffectTime : kind == 3 ? call.realInstanceAge : call.realFrameDelta;
    return Result::Ok;
}

template <unsigned kind> auto makeHostTimeDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const auto id = std::array<std::uint8_t, 16>{
        0xc9, 0x82, 0xe1, 0xbe, 0x8f, 0x78, 0x4d, 0x28,
        0x9a, 0x65, 0x4f, 0x4a, 0xa4, 0x52, 0x49, static_cast<std::uint8_t>(0xe6 + kind)};
    std::copy(id.begin(), id.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.outputWidth = 1;
    descriptor.function = evaluateHostTime<kind>;
    return descriptor;
}

struct CapturedLifetime final
{
    double duration;
    bool captured;
};

template <bool normalized> auto evaluateLifetime(CustomNodeInvocation& call) noexcept -> Result
{
    auto state = CapturedLifetime{};
    std::memcpy(&state, call.state, sizeof(state));
    if (!state.captured)
    {
        if (!std::isfinite(call.inputs[0]) || call.inputs[0] < 0)
        {
            return Result::InvalidValueType;
        }
        state.duration = call.inputs[0];
        state.captured = true;
        std::memcpy(call.state, &state, sizeof(state));
    }
    if (!std::isfinite(call.time) || call.time < 0)
    {
        return Result::InvalidArgument;
    }
    auto age = std::min(call.time, state.duration);
    call.outputs[0] = normalized ? (state.duration == 0 ? 1 : age / state.duration) : state.duration - age;
    return Result::Ok;
}

template <bool normalized> auto makeLifetimeDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const auto id = std::array<std::uint8_t, 16>{
        0x4a, 0x94, 0xe8, 0x82, 0x39, 0xe8, 0x4e, 0xfb,
        0x92, 0xc7, 0x18, 0,    0,    0,    0,    normalized ? std::uint8_t{2} : std::uint8_t{1}};
    std::copy(id.begin(), id.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.inputCount = 1;
    descriptor.inputWidths[0] = 1;
    descriptor.outputWidth = 1;
    descriptor.stateBytes = sizeof(CapturedLifetime);
    descriptor.function = evaluateLifetime<normalized>;
    return descriptor;
}

auto lifetimeNodeDescriptors() noexcept -> const builtin_node_detail::LifetimeNodeDescriptors&
{
    static const auto descriptors =
        builtin_node_detail::LifetimeNodeDescriptors{makeLifetimeDescriptor<false>(), makeLifetimeDescriptor<true>()};
    return descriptors;
}
auto hostTimeNodeDescriptors() noexcept -> const builtin_node_detail::HostTimeNodeDescriptors&
{
    static const auto descriptors =
        builtin_node_detail::HostTimeNodeDescriptors{makeHostTimeDescriptor<0>(), makeHostTimeDescriptor<1>(),
            makeHostTimeDescriptor<2>(), makeHostTimeDescriptor<3>(), makeHostTimeDescriptor<4>()};
    return descriptors;
}
} // namespace liberafx::builtin_time_detail

namespace liberafx::builtin_node_detail
{
auto lifetimeNodeDescriptors() noexcept -> const LifetimeNodeDescriptors&
{
    return builtin_time_detail::lifetimeNodeDescriptors();
}

auto hostTimeNodeDescriptors() noexcept -> const HostTimeNodeDescriptors&
{
    return builtin_time_detail::hostTimeNodeDescriptors();
}
} // namespace liberafx::builtin_node_detail
