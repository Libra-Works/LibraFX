#pragma once

#include "liberafx/VfxRuntime.h"
#include <array>

namespace liberafx::builtin_node_detail
{
constexpr auto arithmeticNodeCount = 47;
constexpr auto matrixNodeCount = 13;
constexpr auto orderedMatrixNodeCount = 4;
constexpr auto signalNodeCount = 2;
constexpr auto lifetimeNodeCount = 2;
constexpr auto hostTimeNodeCount = 2;
constexpr auto delayNodeCount = 1;
constexpr auto forceNodeCount = 2;
constexpr auto phaseNodeCount = 3;
using ArithmeticNodeDescriptors = std::array<CustomNodeDescriptor, arithmeticNodeCount>;
using MatrixNodeDescriptors = std::array<CustomNodeDescriptor, matrixNodeCount>;
using OrderedMatrixNodeDescriptors = std::array<CustomNodeDescriptor, orderedMatrixNodeCount>;
using SignalNodeDescriptors = std::array<CustomNodeDescriptor, signalNodeCount>;
using LifetimeNodeDescriptors = std::array<CustomNodeDescriptor, lifetimeNodeCount>;
using HostTimeNodeDescriptors = std::array<CustomNodeDescriptor, hostTimeNodeCount>;
using ForceNodeDescriptors = std::array<CustomNodeDescriptor, forceNodeCount>;
using PhaseNodeDescriptors = std::array<CustomNodeDescriptor, phaseNodeCount>;
constexpr auto builtinNodeCount = arithmeticNodeCount + matrixNodeCount + orderedMatrixNodeCount + signalNodeCount +
                                  lifetimeNodeCount + hostTimeNodeCount + delayNodeCount + forceNodeCount +
                                  phaseNodeCount;

auto arithmeticNodeDescriptors() noexcept -> const ArithmeticNodeDescriptors&;
auto matrixNodeDescriptors() noexcept -> const MatrixNodeDescriptors&;
auto orderedMatrixNodeDescriptors() noexcept -> const OrderedMatrixNodeDescriptors&;
auto signalNodeDescriptors() noexcept -> const SignalNodeDescriptors&;
auto lifetimeNodeDescriptors() noexcept -> const LifetimeNodeDescriptors&;
auto hostTimeNodeDescriptors() noexcept -> const HostTimeNodeDescriptors&;
auto forceNodeDescriptors() noexcept -> const ForceNodeDescriptors&;
auto phaseNodeDescriptors() noexcept -> const PhaseNodeDescriptors&;
} // namespace liberafx::builtin_node_detail
