#include "LbQuaternionRandomNodeSupport.h"

namespace liberafx::quaternion_random_node_detail
{
auto makeDescriptor(unsigned id, std::initializer_list<unsigned> widths, unsigned outputWidth, NodeFunction function) noexcept
    -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    constexpr auto prefix = std::array<std::uint8_t, 10>{0x9b, 0xd7, 0xa6, 0x20, 0xc8, 0xe1, 0x4f, 0x35, 0xa9, 0x62};
    std::copy(prefix.begin(), prefix.end(), descriptor.typeId);
    descriptor.typeId[15] = static_cast<std::uint8_t>(id);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Parameter;
    descriptor.inputCount = static_cast<std::uint8_t>(widths.size());
    auto index = 0u;
    for (auto width : widths)
    {
        descriptor.inputWidths[index++] = static_cast<std::uint8_t>(width);
    }
    descriptor.outputWidth = static_cast<std::uint8_t>(outputWidth);
    descriptor.function = function;
    return descriptor;
}

auto quaternionRandomNodeDescriptors() noexcept -> const QuaternionRandomNodeDescriptors&
{
    static const auto descriptors = QuaternionRandomNodeDescriptors{
        makeDescriptor(0x01, {}, 4, quaternionIdentity),
        makeDescriptor(0x02, {1, 1, 1, 1}, 4, quaternionCompose),
        makeDescriptor(0x03, {4}, 4, quaternionNormalize),
        makeDescriptor(0x04, {4}, 4, quaternionConjugate),
        makeDescriptor(0x05, {4}, 4, quaternionInverse),
        makeDescriptor(0x06, {4, 4}, 4, quaternionMultiply),
        makeDescriptor(0x07, {3}, 4, quaternionFromEuler),
        makeDescriptor(0x08, {4}, 3, quaternionToEuler),
        makeDescriptor(0x09, {3, 1}, 4, quaternionAxisAngle),
        makeDescriptor(0x0a, {4, 4, 1}, 4, quaternionSlerp),
        makeDescriptor(0x0b, {4, 3}, 3, quaternionRotateVector),
        makeDescriptor(0x21, {1, 1, 1, 1}, 1, randomFloat),
        makeDescriptor(0x22, {1, 1, 1, 1}, 1, randomInt),
        makeDescriptor(0x23, {1, 1, 3, 3}, 3, randomVector3),
        makeDescriptor(0x24, {1, 1}, 3, randomUnitSphere),
        makeDescriptor(0x25, {1, 1}, 3, randomInSphere)};
    return descriptors;
}
}
