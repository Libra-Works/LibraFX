using System.Text.Json;
using Avalonia.Controls;

namespace LiberaFx.Inspector.Abstractions;

public static class InspectorSdk
{
    public const int ApiVersion = 1;
}

/// <summary>
/// Entry point implemented by an inspector extension. Metadata used for discovery
/// is read from liberafx-inspector.json and never from this type before enablement.
/// </summary>
public interface IInspectorPlugin
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }
    int ApiVersion { get; }
    IReadOnlyList<IInspectorDefinition> Inspectors { get; }
}

public interface IInspectorDefinition
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    IReadOnlyList<string> SupportedTypes { get; }
    Control CreateEditor(IInspectorContext context);
}

/// <summary>
/// A short-lived, document-and-selection-bound editing capability.
/// Values are JSON snapshots; edits are validated and recorded by the host.
/// Access this context and its events on the editor UI thread. Dispose it when
/// the editor is released; Lifetime is cancelled when its target is no longer active.
/// </summary>
public interface IInspectorContext : IDisposable
{
    string Type { get; }
    string Name { get; }
    string Description { get; }
    JsonElement Value { get; }
    bool IsReadOnly { get; }
    CancellationToken Lifetime { get; }
    event EventHandler? ValueChanged;
    bool TrySetValue(JsonElement value, out string? error);
}
