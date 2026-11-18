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
    private async Task ConnectAsync() => await RunAsync(async token =>
    {
        client?.Dispose();
        client = null;
        Instances.Clear();
        SelectedInstance = null;
        OnPropertyChanged(nameof(Destination));
        client = await LiveGameSpoolClient.ConnectAsync(EndpointDirectory, token, timeout);
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(Connected));
        var response = await client.ListAsync(token);
        Accept(response, "Listed");
    });
    private async Task RefreshAsync() => await RunAsync(async token => Accept(await client!.ListAsync(token), "Listed"));
    private async Task ApplyAsync() => await RunAsync(async token =>
    {
        // Capture document and selection ON UI THREAD before compiler conversion.
        // Changing active documents later cannot redirect an in-flight update.
        var target = SelectedInstance?.Target ?? throw new InvalidOperationException(Text("SelectTarget"));
        var source = captureActiveGraph();
        SetStatus("Compiling", source.Name);
        var package = await Task.Run(() => LiveGameGraphCompiler.Compile(source), token).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        SetStatus("Applying", source.Name, target);
        Accept(await client!.ReplaceAsync(target, package, token), "Applied");
    });
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed || Busy) return;
        using var current = new CancellationTokenSource(timeout);
        operation = current;
        Busy = true;
        try { await action(current.Token); }
        catch (LiveGameOutcomeUnknownException) { SetStatus("UnknownOutcome"); }
        catch (OperationCanceledException) { SetStatus("Cancelled"); }
        catch (Exception error) { SetStatus("Failed", error.Message); }
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
        // Closing/cancelling the window never sends an engine destroy command.
        operation?.Cancel();
        client?.Dispose();
        client = null;
        Instances.Clear();
        SelectedInstance = null;
        SetStatus("Offline");
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(Connected)); OnPropertyChanged(nameof(RequiresRefresh));
        NotifyCommands();
    }
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
