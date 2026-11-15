namespace LiberaFx.Services;

public sealed record ProfileCost(PreviewMetric Metric, ulong Calls, double InclusiveMs, double ExclusiveMs,
    bool Cumulative);

public sealed record ProfileSample(long ProgramSequence, ulong SnapshotId, double ReceivedSeconds,
    IReadOnlyList<double> Categories, IReadOnlyList<ProfileCost> IntervalCosts,
    IReadOnlyList<ProfileCost> CumulativeCosts, bool Baseline, double CumulativeMs, decimal CumulativeCalls)
{
    public double TotalMs => Categories.Sum();
}

// Keep only summaries/top costs, not 300 copies of potentially 10,000 native counters.
public sealed class PreviewProfileHistory
{
    public const int Capacity = 300;
    public const int CostLimit = 64;
    private readonly List<ProfileSample> _samples = [];
    private Dictionary<(string, Guid?), PreviewMetric> _previous = [];
    private long? _sequence;
    private ulong? _snapshot;
    private double _origin;
    public IReadOnlyList<ProfileSample> Samples => _samples;

    public static int Category(string name) => name switch
    {
        "Libra.Node" => 0,
        "Libra.Evaluate" => 1,
        "Libra.Schedule" => 2,
        "Libra.Signal" => 3,
        "Libra.Bind" or "Libra.ReadValue" or "Libra.ReadParameter" or "Libra.WriteParameter" => 4,
        _ => 5
    };

    public void Clear()
    {
        _samples.Clear();
        _previous.Clear();
        _sequence = null;
        _snapshot = null;
    }

    public bool Add(PreviewProfile profile, double receivedSeconds)
    {
        if (_sequence == profile.ProgramSequence && _snapshot >= profile.SnapshotId) return false;
        var reset = _sequence != profile.ProgramSequence || profile.Metrics.Any(metric =>
            _previous.TryGetValue((metric.Event, metric.NodeId), out var old) &&
            (metric.Calls < old.Calls || metric.ExclusiveNs < old.ExclusiveNs || metric.InclusiveNs < old.InclusiveNs));
        if (reset) Clear();
        var baseline = _snapshot is null;
        if (baseline) _origin = receivedSeconds;
        var categories = new double[6];
        var interval = new List<ProfileCost>();
        foreach (var metric in profile.Metrics)
        {
            if (baseline) continue;
            _previous.TryGetValue((metric.Event, metric.NodeId), out var old);
            var calls = metric.Calls - (old?.Calls ?? 0);
            var exclusive = (metric.ExclusiveNs - (old?.ExclusiveNs ?? 0)) / 1_000_000d;
            var inclusive = (metric.InclusiveNs - (old?.InclusiveNs ?? 0)) / 1_000_000d;
            categories[Category(metric.Event)] += exclusive;
            if (calls > 0 || inclusive > 0) interval.Add(new(metric, calls, inclusive, exclusive, false));
        }
        _previous = profile.Metrics.ToDictionary(metric => (metric.Event, metric.NodeId));
        _sequence = profile.ProgramSequence;
        _snapshot = profile.SnapshotId;
        var cumulative = profile.Metrics.Select(metric =>
            new ProfileCost(metric, metric.Calls, metric.InclusiveMs, metric.ExclusiveMs, true));
        _samples.Add(new(profile.ProgramSequence, profile.SnapshotId, receivedSeconds - _origin, categories,
            Top(interval), Top(cumulative), baseline, profile.Metrics.Sum(metric => metric.ExclusiveMs),
            profile.Metrics.Sum(metric => (decimal)metric.Calls)));
        if (_samples.Count > Capacity) _samples.RemoveAt(0);
        return true;
    }

    private static ProfileCost[] Top(IEnumerable<ProfileCost> costs) =>
        costs.OrderByDescending(cost => cost.ExclusiveMs).ThenBy(cost => cost.Metric.Event, StringComparer.Ordinal)
            .Take(CostLimit).ToArray();
}
