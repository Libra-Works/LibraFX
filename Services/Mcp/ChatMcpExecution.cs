using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels.Graph;
using ModelContextProtocol;

namespace LiberaFx.Services.Mcp;

/// <summary>In-process capability carried over authenticated HTTP, checked on the UI thread.</summary>
public static class ChatMcpExecution
{
    public const string Header = "X-LiberaFx-Chat-Context";
    private static readonly AsyncLocal<string?> RequestContext = new();
    private static readonly ConcurrentDictionary<string, Lease> Leases = new();

    internal static void RequireProjectFileAccess()
    {
        if (RequestContext.Value is not null)
            throw new McpException("Project file operations are not available in a graph-scoped chat context.");
    }

    public static IDisposable EnterRequest(string? token)
    {
        var previous = RequestContext.Value;
        RequestContext.Value = string.IsNullOrEmpty(token) ? null : token;
        return new Scope(() => RequestContext.Value = previous);
    }

    // Capture before dispatch: do not rely on Avalonia propagating ExecutionContext.
    public static async Task<T> InvokeAsync<T>(Func<T> action)
    {
        var token = RequestContext.Value;
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token is null) return action(); // Existing external MCP clients retain their contract.
            if (!Leases.TryGetValue(token, out var lease))
                throw new McpException("Chat context expired. No operation was performed.");
            return lease.Execute(action);
        });
    }

    public static Lease Capture(ActiveGraphProvider graphs)
    {
        if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("UI thread required.");
        var lease = new Lease(graphs);
        if (!Leases.TryAdd(lease.Token, lease)) throw new InvalidOperationException("Cannot create chat context.");
        return lease;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public sealed class Lease : IDisposable
    {
        private readonly ActiveGraphProvider _graphs;
        private readonly GraphWorkspaceViewModel _graph;
        private readonly CancellationTokenSource _changed = new();
        private GraphSnapshot _document;
        private GraphSnapshot _level;
        private bool _executing;
        private volatile bool _invalid;
        private bool _disposed;
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public CancellationToken Changed => _changed.Token;

        internal Lease(ActiveGraphProvider graphs)
        {
            _graphs = graphs;
            _graph = graphs.Graph;
            _document = _graph.CreateSnapshot();
            _level = _graph.CreateCurrentSnapshot();
            graphs.PropertyChanged += OnDocumentChanged;
            _graph.PropertyChanged += OnGraphChanged;
            _graph.History.StateChanged += OnHistoryChanged;
        }

        public void Validate()
        {
            if (!Dispatcher.UIThread.CheckAccess() || _disposed || _invalid ||
                !ReferenceEquals(_graph, _graphs.Graph) ||
                !GraphSnapshot.ContentEquals(_document, _graph.CreateSnapshot()) ||
                !GraphSnapshot.ContentEquals(_level, _graph.CreateCurrentSnapshot()))
                throw new McpException("Document, graph level or revision changed. Start a new chat turn.");
        }

        public void Revoke()
        {
            _invalid = true;
            if (!_disposed) _changed.Cancel();
        }

        internal T Execute<T>(Func<T> action)
        {
            Validate();
            _executing = true;
            try { return action(); }
            finally
            {
                _executing = false;
                // Accept only changes made synchronously by this one guarded tool.
                _document = _graph.CreateSnapshot();
                _level = _graph.CreateCurrentSnapshot();
            }
        }

        private void Invalidate()
        {
            if (_executing || _invalid || _disposed) return;
            _invalid = true;
            _changed.Cancel();
        }
        private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e) => Invalidate();
        private void OnHistoryChanged(object? sender, EventArgs e) => Invalidate();
        private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GraphWorkspaceViewModel.IsRootGraph)) Invalidate();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Leases.TryRemove(Token, out _);
            _graphs.PropertyChanged -= OnDocumentChanged;
            _graph.PropertyChanged -= OnGraphChanged;
            _graph.History.StateChanged -= OnHistoryChanged;
            _changed.Cancel();
            _changed.Dispose();
        }
    }
}
