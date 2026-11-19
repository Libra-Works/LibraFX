using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

namespace LiberaFx.Tests;

internal static class CpuParticleFlowTests
{
    public static void Run()
    {
        var text = LocalizationService.Shared;
        var language = text.SelectedLanguage;
        var history = new UndoRedoManager();
        var source = new GraphWorkspaceViewModel(new UndoRedoManager());
        var cpu = source.CreateNode(GraphNodeDefinition.Core.Single(node => node.TypeName == "CpuParticle").Kind, 80, 80);
        var saved = source.CreateSnapshot();
        var graph = new GraphWorkspaceViewModel(history);
        graph.InitializeFromSnapshot(JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(saved))!);
        var editor = new GraphEditorView { DataContext = graph };
        var window = new Window { Width = 1000, Height = 800, Content = editor };
        window.Show();
        try
        {
            Pump();
            Require(!graph.IsCpuParticleOverview, "The root graph must remain the ordinary node canvas.");
            Require(graph.EnterGraph(graph.Nodes.Single(node => node.Id == cpu.Id)), "Saved CPU systems remain navigable.");
            Pump();
            var flow = editor.GetVisualDescendants().OfType<CpuParticleFlowView>()
                .Single(view => ReferenceEquals(view.DataContext, graph));
            Require(flow.IsEffectivelyVisible && !editor.FindControl<ScrollViewer>("GraphScrollViewer")!.IsVisible,
                "The CPU overview must display Flow cards instead of structural graph nodes.");
            var originalIds = graph.Nodes.Select(node => node.Id).Order().ToArray();
            Require(Buttons().Length == 3, "The fixed CPU Flow has exactly Spawn, Update and Draw actions.");
            var beforeNavigation = graph.CreateSnapshot();
            Open("CpuParticleSpawn");
            Require(!graph.IsCpuParticleOverview && graph.CurrentGraphPath.Count == 2,
                "The Spawn card must open the ordinary per-particle child graph.");
            Require(GraphSnapshot.ContentEquals(beforeNavigation, graph.CreateSnapshot()),
                "Opening a Flow stage must not rewrite a saved graph or add an edit.");
            graph.CreateNode(GraphNodeDefinition.Core.Single(node => node.TypeName == "CpuParticleSetLife").Kind, 800, 100);
            var edited = graph.CreateSnapshot();
            Require(history.Undo() && GraphSnapshot.ContentEquals(beforeNavigation, graph.CreateSnapshot()),
                "Undo inside a Flow stage must restore the saved document.");
            Require(history.Redo() && GraphSnapshot.ContentEquals(edited, graph.CreateSnapshot()),
                "Redo inside a Flow stage must restore the same edit and IDs.");
            graph.NavigateToGraph(1);
            Pump();
            Require(graph.IsCpuParticleOverview && originalIds.SequenceEqual(graph.Nodes.Select(node => node.Id).Order()),
                "Returning from an edited stage must retain structural stage identities.");
            Open("CpuParticleUpdate");
            Require(!graph.IsCpuParticleOverview && graph.CurrentGraphPath.Count == 2,
                "The Update card must open its own child graph.");
            graph.NavigateToGraph(1);
            Pump();
            var beforeDraw = graph.CreateSnapshot();
            Open("CpuParticleSpriteDraw");
            Require(graph.IsCpuParticleOverview &&
                graph.InspectorNode is { } draw && CpuParticleSchema.Name(draw.Definition.Kind) == "CpuParticleSpriteDraw" &&
                GraphSnapshot.ContentEquals(beforeDraw, graph.CreateSnapshot()),
                "Draw must select the existing Inspector target without creating a graph edit.");

            var beforeLanguage = graph.CreateSnapshot();
            foreach (var id in new[] { "en", "ja" })
            {
                text.SelectedLanguage = text.Languages.Single(item => item.Id == id);
                Pump();
                var heading = ((CpuParticleFlowStage)Buttons()[0].Tag!).Title;
                Require(heading == (id == "en" ? "1. Spawn" : "1. 生成"), "Flow labels must follow the UI language.");
            }
            Require(GraphSnapshot.ContentEquals(beforeLanguage, graph.CreateSnapshot()),
                "Language changes must not affect saved names or graph history.");

            flow.FindControl<Expander>("FlowNotes")!.IsExpanded = true;
            Pump();
            Invoke(flow.FindControl<Button>("AddNoteButton")!);
            var note = graph.CommentNodes.Single();
            note.CommentText = "Preserved CPU Flow note";
            var withNote = graph.CreateSnapshot();
            GraphValidator.ValidateDocument(withNote);
            var restored = JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(withNote))!;
            Require(GraphSnapshot.ContentEquals(withNote, restored), "Flow notes and nested edits must roundtrip.");
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            var project = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
            Require(GraphSnapshot.ContentEquals(withNote, project.Graph), "CPU Flow must preserve the project format.");
            Require(history.Undo() && history.Undo() &&
                GraphSnapshot.ContentEquals(beforeLanguage, graph.CreateSnapshot()),
                "Note text and creation must use the document's ordinary undo history.");

            graph.NavigateToGraph(0);
            var inline = graph.OpenInlineGraph(graph.GetNode(cpu.Id));
            editor = new GraphEditorView { DataContext = inline };
            window.Content = editor;
            Pump();
            Require(inline.IsCpuParticleOverview && Buttons().Length == 3,
                "Inline CPU systems must expose the same Flow projection.");
            Open("CpuParticleSpawn");
            Require(graph.CurrentGraphPath.Count == 2 && !graph.IsCpuParticleOverview,
                "Opening an inline stage must maximize the persisted stage in its owning document.");
            graph.NavigateToGraph(0);
            graph.DeleteNode(graph.GetNode(cpu.Id));
            Pump();
            Require(inline.IsReadOnly && !inline.IsCpuParticleOverview && Buttons().Length == 0,
                "A removed inline system must not retain actionable stale Flow cards.");
            Require(history.Undo(), "Removing an inline CPU system must remain undoable.");
            Pump();
            Require(!inline.IsReadOnly && inline.IsCpuParticleOverview && Buttons().Length == 3,
                "Undo must reconnect the inline Flow to the restored stage identities.");
        }
        finally
        {
            window.Close();
            text.SelectedLanguage = language;
        }

        Button[] Buttons() => editor.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.Tag is CpuParticleFlowStage).ToArray();
        void Open(string kind)
        {
            Invoke(Buttons().Single(button => button.Tag is CpuParticleFlowStage { Node: { } node } &&
                CpuParticleSchema.Name(node.Definition.Kind) == kind));
            Pump();
        }
        void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Invoke(Button button) =>
        new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
