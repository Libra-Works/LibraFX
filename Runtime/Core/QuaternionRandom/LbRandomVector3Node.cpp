#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto randomVector3(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {1, 1, 3, 3}, 3);
    if (status != Result::Ok)
    {
        return status;
    }
    auto seed = std::uint32_t{}, sampleIndex = std::uint32_t{};
    if (!randomInputs(call, seed, sampleIndex))
    {
        return Result::InvalidArgument;
    }
    auto value = math::LbVector{};
    for (auto lane = 0u; lane < 3; ++lane)
    {
        value[lane] = randomRange(call.inputs[8 + lane], call.inputs[12 + lane], math::randomUnit(seed, sampleIndex, lane));
    }
    return output(call, value);
}
}
