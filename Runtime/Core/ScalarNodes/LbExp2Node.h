#pragma once
#include "LbScalarNodeCommon.h"

namespace liberafx::scalar_node_detail
{
inline auto exp2Value(CustomNodeInvocation& call) noexcept -> Result
{
    if (validate(call) != Result::Ok)
    {
        return Result::NonFinite;
    }
    return store(call, std::exp2(call.inputs[0]));
}
}
