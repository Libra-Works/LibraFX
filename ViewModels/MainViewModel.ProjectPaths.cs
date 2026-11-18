using Avalonia.Threading;
using LiberaFx.Models.Project;

namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private readonly SemaphoreSlim _projectPathGate = new(1, 1);

    public async Task<GraphDocumentViewModel> OpenLocalProjectAsync(string path)
    {
        Dispatcher.UIThread.VerifyAccess();
        path = ValidateLocalProjectPath(path);
        await _projectPathGate.WaitAsync();
        try
        {
            var key = new Uri(path).AbsoluteUri;
            if (FindProjectDocument(key) is { } existing)
            {
                ActivateDocument(existing);
                return existing;
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var project = await _projectFiles.LoadAsync(stream);
            return AddLoadedProject(project, Path.GetFileName(path), key);
        }
        finally { _projectPathGate.Release(); }
    }

    public async Task<GraphDocumentViewModel> SaveLocalProjectAsync(string path, bool overwrite = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        path = ValidateLocalProjectPath(path);
        var document = ActiveDocument ?? throw new InvalidOperationException("There is no active project.");
        await _projectPathGate.WaitAsync();
        string? temporary = null;
        try
        {
            if (!Documents.Contains(document))
                throw new InvalidOperationException("The selected document has closed.");
            var key = new Uri(path).AbsoluteUri;
            void CheckTarget()
            {
                if (FindProjectDocument(key) is { } other && !ReferenceEquals(other, document))
                    throw new IOException("The destination belongs to another open document.");
                if (!overwrite && File.Exists(path))
                    throw new IOException("The destination already exists. Set overwrite=true to replace it.");
            }
            CheckTarget();
            using var buffer = new MemoryStream();
            var saved = await _projectFiles.SaveAsync(buffer, document.Workspace);
            temporary = Path.Combine(Path.GetDirectoryName(path)!, $".liberafx-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, buffer.ToArray());
            CheckTarget();
            File.Move(temporary, path, overwrite);
            temporary = null;
            var oldKey = document.FileKey;
            document.MarkSaved(Path.GetFileName(path), key, saved.Graph, saved.History);
            if (oldKey is not null && oldKey != key &&
                !Documents.Any(other => other != document && other.FileKey == oldKey))
                ReleaseStorageFile(oldKey);
            SetFileStatus("Messages.ProjectSaved", document.FileName);
            return document;
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            finally { _projectPathGate.Release(); }
        }
    }

    internal static string ValidateLocalProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.GetExtension(path).Equals(".lfxproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Supply an absolute local .lfxproj path, not a relative path, URI or network share.");
        var full = Path.GetFullPath(path);
        if (new Uri(full).IsUnc)
            throw new ArgumentException("Network shares are not local project paths.");
        if (OperatingSystem.IsWindows() && full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':'))
            throw new ArgumentException("Alternate data streams are not project files.");
        return full;
    }

    private GraphDocumentViewModel? FindProjectDocument(string key) =>
        Documents.FirstOrDefault(document => string.Equals(document.FileKey, key,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private GraphDocumentViewModel AddLoadedProject(LoadedLiberaFxProject project, string name, string key)
    {
        if (FindProjectDocument(key) is { } existing)
        {
            ActivateDocument(existing);
            return existing;
        }
        var workspace = CreateWorkspace();
        workspace.InitializeFromSnapshot(project.Graph);
        if (project.History is not null) workspace.History.LoadSnapshot(project.History);
        var document = new GraphDocumentViewModel(workspace)
        {
            Id = Guid.NewGuid().ToString("N"),
            CanClose = false
        };
        document.MarkSaved(name, key, project.Graph, project.History);
        Documents.Add(document);
        _dockFactory.DocumentsDock.AddDocument(document);
        ActivateDocument(document);
        SetFileStatus("Messages.ProjectOpened", name);
        return document;
    }
}
