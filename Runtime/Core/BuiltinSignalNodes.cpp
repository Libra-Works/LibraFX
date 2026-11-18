#include "BuiltinNodesInternal.h"
#include "liberafx/BuiltinSignals.h"
#include "liberafx/VfxRuntime.h"
#include <algorithm>
#include <array>
#include <cstdint>

namespace liberafx::builtin_signal_detail
{
auto makeSignalPassthroughDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    std::copy(signalPassthroughTypeId.begin(), signalPassthroughTypeId.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.branchCount = 1;
    descriptor.function = signalPassthrough;
    return descriptor;
}

auto makeGameIoOutputSignalDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const auto typeId = std::array<std::uint8_t, 16>{0x7f, 0xf0, 0x78, 0xd2, 0xdc, 0xe1, 0x42, 0xb0,
                                                     0x93, 0xfb, 0xb4, 0x02, 0x00, 0x00, 0x00, 0x01};
    std::copy(typeId.begin(), typeId.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.branchCount = 1;
    descriptor.function = signalPassthrough;
    return descriptor;
}

template <std::uint8_t scope> auto requestForce(CustomNodeInvocation& call) noexcept -> Result
{
    call.forceScope = scope;
    return Result::Ok;
}

template <std::uint8_t scope> auto makeForceDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const auto id =
        std::array<std::uint8_t, 16>{0x4a, 0x94, 0xe8, 0x82, 0x39, 0xe8, 0x4e, 0xfb,
                                     0x92, 0xc7, 0x18, 0,    0,    0,    0,    static_cast<std::uint8_t>(0x10 + scope)};
    std::copy(id.begin(), id.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.function = requestForce<scope>;
    return descriptor;
}

template <std::uint8_t phase> auto requestPhase(CustomNodeInvocation& call) noexcept -> Result
{
    call.enterPhase = phase;
    return Result::Ok;
}

template <std::uint8_t phase> auto makePhaseDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const auto id =
        std::array<std::uint8_t, 16>{0x4a, 0x94, 0xe8, 0x82, 0x39, 0xe8, 0x4e, 0xfb,
                                     0x92, 0xc7, 0x18, 0,    0,    0,    0,    static_cast<std::uint8_t>(0x21 + phase)};
    std::copy(id.begin(), id.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.function = requestPhase<phase>;
    return descriptor;
}

auto signalNodeDescriptors() noexcept -> const builtin_node_detail::SignalNodeDescriptors&
{
    static const auto descriptors = builtin_node_detail::SignalNodeDescriptors{makeSignalPassthroughDescriptor(),
                                                                               makeGameIoOutputSignalDescriptor()};
    return descriptors;
}
auto forceNodeDescriptors() noexcept -> const builtin_node_detail::ForceNodeDescriptors&
{
    static const auto descriptors =
        builtin_node_detail::ForceNodeDescriptors{makeForceDescriptor<1>(), makeForceDescriptor<2>()};
    return descriptors;
}
auto phaseNodeDescriptors() noexcept -> const builtin_node_detail::PhaseNodeDescriptors&
{
    static const auto descriptors = builtin_node_detail::PhaseNodeDescriptors{
        makePhaseDescriptor<1>(), makePhaseDescriptor<2>(), makePhaseDescriptor<3>()};
    return descriptors;
}
} // namespace liberafx::builtin_signal_detail

namespace liberafx::builtin_node_detail
{
auto signalNodeDescriptors() noexcept -> const SignalNodeDescriptors&
{
    return builtin_signal_detail::signalNodeDescriptors();
}

auto forceNodeDescriptors() noexcept -> const ForceNodeDescriptors&
{
    return builtin_signal_detail::forceNodeDescriptors();
}

auto phaseNodeDescriptors() noexcept -> const PhaseNodeDescriptors&
{
    return builtin_signal_detail::phaseNodeDescriptors();
}
} // namespace liberafx::builtin_node_detail
