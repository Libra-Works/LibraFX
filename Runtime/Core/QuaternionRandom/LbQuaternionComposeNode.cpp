#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto quaternionCompose(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {1, 1, 1, 1}, 4);
    if (status != Result::Ok)
    {
        return status;
    }
    return output(call, math::LbQuaternion{call.inputs[0], call.inputs[4], call.inputs[8], call.inputs[12]});
}
}
