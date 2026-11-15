#include "Modules/ModuleManager.h"

class FLibraRuntimeCoreModule final : public IModuleInterface
{
public:
    bool SupportsDynamicReloading() override { return false; }
};

IMPLEMENT_MODULE(FLibraRuntimeCoreModule, LibraRuntimeCore)
