using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

[McpServerToolType]
public sealed class GraphDocumentationMcpTools
{
    private static readonly string[] ApprovedSamples =
        ["Fireworks.lfxproj", "SolarSystem.lfxproj", "SolarSystemMatrix.lfxproj"];

    internal static string SamplePath(string sample)
    {
        if (!ApprovedSamples.Contains(sample, StringComparer.Ordinal))
            throw new McpException("Choose a bundled documentation sample: " + string.Join(", ", ApprovedSamples));
        return Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", sample);
    }

    [McpServerTool(Name = "libera_graph_capture_documentation", ReadOnly = true, OpenWorld = false)]
    [Description("Capture only the visible graph of an unchanged, explicitly opened bundled sample. Requires an absolute isolated LIBERAFX_DATA_ROOT and LIBERAFX_MCP_DEBUG=1. Returns PNG and reproducibility metadata, never writes files or captures other panes/apps. This is not engine rendering evidence.")]
    public static async Task<CallToolResult> Capture(MainViewModel documents,
        [Description("Exact bundled filename: Fireworks.lfxproj, SolarSystem.lfxproj or SolarSystemMatrix.lfxproj.")] string sample,
        [Description("Explicitly frame all before capture; changes viewport only, not graph/history.")] bool frameAll = true)
    {
        ChatMcpExecution.RequireProjectFileAccess();
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new McpException("Documentation capture requires an isolated absolute LIBERAFX_DATA_ROOT.");
        var path = SamplePath(sample);
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new McpException("The bundled sample is missing or too large.");
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new MemoryStream(bytes, writable: false);
        var expected = (await new ProjectFileService().LoadAsync(stream)).Graph;
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var document = documents.ActiveDocument;
            if (document?.FileKey is not { } fileKey || document.IsDirty ||
                !Uri.TryCreate(fileKey, UriKind.Absolute, out var uri) || !uri.IsFile ||
                !string.Equals(Path.GetFullPath(uri.LocalPath), Path.GetFullPath(path),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !document.Workspace.IsRootGraph ||
                !GraphSnapshot.ContentEquals(expected, document.Workspace.CreateSnapshot()))
                throw new McpException("Open the exact bundled sample without edits and navigate to its root graph first. Other open documents are not captured.");
            var view = GraphDebugTools.FindView(documents.ActiveGraphProvider);
            if (frameAll) view.FrameAll();
            TopLevel.GetTopLevel(view)?.UpdateLayout();
            var png = GraphDebugTools.Capture(view);
            var metadata = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                scope = "graph-viewport-only",
                sample,
                sampleSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                language = LocalizationService.Shared.SelectedLanguage.Id,
                theme = Application.Current?.ActualThemeVariant.ToString(),
                frameAll,
                state = GraphDebugTools.Inspect(view),
                enginePlaybackVerified = false
            });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = metadata }, ImageContentBlock.FromBytes(png, "image/png")]
            };
        });
    }
}
