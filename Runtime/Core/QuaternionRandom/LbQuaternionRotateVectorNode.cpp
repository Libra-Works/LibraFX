#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionRotateVector(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {4, 3}, 3);
    if (status != Result::Ok)
    {
        return status;
    }
    auto unit = math::LbQuaternion{};
    if (!math::normalizeQuaternion(quaternionInput(call), unit))
    {
        return Result::InvalidArgument;
    }
    auto result = math::LbVector{};
    if (!math::rotateByQuaternion(unit, vectorInput(call, 1), result))
    {
        return Result::NonFinite;
    }
    return output(call, result);
}
}
