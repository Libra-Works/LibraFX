#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionConjugate(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {4}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    return output(call, math::conjugateQuaternion(quaternionInput(call)));
}
}
