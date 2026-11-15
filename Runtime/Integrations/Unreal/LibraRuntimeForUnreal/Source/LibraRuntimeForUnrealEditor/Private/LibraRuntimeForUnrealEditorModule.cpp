#include "Modules/ModuleManager.h"

class FLibraRuntimeForUnrealEditorModule final : public IModuleInterface
{
public:
    bool SupportsDynamicReloading() override { return false; }
};

IMPLEMENT_MODULE(FLibraRuntimeForUnrealEditorModule, LibraRuntimeForUnrealEditor)
