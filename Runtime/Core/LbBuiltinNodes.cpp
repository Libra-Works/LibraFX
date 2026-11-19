#include "LbBuiltinNodesInternal.h"
#include "LbDelayNode.h"
#include "liberafx/LbCpuParticles.h"
#include "ScalarNodes/LbModuloNode.h"
#include "ScalarNodes/LbFractionNode.h"
#include "ScalarNodes/LbCeilNode.h"
#include "ScalarNodes/LbExpNode.h"
#include "ScalarNodes/LbExp2Node.h"
#include "ScalarNodes/LbSmoothstepNode.h"
#include "ScalarNodes/LbInverseLerpNode.h"
#include "ScalarNodes/LbSaturateNode.h"
#include "ScalarNodes/LbStepNode.h"
#include "ScalarNodes/LbTruncateNode.h"
#include <algorithm>
#include <array>
#include <cstddef>

namespace liberafx
{
auto builtinNodes(std::size_t& count) noexcept -> const CustomNodeDescriptor*
{
    static const auto descriptors = []
    {
        auto nodes = std::array<CustomNodeDescriptor, builtin_node_detail::builtinNodeCount + 2>{};
        auto offset = std::size_t{0};
        auto append = [&](const auto& source)
        {
            std::copy(source.begin(), source.end(), nodes.begin() + offset);
            offset += source.size();
        };

        append(builtin_node_detail::arithmeticNodeDescriptors());
        append(builtin_node_detail::matrixNodeDescriptors());
        append(builtin_node_detail::signalNodeDescriptors());
        append(builtin_node_detail::lifetimeNodeDescriptors());
        append(builtin_node_detail::hostTimeNodeDescriptors());
        nodes[offset++] = makeDelayDescriptor();
        append(builtin_node_detail::orderedMatrixNodeDescriptors());
        append(builtin_node_detail::forceNodeDescriptors());
        append(builtin_node_detail::phaseNodeDescriptors());
        using namespace scalar_node_detail;
        const auto scalarNodes = std::array<CustomNodeDescriptor, builtin_node_detail::scalarNodeCount>{
            descriptor(48, 2, modulo), descriptor(49, 1, fraction), descriptor(50, 1, ceilValue),
            descriptor(51, 1, expValue), descriptor(52, 1, exp2Value), descriptor(53, 3, smoothstep),
            descriptor(54, 3, inverseLerp), descriptor(55, 1, saturate), descriptor(56, 2, step),
            descriptor(57, 1, truncate)};
        append(scalarNodes);
        append(quaternion_random_node_detail::quaternionRandomNodeDescriptors());
        auto emitterCount = std::size_t{};
        auto emitters = cpuEmitterNodes(emitterCount);
        std::copy_n(emitters, emitterCount, nodes.begin() + offset);
        return nodes;
    }();
    count = descriptors.size();
    return descriptors.data();
}
} // namespace liberafx
