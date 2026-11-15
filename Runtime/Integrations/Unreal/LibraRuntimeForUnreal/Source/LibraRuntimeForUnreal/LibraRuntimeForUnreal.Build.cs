using UnrealBuildTool;

public class LibraRuntimeForUnreal : ModuleRules
{
    public LibraRuntimeForUnreal(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp20;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "LibraRuntimeCore" });
        if (Target.Platform == UnrealTargetPlatform.Win64 && Target.Architecture != UnrealArch.Arm64)
            PrivateDependencyModuleNames.AddRange(new[] { "libWebSockets", "OpenSSL", "SSL", "Sockets" });
    }
}
