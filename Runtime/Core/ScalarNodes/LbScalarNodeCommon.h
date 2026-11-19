#pragma once

#include "liberafx/LbVfxRuntime.h"
#include <cmath>
#include <cstdint>

namespace liberafx::scalar_node_detail
{
inline auto validate(const CustomNodeInvocation& call) noexcept -> Result
{
    for (auto i = 0u; i < call.inputCount; ++i)
    {
        if (!std::isfinite(call.inputs[i * 4]))
        {
            return Result::NonFinite;
        }
    }
    return Result::Ok;
}

inline auto store(CustomNodeInvocation& call, double value) noexcept -> Result
{
    if (!std::isfinite(value))
    {
        return Result::NonFinite;
    }
    call.outputs[0] = value;
    return Result::Ok;
}

inline auto descriptor(std::uint8_t id, std::uint8_t inputCount, CustomNodeFunction function) noexcept
    -> CustomNodeDescriptor
{
    auto result = CustomNodeDescriptor{};
    const std::uint8_t prefix[] = {0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33, 0x8d, 0x44};
    for (auto i = 0u; i < 10; ++i)
    {
        result.typeId[i] = prefix[i];
    }
    result.typeId[15] = id;
    result.abiVersion = 1;
    result.inputCount = inputCount;
    for (auto i = 0u; i < inputCount; ++i)
    {
        result.inputWidths[i] = 1;
    }
    result.outputWidth = 1;
    result.function = function;
    return result;
}
}
