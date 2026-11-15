#include "LibraNativeNodes.h"
#include "Modules/ModuleManager.h"

namespace {
auto Scale(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    invocation.outputs[0] = invocation.inputs[0] * invocation.inputs[4];
    return liberafx::Result::Ok;
}

auto Route(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    invocation.selectedBranch = invocation.inputs[0] >= 0 ? 0 : 1;
    return liberafx::Result::Ok;
}
}

class FLibraProjectNodesModule final : public IModuleInterface
{
public:
    void StartupModule() override
    {
        FModuleManager::LoadModuleChecked<IModuleInterface>(TEXT("LibraRuntimeForUnreal"));
        auto descriptors = TArray<liberafx::CustomNodeDescriptor>{};
        descriptors.SetNum(2);
        for (auto index = 0; index < 2; ++index)
        {
            auto& descriptor = descriptors[index];
            const uint8 typeId[16] = {0xc3, 0xde, 0x33, 0x77, 0x59, 0xae, 0x4a, 0x2d,
                0x89, 0x91, 0x7f, 0x38, 0xe5, 0xd8, 0x21, static_cast<uint8>(0x11 + index)};
            FMemory::Memcpy(descriptor.typeId, typeId, sizeof(typeId));
            descriptor.abiVersion = 1;
            descriptor.execution = index == 0 ? liberafx::CustomExecution::Parameter : liberafx::CustomExecution::Signal;
            descriptor.inputCount = index == 0 ? 2 : 1;
            descriptor.inputWidths[0] = 1;
            descriptor.inputWidths[1] = index == 0 ? 1 : 0;
            descriptor.outputWidth = index == 0 ? 1 : 0;
            descriptor.branchCount = index == 0 ? 0 : 2;
            descriptor.function = index == 0 ? Scale : Route;
        }
        auto result = FLibraNativeNodes::Register(descriptors);
        if (result != ELibraRuntimeResult::Ok)
            UE_LOG(LogTemp, Error, TEXT("Libra example node registration failed (%d)."), static_cast<int>(result));
    }

    bool SupportsDynamicReloading() override { return false; }
};

IMPLEMENT_MODULE(FLibraProjectNodesModule, LibraProjectNodes)
