using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class ParticleAtlasTests
{
    public static void Run()
    {
        var atlas = new LiberaFx.Unity.ParticleAtlas { enabled = true, columns = 2, rows = 2, fps = 4 };
        Require(atlas.IsValid && atlas.Frame(0, 2) == 0 && atlas.Frame(0.25, 2) == 1 &&
            atlas.Frame(0.75, 2) == 3 && atlas.Frame(1, 2) == 0, "Managed atlas frame boundaries/loop.");
        atlas.startFrame = 1;
        Require(atlas.Frame(0, 2) == 1 && atlas.Frame(0.75, 2) == 0, "Managed atlas start offset.");
        atlas.loop = false;
        Require(atlas.Frame(-1, 2) == 1 && atlas.Frame(0.5, 2) == 3 && atlas.Frame(1e100, 2) == 3, "Managed atlas clamping.");
        atlas.overLife = true;
        Require(atlas.Frame(0, 3) == 1 && atlas.Frame(1, 3) == 2 && atlas.Frame(2, 3) == 3 &&
            atlas.Frame(3, 3) == 3 && atlas.Frame(0, 0) == 1, "Managed atlas normalized lifetime.");
        atlas.columns = 0;
        Require(!atlas.IsValid && atlas.Frame(1, 2) == 0, "Invalid managed atlas must not divide by zero.");
        VerifyChoices();
    }

    private static void VerifyChoices()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var cpu = graph.CreateNode(new GraphNodeKind("CpuParticle"));
        Require(graph.EnterGraph(cpu), "Could not enter CPU particle for inspector verification.");
        var node = graph.Nodes.Single(n => CpuParticleSchema.Name(n.Definition.Kind) == "CpuParticleSpriteDraw");
        var host = new ParameterEditorHost();
        void BindCurrent() => host.DataContext = new NodeParameterEditorViewModel(graph, graph.GetNode(node.Id),
            node.Definition.Parameters.Single(p => p.Id == "blendMode"));
        BindCurrent();
        var window = new Window { Content = host };
        var localization = LocalizationService.Shared;
        var previousLanguage = localization.SelectedLanguage;
        window.Show();
        try
        {
            foreach (var language in new[] { "ja", "en" })
            {
                localization.SelectedLanguage = localization.Languages.Single(item => item.Id == language);
                BindCurrent();
                Dispatcher.UIThread.RunJobs();
                var combo = host.GetVisualDescendants().OfType<ComboBox>().Single();
                var choices = combo.ItemsSource!.Cast<NodeParameterChoice>().ToArray();
                Require(choices.Select(p => p.Value).SequenceEqual(["Alpha", "Additive"]) &&
                    choices[1].Name == localization.Get("Cpu.Blend.Additive"), "Localized choices must preserve stored strings.");
                combo.SelectedItem = choices[1];
                Dispatcher.UIThread.RunJobs();
                Require(((NodeParameterEditorViewModel)host.DataContext!).StringChoice == "Additive", "Visible ComboBox must edit the graph.");
                Require(graph.History.Undo(), "String choice edits must be undoable.");
                BindCurrent();
                Require(((NodeParameterEditorViewModel)host.DataContext!).StringChoice == "Alpha", "Undo must restore the stored string.");
                Require(graph.History.Redo(), "String choice edits must be redoable.");
                BindCurrent();
                Require(((NodeParameterEditorViewModel)host.DataContext!).StringChoice == "Additive", "Redo must restore the stored string.");
                graph.History.Undo();
                BindCurrent();
            }
        }
        finally { localization.SelectedLanguage = previousLanguage; window.Close(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
