#pragma once

#include "liberafx/LbVfxRuntime.h"
#include <array>

namespace liberafx::quaternion_random_node_detail
{
constexpr auto quaternionRandomNodeCount = 16;
using QuaternionRandomNodeDescriptors = std::array<CustomNodeDescriptor, quaternionRandomNodeCount>;
auto quaternionRandomNodeDescriptors() noexcept -> const QuaternionRandomNodeDescriptors&;
}
