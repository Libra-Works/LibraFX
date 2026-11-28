using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Settings;

namespace LiberaFx.ViewModels.Graph;

public sealed class GraphDocumentViewModel : Document
{
    private GraphSnapshot _savedSnapshot;
    private Guid? _savedHistoryRevisionId;
    private Guid? _savedHistoryNodeId;
    private string _fileName = LocalizationService.Shared.Get("Messages.Untitled");
    private bool _isDirty;

    public GraphWorkspaceViewModel Workspace { get; }

    public string? FileKey { get; private set; }

    public string FileName => _fileName;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                UpdateTitle();
            }
        }
    }

    public GraphDocumentViewModel(GraphWorkspaceViewModel workspace)
    {
        Workspace = workspace;
        Workspace.SetDocumentName(_fileName);
        _savedSnapshot = workspace.CreateSnapshot();
        _savedHistoryRevisionId = workspace.History.TimelineRevisionId;
        _savedHistoryNodeId = workspace.History.CurrentNodeId;
        Workspace.History.StateChanged += OnHistoryStateChanged;
        MessageLocalization.Observe(this, static document => document.RefreshLocalizedTitle());
        UpdateTitle();
    }

    public void MarkSaved(
        string fileName,
        string fileKey,
        GraphSnapshot savedSnapshot,
        UndoRedoHistorySnapshot? savedHistory)
    {
        var oldName = _fileName;
        var oldKey = FileKey;
        _fileName = string.IsNullOrWhiteSpace(fileName) ? LocalizationService.Shared.Get("Messages.Untitled") : fileName;
        FileKey = fileKey;
        Workspace.SetDocumentName(_fileName);
        _savedSnapshot = savedSnapshot;
        _savedHistoryRevisionId = savedHistory?.TimelineRevisionId;
        _savedHistoryNodeId = savedHistory?.CurrentNodeId;
        if (oldName != _fileName) OnPropertyChanged(nameof(FileName));
        if (oldKey != FileKey) OnPropertyChanged(nameof(FileKey));
        UpdateDirtyState();
        UpdateTitle();
    }

    private void RefreshLocalizedTitle()
    {
        if (FileKey is not null)
        {
            return;
        }
        _fileName = LocalizationService.Shared.Get("Messages.Untitled");
        Workspace.SetDocumentName(_fileName);
        OnPropertyChanged(nameof(FileName));
        UpdateTitle();
    }

    private void OnHistoryStateChanged(object? sender, EventArgs e)
    {
        UpdateDirtyState();
    }

    private void UpdateDirtyState()
    {
        var current = Workspace.CreateSnapshot();
        IsDirty =
            !GraphSnapshotsEqual(_savedSnapshot, current) ||
            _savedHistoryRevisionId != Workspace.History.TimelineRevisionId ||
            _savedHistoryNodeId != Workspace.History.CurrentNodeId;
    }

    private static bool GraphSnapshotsEqual(GraphSnapshot left, GraphSnapshot right) =>
        GraphSnapshot.ContentEquals(left, right);



    private void UpdateTitle()
    {
        Title = IsDirty ? $"{_fileName} *" : _fileName;
    }
}
