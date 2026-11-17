using UnrealBuildTool;

public class LibraRuntimeForUnreal : ModuleRules
{
    public LibraRuntimeForUnreal(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        CppStandard = CppStandardVersion.Cpp20;
        PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "LibraRuntimeCore" });
        PrivateDependencyModuleNames.AddRange(new[] { "RenderCore", "RHI", "Json", "LibraGpuRuntime" });
        var supportsEditorTransport =
            Target.Platform == UnrealTargetPlatform.Win64 ||
            Target.Platform == UnrealTargetPlatform.Mac ||
            Target.IsInPlatformGroup(UnrealPlatformGroup.Unix);
        PrivateDefinitions.Add("LIBRA_WITH_EDITOR_TRANSPORT=" + (supportsEditorTransport ? "1" : "0"));
        if (supportsEditorTransport)
            PrivateDependencyModuleNames.AddRange(new[] { "libWebSockets", "OpenSSL", "SSL", "Sockets" });
    }
}
