using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

namespace LiberaFx.ViewModels;

public sealed class ExecutionDebugPaneViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private ExecutionDebugConnection? _connection;
    private ExecutionDebugFrame? _frame;
    private GraphDocumentViewModel? _document;
    private GraphWorkspaceViewModel? _highlight;
    private readonly Dictionary<Guid, (Guid[] Path, GraphNodeSnapshot Node)> _nodes = [];
    private readonly HashSet<Guid> _breakpoints = [];
    private ExecutionDebugCommand? _pendingControl;
    private string _status = "ExecutionDebug.Off";
    private int _error;
    private bool _starting;
    private bool _packageMode;
    public bool PackageMode { get => _packageMode; set { if (SetProperty(ref _packageMode, value)) Stop(); } }

    public ExecutionDebugPaneViewModel(MainViewModel main)
    {
        _main = main;
        StartCommand = new AsyncRelayCommand(StartAsync, () => OperatingSystem.IsWindows() && _connection is null);
        DisconnectCommand = new RelayCommand(() => Stop(), () => _connection is not null);
        BreakpointCommand = new RelayCommand(ToggleBreakpoint, () => _connection is null ||
            _frame?.State is ExecutionDebugState.Running or ExecutionDebugState.Paused);
        PauseCommand = new RelayCommand(() => SendControl(ExecutionDebugCommand.Pause),
            () => CanSendControl(ExecutionDebugCommand.Pause));
        StepCommand = new RelayCommand(() => SendControl(ExecutionDebugCommand.Step),
            () => CanSendControl(ExecutionDebugCommand.Step));
        ResumeCommand = new RelayCommand(() => SendControl(ExecutionDebugCommand.Resume),
            () => CanSendControl(ExecutionDebugCommand.Resume));
        LocateCommand = new RelayCommand(Locate, () => _frame is { } frame && _nodes.ContainsKey(frame.Node));
        _main.ActiveGraphProvider.PropertyChanged += (_, _) => UpdateHighlight();
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.ActiveDocument)) return;
            Stop();
            _breakpoints.Clear();
            Refresh();
        };
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
    }

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public IRelayCommand BreakpointCommand { get; }
    public IRelayCommand PauseCommand { get; }
    public IRelayCommand StepCommand { get; }
    public IRelayCommand ResumeCommand { get; }
    public IRelayCommand LocateCommand { get; }
    private bool IsPaused => _frame?.State == ExecutionDebugState.Paused;
    public string Status => T(!OperatingSystem.IsWindows() ? "ExecutionDebug.Windows" :
        _pendingControl is null ? _status : "ExecutionDebug.CommandPending", _error);
    public string Invitation => _connection?.Invitation ?? "";
    public string Target => _frame is { Generation: > 0 } frame
        ? frame.Instance != 0
            ? T("ExecutionDebug.Child", frame.Container.ToString("D"), frame.Instance, frame.Generation)
            : T("ExecutionDebug.Root", frame.Generation)
        : "";
    public string CurrentNode => _frame is { Node: var id } frame && id != Guid.Empty
        ? T("ExecutionDebug.Node", NodeName(id), frame.Time)
        : "";
    public string CurrentSignalSource => _frame is { SignalSource: var id } && id != Guid.Empty
        ? T("ExecutionDebug.SignalSource", NodeName(id))
        : "";
    public string Breakpoints => T("ExecutionDebug.Breakpoints", _breakpoints.Count);
    public IReadOnlyList<string> Inputs => !IsPaused ? [] : _frame!.Inputs.Select(input =>
    {
        var kind = T(input.Values.Length == 0 ? "ExecutionDebug.Unavailable" :
            input.Kind == 1 ? "ExecutionDebug.Color" : input.Kind == 2 ? "ExecutionDebug.Int32" : "ExecutionDebug.Numeric");
        var source = input.Source != Guid.Empty ? NodeName(input.Source) :
            T(input.Connected ? "ExecutionDebug.Connected" : "ExecutionDebug.Default");
        return T("ExecutionDebug.Operand", input.Slot + 1, source, kind,
            string.Join(", ", input.Values.Select(value => value.ToString("G17", CultureInfo.CurrentCulture))));
    }).ToArray();

    public IReadOnlyList<string> AuthoredParameters
    {
        get
        {
            if (!IsPaused || !_nodes.TryGetValue(_frame!.Node, out var origin) ||
                !GraphNodeDefinition.TryFind(origin.Node.Kind.Value, out var definition))
            {
                return [];
            }

            var snapshot = origin.Node;
            return definition!.Parameters.Select(parameter =>
            {
                var name = LocalizationService.Shared.GetOrDefault(parameter.DisplayNameKey, parameter.Name);
                var value = snapshot.Parameters?.FirstOrDefault(item => item.Id == parameter.Id)?.Value
                    ?? parameter.DefaultValue;
                return T("ExecutionDebug.AuthoredParameter", name, value.ToString());
            }).ToArray();
        }
    }

    private string NodeName(Guid id) => _nodes.TryGetValue(id, out var node) ? node.Node.Name : id.ToString("D");

    private static string T(string key, params object?[] args) => LocalizationService.Shared.Get(key, args);

    private async Task StartAsync()
    {
        if (_main.ActiveDocument is not { } document || _starting) return;
        _starting = true;
        _document = document;
        document.Workspace.History.StateChanged += OnRevisionChanged;
        _status = "ExecutionDebug.Compiling";
        Refresh();
        try
        {
            var graph = document.Workspace.CreateSnapshot();
            var isPackage = PackageMode;
            var program = await Task.Run(() => isPackage
                ? SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph))
                : VfxProgram.Compile(graph).ToBinary());
            if (_document != document) return;
            _nodes.Clear();
            Index(graph, []);
            var connection = new ExecutionDebugConnection(program);
            _connection = connection;
            _status = "ExecutionDebug.Waiting";
            Refresh();
            _ = ObserveAsync(connection, _breakpoints.Where(_nodes.ContainsKey).ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Stop("ExecutionDebug.CannotStart");
        }
        finally { _starting = false; }
    }

    private async Task ObserveAsync(ExecutionDebugConnection connection, Guid[] breakpoints)
    {
        try
        {
            await connection.RunAsync(breakpoints, (command, frame) =>
                Dispatcher.UIThread.Post(() => Receive(connection, command, frame))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException) { }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_connection == connection)
                    Stop(_error == 2 ? "ExecutionDebug.Mismatch" : "ExecutionDebug.Disconnected");
            });
        }
    }

    private void Receive(ExecutionDebugConnection connection, ExecutionDebugCommand command, ExecutionDebugFrame frame)
    {
        if (_connection != connection)
        {
            return;
        }
        if (_pendingControl == command)
        {
            _pendingControl = null;
        }
        else if (command == ExecutionDebugCommand.Poll &&
            _frame is { } previous && frame.State == previous.State && frame.Sequence == previous.Sequence &&
            frame.Result == previous.Result)
        {
            return;
        }
        _frame = frame;
        _error = frame.Result;
        _status = frame.Result == 2 ? "ExecutionDebug.Mismatch" :
            frame.Result != 0 ? "ExecutionDebug.Rejected" : $"ExecutionDebug.{frame.State}";
        UpdateHighlight();
        Refresh();
    }

    private void Index(GraphSnapshot graph, Guid[] path)
    {
        foreach (var node in graph.Nodes)
        {
            _nodes.TryAdd(node.Id, (path, node));
            if (node.ChildGraph is { } child) Index(child, [.. path, node.Id]);
        }
    }

    private void Locate()
    {
        if (_frame is { } frame && _nodes.TryGetValue(frame.Node, out var node))
            _document?.Workspace.NavigateToNode(node.Path, frame.Node);
    }

    private void OnRevisionChanged(object? sender, EventArgs e) => Stop("ExecutionDebug.Revision");
    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    public void Dispose()
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        _breakpoints.Clear();
        Stop();
    }

    internal void Stop(string status = "ExecutionDebug.Off")
    {
        if (_document is { } document) document.Workspace.History.StateChanged -= OnRevisionChanged;
        _document = null;
        _connection?.Dispose();
        _connection = null;
        _frame = null;
        _pendingControl = null;
        _error = 0;
        _status = status;
        UpdateHighlight();
        Refresh();
    }

    private void UpdateHighlight()
    {
        var workspace = _main.ActiveGraphProvider.Graph;
        if (_highlight != workspace)
        {
            _highlight?.SetExecutionDebug(null, []);
            _highlight = workspace;
        }
        workspace.SetExecutionDebug(_frame, _breakpoints.ToHashSet());
    }

    private void ToggleBreakpoint()
    {
        if (!BreakpointCommand.CanExecute(null) || _main.ActiveGraphProvider.Graph.SelectedNode is not { } node)
        {
            return;
        }
        var add = !_breakpoints.Contains(node.Id);
        if (add && _breakpoints.Count >= 128) { _status = "ExecutionDebug.Limit"; Refresh(); return; }
        if (_connection is not null && !Send(add ? ExecutionDebugCommand.AddBreakpoint : ExecutionDebugCommand.RemoveBreakpoint, node.Id))
            return;
        if (add) _breakpoints.Add(node.Id); else _breakpoints.Remove(node.Id);
        UpdateHighlight();
        Refresh();
    }

    private bool CanSendControl(ExecutionDebugCommand command) => _pendingControl is null &&
        (command == ExecutionDebugCommand.Pause ? _frame?.State == ExecutionDebugState.Running : IsPaused);

    private void SendControl(ExecutionDebugCommand command)
    {
        if (CanSendControl(command) && Send(command))
        {
            _pendingControl = command;
            Refresh();
        }
    }

    private bool Send(ExecutionDebugCommand command, Guid id = default)
    {
        if (_connection?.Send(command, id) == true) return true;
        _status = "ExecutionDebug.Rejected";
        Refresh();
        return false;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Invitation));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CurrentNode));
        OnPropertyChanged(nameof(CurrentSignalSource));
        OnPropertyChanged(nameof(Breakpoints));
        OnPropertyChanged(nameof(Inputs));
        OnPropertyChanged(nameof(AuthoredParameters));
        StartCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        BreakpointCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StepCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        LocateCommand.NotifyCanExecuteChanged();
    }
}
