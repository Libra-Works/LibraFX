#include "BuiltinNodesInternal.h"
#include "DelayNode.h"
#include <algorithm>
#include <array>
#include <cstddef>

namespace liberafx
{
auto builtinNodes(std::size_t& count) noexcept -> const CustomNodeDescriptor*
{
    static const auto descriptors = []
    {
        auto nodes = std::array<CustomNodeDescriptor, builtin_node_detail::builtinNodeCount>{};
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
        return nodes;
    }();
    count = descriptors.size();
    return descriptors.data();
}
} // namespace liberafx
