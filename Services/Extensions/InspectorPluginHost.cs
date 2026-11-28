using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Inspector.Abstractions;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

internal sealed class InspectorPluginPreferences
{
    public int SchemaVersion { get; set; } = 2;
    public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> Trusted { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Fingerprints { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class InspectorPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly string ContractAssembly = typeof(IInspectorPlugin).Assembly.GetName().Name!;

    public InspectorPluginLoadContext(string assemblyPath) : base(Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == ContractAssembly || assemblyName.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
            return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

internal sealed class InspectorEditorHost : ContentControl, IDisposable
{
    private readonly Control _control;
    private readonly InspectorContext _context;

    public InspectorEditorHost(Control control, InspectorContext context)
    {
        _control = control;
        _context = context;
        Content = control;
    }

    public void Dispose()
    {
        _context.Dispose();
        if (_control is IDisposable disposable) disposable.Dispose();
    }
}

internal sealed class InspectorContext : IInspectorContext
{
    private readonly NodeParameterEditorViewModel _parameter;
    private readonly Func<bool> _isCurrent;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public InspectorContext(NodeParameterEditorViewModel parameter, Func<bool> isCurrent)
    {
        _parameter = parameter;
        _isCurrent = isCurrent;
        _lifetimeToken = _lifetime.Token;
        _parameter.PropertyChanged += OnParameterChanged;
    }

    public string Type => _parameter.Type;
    public string Name => _parameter.Name;
    public string Description => _parameter.Description;
    public JsonElement Value => _parameter.InspectorValue;
    public bool IsReadOnly
    {
        get
        {
            if (_disposed) return true;
            if (!_isCurrent())
            {
                Dispose();
                return true;
            }
            return !_parameter.IsEnabled;
        }
    }
    private readonly CancellationToken _lifetimeToken;
    public CancellationToken Lifetime => _lifetimeToken;
    public event EventHandler? ValueChanged;

    public bool TrySetValue(JsonElement value, out string? error)
    {
        error = null;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            error = "Inspector edits must be requested on the UI thread.";
            return false;
        }
        if (IsReadOnly)
        {
            error = "This inspector context is no longer active or is read-only.";
            return false;
        }
        if (!_parameter.TrySetInspectorValue(value))
        {
            error = _parameter.Status;
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parameter.PropertyChanged -= OnParameterChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        ValueChanged = null;
    }

    private void OnParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeParameterEditorViewModel.ValueText) or nameof(NodeParameterEditorViewModel.JsonValue) or
            nameof(NodeParameterEditorViewModel.ColorValue) or nameof(NodeParameterEditorViewModel.X) or
            nameof(NodeParameterEditorViewModel.Y) or nameof(NodeParameterEditorViewModel.Z) or
            nameof(NodeParameterEditorViewModel.W))
            ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
