#pragma once
#include "LbScalarNodeCommon.h"

namespace liberafx::scalar_node_detail
{
inline auto step(CustomNodeInvocation& call) noexcept -> Result
{
    if (validate(call) != Result::Ok)
    {
        return Result::NonFinite;
    }
    return store(call, call.inputs[0] < call.inputs[4] ? 0 : 1);
}
}
