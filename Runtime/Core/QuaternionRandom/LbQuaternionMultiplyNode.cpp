#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionMultiply(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {4, 4}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    return output(call, math::multiplyQuaternion(quaternionInput(call), quaternionInput(call, 1)));
}
}
