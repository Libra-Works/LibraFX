#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto randomInSphere(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {1, 1}, 3);
    if (status != Result::Ok)
    {
        return status;
    }
    auto seed = std::uint32_t{}, sampleIndex = std::uint32_t{};
    if (!randomInputs(call, seed, sampleIndex))
    {
        return Result::InvalidArgument;
    }
    return output(call, math::randomSpherePoint(seed, sampleIndex));
}
}
