#pragma once

#include "liberafx/LbCustomNodePolicy.h"
#include "liberafx/LbVfxRuntime.h"

namespace liberafx::host {

struct ProjectNodeRegistry final {
    const CustomNodeDescriptor* descriptors = nullptr;
    std::size_t count = 0;
    const CustomNodePolicy *policies = nullptr;
    std::size_t policyCount = 0;
};

#ifdef LIBERAFX_HAS_PROJECT_NODES
// Implement in the project's native source. Return an immutable descriptor
// array whose callbacks and contexts remain valid until the adapter unloads.
ProjectNodeRegistry projectNodes() noexcept;
#else
inline auto projectNodes() noexcept -> ProjectNodeRegistry
{
    return {};
}
#endif

}
