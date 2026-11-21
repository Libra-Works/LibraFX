using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;

internal static class UndoHistoryArchiveTests
{
    public static void Run()
    {
        var id = Guid.NewGuid();
        GraphSnapshot State(int value) => new([new(id, GraphNodeKind.Float, "Value", 0, 0,
            "Float", GraphPortCategory.Parameter, Parameters:
            [new("value", "float", JsonSerializer.SerializeToElement((double)value))])], []);
        var current = State(0);
        var publish = new ConcurrentQueue<Action>();
        var history = new UndoRedoManager();
        history.Configure(() => current, state => current = state, _ => { }, publish.Enqueue);
        for (var i = 1; i <= 450; i++)
        {
            var state = State(i);
            history.Execute(new Change(() => current = state));
        }
        var timer = Stopwatch.StartNew();
        while (history.CreateSnapshot().Archives.Count == 0 && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            while (publish.TryDequeue(out var action)) action();
            Thread.Sleep(5);
        }
        var snapshot = history.CreateSnapshot();
        Require(snapshot.Entries.Count == 451 && snapshot.Archives.Count > 0, "Cold history was not compressed or entries were lost.");
        var block = snapshot.Archives[0];
        Require(block.Count == 200 && block.Data.Length < block.UncompressedBytes,
            "Ordinary history must pack 200 complete states into a smaller block.");
        var revision = history.TimelineRevisionId;
        var display = history.GetDisplayItems();
        Require(display.Count < snapshot.Entries.Count && display.Any(item => item.IsArchive && item.ArchiveCount == 200),
            "The History window must replace a cold block with one expandable item.");
        history.ToggleArchive(block.Id);
        Require(history.GetDisplayItems().Count > display.Count && history.TimelineRevisionId == revision,
            "Expanding history must reveal entries without dirtying the document.");
        history.ToggleArchive(block.Id);
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var saved = JsonSerializer.SerializeToUtf8Bytes(snapshot, json);
        var restored = JsonSerializer.Deserialize<UndoRedoHistorySnapshot>(saved, json)!;
        var loaded = new UndoRedoManager();
        loaded.Configure(() => current, state => current = state, _ => { });
        loaded.LoadSnapshot(restored);
        var target = restored.Entries[100];
        Require(loaded.NavigateTo(target.Id) && GraphSnapshot.ContentEquals(current, State(100)),
            "Navigating an archived entry must restore its exact graph after save/reload.");
        Require(loaded.Undo() && GraphSnapshot.ContentEquals(current, State(99)) &&
            loaded.Redo() && GraphSnapshot.ContentEquals(current, State(100)), "Undo/Redo failed across an archive.");
        loaded.Execute(new Change(() => current = State(999)));
        Require(loaded.GetItems().Count == 452, "Branching from archived history discarded existing futures.");
        var maximum = UndoHistoryPreferences.MaximumSavedEntries;
        try
        {
            UndoHistoryPreferences.MaximumSavedEntries = 10;
            var limited = loaded.CreateSnapshot(forSaving: true);
            Require(limited.Entries.Count == 10 && loaded.CreateSnapshot().Entries.Count == 452,
                "The saved-history limit must leave the open document's full Undo history intact.");
            var limitedManager = new UndoRedoManager();
            limitedManager.Configure(() => current, state => current = state, _ => { });
            limitedManager.LoadSnapshot(limited);
            Require(GraphSnapshot.ContentEquals(current, State(999)), "A finite save limit changed the current graph.");
        }
        finally { UndoHistoryPreferences.MaximumSavedEntries = maximum; }
        var corrupt = block with { UncompressedBytes = 1, Data = block.Data.ToArray() };
        try { corrupt.Read(0); throw new InvalidOperationException("An invalid compressed block was accepted."); }
        catch (InvalidDataException) { }
    }
    private sealed class Change(Action apply) : IUndoableCommand
    {
        public string Description => "Edit value";
        public void Execute() => apply();
        public void Undo() => throw new InvalidOperationException("History must restore stored states.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
