using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class LiveGameWindowViewModel : ObservableObject, IDisposable
{
    private readonly Func<LiveGameGraphSource> captureActiveGraph;
    private readonly TimeSpan timeout;
    private LiveGameSpoolClient? client;
    private CancellationTokenSource? operation;
    private string endpointDirectory = string.Empty, statusKey = "Offline";
    private object?[] statusValues = [];
    private LiveGameInstance? selectedInstance;
    private bool busy, consent, disposed;
    private long connectionVersion;
    public Task DebugDisconnectCompletion { get; private set; } = Task.CompletedTask;
    public ObservableCollection<LiveGameInstance> Instances { get; } = [];
    public string EndpointDirectory
    {
        get => endpointDirectory;
        set
        {
            if (busy || !SetProperty(ref endpointDirectory, value)) return;
            Disconnect();
        }
    }
    public string Status => Text(statusKey, statusValues);
    public string Destination => client is null ? string.Empty :
        Text("Destination", client.Endpoint.Engine, client.Endpoint.Process, client.Endpoint.Session);
    public bool Busy { get => busy; private set { if (SetProperty(ref busy, value)) NotifyCommands(); } }
    public bool Connected => client is not null;
    public bool RequiresRefresh => client?.RequiresRefresh == true;
    public bool Consent
    {
        get => consent;
        set { if (SetProperty(ref consent, value)) { if (!value) Disconnect(); NotifyCommands(); } }
    }
    public LiveGameInstance? SelectedInstance
    { get => selectedInstance; set { if (SetProperty(ref selectedInstance, value)) NotifyCommands(); } }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public LiveGameWindowViewModel(Func<LiveGameGraphSource> captureActiveGraph, TimeSpan? timeout = null)
    {
        this.captureActiveGraph = captureActiveGraph;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !disposed && !Busy && Consent && !string.IsNullOrWhiteSpace(EndpointDirectory));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !disposed && !Busy && Connected);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !disposed && !Busy && Connected && Consent &&
            !RequiresRefresh && SelectedInstance is not null);
        DisconnectCommand = new RelayCommand(Disconnect, () => !disposed && !Busy && Connected);
        CancelCommand = new RelayCommand(() => operation?.Cancel(), () => Busy);
    }
    private async Task ConnectAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed || Busy) return;
        var version = ++connectionVersion;
        await RunAsync(async token =>
        {
            if (client is { } previous)
            {
                DebugDisconnectCompletion = previous.DisconnectDebugAsync();
                ObserveCleanup(DebugDisconnectCompletion);
            }
            client = null;
            Instances.Clear();
            SelectedInstance = null;
            OnPropertyChanged(nameof(Destination));
            LiveGameSpoolClient connected;
            try
            {
                connected = await LiveGameSpoolClient.ConnectOwnedAsync(EndpointDirectory, token, timeout,
                    candidate =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (disposed || version != connectionVersion) throw new OperationCanceledException(token);
                        client = candidate;
                    });
            }
            catch
            {
                if (version == connectionVersion && client is { } failed)
                {
                    DebugDisconnectCompletion = failed.DebugDisconnectCompletion;
                    ObserveCleanup(DebugDisconnectCompletion);
                    client = null;
                    OnPropertyChanged(nameof(Destination));
                }
                throw;
            }
            if (disposed || version != connectionVersion || token.IsCancellationRequested)
            {
                connected.Dispose();
                token.ThrowIfCancellationRequested();
                return;
            }
            client = connected;
            OnPropertyChanged(nameof(Destination));
            OnPropertyChanged(nameof(Connected));
            var response = await connected.ListAsync(token);
            token.ThrowIfCancellationRequested();
            Accept(response, "Listed");
        });
    }
    private async Task RefreshAsync() => await RunAsync(async token => Accept(await client!.ListAsync(token), "Listed"));
    private async Task ApplyAsync() => await RunAsync(async token =>
    {
        // Capture document and selection ON UI THREAD before compiler conversion.
        // Changing active documents later cannot redirect an in-flight update.
        var target = SelectedInstance?.Target ?? throw new InvalidOperationException(Text("SelectTarget"));
        var source = captureActiveGraph();
        var destination = client ?? throw new InvalidOperationException(Text("Offline"));
        SetStatus("Compiling", source.Name);
        var package = await Task.Run(() => LiveGameGraphCompiler.Compile(source), token).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        SetStatus("Applying", source.Name, target);
        Accept(await destination.ReplaceAsync(target, package, token), "Applied");
    });
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed || Busy) return;
        using var current = new CancellationTokenSource(timeout);
        operation = current;
        Busy = true;
        var version = connectionVersion;
        try { await action(current.Token); }
        catch (LiveGameOutcomeUnknownException) { if (!disposed && version == connectionVersion) SetStatus("UnknownOutcome"); }
        catch (OperationCanceledException) { if (!disposed && version == connectionVersion) SetStatus("Cancelled"); }
        catch (Exception error) { if (!disposed && version == connectionVersion) SetStatus("Failed", error.Message); }
        finally
        {
            operation = null;
            Busy = false;
            OnPropertyChanged(nameof(Connected));
            OnPropertyChanged(nameof(RequiresRefresh));
            NotifyCommands();
        }
    }
    private void Accept(LiveGameResponse response, string success)
    {
        if (disposed || operation?.IsCancellationRequested == true) return;
        var selected = SelectedInstance?.Target;
        Instances.Clear();
        foreach (var instance in response.Instances) Instances.Add(instance);
        SelectedInstance = Instances.FirstOrDefault(instance => instance.Target == selected);
        if (response.Result == "Ok") SetStatus(success, Instances.Count);
        else SetStatus("Rejected", response.Result, response.Error ?? string.Empty);
    }
    private void NotifyCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private void Disconnect()
    {
        // Only debug resources are released. No Stop/Destroy/NaturalEnd command.
        var version = ++connectionVersion;
        operation?.Cancel();
        var detached = client;
        client = null;
        Instances.Clear();
        SelectedInstance = null;
        SetStatus(detached is null ? "Offline" : detached.SupportsDebugDisconnect ? "DebugClearing" : "DebugClearUnsupported");
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(Connected)); OnPropertyChanged(nameof(RequiresRefresh));
        NotifyCommands();
        if (detached is not null)
        {
            DebugDisconnectCompletion = DisconnectCapturedAsync(detached, version);
            ObserveCleanup(DebugDisconnectCompletion);
        }
    }
    private async Task DisconnectCapturedAsync(LiveGameSpoolClient detached, long version)
    {
        try
        {
            var response = await detached.DisconnectDebugAsync();
            if (!disposed) await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!disposed && version == connectionVersion)
                    SetStatus(response is null ? "DebugClearUnsupported" : "DebugCleared");
            });
        }
        catch (Exception error)
        {
            if (!disposed) await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!disposed && version == connectionVersion) SetStatus("DebugClearFailed", error.Message);
            });
            throw; // acknowledgement remains observable by the host/test caller
        }
    }
    private static void ObserveCleanup(Task task) =>
        _ = task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Disconnect();
    }
    private void SetStatus(string key, params object?[] values)
    { statusKey = key; statusValues = values; OnPropertyChanged(nameof(Status)); }
    public void RefreshLocalizedText()
    { OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Destination)); }
    public void ReportLocalFailure(Exception error) => SetStatus("Failed", error.Message);
    private static string Text(string key, params object?[] values) => LocalizationService.Shared.Get("LiveGame." + key, values);
}
