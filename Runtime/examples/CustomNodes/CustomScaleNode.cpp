#include "liberafx/VfxRuntime.h"

namespace project_example {

auto scale(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    invocation.outputs[0] = invocation.inputs[0] * invocation.inputs[4];
    return liberafx::Result::Ok;
}

auto customScaleDescriptor() noexcept -> const liberafx::CustomNodeDescriptor&
{
    static const auto descriptor = [] {
        auto value = liberafx::CustomNodeDescriptor{};
        constexpr std::uint8_t typeId[16] = {
            0xc3, 0xde, 0x33, 0x77, 0x59, 0xae, 0x4a, 0x2d,
            0x89, 0x91, 0x7f, 0x38, 0xe5, 0xd8, 0x21, 0x11};
        for (auto index = 0u; index < 16; ++index) value.typeId[index] = typeId[index];
        value.abiVersion = 1;
        value.execution = liberafx::CustomExecution::Parameter;
        value.inputCount = 2;
        value.inputWidths[0] = 1;
        value.inputWidths[1] = 1;
        value.outputWidth = 1;
        value.function = scale;
        return value;
    }();
    return descriptor;
}

}
