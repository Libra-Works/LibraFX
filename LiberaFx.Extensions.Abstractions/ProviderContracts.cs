using System.Text.Json;

namespace LiberaFx.Extensions.Abstractions;

public interface IExtensionProvider
{
    string Id { get; }
    string Name { get; }
}

public sealed record VcsRevision(string Id, string Summary, DateTimeOffset? Timestamp);

public interface IVersionControlProvider : IExtensionProvider
{
    Task<IReadOnlyList<VcsRevision>> GetHistoryAsync(string localFile, CancellationToken cancellation);
    Task<byte[]> ReadRevisionAsync(string localFile, string revision, CancellationToken cancellation);
}

public sealed record ChatMessage(string Role, string Text, string? ToolCallId = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null);
public sealed record ChatTool(string Name, string Description, JsonElement InputSchema);
public sealed record ChatToolCall(string Id, string Name, JsonElement Arguments);
public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ChatTool> Tools);
public sealed record ChatReply(string Text, IReadOnlyList<ChatToolCall> ToolCalls);

public interface IChatAdapter : IExtensionProvider
{
    string Destination { get; }
    string PrivacyNotice { get; }
    Task<ChatReply> SendAsync(ChatRequest request, CancellationToken cancellation);
}

public sealed record CrashAttachment(string Name, string MediaType, ReadOnlyMemory<byte> Content);
public sealed record CrashReport(string Title, string Body, IReadOnlyList<CrashAttachment>? Attachments = null);
public sealed record ReportDestination(string Label, Uri Address, string Visibility);

public enum CrashAttachmentSupport { None, InlineText }

public interface ICrashReportProvider : IExtensionProvider
{
    // Older providers remain fail-closed until they explicitly opt in.
    CrashAttachmentSupport AttachmentSupport => CrashAttachmentSupport.None;
    Task<ReportDestination> GetDestinationAsync(CancellationToken cancellation);
    Task<Uri> SubmitAsync(ReportDestination confirmedDestination, CrashReport report,
        CancellationToken cancellation);
}
