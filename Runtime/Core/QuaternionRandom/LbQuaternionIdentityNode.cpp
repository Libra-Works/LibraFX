#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionIdentity(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    return output(call, math::LbQuaternion{0, 0, 0, 1});
}
}
