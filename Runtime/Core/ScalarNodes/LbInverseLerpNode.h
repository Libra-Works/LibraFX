#pragma once
#include "LbScalarNodeCommon.h"

namespace liberafx::scalar_node_detail
{
inline auto inverseLerp(CustomNodeInvocation& call) noexcept -> Result
{
    if (validate(call) != Result::Ok)
    {
        return Result::NonFinite;
    }
    auto delta = call.inputs[0] - call.inputs[4];
    auto span = call.inputs[8] - call.inputs[4];
    if (span == 0)
    {
        return Result::DivideByZero;
    }
    if (!std::isfinite(delta) || !std::isfinite(span))
    {
        return Result::NonFinite;
    }
    return store(call, delta / span);
}
}
