using UnrealBuildTool;
using System.IO;

public class LibraGpuRuntime : ModuleRules
{
    public LibraGpuRuntime(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp20;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "LibraRuntimeCore" });
        PrivateDependencyModuleNames.AddRange(new[] { "Projects", "RenderCore", "RHI", "Renderer", "Json" });
        AddEngineThirdPartyPrivateStaticDependencies(Target, "OpenSSL");
        var pluginRoot = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", ".."));
        var coreInclude = Path.Combine(pluginRoot, "Source", "ThirdParty", "LibraRuntimeCore", "include");
        var marker = Path.Combine(pluginRoot, ".libra-core-source-path");
        if (!File.Exists(Path.Combine(coreInclude, "liberafx", "gpu", "LbFluid2D.h")) && File.Exists(marker))
        {
            var linkedRuntime = File.ReadAllText(marker).Trim();
            coreInclude = Path.Combine(linkedRuntime, "include");
        }
        if (!File.Exists(Path.Combine(coreInclude, "liberafx", "gpu", "LbFluid2D.h")))
            coreInclude = Path.GetFullPath(Path.Combine(pluginRoot, "..", "..", "..", "..", "Runtime", "include"));
        if (!File.Exists(Path.Combine(coreInclude, "liberafx", "gpu", "LbFluid2D.h")))
            throw new BuildException("The shared Libra fluid runtime contract include is missing.");
        PublicIncludePaths.Add(coreInclude);
    }
}
