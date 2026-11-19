#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionToEuler(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {4}, 3);
    if (status != Result::Ok)
    {
        return status;
    }
    auto result = math::LbVector{};
    if (!math::quaternionToEuler(quaternionInput(call), result))
    {
        return Result::InvalidArgument;
    }
    return output(call, result);
}
}
