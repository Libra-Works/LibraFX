using UnrealBuildTool;

public class LibraRuntimeForUnrealEditor : ModuleRules
{
    public LibraRuntimeForUnrealEditor(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        PrivateDependencyModuleNames.AddRange(new[] {
            "Core", "CoreUObject", "Engine", "UnrealEd", "AssetRegistry", "Json",
            "Slate", "SlateCore", "InputCore", "EditorStyle", "EditorFramework",
            "AdvancedPreviewScene",
            "LibraRuntimeCore", "LibraRuntimeForUnreal", "LibraGpuRuntime"
        });
    }
}
