using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

[McpServerToolType]
public sealed class ProjectFileMcpTools
{
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
