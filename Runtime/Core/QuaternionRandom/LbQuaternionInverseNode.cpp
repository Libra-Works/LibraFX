#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionInverse(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {4}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    auto value = quaternionInput(call);
    if (value == math::LbQuaternion{})
    {
        return Result::InvalidArgument;
    }
    auto result = math::LbQuaternion{};
    if (!math::inverseQuaternion(value, result))
    {
        return Result::NonFinite;
    }
    return output(call, result);
}
}
