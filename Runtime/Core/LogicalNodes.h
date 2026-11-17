#pragma once

#include "liberafx/VfxRuntime.h"

namespace liberafx {

// Immutable logical descriptors registered by the v9 bytecode reader.
LIBRA_RUNTIME_CORE_API const LogicalNodeDescriptor* logicalNodes(std::size_t& count) noexcept;

}
