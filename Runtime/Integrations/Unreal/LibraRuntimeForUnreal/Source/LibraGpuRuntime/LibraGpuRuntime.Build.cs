using UnrealBuildTool;

public class LibraGpuRuntime : ModuleRules
{
    public LibraGpuRuntime(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp20;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine" });
        PrivateDependencyModuleNames.AddRange(new[] { "Projects", "RenderCore", "RHI", "Renderer", "Json" });
        AddEngineThirdPartyPrivateStaticDependencies(Target, "OpenSSL");
    }
}
