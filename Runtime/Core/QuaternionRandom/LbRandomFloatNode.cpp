#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto randomFloat(CustomNodeInvocation& call) noexcept -> Result
{
    auto status = validate(call, {1, 1, 1, 1}, 1);
    if (status != Result::Ok)
    {
        return status;
    }
    auto seed = std::uint32_t{}, sampleIndex = std::uint32_t{};
    if (!randomInputs(call, seed, sampleIndex))
    {
        return Result::InvalidArgument;
    }
    return output(call, std::array<double, 1>{
        randomRange(call.inputs[8], call.inputs[12], math::randomUnit(seed, sampleIndex))});
}
}
