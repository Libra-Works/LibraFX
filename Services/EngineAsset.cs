namespace LiberaFx.Services;

public sealed record EngineMaterialParameter(string Name, string Kind,
    System.Text.Json.JsonElement Value, int Association = 0, int Index = -1);
public sealed record EngineMaterial(int Slot, string Path, string Name,
    IReadOnlyList<EngineMaterialParameter> Parameters);
public sealed record EngineAsset(string Path, string Name, string AssetClass, string Engine,
    int Width = 0, int Height = 0, IReadOnlyList<EngineMaterial>? Materials = null);
public sealed record EngineAssetPage(IReadOnlyList<EngineAsset> Assets, int Total, int Offset);
public sealed record EngineAssetThumbnail(string Path, string Mime, byte[] Image, int Width, int Height);
