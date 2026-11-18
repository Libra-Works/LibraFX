#include "LbDelayNode.h"
#include <algorithm>
#include <array>

namespace liberafx {
namespace {
auto evaluateDelay(CustomNodeInvocation& call) noexcept -> Result
{
    call.selectedBranch = 0;
    call.deferSignal = true;
    call.delaySeconds = call.inputs[0];
    return Result::Ok;
}
}

auto makeDelayDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const auto id = std::array<std::uint8_t, 16>{
        0xc9, 0x82, 0xe1, 0xbe, 0x8f, 0x78, 0x4d, 0x28,
        0x9a, 0x65, 0x4f, 0x4a, 0xa4, 0x52, 0x49, 0xe4};
    std::copy(id.begin(), id.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.inputCount = 1;
    descriptor.inputWidths[0] = 1;
    descriptor.branchCount = 1;
    descriptor.function = evaluateDelay;
    return descriptor;
}
}
