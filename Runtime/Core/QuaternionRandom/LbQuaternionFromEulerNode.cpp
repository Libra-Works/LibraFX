#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionFromEuler(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {3}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    return output(call, math::quaternionFromEuler(vectorInput(call)));
}
}
