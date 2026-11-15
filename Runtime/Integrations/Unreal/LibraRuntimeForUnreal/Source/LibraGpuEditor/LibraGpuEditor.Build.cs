using UnrealBuildTool;

public class LibraGpuEditor : ModuleRules
{
    public LibraGpuEditor(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp20;
        PrivateDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "UnrealEd", "LibraGpuRuntime" });
    }
}
