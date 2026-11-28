using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Appearance;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.ViewModels.Main;

public sealed partial class MainViewModel
{
    private void CreateDocument()
    {
        var document = new GraphDocumentViewModel(CreateWorkspace(initializeNew: true))
        {
            Id = Guid.NewGuid().ToString("N"),
            CanClose = false
        };
        Documents.Add(document);
        _dockFactory.DocumentsDock.AddDocument(document);
        ActivateDocument(document);
        SetFileStatus("Messages.DocumentCreated");
    }

    private async Task OpenProjectsAsync()
    {
        if (_storageProvider is null)
        {
            SetFileStatus("Messages.FilePickerUnavailable");
            return;
        }

        try
        {
            var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Shared.Get("Messages.OpenProjectTitle"),
                AllowMultiple = true,
                FileTypeFilter = [ProjectFileType]
            });

            foreach (var file in files)
            {
                await OpenProjectAsync(file);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            SetFileStatus("Messages.OpenProjectFailed", ex.Message);
        }
    }

    public async Task OpenProjectPathAsync(string path)
    {
        if (_storageProvider is null) throw new InvalidOperationException("File picker is unavailable.");
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".lfxproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a local .lfxproj file.");
        var file = await _storageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path)))
            ?? throw new FileNotFoundException("Project file was not found.", path);
        await OpenProjectAsync(file);
    }

    private async Task OpenProjectAsync(IStorageFile file)
    {
        var retained = false;
        try
        {
            var fileKey = GetFileKey(file);
            var existing = Documents.FirstOrDefault(document => document.FileKey == fileKey);
            if (existing is not null)
            {
                ActivateDocument(existing);
                SetFileStatus("Messages.AlreadyOpen", file.Name);
                return;
            }

            await using var stream = await file.OpenReadAsync();
            var project = await _projectFiles.LoadAsync(stream);
            AddLoadedProject(project, file.Name, fileKey);
            StoreStorageFile(fileKey, file);
            retained = true;
            SetFileStatus("Messages.ProjectOpened", file.Name);
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            SetFileStatus("Messages.OpenFileFailed", file.Name, ex.Message);
        }
        finally
        {
            if (!retained)
            {
                file.Dispose();
            }
        }
    }

    public async Task ExportVfxAsync()
    {
        var document = ActiveDocument;
        if (document is null)
        {
            SetFileStatus("Messages.NoDocumentToExport");
            return;
        }
        if (_storageProvider is null)
        {
            SetFileStatus("Messages.FilePickerUnavailable");
            return;
        }

        IStorageFile? file = null;
        try
        {
            var snapshot = document.Workspace.CreateSnapshot();
            byte[] bytes;
            if (PreviewResourceBindings.HasMappings(snapshot))
            {
                var identityPeers = PreviewTelemetry.Clients()
                    .Where(peer => PreviewAssets.Supports(peer.ClientId, "resourceIdentity"))
                    .ToArray();
                if (identityPeers.Length == 1)
                {
                    var context = await PreviewResourceBindings.ResolveAsync(
                        snapshot, PreviewAssets, identityPeers[0].ClientId, CancellationToken.None);
                    bytes = EffectBinary.Compile(snapshot, context);
                }
                else if (identityPeers.Length > 1)
                {
                    throw new InvalidOperationException("Multiple Resource Identity editors are connected; keep one connected before exporting.");
                }
                else
                {
                    bytes = EffectBinary.Compile(snapshot);
                }
            }
            else
            {
                bytes = EffectBinary.Compile(snapshot);
            }
            file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Shared.Get("Messages.ExportBinaryTitle"),
                DefaultExtension = EffectBinary.FileExtension.TrimStart('.'),
                SuggestedFileName = Path.GetFileNameWithoutExtension(document.FileName) + EffectBinary.FileExtension,
                FileTypeChoices = [VfxBinaryFileType]
            });
            if (file is null) return;
            if (!file.Name.EndsWith(EffectBinary.FileExtension, StringComparison.OrdinalIgnoreCase) ||
                Documents.Any(open => open.FileKey is not null &&
                    string.Equals(open.FileKey, GetFileKey(file), StringComparison.OrdinalIgnoreCase)))
                throw new IOException(LocalizationService.Shared.Get("Messages.Error.ChooseBinaryFile"));

            await WriteStorageFileAtomicallyAsync(file, bytes);
            SetFileStatus("Messages.BinaryExported", file.Name);
        }
        catch (Exception ex) when (IsFileOperationException(ex) ||
                                   ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SetFileStatus("Messages.ExportFailed", ex.Message);
        }
        catch (OperationCanceledException) { }
        finally { file?.Dispose(); }
    }

    public async Task<bool> SaveActiveProjectAsync(bool saveAs)
    {
        var document = ActiveDocument;
        if (document is null)
        {
            SetFileStatus("Messages.NoDocumentToSave");
            return false;
        }
        if (!saveAs && document.FileKey is { } key && !_storageFiles.ContainsKey(key) &&
            Uri.TryCreate(key, UriKind.Absolute, out var local) && local.IsFile && !local.IsUnc)
        {
            try
            {
                await SaveLocalProjectAsync(local.LocalPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (IsFileOperationException(ex) ||
                                       ex is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                SetFileStatus("Messages.SaveProjectFailed", ex.Message);
                return false;
            }
        }
        if (_storageProvider is null)
        {
            SetFileStatus("Messages.FilePickerUnavailable");
            return false;
        }

        IStorageFile? file = null;
        var isCachedFile = false;
        var retainedFile = false;
        try
        {
            if (!saveAs && document.FileKey is not null)
            {
                _storageFiles.TryGetValue(document.FileKey, out file);
                isCachedFile = file is not null;
            }

            file ??= await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Shared.Get("Messages.SaveProjectTitle"),
                DefaultExtension = ProjectFileService.FileExtension.TrimStart('.'),
                SuggestedFileName = document.FileName,
                FileTypeChoices = [ProjectFileType]
            });

            if (file is null)
            {
                return false;
            }

            await using var buffer = new MemoryStream();
            var savedProject = await _projectFiles.SaveAsync(buffer, document.Workspace);
            await WriteStorageFileAtomicallyAsync(file, buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));

            var fileKey = GetFileKey(file);
            var oldFileKey = document.FileKey;
            StoreStorageFile(fileKey, file);
            retainedFile = true;
            document.MarkSaved(
                file.Name,
                fileKey,
                savedProject.Graph,
                savedProject.History);
            if (oldFileKey is not null &&
                oldFileKey != fileKey &&
                !Documents.Any(openDocument => openDocument != document && openDocument.FileKey == oldFileKey))
            {
                ReleaseStorageFile(oldFileKey);
            }

            SetFileStatus("Messages.ProjectSaved", file.Name);
            return true;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            SetFileStatus("Messages.SaveProjectFailed", ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (file is not null && !isCachedFile && !retainedFile)
            {
                file.Dispose();
            }
        }
    }

    public async Task<bool> SaveAllDocumentsAsync()
    {
        var previousDocument = ActiveDocument;
        foreach (var document in Documents.Where(document => document.IsDirty).ToArray())
        {
            ActivateDocument(document);
            if (!await SaveActiveProjectAsync(saveAs: false))
            {
                if (previousDocument is not null && Documents.Contains(previousDocument))
                {
                    ActivateDocument(previousDocument);
                }

                return false;
            }
        }

        if (previousDocument is not null && Documents.Contains(previousDocument))
        {
            ActivateDocument(previousDocument);
        }

        var allSaved = Documents.All(document => !document.IsDirty);
        if (!allSaved)
        {
            SetFileStatus("Messages.DocumentsChangedWhileSaving");
        }

        return allSaved;
    }

    public void CloseActiveDocument(bool discardChanges)
    {
        var document = ActiveDocument
            ?? throw new InvalidOperationException("There is no active document.");
        if (document.IsDirty && !discardChanges)
        {
            throw new InvalidOperationException("Save or discard the document before closing it.");
        }

        var index = Documents.IndexOf(document);
        _dockFactory.RemoveDockable(document, collapse: false);
        Documents.Remove(document);
        if (document.FileKey is not null &&
            !Documents.Any(openDocument => openDocument.FileKey == document.FileKey))
        {
            ReleaseStorageFile(document.FileKey);
        }

        if (Documents.Count == 0)
        {
            CreateDocument();
            return;
        }

        ActivateDocument(Documents[Math.Clamp(index, 0, Documents.Count - 1)]);
    }

}
