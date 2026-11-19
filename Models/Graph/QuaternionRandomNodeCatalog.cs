namespace LiberaFx.Models.Graph;

/// <summary>Stable built-in identities; definitions and embedded fallbacks remain JSON-driven.</summary>
public static class QuaternionRandomNodeCatalog
{
    public const string IdPrefix = "9bd7a620-c8e1-4f35-a962-";
    public static IReadOnlyDictionary<Guid, string> BuiltInIds { get; } = new Dictionary<Guid, string>
    {
        [Guid.Parse(IdPrefix + "000000000001")] = "QuaternionIdentity",
        [Guid.Parse(IdPrefix + "000000000002")] = "QuaternionCompose",
        [Guid.Parse(IdPrefix + "000000000003")] = "QuaternionNormalize",
        [Guid.Parse(IdPrefix + "000000000004")] = "QuaternionConjugate",
        [Guid.Parse(IdPrefix + "000000000005")] = "QuaternionInverse",
        [Guid.Parse(IdPrefix + "000000000006")] = "QuaternionMultiply",
        [Guid.Parse(IdPrefix + "000000000007")] = "QuaternionFromEuler",
        [Guid.Parse(IdPrefix + "000000000008")] = "QuaternionToEuler",
        [Guid.Parse(IdPrefix + "000000000009")] = "QuaternionAxisAngle",
        [Guid.Parse(IdPrefix + "00000000000a")] = "QuaternionSlerp",
        [Guid.Parse(IdPrefix + "00000000000b")] = "QuaternionRotateVector",
        [Guid.Parse(IdPrefix + "000000000021")] = "RandomFloat",
        [Guid.Parse(IdPrefix + "000000000022")] = "RandomInt",
        [Guid.Parse(IdPrefix + "000000000023")] = "RandomVector3",
        [Guid.Parse(IdPrefix + "000000000024")] = "RandomUnitSphere",
        [Guid.Parse(IdPrefix + "000000000025")] = "RandomInSphere"
    };

    // Wrap GraphNodeDefinition's existing dictionary initializer with this method.
    // This preserves the established disk -> embedded JSON fallback and name normalization.
    public static Dictionary<Guid, string> WithBuiltInIds(Dictionary<Guid, string> ids)
    {
        foreach (var (id, name) in BuiltInIds)
        {
            ids.Add(id, name);
        }
        return ids;
    }
}
