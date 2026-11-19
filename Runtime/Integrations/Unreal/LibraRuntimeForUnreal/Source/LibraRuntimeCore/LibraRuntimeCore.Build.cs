using System.IO;
using UnrealBuildTool;

public class LibraRuntimeCore : ModuleRules
{
    public LibraRuntimeCore(ReadOnlyTargetRules Target) : base(Target)
    {
        if (Target.Version.MajorVersion < 5 ||
            (Target.Version.MajorVersion == 5 && Target.Version.MinorVersion < 7))
        {
            throw new BuildException("Libra Runtime requires Unreal Engine 5.7 or later.");
        }

        PCHUsage = PCHUsageMode.NoPCHs;
        bUseUnity = false;
        // Core catches allocation failures and translates them into runtime results.
        bEnableExceptions = true;
        CppStandard = CppStandardVersion.Cpp20;
        PrivateDependencyModuleNames.Add("Core");
        PublicDefinitions.Add("LIBRA_RUNTIME_CORE_API=LIBRARUNTIMECORE_API");

        var pluginRoot = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", ".."));
        var coreRoot = Path.Combine(pluginRoot, "Source", "ThirdParty", "LibraRuntimeCore");
        var developmentPath = Path.Combine(pluginRoot, ".libra-core-source-path");
        if (!Directory.Exists(coreRoot) && File.Exists(developmentPath))
        {
            coreRoot = Path.GetFullPath(File.ReadAllText(developmentPath).Trim());
        }
        if (!File.Exists(Path.Combine(coreRoot, "Core", "LbVfxProgram.cpp")) ||
            !File.Exists(Path.Combine(coreRoot, "Core", "LbVfxInstance.cpp")))
        {
            throw new BuildException("LibraRuntimeCore sources are missing. Use Install-LibraRuntimeForUnreal.ps1 (copy or -Link).");
        }

        PublicIncludePaths.Add(Path.Combine(coreRoot, "include"));
        PrivateIncludePaths.Add(coreRoot);
        PrivateIncludePaths.Add(Path.Combine(coreRoot, "Core"));
        foreach (var directory in new[] { "Core", "include" })
        {
            ExternalDependencies.AddRange(Directory.GetFiles(Path.Combine(coreRoot, directory), "*", SearchOption.AllDirectories));
        }
    }
}
