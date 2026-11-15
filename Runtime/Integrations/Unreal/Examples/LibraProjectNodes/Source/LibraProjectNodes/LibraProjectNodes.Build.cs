using UnrealBuildTool;

public class LibraProjectNodes : ModuleRules
{
    public LibraProjectNodes(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp20;
        PrivateDependencyModuleNames.AddRange(new[] { "Core", "LibraRuntimeForUnreal" });
    }
}
