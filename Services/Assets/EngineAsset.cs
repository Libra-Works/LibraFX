namespace LiberaFx.Services;

public sealed record EngineMaterialParameter(string Name, string Kind,
    System.Text.Json.JsonElement Value, int Association = 2, int Index = -1);
public sealed record EngineMaterial(int Slot, string Path, string Name,
    IReadOnlyList<EngineMaterialParameter> Parameters);
public sealed record EngineAsset(string Path, string Name, string AssetClass, string Engine,
    int Width = 0, int Height = 0, IReadOnlyList<EngineMaterial>? Materials = null);
public sealed record EngineAssetPage(IReadOnlyList<EngineAsset> Assets, int Total, int Offset);
public sealed record EngineFolderEntry(string Path, string Name);
public sealed record EngineFolderPage(IReadOnlyList<EngineFolderEntry> Folders, int Total, int Offset);
public sealed record EngineAssetThumbnail(string Path, string Mime, byte[] Image, int Width, int Height);

// Render delivery only depends on this UI-independent boundary, including in the standalone converter.
public interface IRenderResourceBindingContext
{
    IReadOnlyList<Guid> GetInstancePath(Guid nodeId) => Array.Empty<Guid>();
    IReadOnlyList<Guid> GetInstancePath(Guid nodeId, IReadOnlyList<Guid> localPath)
    {
        var boundPath = GetInstancePath(nodeId);
        return boundPath.Count > 0 ? boundPath : localPath;
    }
    bool HasResourceUse(Guid nodeId, string location);
    bool HasResourceUse(Guid nodeId, string location, IReadOnlyList<Guid> instancePath) =>
        HasResourceUse(nodeId, location);
    void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters);
    void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters,
        IReadOnlyList<Guid> instancePath) => ValidateRenderNode(nodeId, parameters);
    void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters,
        IReadOnlyList<Guid> instancePath, IReadOnlyList<int> materialSlots) =>
        ValidateRenderNode(nodeId, parameters, instancePath);
    string ResolvePath(Guid nodeId, string location, string kind, string legacyValue);
    string ResolvePath(Guid nodeId, string location, string kind, string legacyValue,
        IReadOnlyList<Guid> instancePath) => ResolvePath(nodeId, location, kind, legacyValue);
    (string Name, int Association, int Index)? ResolveParameter(Guid nodeId, string pinId, int slot, string kind);
    (string Name, int Association, int Index)? ResolveParameter(Guid nodeId, string pinId, int slot, string kind,
        IReadOnlyList<Guid> instancePath) => ResolveParameter(nodeId, pinId, slot, kind);
}
