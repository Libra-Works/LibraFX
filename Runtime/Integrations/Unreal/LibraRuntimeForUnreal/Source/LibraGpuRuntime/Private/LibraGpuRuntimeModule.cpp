#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "ShaderCore.h"

class FLibraGpuRuntimeModule final : public IModuleInterface
{
public:
    void StartupModule() override
    {
        auto plugin = IPluginManager::Get().FindPlugin(TEXT("LibraRuntimeForUnreal"));
        if (plugin)
            AddShaderSourceDirectoryMapping(TEXT("/LibraGpu"), FPaths::Combine(plugin->GetBaseDir(), TEXT("Shaders")));
    }
};

IMPLEMENT_MODULE(FLibraGpuRuntimeModule, LibraGpuRuntime)
