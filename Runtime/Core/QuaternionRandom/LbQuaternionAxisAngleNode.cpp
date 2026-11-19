#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionAxisAngle(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {3, 1}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    auto result = math::LbQuaternion{};
    if (!math::quaternionFromAxisAngle(vectorInput(call), call.inputs[4], result))
    {
        return Result::InvalidArgument;
    }
    return output(call, result);
}
}
