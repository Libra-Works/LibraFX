using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

/// <summary>Session-only chat: no keys, transcripts or tool results are persisted or logged.</summary>
public sealed partial class ChatWindow : UserControl, IDisposable
{
    private readonly MainViewModel _main;
    private readonly ComboBox _adapters = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _endpoint = new() { Text = "http://127.0.0.1:1234/v1/chat/completions" };
    private readonly TextBox _model = new();
    private readonly TextBox _key = new() { PasswordChar = '●' };
    private readonly Control _endpointRow;
    private readonly Control _modelRow;
    private readonly Control _keyRow;
    private readonly TextBlock _destination = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _transcript = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        MinHeight = 120, MaxHeight = 300
    };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65 };
    private readonly CheckBox _share = new();
    private readonly Button _connect = new();
    private readonly Button _disconnect = new();
    private readonly Button _send = new();
    private readonly Button _cancel = new();
    private readonly Button _clear = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(8) };
    private CancellationTokenSource? _operation;
    private ChatMcpClient? _mcp;
    private OpenAiCompatibleChatAdapter? _configured;
    private IChatAdapter? _activeAdapter;
    private ChatMcpExecution.Lease? _conversationContext;
    private string? _selectedDestination;
    private string? _selectedPrivacy;
    private bool _busy;
    private bool _checking;
    private bool _closed;
    private bool _reloading;
    private Window? _confirmation;
    private static string Text(string key) => LocalizationService.Shared.Get("Chat." + key);
    private sealed record Choice(string Name, IChatAdapter? Adapter)
    {
        public override string ToString() => Name;
    }

    public ChatWindow(MainViewModel main)
    {
        _main = main;
        _connect.Content = Text("Connect");
        _disconnect.Content = Text("Disconnect");
        _send.Content = Text("Send");
        _cancel.Content = Text("Cancel");
        _clear.Content = Text("Clear");
        _explainSelection.Content = Text("ExplainSelection");
        _share.Content = Text("Share");
        _share.IsChecked = false;
        _input.PlaceholderText = Text("Input");
        _key.PlaceholderText = Text("Key");
        _model.PlaceholderText = Text("Model");
        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(new TextBlock { Text = Text("Explanation"), TextWrapping = TextWrapping.Wrap });
        top.Children.Add(Label("Adapter", _adapters));
        top.Children.Add(_endpointRow = Label("Endpoint", _endpoint));
        top.Children.Add(_modelRow = Label("Model", _model));
        top.Children.Add(_keyRow = Label("Key", _key));
        top.Children.Add(_destination);
        top.Children.Add(_share);
        top.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _connect, _disconnect, _clear }
        });
        top.Children.Add(_status);
        var bottom = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _input,
                _nodeReferences,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _send, _cancel, _explainSelection } }
            }
        };
        var grid = new Grid { Margin = new Thickness(12), MinHeight = 600, RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        grid.Children.Add(top);
        Grid.SetRow(_transcript, 1);
        grid.Children.Add(_transcript);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);
        Content = new ScrollViewer { Content = grid };
        _connect.Click += async (_, _) => await ConnectAsync();
        _disconnect.Click += (_, _) => Disconnect("Offline");
        _cancel.Click += (_, _) => Disconnect("Cancelled");
        _clear.Click += (_, _) => Disconnect("Cleared");
        _send.Click += async (_, _) => await SendAsync();
        _explainSelection.Click += (_, _) => ExplainSelection();
        _adapters.SelectionChanged += (_, _) => { if (!_reloading) SettingsChanged(); };
        _endpoint.TextChanged += (_, _) => SettingsChanged();
        _model.TextChanged += (_, _) => SettingsChanged();
        _key.TextChanged += (_, _) => SettingsChanged();
        _share.IsCheckedChanged += (_, _) =>
        {
            if (_mcp is not null) Disconnect("ConsentChanged", resetConsent: false);
        };
        _main.Extensions.ChatAdapters.CollectionChanged += OnAdaptersChanged;
        _heartbeat.Tick += async (_, _) => await HeartbeatAsync();
        ReloadAdapters();
        SetStatus("Offline");
        Refresh();
    }

    internal void EndSession()
    {
        Disconnect("Offline");
        _key.Text = "";
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _main.Extensions.ChatAdapters.CollectionChanged -= OnAdaptersChanged;
        EndSession();
    }

    private static Control Label(string key, Control control)
    {
        Avalonia.Automation.AutomationProperties.SetName(control, Text(key));
        return new StackPanel { Spacing = 2, Children = { new TextBlock { Text = Text(key) }, control } };
    }

    private void OnAdaptersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Disconnect("AdaptersChanged");
        ReloadAdapters();
    }

    private void ReloadAdapters()
    {
        _reloading = true;
        _adapters.ItemsSource = new[] { new Choice(Text("Compatible"), null) }
            .Concat(_main.Extensions.ChatAdapters.Select(adapter => new Choice(adapter.Name, adapter))).ToArray();
        _adapters.SelectedIndex = 0;
        _reloading = false;
        RefreshDestination();
    }

    private void SettingsChanged()
    {
        if (_reloading || _closed) return;
        Disconnect("Offline");
        RefreshDestination();
    }

    private void RefreshDestination()
    {
        var adapter = (_adapters.SelectedItem as Choice)?.Adapter;
        var configurable = adapter is null;
        _endpointRow.IsVisible = _modelRow.IsVisible = _keyRow.IsVisible = configurable;
        _selectedDestination = adapter?.Destination;
        _selectedPrivacy = adapter?.PrivacyNotice;
        _destination.Text = adapter is null
            ? Text("Destination") + "\n" + (_endpoint.Text ?? "") + " • " + (_model.Text ?? "") + "\n" + Text("CompatiblePrivacy")
            : Text("Destination") + "\n" + adapter.Destination + "\n" + adapter.PrivacyNotice;
    }

    private void SetStatus(string key) => _status.Text = Text(key);

    private void Refresh()
    {
        var connected = _mcp?.Connected == true && _activeAdapter is not null;
        _input.IsEnabled = connected && !_busy;
        _send.IsEnabled = connected && !_busy;
        _connect.IsEnabled = !_busy && !connected && _adapters.SelectedItem is Choice;
        _disconnect.IsEnabled = connected || _busy;
        _cancel.IsEnabled = _busy;
        _adapters.IsEnabled = !_busy;
        _endpoint.IsEnabled = _model.IsEnabled = _key.IsEnabled = !_busy;
        _share.IsEnabled = !_busy;
        _explainSelection.IsEnabled = connected && !_busy && _share.IsChecked == true;
        _nodeReferences.IsEnabled = !_busy;
    }

    private async Task ConnectAsync()
    {
        if (_busy || _closed) return;
        _busy = true;
        _operation = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var operation = _operation;
        var client = default(ChatMcpClient);
        try
        {
            var choice = _adapters.SelectedItem as Choice ?? throw new InvalidOperationException();
            _activeAdapter = choice.Adapter ?? (_configured = new OpenAiCompatibleChatAdapter(
                _endpoint.Text ?? "", _model.Text ?? "", _key.Text ?? ""));
            if (choice.Adapter is not null) ValidateAdapter(_activeAdapter);
            client = new ChatMcpClient(_main.McpEndpoint, _main.McpAccessToken);
            Refresh();
            SetStatus("Connecting");
            await client.ConnectAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _mcp = client;
            client = null;
            SetStatus("Connected");
            _heartbeat.Start();
        }
        catch (OperationCanceledException) { Disconnect("Cancelled"); }
        catch { Disconnect("ConnectFailed"); }
        finally
        {
            client?.Dispose();
            operation.Dispose();
            if (ReferenceEquals(_operation, operation)) _operation = null;
            _busy = false;
            Refresh();
        }
    }

    private async Task HeartbeatAsync()
    {
        if (_busy || _checking || _mcp is not { Connected: true } client) return;
        _checking = true;
        using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await client.PingAsync(operation.Token); }
        catch { if (!_closed && ReferenceEquals(_mcp, client)) Disconnect("ConnectionLost"); }
        finally { _checking = false; }
    }

    private async Task SendAsync()
    {
        if (_busy || _mcp is not { Connected: true } client || _activeAdapter is not { } adapter ||
            string.IsNullOrWhiteSpace(_input.Text)) return;
        var prompt = _input.Text;
        if (prompt.Length > 32000) { SetStatus("Limit"); return; }
        if (_conversationContext is { } previousContext)
        {
            try { previousContext.Validate(); }
            catch
            {
                Disconnect("ContextChanged");
                _input.Text = prompt;
                return;
            }
            finally { previousContext.Dispose(); _conversationContext = null; }
        }
        _busy = true;
        using var lease = ChatMcpExecution.Capture(_main.ActiveGraphProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lease.Changed);
        operation.CancelAfter(TimeSpan.FromMinutes(5));
        _operation = operation;
        Refresh();
        using var revokeOnCancel = operation.Token.Register(lease.Revoke);
        var share = _share.IsChecked == true;
        try
        {
            lease.Validate();
            if (_messages.Count == 0 && share) _messages.Add(new("system", ReferenceInstructions));
            if (_navigationContextPending && share &&
                !await RefreshNavigationContextAsync(client, lease, operation.Token)) return;
            if (!PrepareSelectionContext(share)) return;
            _messages.Add(new("user", prompt));
            _input.Text = "";
            Append(Text("You"), prompt);
            var tools = share ? client.Tools : Array.Empty<ChatTool>();
            for (var round = 0; round < 12; round++)
            {
                ValidateAdapter(adapter);
                lease.Validate();
                operation.Token.ThrowIfCancellationRequested();
                if (_messages.Count > 128 || _messages.Sum(message => message.Text.Length) > 1024 * 1024)
                    throw new InvalidDataException("Conversation limit.");
                SetStatus("Waiting");
                var request = new ChatRequest(_messages.ToArray(), tools);
                // Adapters have no UI capability. Bound the host wait even if a trusted DLL
                // fails to honor cancellation; revoked MCP capabilities cannot edit later.
                var reply = await Task.Run(() => adapter.SendAsync(request, operation.Token), operation.Token)
                    .WaitAsync(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                lease.Validate();
                ValidateAdapter(adapter);
                if (!ReferenceEquals(_activeAdapter, adapter) || !ReferenceEquals(_mcp, client))
                    throw new OperationCanceledException();
                if (reply is null || reply.ToolCalls is null || reply.Text is null || reply.Text.Length > 1024 * 1024 ||
                    reply.ToolCalls.Count > 16)
                    throw new InvalidDataException("Invalid adapter reply.");
                if (reply.ToolCalls.Any(call => string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 256 ||
                    !tools.Any(tool => tool.Name == call.Name) || call.Arguments.ValueKind != JsonValueKind.Object ||
                    call.Arguments.GetRawText().Length > 256 * 1024) ||
                    reply.ToolCalls.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count() != reply.ToolCalls.Count)
                    throw new InvalidDataException("Invalid or unapproved tool call.");
                _messages.Add(new("assistant", reply.Text, ToolCalls: reply.ToolCalls));
                if (reply.Text.Length > 0)
                {
                    Append(adapter.Name, ChatGraphReferences.DisplayText(reply.Text));
                    ShowNodeReferences(reply.Text);
                }
                if (reply.ToolCalls.Count == 0)
                {
                    _conversationContext = ChatMcpExecution.Capture(_main.ActiveGraphProvider);
                    SetStatus("Connected");
                    return;
                }
                foreach (var call in reply.ToolCalls)
                {
                    if (!share || _share.IsChecked != true) throw new InvalidOperationException("No project sharing consent.");
                    lease.Validate();
                    if (ChatMcpClient.NeedsConfirmation(call.Name))
                    {
                        SetStatus("Confirmation");
                        if (!await ConfirmAsync(adapter.Destination, call, operation.Token))
                            throw new OperationCanceledException();
                    }
                    operation.Token.ThrowIfCancellationRequested();
                    lease.Validate();
                    SetStatus("ToolRunning");
                    var result = await client.CallAsync(call, lease.Token, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    lease.Validate();
                    _messages.Add(new("tool", result, call.Id));
                    Append(Text("Tool"), call.Name + "\n" + result);
                }
            }
            SetStatus("Limit");
            // A partial tool cycle must not be sent as valid conversation on the next turn.
            _messages.Clear();
        }
        catch (OperationCanceledException)
        {
            _messages.Clear();
            SetStatus("Cancelled");
        }
        catch
        {
            Disconnect(client.Connected ? "SendFailed" : "ConnectionLost");
        }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
            _busy = false;
            Refresh();
        }
    }

    private void ValidateAdapter(IChatAdapter adapter)
    {
        // The built-in endpoint/model are immutable. Trusted DLL metadata must
        // remain what was shown when the user selected it and gave consent.
        if (adapter is OpenAiCompatibleChatAdapter) return;
        if (!string.Equals(adapter.Destination, _selectedDestination, StringComparison.Ordinal) ||
            !string.Equals(adapter.PrivacyNotice, _selectedPrivacy, StringComparison.Ordinal))
            throw new InvalidOperationException("Adapter destination/privacy changed; select it again.");
    }

    private async Task<bool> ConfirmAsync(string destination, ChatToolCall call, CancellationToken cancellation)
    {
        var dialog = new Window
        {
            Title = Text("ConfirmTitle"), Width = 680, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _confirmation = dialog;
        var accept = new Button { Content = Text("AllowEdit") };
        var reject = new Button { Content = Text("Reject") };
        var arguments = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Text = call.Arguments.GetRawText()
        };
        var grid = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        grid.Children.Add(new TextBlock
        {
            Text = Text("ConfirmExplanation") + "\n" + destination + "\n" + call.Name, TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(arguments, 1);
        grid.Children.Add(arguments);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { reject, accept } };
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        dialog.Content = grid;
        accept.Click += (_, _) => dialog.Close(true);
        reject.Click += (_, _) => dialog.Close(false);
        using var registration = cancellation.Register(() =>
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_confirmation, dialog)) dialog.Close(false); }));
        try { return TopLevel.GetTopLevel(this) is Window owner && await dialog.ShowDialog<bool>(owner); }
        finally { if (ReferenceEquals(_confirmation, dialog)) _confirmation = null; }
    }

    private void Append(string who, string text)
    {
        var previous = _transcript.Text ?? "";
        if (previous.Length > 2 * 1024 * 1024) previous = Text("Truncated") + "\n";
        _transcript.Text = previous + who + ":\n" + text + "\n\n";
        _transcript.CaretIndex = _transcript.Text.Length;
    }

    private void Disconnect(string reason, bool resetConsent = true)
    {
        _heartbeat.Stop();
        _operation?.Cancel();
        _confirmation?.Close(false);
        _mcp?.Dispose();
        _mcp = null;
        if (resetConsent) _share.IsChecked = false;
        _activeAdapter = null;
        _conversationContext?.Dispose();
        _conversationContext = null;
        _configured?.Dispose();
        _configured = null;
        _messages.Clear();
        _navigationContextPending = false;
        _selectionWorkspace = null;
        _selectionIds = [];
        _selectionPath = [];
        _nodeReferences.Children.Clear();
        _transcript.Text = "";
        _input.Text = "";
        SetStatus(reason);
        Refresh();
    }
}
