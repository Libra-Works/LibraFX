#pragma once

#include "LbQuaternionRandomNodes.h"
#include "liberafx/math/LbQuaternionMath.h"
#include "liberafx/math/LbRandomMath.h"
#include <algorithm>
#include <cmath>
#include <initializer_list>

namespace liberafx::quaternion_random_node_detail
{
using NodeFunction = Result (*)(CustomNodeInvocation&) noexcept;

// Runtime validates descriptor shape before dispatch. Direct calls are checked here as well.
inline auto validate(CustomNodeInvocation& call, std::initializer_list<unsigned> widths, unsigned outputWidth) noexcept -> Result
{
    if (call.inputs == nullptr && widths.size() != 0)
    {
        return Result::InvalidArgument;
    }
    if (call.outputs == nullptr || call.inputCount != widths.size() || call.outputWidth != outputWidth ||
        (widths.size() != 0 && call.inputWidths == nullptr))
    {
        return Result::InvalidArgument;
    }
    for (auto lane = 0u; lane < outputWidth; ++lane)
    {
        call.outputs[lane] = 0;
    }
    auto index = 0u;
    for (auto width : widths)
    {
        if (call.inputWidths[index] != width)
        {
            return Result::InvalidArgument;
        }
        for (auto lane = 0u; lane < width; ++lane)
        {
            if (!std::isfinite(call.inputs[index * 4 + lane]))
            {
                return Result::NonFinite;
            }
        }
        ++index;
    }
    return Result::Ok;
}

inline auto quaternionInput(const CustomNodeInvocation& call, unsigned input = 0) noexcept -> math::LbQuaternion
{
    auto offset = input * 4;
    return {call.inputs[offset], call.inputs[offset + 1], call.inputs[offset + 2], call.inputs[offset + 3]};
}

inline auto vectorInput(const CustomNodeInvocation& call, unsigned input = 0) noexcept -> math::LbVector
{
    auto offset = input * 4;
    return {call.inputs[offset], call.inputs[offset + 1], call.inputs[offset + 2]};
}

template <std::size_t count>
auto output(CustomNodeInvocation& call, const std::array<double, count>& value) noexcept -> Result
{
    for (auto component : value)
    {
        if (!std::isfinite(component))
        {
            return Result::NonFinite;
        }
    }
    std::copy(value.begin(), value.end(), call.outputs);
    return Result::Ok;
}

inline auto randomInputs(CustomNodeInvocation& call, std::uint32_t& seed, std::uint32_t& sampleIndex) noexcept -> bool
{
    auto a = call.inputs[0], b = call.inputs[4];
    if (a < 0 || a > 4294967295.0 || a != std::floor(a) ||
        b < 0 || b > 4294967295.0 || b != std::floor(b))
    {
        return false;
    }
    seed = static_cast<std::uint32_t>(a);
    sampleIndex = static_cast<std::uint32_t>(b);
    return true;
}

inline auto randomRange(double low, double high, double unit) noexcept -> double
{
    if (low > high)
    {
        std::swap(low, high);
    }
    if (low == high)
    {
        return low;
    }
    return low * (1 - unit) + high * unit;
}

auto quaternionIdentity(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionCompose(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionNormalize(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionConjugate(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionInverse(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionMultiply(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionFromEuler(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionToEuler(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionAxisAngle(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionSlerp(CustomNodeInvocation& call) noexcept -> Result;
auto quaternionRotateVector(CustomNodeInvocation& call) noexcept -> Result;
auto randomFloat(CustomNodeInvocation& call) noexcept -> Result;
auto randomInt(CustomNodeInvocation& call) noexcept -> Result;
auto randomVector3(CustomNodeInvocation& call) noexcept -> Result;
auto randomUnitSphere(CustomNodeInvocation& call) noexcept -> Result;
auto randomInSphere(CustomNodeInvocation& call) noexcept -> Result;
}
