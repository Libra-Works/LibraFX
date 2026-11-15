#include "Modules/ModuleManager.h"
#include "LibraEditorPreviewSession.h"

class FLibraRuntimeForUnrealEditorModule final : public IModuleInterface
{
public:
    void StartupModule() override { preview.Start(); }
    void ShutdownModule() override { preview.Stop(); }
    bool SupportsDynamicReloading() override { return false; }
private:
    FLibraEditorPreviewSession preview;
};

IMPLEMENT_MODULE(FLibraRuntimeForUnrealEditorModule, LibraRuntimeForUnrealEditor)
