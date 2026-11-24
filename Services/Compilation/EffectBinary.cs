using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public static class EffectBinary
{
    public const string FileExtension = ".lfxb";

    public static byte[] Compile(GraphSnapshot graph)
    {
        if (graph.ResourceIdentities is not null || ResourceIdentityExport.HasEmbeddedMetadata(graph))
        {
            throw new InvalidDataException(
                "Portable resource identities require explicit resource mapping export; unresolved identities cannot be discarded.");
        }

        return SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph));
    }

    public static byte[] Compile(GraphSnapshot graph, ResourceIdentityRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph, resourceContext: context));
    }
}
