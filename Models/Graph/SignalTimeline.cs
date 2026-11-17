namespace LiberaFx.Models.Graph;

public enum SignalTimelineMode { Preview, Runtime }

public sealed record SignalTimelineKey(Guid Id, double TimeSeconds, SignalTimelineMode Mode);

public static class SignalTimeline
{
    public const int MaximumKeysPerNode = 512;

    public static bool Supports(GraphNodeKind kind) =>
        kind.Value is "Signal" or "InputSignal" or "GroupInputSignal" or "SpawnInputSignal";

    public static bool Equal(IReadOnlyList<SignalTimelineKey>? left, IReadOnlyList<SignalTimelineKey>? right) =>
        (left ?? []).SequenceEqual(right ?? []);

    public static bool ContainsKeys(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.SignalKeys is { Count: > 0 } ||
        node.ChildGraph is { } child && ContainsKeys(child));

    public static void Validate(GraphNodeKind kind, IReadOnlyList<SignalTimelineKey>? keys)
    {
        if (keys is null) return;
        if (!Supports(kind) || keys.Count > MaximumKeysPerNode)
            throw new InvalidDataException("Signal timeline keys require a supported Signal entry node.");
        var ids = new HashSet<Guid>();
        if (keys.Any(key => key is null || key.Id == Guid.Empty || !ids.Add(key.Id) ||
            !double.IsFinite(key.TimeSeconds) || key.TimeSeconds is < 0 or > 1_000_000 ||
            !Enum.IsDefined(key.Mode)))
            throw new InvalidDataException("Signal timeline keys require unique IDs, valid modes and nonnegative finite seconds.");
    }
}
