#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionSlerp(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {4, 4, 1}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    auto result = math::LbQuaternion{};
    if (!math::slerpQuaternion(quaternionInput(call), quaternionInput(call, 1), call.inputs[8], result))
    {
        return Result::InvalidArgument;
    }
    return output(call, result);
}
}
