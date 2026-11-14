#if !LIBERAFX_CONVERTER
using Avalonia.Platform;
#endif

namespace LiberaFx.Models.Graph;

internal static class BuiltInAssets
{
    public static Stream Open(string directory, string name)
    {
#if LIBERAFX_CONVERTER
        return typeof(BuiltInAssets).Assembly.GetManifestResourceStream($"LiberaFx.Assets.{directory}.{name}.json")
            ?? throw new InvalidDataException($"Missing embedded asset {directory}/{name}.json.");
#else
        return AssetLoader.Open(new Uri($"avares://LiberaFx/Assets/{directory}/{name}.json"));
#endif
    }
}
