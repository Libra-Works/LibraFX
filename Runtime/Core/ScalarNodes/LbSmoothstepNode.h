#pragma once
#include "LbScalarNodeCommon.h"
#include <algorithm>

namespace liberafx::scalar_node_detail
{
inline auto smoothstep(CustomNodeInvocation& call) noexcept -> Result
{
    if (validate(call) != Result::Ok)
    {
        return Result::NonFinite;
    }
    auto x = call.inputs[0];
    auto low = call.inputs[4];
    auto high = call.inputs[8];
    if (low >= high)
    {
        return Result::InvalidArgument;
    }
    if (x <= low)
    {
        return store(call, 0);
    }
    if (x >= high)
    {
        return store(call, 1);
    }
    auto span = high - low;
    auto delta = x - low;
    if (!std::isfinite(span) || !std::isfinite(delta))
    {
        return Result::NonFinite;
    }
    auto t = delta / span;
    return store(call, t * t * (3 - 2 * t));
}
}
