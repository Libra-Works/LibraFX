using System.Text.Json;
using LiberaFx.Models.Resources;

namespace LiberaFx.Services;

public static class EngineAssetScopeSettings
{
    private sealed record Setting(EngineProjectIdentity Project, string Folder);
    private static string FileFor(EngineProjectIdentity project)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(project.Engine + ":" + project.ProjectId.ToString("D"))));
        return Path.Combine(AppDataDirectory.Root, "LiberaFx", "asset-scopes", key + ".json");
    }
    public static string Load(EngineProjectIdentity project)
    {
        var file = FileFor(project);
        if (!File.Exists(file)) return "";
        if (new FileInfo(file).Length > 4096) throw new InvalidDataException("Asset scope setting is too large.");
        var setting = JsonSerializer.Deserialize<Setting>(File.ReadAllText(file));
        if (setting?.Project != project) throw new InvalidDataException("Asset scope belongs to a different engine project.");
        return EngineAssetFolder.Normalize(setting.Folder);
    }
    public static void Save(EngineProjectIdentity project, string folder)
    {
        folder = EngineAssetFolder.Normalize(folder);
        var file = FileFor(project);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Setting(project, folder)));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
