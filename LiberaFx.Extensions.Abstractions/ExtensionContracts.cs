using System.Text.Json;

namespace LiberaFx.Extensions.Abstractions;

public static class ExtensionSdk
{
    public const int ApiVersion = 1;
    public const string CommandPoint = "liberafx.editor.commands";
    public const string VcsPoint = "liberafx.editor.vcs";
    public const string ChatPoint = "liberafx.editor.chat";
    public const string CrashReportPoint = "liberafx.editor.crash-reports";
}

public interface IEditorExtension : IDisposable
{
    void Register(IExtensionRegistration registration);
}

public interface IExtensionRegistration
{
    void AddCommand(ExtensionCommand command);
    void AddVersionControlProvider(IVersionControlProvider provider);
    void AddChatAdapter(IChatAdapter adapter);
    void AddCrashReportProvider(ICrashReportProvider provider);
}

public sealed record ExtensionCommand(string Id, string Name, string Description,
    Func<IExtensionDocumentContext, CancellationToken, Task> ExecuteAsync);

/// <summary>A UI-thread capability bound to one document, graph path and history revision.</summary>
public interface IExtensionDocumentContext : IDisposable
{
    JsonElement Snapshot { get; }
    CancellationToken Lifetime { get; }
    bool IsCurrent { get; }
    bool TryApplyPlan(JsonElement plan, out string? error);
}
