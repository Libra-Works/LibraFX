#pragma once
#include "LbScalarNodeCommon.h"
#include <algorithm>

namespace liberafx::scalar_node_detail
{
inline auto saturate(CustomNodeInvocation& call) noexcept -> Result
{
    if (validate(call) != Result::Ok)
    {
        return Result::NonFinite;
    }
    return store(call, std::max(0.0, std::min(1.0, call.inputs[0])));
}
}
