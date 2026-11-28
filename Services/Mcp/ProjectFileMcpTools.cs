using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services.Mcp;

[McpServerToolType]
public sealed class ProjectFileMcpTools
{
    [McpServerTool(Name = "libera_graph_status", ReadOnly = true, OpenWorld = false)]
    [Description("Read the current document path (null for unsaved documents), graph breadcrumbs, editor problems and undo/redo availability. Unavailable to graph-scoped chat requests.")]
    public static Task<string> Status(MainViewModel documents)
    {
        ChatMcpExecution.RequireProjectFileAccess();
        return ChatMcpExecution.InvokeAsync(() =>
        {
            var document = documents.ActiveDocument ?? throw new McpException("No document is open.");
            var graph = document.Workspace;
            var path = graph.CurrentGraphPath;
            return JsonSerializer.Serialize(new
            {
                documentId = document.Id,
                documentPath = Uri.TryCreate(document.FileKey, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null,
                fileName = document.FileName,
                isDirty = document.IsDirty,
                isReadOnly = graph.IsReadOnly,
                breadcrumbs = graph.Breadcrumbs.Select(crumb => new
                {
                    name = crumb.Name, depth = crumb.Depth,
                    nodeId = crumb.Depth > 0 && crumb.Depth <= path.Count ? (Guid?)path[crumb.Depth - 1] : null
                }),
                problems = graph.TypeProblems.Select(problem => new { nodeId = problem.Node.Id, message = problem.Message }),
                canUndo = graph.History.CanUndo, canRedo = graph.History.CanRedo
            });
        });
    }

    [McpServerTool(Name = "libera_graph_open_project", Destructive = false, OpenWorld = false)]
    [Description("Open and activate an absolute local .lfxproj path without a file dialog. Preserves other open documents and their unsaved edits. Reopening an already open file activates it without discarding changes. Validates the project and restores its full history. Unavailable to graph-scoped chat requests.")]
    public static Task<string> Open(MainViewModel documents,
        [Description("Absolute local .lfxproj file path. Relative paths, network shares and URIs are rejected.")] string path) =>
        Execute(() => documents.OpenLocalProjectAsync(path));

    [McpServerTool(Name = "libera_graph_save_project", Destructive = true, OpenWorld = false)]
    [Description("Atomically save the active document and its full history to an absolute local .lfxproj path without a file dialog. Existing files require overwrite=true; another open document's destination is always rejected. Does not create directories. Unavailable to graph-scoped chat requests.")]
    public static Task<string> Save(MainViewModel documents,
        [Description("Absolute local .lfxproj destination path.")] string path,
        [Description("Explicit permission to replace an existing destination file. Defaults to false.")] bool overwrite = false) =>
        Execute(() => documents.SaveLocalProjectAsync(path, overwrite));

    private static async Task<string> Execute(Func<Task<GraphDocumentViewModel>> action)
    {
        ChatMcpExecution.RequireProjectFileAccess();
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var document = await action();
                return JsonSerializer.Serialize(new
                {
                    documentId = document.Id,
                    path = new Uri(document.FileKey!).LocalPath,
                    fileName = document.FileName,
                    isDirty = document.IsDirty,
                    nodeCount = document.Workspace.CreateSnapshot().Nodes.Count,
                    historyNodeId = document.Workspace.History.CurrentNodeId
                });
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or InvalidDataException or JsonException)
        {
            throw new McpException(ex.Message);
        }
    }
}
