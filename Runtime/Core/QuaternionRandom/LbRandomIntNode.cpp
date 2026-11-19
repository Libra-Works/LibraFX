#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto randomInt(CustomNodeInvocation& call) noexcept -> Result
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
    auto low = call.inputs[8], high = call.inputs[12];
    if (low < -2147483648.0 || low > 2147483647.0 || low != std::floor(low) ||
        high < -2147483648.0 || high > 2147483647.0 || high != std::floor(high))
    {
        return Result::InvalidArgument;
    }
    return output(call, std::array<double, 1>{static_cast<double>(math::randomInteger(
        seed, sampleIndex, static_cast<std::int32_t>(low), static_cast<std::int32_t>(high)))});
}
}
