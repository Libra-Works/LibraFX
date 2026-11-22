#pragma once
#include "liberafx/LbVfxRuntime.h"
#include <cstring>

namespace liberafx
{
// Separate from the ABI-1 descriptor: existing node binaries/descriptor layout
// do not acquire permissions by accident. Policies match the complete callback
// identity and signature, including context and initialize/cleanup functions.
struct CustomNodePolicy final
{
    CustomNodeDescriptor descriptor{};
    bool reusable = false;
    // Includes initialize/cleanup reached during advance (birth/retirement).
    bool workerSafe = false;
    // Includes initialization AND failure cleanup of separate prepared hosts.
    bool parallelPreparation = false;
};
inline bool sameCustomNodeDescriptor(const CustomNodeDescriptor &a, const CustomNodeDescriptor &b) noexcept
{
    return std::memcmp(a.typeId, b.typeId, 16) == 0 && a.abiVersion == b.abiVersion && a.execution == b.execution &&
           a.inputCount == b.inputCount && std::memcmp(a.inputWidths, b.inputWidths, 4) == 0 &&
           std::memcmp(a.inputIsColor, b.inputIsColor, 4) == 0 && a.outputWidth == b.outputWidth &&
           a.outputIsColor == b.outputIsColor && a.branchCount == b.branchCount && a.stateBytes == b.stateBytes &&
           a.stateAlignment == b.stateAlignment && a.function == b.function && a.initialize == b.initialize &&
           a.cleanup == b.cleanup && a.context == b.context;
}
} // namespace liberafx
