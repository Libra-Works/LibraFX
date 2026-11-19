#pragma once
#include "LbScalarNodeCommon.h"

namespace liberafx::scalar_node_detail
{
inline auto ceilValue(CustomNodeInvocation& call) noexcept -> Result
{
    if (validate(call) != Result::Ok)
    {
        return Result::NonFinite;
    }
    return store(call, std::ceil(call.inputs[0]));
}
}
