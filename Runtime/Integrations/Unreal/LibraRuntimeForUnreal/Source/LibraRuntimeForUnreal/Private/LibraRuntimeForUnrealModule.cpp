#include "Modules/ModuleManager.h"

class FLibraRuntimeForUnrealModule final : public IModuleInterface
{
public:
    bool SupportsDynamicReloading() override { return false; }
};

IMPLEMENT_MODULE(FLibraRuntimeForUnrealModule, LibraRuntimeForUnreal)
