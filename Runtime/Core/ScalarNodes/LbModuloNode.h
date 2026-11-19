#pragma once
#include "LbScalarNodeCommon.h"

namespace liberafx::scalar_node_detail
{
inline auto modulo(CustomNodeInvocation& call) noexcept -> Result
{
    if (validate(call) != Result::Ok)
    {
        return Result::NonFinite;
    }
    if (call.inputs[4] == 0)
    {
        return Result::DivideByZero;
    }
    return store(call, std::fmod(call.inputs[0], call.inputs[4]));
}
}
