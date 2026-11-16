#include "LibraNativeNodes.h"
#include "LibraNativeNodesInternal.h"
#include "liberafx/BuiltinSignals.h"

namespace {
auto CurrentRegistry() -> TSharedRef<const FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe>&
{
    static auto snapshot = [] {
        auto initial = MakeShared<FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe>();
        auto descriptor = liberafx::CustomNodeDescriptor{};
        const auto& typeId = liberafx::signalPassthroughTypeId;
        FMemory::Memcpy(descriptor.typeId, typeId.data(), typeId.size());
        descriptor.abiVersion = 1;
        descriptor.execution = liberafx::CustomExecution::Signal;
        descriptor.branchCount = 1;
        descriptor.function = liberafx::signalPassthrough;
        initial->descriptors.Add(descriptor);
        return TSharedRef<const FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe>(initial);
    }();
    return snapshot;
}

auto IsValidDescriptor(const liberafx::CustomNodeDescriptor& descriptor) -> bool
{
    auto hasIdentity = false;
    for (auto byte : descriptor.typeId) hasIdentity |= byte != 0;
    if (!hasIdentity || descriptor.abiVersion == 0 || !descriptor.function || descriptor.inputCount > 4 ||
        descriptor.stateBytes > static_cast<uint64>(MAX_int64) - 7) return false;
    if (descriptor.execution == liberafx::CustomExecution::Parameter)
    {
        if (descriptor.outputWidth < 1 || descriptor.outputWidth > 4 || descriptor.branchCount != 0 ||
            (descriptor.outputIsColor && descriptor.outputWidth != 4)) return false;
    }
    else if (descriptor.execution == liberafx::CustomExecution::Signal)
    {
        if (descriptor.outputWidth != 0 || descriptor.outputIsColor ||
            descriptor.branchCount < 1 || descriptor.branchCount > 4) return false;
    }
    else return false;
    for (auto index = 0; index < descriptor.inputCount; ++index)
        if (descriptor.inputWidths[index] < 1 || descriptor.inputWidths[index] > 4 ||
            descriptor.inputIsColor[index] > 1 ||
            (descriptor.inputIsColor[index] && descriptor.inputWidths[index] != 4)) return false;
    return true;
}
}

auto LibraNativeNodeSnapshot() -> TSharedRef<const FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe>
{
    check(IsInGameThread());
    return CurrentRegistry();
}

auto FLibraNativeNodes::Register(TConstArrayView<liberafx::CustomNodeDescriptor> descriptors) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (descriptors.IsEmpty()) return ELibraRuntimeResult::InvalidArgument;
    auto candidate = MakeShared<FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe>();
    candidate->descriptors = CurrentRegistry()->descriptors;
    for (const auto& descriptor : descriptors)
    {
        if (!IsValidDescriptor(descriptor)) return ELibraRuntimeResult::InvalidArgument;
        for (const auto& previous : candidate->descriptors)
            if (previous.abiVersion == descriptor.abiVersion &&
                FMemory::Memcmp(previous.typeId, descriptor.typeId, sizeof(descriptor.typeId)) == 0)
                return ELibraRuntimeResult::InvalidArgument;
        candidate->descriptors.Add(descriptor);
    }
    CurrentRegistry() = candidate;
    return ELibraRuntimeResult::Ok;
}
