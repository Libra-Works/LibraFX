using System.Globalization;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class PreviewProfilerWindow : Window
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly PreviewProfilerSettingsService _settings;
    private PreviewProfile? _displayedProfile;
    private PreviewValues? _displayedValues;
    private string _peerSignature = "";
    private string? _controlMessage;
    private bool _busy;
    private bool _refreshing;
    private bool _loadingSettings;
    private Guid? _lastPeerId;
    private string? _settingsMessageKey;
    private object?[] _settingsMessageArgs = [];
    private readonly PreviewProfileHistory _history = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ulong? _selectedSnapshot;
    private (string, Guid?)? _selectedCost;
    private sealed record PeerOption(Guid Id, string Label) { public override string ToString() => Label; }

    public PreviewProfilerWindow()
        : this(new PreviewProfilerSettingsService())
    {
    }

    public PreviewProfilerWindow(PreviewProfilerSettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        _loadingSettings = true;
        NodesCheck.IsChecked = _settings.Current.Nodes;
        AccessorsCheck.IsChecked = _settings.Current.Accessors;
        IntervalInput.Value = _settings.Current.IntervalMs;
        _loadingSettings = false;
        NodesCheck.IsCheckedChanged += OnSettingsChanged;
        AccessorsCheck.IsCheckedChanged += OnSettingsChanged;
        IntervalInput.ValueChanged += (_, _) => SaveSettings();
        if (_settings.LoadError is { } loadError)
        {
            SetSettingsMessage("SettingsLoadFailed", loadError);
        }
        HistoryChart.SampleSelected += index =>
        {
            _selectedSnapshot = _history.Samples[index].SnapshotId;
            LiveCheck.IsChecked = false;
            RenderSelected();
        };
        CostChart.CostSelected += cost =>
        {
            _selectedCost = (cost.Metric.Event, cost.Metric.NodeId);
            ShowCost(cost);
        };
        RenderSelected();
        _refresh.Tick += (_, _) => Refresh();
        Opened += (_, _) =>
        {
            LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
            Refresh();
            _refresh.Start();
        };
        Closed += (_, _) =>
        {
            _refresh.Stop();
            LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        };
    }

    private static string Text(string key, params object?[] args) => LocalizationService.Shared.Get("Profiler." + key, args);
    private Guid? SelectedId => (PeerSelector.SelectedItem as PeerOption)?.Id;
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _peerSignature = "";
        _displayedValues = null;
        Refresh();
        RenderSelected();
    }
    private void OnPeerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        _controlMessage = null;
        Refresh();
    }

    private void Refresh()
    {
        if (DataContext is not MainViewModel model) return;
        var peers = model.PreviewTelemetry.Clients().OrderBy(p => p.ClientId).ToArray();
        var signature = string.Join("|", peers.Select(p => $"{p.ClientId}:{p.Engine}:{p.Role}:{string.Join(",", p.Capabilities)}"));
        if (signature != _peerSignature)
        {
            var selected = SelectedId;
            _refreshing = true;
            var options = peers.Select(p => new PeerOption(p.ClientId, Text("PeerLabel", p.Engine ?? Text("Unknown"),
                Text(p.Role switch { "editor" => "RoleEditor", "runtime" => "RoleRuntime", "viewer" => "RoleViewer", _ => "Unknown" }),
                p.Protocol, p.ClientId))).ToArray();
            PeerSelector.ItemsSource = options;
            // Do not silently redirect a disconnected peer's controls to another client.
            PeerSelector.SelectedItem = selected.HasValue ? options.FirstOrDefault(p => p.Id == selected.Value) :
                options.Length == 1 && !_lastPeerId.HasValue ? options[0] : null;
            _refreshing = false;
            _peerSignature = signature;
        }
        var peer = peers.FirstOrDefault(p => p.ClientId == SelectedId);
        if (_lastPeerId != SelectedId)
        {
            _lastPeerId = SelectedId;
            _displayedProfile = null;
            _displayedValues = null;
            MetricList.ItemsSource = null;
            _history.Clear();
            _selectedSnapshot = null;
            _selectedCost = null;
            RenderSelected();
            ValuesText.Text = "";
            _controlMessage = null;
            WatchInput.Text = peer is null ? "" : string.Join(", ", peer.WatchedNodes);
        }
        var profileSupported = peer?.Capabilities.Contains("profiling") == true;
        var valuesSupported = peer?.Capabilities.Contains("values") == true;
        StartButton.IsEnabled = StopButton.IsEnabled = profileSupported && !_busy;
        WatchButton.IsEnabled = SelectionButton.IsEnabled = valuesSupported && !_busy;
        PeerSelector.IsEnabled = !_busy;
        NodesCheck.IsEnabled = AccessorsCheck.IsEnabled = IntervalInput.IsEnabled = profileSupported && !_busy;
        PeerStatus.Text = peer is null ? Text(peers.Length == 0 ? "Disconnected" : "SelectPeer") :
            Text("Capabilities", peer.Capabilities.Count == 0 ? Text("None") : string.Join(", ", peer.Capabilities));
        CaptureStatus.Text = peer?.RequestedProfile is { } requested ?
            Text("Requested", Text(requested.Enabled ? "Enabled" : "Disabled"), requested.IntervalMs,
                Text(requested.Nodes ? "Enabled" : "Disabled"), Text(requested.Accessors ? "Enabled" : "Disabled")) : Text("NotRequested");
        ProfileStatus.Text = !profileSupported ? Text("ProfileUnsupported") :
            peer?.Profile is { } profile ? Text("Snapshot", profile.ProgramSequence, profile.SnapshotId, profile.Metrics.Count, profile.Dropped) :
                Text("ProfileEmpty");
        if (peer?.ProfilePending == true) ProfileStatus.Text += " " + Text("Pending");
        if (!ReferenceEquals(_displayedProfile, peer?.Profile))
        {
            _displayedProfile = peer?.Profile;
            MetricList.ItemsSource = _displayedProfile?.Metrics;
            if (_displayedProfile is not null) _history.Add(_displayedProfile, _clock.Elapsed.TotalSeconds);
            RenderSelected();
        }
        ValuesStatus.Text = !valuesSupported ? Text("ValuesUnsupported") :
            peer?.Values is { } values ? Text("ValueSnapshot", values.ProgramSequence,
                values.EvaluationTime.ToString("R", CultureInfo.InvariantCulture), values.Values.Count) : Text("ValuesEmpty");
        if (!ReferenceEquals(_displayedValues, peer?.Values))
        {
            _displayedValues = peer?.Values;
            ValuesText.Text = _displayedValues is null ? "" : string.Join(Environment.NewLine, _displayedValues.Values.Select(value =>
                $"{value.NodeId:D}    [{string.Join(", ", value.Components.Select(c => c.ToString("R", CultureInfo.InvariantCulture)))}]"));
        }
        ControlStatus.Text = _settingsMessageKey is { } key
            ? Text(key, _settingsMessageArgs)
            : _controlMessage is null ? "" : Text(_controlMessage);
    }

    private void OnHistoryModeChanged(object? sender, RoutedEventArgs e) => RenderSelected();
    private void OnCostModeChanged(object? sender, SelectionChangedEventArgs e) => RenderSelected();
    private void OnSettingsChanged(object? sender, RoutedEventArgs e) => SaveSettings();

    private void SaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        try
        {
            _settings.Save(new(NodesCheck.IsChecked == true, AccessorsCheck.IsChecked == true,
                checked((int)(IntervalInput.Value ?? 500))));
            SetSettingsMessage(null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _loadingSettings = true;
            NodesCheck.IsChecked = _settings.Current.Nodes;
            AccessorsCheck.IsChecked = _settings.Current.Accessors;
            IntervalInput.Value = _settings.Current.IntervalMs;
            _loadingSettings = false;
            SetSettingsMessage("SettingsSaveFailed", ex.Message);
        }
    }

    private void SetSettingsMessage(string? key, params object?[] args)
    {
        _settingsMessageKey = key;
        _settingsMessageArgs = args;
        if (ControlStatus is not null)
        {
            ControlStatus.Text = key is null ? "" : Text(key, args);
        }
    }

    private void OnClearHistory(object? sender, RoutedEventArgs e)
    {
        _history.Clear();
        _selectedSnapshot = null;
        _displayedProfile = null;
        Refresh();
    }

    private string? NodeName(Guid id) =>
        (DataContext as MainViewModel)?.Graph.Nodes.FirstOrDefault(node => node.Id == id)?.Name;

    private void RenderSelected()
    {
        if (HistoryChart is null || CostChart is null || CostMode is null || LiveCheck is null) return;
        var samples = _history.Samples;
        var index = LiveCheck.IsChecked == true ? samples.Count - 1 :
            Enumerable.Range(0, samples.Count).FirstOrDefault(i => samples[i].SnapshotId == _selectedSnapshot, -1);
        if (index < 0 && samples.Count > 0) index = samples.Count - 1;
        var sample = index < 0 ? null : samples[index];
        _selectedSnapshot = sample?.SnapshotId;
        HistoryChart.Update(samples, index, LiveCheck.IsChecked == true);
        var costs = sample is null ? Array.Empty<ProfileCost>() :
            CostMode.SelectedIndex == 0 ? sample.IntervalCosts : sample.CumulativeCosts;
        CostChart.Update(costs, NodeName);
        var total = sample?.CumulativeMs ?? 0;
        TotalWork.Text = total.ToString("0.####", CultureInfo.CurrentCulture) + " ms";
        RecordedCalls.Text = (sample?.CumulativeCalls ?? 0)
            .ToString("N0", CultureInfo.CurrentCulture);
        PeakInterval.Text = samples.Select(s => s.TotalMs).DefaultIfEmpty(0).Max()
            .ToString("0.####", CultureInfo.CurrentCulture) + " ms";
        HistoryCount.Text = $"{samples.Count} / {PreviewProfileHistory.Capacity}";
        SelectedSampleStatus.Text = sample is null ? Text("ProfileEmpty") :
            Text("SelectedSample", sample.ProgramSequence, sample.SnapshotId,
                sample.ReceivedSeconds.ToString("0.00", CultureInfo.CurrentCulture)) +
            (sample.Baseline ? " " + Text("Baseline") : "");
        var selected = costs.FirstOrDefault(cost => _selectedCost == (cost.Metric.Event, cost.Metric.NodeId));
        if (selected is null) CostDetail.Text = Text("DetailHelp");
        else ShowCost(selected);
    }

    private void ShowCost(ProfileCost cost)
    {
        var metric = cost.Metric;
        var name = metric.NodeId is { } id ? NodeName(id) ?? id.ToString("D") : metric.Event;
        CostDetail.Text = name + Environment.NewLine + metric.Event + Environment.NewLine +
            (metric.NodeId?.ToString("D") ?? "") + Environment.NewLine + Environment.NewLine +
            Text("DetailNumbers", cost.Calls, cost.ExclusiveMs.ToString("0.######", CultureInfo.CurrentCulture),
                cost.InclusiveMs.ToString("0.######", CultureInfo.CurrentCulture),
                (cost.Calls == 0 ? 0 : cost.InclusiveMs / cost.Calls).ToString("0.######", CultureInfo.CurrentCulture)) +
            (cost.Cumulative ? Environment.NewLine + Text("DetailRange",
                metric.MinMs.ToString("0.######", CultureInfo.CurrentCulture),
                metric.MaxMs.ToString("0.######", CultureInfo.CurrentCulture)) : "");
    }

    private async void OnStart(object? sender, RoutedEventArgs e) => await CaptureAsync(true);
    private async void OnStop(object? sender, RoutedEventArgs e) => await CaptureAsync(false);
    private Task CaptureAsync(bool enabled) => RunControlAsync((model, id) => model.PreviewTelemetry.ProfileControlAsync(
        enabled, NodesCheck.IsChecked == true, AccessorsCheck.IsChecked == true, checked((int)(IntervalInput.Value ?? 500)), id));

    private void OnUseSelection(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
            WatchInput.Text = string.Join(", ", model.Graph.SelectedNodes.Take(65).Select(node => node.Id.ToString("D")));
    }

    private async void OnWatch(object? sender, RoutedEventArgs e) => await RunControlAsync((model, id) =>
    {
        var tokens = (WatchInput.Text ?? "").Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 64) throw new ArgumentException();
        var ids = tokens.Select(token => Guid.TryParseExact(token, "D", out var nodeId) ? nodeId :
            throw new ArgumentException()).ToArray();
        return model.PreviewTelemetry.WatchValuesAsync(ids, id);
    });

    private async Task RunControlAsync(Func<MainViewModel, Guid, Task> action)
    {
        if (_busy || DataContext is not MainViewModel model || SelectedId is not { } id) return;
        _busy = true;
        _controlMessage = "Sending";
        Refresh();
        try
        {
            await action(model, id);
            _controlMessage = "Sent";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or
            OperationCanceledException or System.Net.WebSockets.WebSocketException)
        { _controlMessage = ex is ArgumentException or OverflowException ? "InvalidControl" : "SendFailed"; }
        finally { _busy = false; Refresh(); }
    }
}
