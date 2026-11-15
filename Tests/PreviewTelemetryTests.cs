using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class PreviewTelemetryTests
{
    private static readonly Guid Node = Guid.Parse("f5a521e8-1a2f-4ed8-b4d3-f0507be71a09");
    private const string Hello = """{"type":"hello","engine":"Unreal","role":"viewer","version":1,"capabilities":["profiling","values"]}""";

    public static void Run()
    {
        VerifyReceive();
        VerifyHistory();
        VerifyFrozenChart();
        VerifyControlsAsync().GetAwaiter().GetResult();
        VerifyPanel();
    }

    private static bool Receive(PreviewTelemetryStore store, Guid id, string json) => store.Receive(id, Encoding.UTF8.GetBytes(json));
    private static string Metric(string name, ulong inclusive = 2_000_000, ulong exclusive = 1_000_000) =>
        JsonSerializer.Serialize(new { @event = name, nodeId = Node, calls = 2UL, inclusiveNs = inclusive,
            exclusiveNs = exclusive, minNs = 500_000UL, maxNs = 1_500_000UL });
    private static string Page(long program, ulong snapshot, int offset, int total, params string[] metrics) =>
        $$"""{"type":"profile","programSequence":{{program}},"snapshotId":{{snapshot}},"offset":{{offset}},"total":{{total}},"dropped":3,"metrics":[{{string.Join(",", metrics)}}]}""";
    private static string Values(long sequence = 7, string components = "1.25,-2,3,4") =>
        $$"""{"type":"values","programSequence":{{sequence}},"evaluationTime":0.375,"values":[{"nodeId":"{{Node:D}}","components":[{{components}}]}]}""";

    private static void VerifyReceive()
    {
        var store = new PreviewTelemetryStore();
        var id = Guid.NewGuid();
        store.Connect(id, 3, _ => Task.CompletedTask);
        Require(!Receive(store, id, Page(7, 1, 0, 1, Metric("node"))), "Unnegotiated profiles must be rejected.");
        Require(Receive(store, id, Hello), "Viewer roles may explicitly support telemetry.");
        Require(!Receive(store, id, Hello), "A connection must negotiate only once.");
        Require(Receive(store, id, Page(7, 1, 0, 2, Metric("node"))), "First page must be accepted.");
        Require(store.Clients(id).Single() is { Profile: null, ProfilePending: true }, "Partial pages must not become complete.");
        Require(Receive(store, id, Page(7, 1, 1, 2, Metric("accessor"))), "Contiguous pages must complete.");
        var complete = store.Clients(id).Single().Profile!;
        Require(complete.Metrics.Count == 2 && complete.Dropped == 3 && complete.ProgramSequence == 7 &&
            complete.Metrics[0].ExclusiveMs == 1 && complete.Metrics[0].InclusiveMs == 2 &&
            complete.Metrics[0].MinMs == 0.5 && complete.Metrics[0].MaxMs == 1.5, "Profile units or metadata are incorrect.");
        Require(!Receive(store, id, Page(7, 1, 0, 1, Metric("node"))), "Completed snapshots must not be replayed.");
        Require(Receive(store, id, Page(7, 2, 0, 2, Metric("old"))), "New partial snapshot must begin.");
        Require(Receive(store, id, Page(7, 3, 0, 2, Metric("new"))), "New snapshots must abandon older partial pages.");
        Require(!Receive(store, id, Page(7, 2, 1, 2, Metric("old-end"))), "Abandoned pages must not complete.");
        Require(ReferenceEquals(store.Clients(id).Single().Profile, complete), "Incomplete/rejected snapshots must preserve completed data.");
        Require(Receive(store, id, Page(7, 3, 1, 2, Metric("new-end"))), "The newest snapshot must complete.");
        Require(!Receive(store, id, Page(7, 4, 0, 1, Metric("bad", exclusive: 3_000_000))), "Exclusive cannot exceed inclusive.");
        Require(!Receive(store, id, Page(7, 4, 0, 1, Metric("bad").Replace("\"minNs\":500000", "\"minNs\":2000000"))), "Minimum cannot exceed maximum.");
        Require(!Receive(store, id, Page(7, 4, 0, 10014, Metric("bad"))), "Metric totals must be bounded.");
        Require(!Receive(store, id, Page(7, 4, 0, 257, Enumerable.Range(0, 257).Select(i => Metric("e" + i)).ToArray())),
            "Metric pages must be bounded.");
        Require(!Receive(store, id, Page(7, 4, 0, 2, Metric("duplicate"), Metric("duplicate"))), "Duplicate aggregated metrics must be rejected.");
        Require(!Receive(store, id, """{"type":"profile"}""") && !Receive(store, id, "[]") &&
            !Receive(store, id, """{"type":"values","type":"hello"}"""), "Malformed objects must be safely rejected.");
        Require(!store.Receive(id, new byte[PreviewTelemetryStore.MaxFrameBytes + 1]), "JSON frame size must be bounded.");
        Require(!store.Receive(id, new byte[] { 0xff }), "Malformed UTF8 must be rejected.");
        Require(Receive(store, id, Values()), "Finite native values must be accepted.");
        var values = store.Clients(id).Single().Values!;
        Require(values.ProgramSequence == 7 && values.EvaluationTime == 0.375 &&
            values.Values.Single().Components.SequenceEqual(new double[] { 1.25, -2, 3, 4 }), "Value metadata must be exact.");
        Require(!Receive(store, id, Values(6)) && !Receive(store, id, Values(8, "1e999")) &&
            !Receive(store, id, Values(8, "")) && !Receive(store, id, Values(8, "1,2,3,4,5")) &&
            !Receive(store, id, Values(8).Replace(Node.ToString("D"), "not-guid")), "Invalid and stale values must be rejected.");
        Require(ReferenceEquals(store.Clients(id).Single().Values, values), "Invalid values must not overwrite feedback.");
        Require(Receive(store, id, Page(8, 0, 0, 0)), "A newer program may restart snapshot IDs and report an empty snapshot.");
        Require(store.Clients(id).Single().Profile!.Metrics.Count == 0, "A complete empty snapshot is valid.");
        store.Disconnect(id);
        Require(store.Clients().Count == 0 && !Receive(store, id, Values()), "Disconnect must remove all feedback.");
    }

    private static void VerifyHistory()
    {
        var history = new PreviewProfileHistory();
        PreviewProfile Sample(ulong id, ulong calls, ulong exclusive, long program = 1) =>
            new(program, id, 0, new[] { new PreviewMetric("Libra.Node", Node, calls, exclusive, exclusive, 1, exclusive) });
        history.Add(Sample(1, 2, 2_000_000), 10);
        Require(history.Samples.Single().Baseline && history.Samples.Single().TotalMs == 0 &&
            history.Samples.Single().CumulativeMs == 2, "First cumulative counters must not invent interval work.");
        history.Add(Sample(2, 5, 3_500_000), 11);
        var delta = history.Samples.Last();
        Require(delta.TotalMs == 1.5 && delta.IntervalCosts.Single().Calls == 3 &&
            delta.CumulativeCosts.Single().Calls == 5 && delta.ReceivedSeconds == 1,
            "History must subtract counters, not repeat cumulative totals or fake frame timestamps.");
        Require(!history.Add(Sample(2, 5, 3_500_000), 12), "Duplicate snapshots must not add samples.");
        history.Add(Sample(3, 5, 3_500_000), 13);
        Require(history.Samples.Last().TotalMs == 0 && history.Samples.Last().IntervalCosts.Count == 0,
            "Paused work must not produce fabricated activity.");
        history.Add(Sample(4, 1, 1_000_000), 14);
        Require(history.Samples.Count == 1 && history.Samples[0].Baseline, "Reset counters need a new baseline.");
        history.Add(Sample(0, 1, 1_000_000, 2), 15);
        Require(history.Samples.Count == 1 && history.Samples[0].ProgramSequence == 2,
            "Program replacements must not mix incompatible counters.");
        for (ulong i = 1; i <= 310; ++i) history.Add(Sample(i, i + 1, (i + 1) * 1_000_000, 2), 15 + i);
        Require(history.Samples.Count == 300 && history.Samples.Last().SnapshotId == 310,
            "Graphical history must remain bounded while retaining the latest sample.");
        history.Clear();
        Require(history.Samples.Count == 0, "Disconnect/clear must remove chart history.");
    }

    private static void VerifyFrozenChart()
    {
        var history = new PreviewProfileHistory();
        void Add(ulong id) => history.Add(new PreviewProfile(1, id, 0,
            new[] { new PreviewMetric("Libra.Node", Node, id, id * 1_000_000, id * 1_000_000, 1, 1_000_000) }), id);
        Add(1);
        Add(2);
        var chart = new ProfilerHistoryChart();
        chart.Arrange(new Rect(0, 0, 800, 240));
        chart.Update(history.Samples, 1, followLive: false);
        Add(3);
        chart.Update(history.Samples, 1, followLive: false);
        var hitTest = typeof(ProfilerHistoryChart).GetMethod("IndexAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int RightEdge() => (int)hitTest.Invoke(chart, new object[] { 782d })!;
        Require(history.Samples[RightEdge()].SnapshotId == 2,
            "New snapshots must not move the displayed history or its hit targets when live follow is off.");
        using (var drawing = new DrawingGroup().Open()) chart.Render(drawing);
        chart.Update(history.Samples, 2, followLive: true);
        Require(history.Samples[RightEdge()].SnapshotId == 3,
            "Resuming live follow must display and hit-test the newest sample.");
        using (var drawing = new DrawingGroup().Open()) chart.Render(drawing);
        history.Clear();
        chart.Update(history.Samples, -1);
        using (var drawing = new DrawingGroup().Open()) chart.Render(drawing);
    }

    private static async Task VerifyControlsAsync()
    {
        var store = new PreviewTelemetryStore();
        var id = Guid.NewGuid();
        var sent = new List<byte[]>();
        store.Connect(id, 3, bytes => { sent.Add(bytes); return Task.CompletedTask; });
        await RejectAsync(() => store.ProfileControlAsync(true, true, true, 500), "Capability negotiation is required.");
        Require(Receive(store, id, Hello), "Hello must be accepted.");
        await store.ProfileControlAsync(true, true, false, 100);
        await store.WatchValuesAsync([Node]);
        Require(sent.Count == 2 && store.Clients(id).Single().RequestedProfile!.Enabled, "Controls must be sent and reflected.");
        foreach (var frame in sent)
        {
            Require(frame.AsSpan(0, 4).SequenceEqual("LFXP"u8) && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) == 1 &&
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)) == 4 &&
                frame.AsSpan(8, 36).IndexOfAnyExcept((byte)0) < 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(44)) == frame.Length - 48,
                "Controls must have kind4 and zero sequence/playback header fields.");
        }
        using (var json = JsonDocument.Parse(sent[1].AsMemory(48)))
            Require(json.RootElement.GetProperty("nodeIds")[0].GetGuid() == Node, "Watch IDs must be sent exactly.");
        await RejectAsync(() => store.ProfileControlAsync(true, false, false, 99), "Interval lower bound must be enforced.");
        await RejectAsync(() => store.ProfileControlAsync(true, false, false, 5001), "Interval upper bound must be enforced.");
        await RejectAsync(() => store.WatchValuesAsync(Enumerable.Range(0, 65).Select(_ => Guid.NewGuid())), "Watch count must be bounded.");
        await RejectAsync(() => store.WatchValuesAsync([Node, Node]), "Watch IDs must be distinct.");
        await store.WatchValuesAsync([]);
        Require(store.Clients(id).Single().WatchedNodes.Count == 0, "An empty watch list must clear watches.");
        var second = Guid.NewGuid();
        store.Connect(second, 3, _ => Task.CompletedTask);
        await RejectAsync(() => store.ProfileControlAsync(true, true, true, 500), "Multiple peers require a target.");
        Require(Receive(store, second, Hello.Replace("[\"profiling\",\"values\"]", "[]").Replace("\"viewer\"", "\"editor\"")),
            "An editor may advertise no capabilities.");
        await RejectAsync(() => store.ProfileControlAsync(true, true, true, 500, second), "Editor roles do not imply profiling.");
        store.Disconnect(second);
        foreach (var protocol in new[] { 1, 2 })
        {
            var legacy = Guid.NewGuid();
            store.Connect(legacy, protocol, _ => throw new InvalidOperationException("Legacy must never receive controls."));
            Require(!Receive(store, legacy, Hello), "Legacy clients must not accept hello.");
            await RejectAsync(() => store.WatchValuesAsync([Node], legacy), "Legacy controls must be rejected.");
            store.Disconnect(legacy);
        }
        var control = Encoding.UTF8.GetBytes("""{"type":"watch_values","nodeIds":[]}""");
        var padded = control.Concat(Enumerable.Repeat((byte)' ', 4096 - control.Length)).ToArray();
        Require(CompiledPreviewProtocol.CreateControl(padded).Length == 4144, "Exactly4096 byte valid controls must be accepted.");
        foreach (var invalid in new[]
        {
            padded.Concat(new byte[] { 32 }).ToArray(), new byte[] { 0xff },
            Encoding.UTF8.GetBytes("""{"type":"profile_control","enabled":true,"nodes":true,"accessors":false,"intervalMs":99}"""),
            Encoding.UTF8.GetBytes("""{"type":"watch_values","nodeIds":["bad"]}"""),
            Encoding.UTF8.GetBytes("""{"type":"watch_values","type":"watch_values","nodeIds":[]}""")
        })
        {
            try { CompiledPreviewProtocol.CreateControl(invalid); throw new Exception("Invalid controls must be rejected."); }
            catch (ArgumentException) { }
        }
        Require(new PreviewTelemetryStore().Clients().Count == 0, "Hosts must not share static telemetry.");
    }

    private static void VerifyPanel()
    {
        var model = new MainViewModel();
        var store = model.PreviewTelemetry;
        var id = Guid.NewGuid();
        store.Connect(id, 3, _ => Task.CompletedTask);
        var window = new PreviewProfilerWindow { DataContext = model };
        var language = LocalizationService.Shared;
        var original = language.SelectedLanguage;
        window.Show();
        try
        {
            Refresh(window);
            Require(!window.FindControl<Button>("StartButton")!.IsEnabled, "Unnegotiated clients must not enable capture.");
            Require(Receive(store, id, Hello), "Panel peer negotiation failed.");
            Refresh(window);
            Require(window.FindControl<Button>("StartButton")!.IsEnabled && window.FindControl<Button>("WatchButton")!.IsEnabled,
                "Advertised capabilities must enable the real controls.");
            Require(Receive(store, id, Page(7, 1, 0, 1, Metric("node"))) && Receive(store, id, Values()), "Panel fixtures failed.");
            Refresh(window);
            Require(window.FindControl<ListBox>("MetricList")!.Items.Count == 1 &&
                window.FindControl<TextBlock>("HistoryCount")!.Text == "1 / 300" &&
                window.FindControl<TextBlock>("TotalWork")!.Text == "1 ms" &&
                window.FindControl<TextBlock>("ValuesStatus")!.Text!.Contains("0.375") &&
                window.FindControl<TextBox>("ValuesText")!.Text!.Contains(Node.ToString("D")),
                "Panel must display actual metrics and native value/time feedback.");
            foreach (var code in new[] { "ja", "en" })
            {
                language.SelectedLanguage = language.Languages.Single(l => l.Id == code);
                Dispatcher.UIThread.RunJobs();
                Require(window.Title == language.Get("Profiler.Title") &&
                    window.FindControl<Button>("StartButton")!.Content?.ToString() == language.Get("Profiler.Start"),
                    "Profiler controls and title must be localized live.");
            }
            store.Disconnect(id);
            Refresh(window);
            Require(!window.FindControl<Button>("StartButton")!.IsEnabled &&
                window.FindControl<ListBox>("MetricList")!.Items.Count == 0 &&
                window.FindControl<TextBlock>("HistoryCount")!.Text == "0 / 300" &&
                string.IsNullOrEmpty(window.FindControl<TextBox>("ValuesText")!.Text),
                "Disconnect must clear the panel and disable controls.");
        }
        finally { window.Close(); language.SelectedLanguage = original; }
    }

    private static void Refresh(PreviewProfilerWindow window) =>
        typeof(PreviewProfilerWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
