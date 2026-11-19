using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class CpuParticleDocumentationCapture
{
    public static void Capture(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
        {
            throw new ArgumentException("Supply an absolute, new documentation capture directory.");
        }
        Directory.CreateDirectory(directory);
        _ = new ThemeSettingsService();
        var text = LocalizationService.Shared;
        var originalLanguage = text.SelectedLanguage;
        var originalNodeLanguage = text.SelectedNodeLanguage;
        try
        {
            foreach (var language in new[] { "en", "ja" })
            {
                text.SelectedLanguage = text.Languages.Single(item => item.Id == language);
                text.SelectedNodeLanguage = text.Languages.Single(item => item.Id == language);
                CaptureLanguage(language);
            }
        }
        finally
        {
            text.SelectedNodeLanguage = originalNodeLanguage;
            text.SelectedLanguage = originalLanguage;
        }

        void CaptureLanguage(string language)
        {
            var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            var emitter = Create("EmitterRate", 80, 140);
            var system = Create("CpuParticle", 470, 140);
            if (!workspace.TryConnectNodes(emitter, system, out var error,
                    targetPortId: "emitted", sourcePortId: "value"))
            {
                throw new InvalidOperationException(error);
            }
            _ = SpawnableGroupPackage.Compile(workspace.CreateSnapshot());
            var active = new ActiveGraphProvider(workspace);
            using var pane = new NodePropertiesPaneViewModel(active);
            var editor = new GraphEditorView { DataContext = workspace };
            var inspector = new NodePropertiesView { DataContext = pane };
            var content = new Grid { ColumnDefinitions = new("*,400") };
            content.Children.Add(editor);
            Grid.SetColumn(inspector, 1);
            content.Children.Add(inspector);
            var window = new Window
            {
                Title = "LIBERA FX - CPU particles",
                Width = 1440,
                Height = 840,
                Content = content
            };
            window.Show();
            try
            {
                workspace.SelectNode(system);
                Save("root");
                Enter(system);
                Save("stages");
                var spawn = Find("CpuParticleSpawn");
                Enter(spawn);
                workspace.SelectNode(Find("CpuParticleSetVelocity"));
                Save("spawn");
                workspace.NavigateToGraph(1);
                Enter(Find("CpuParticleUpdate"));
                workspace.SelectNode(Find("CpuParticleMove"));
                Save("update");
                workspace.NavigateToGraph(1);
                workspace.SelectNode(Find("CpuParticleSpriteDraw"));
                Save("draw");
                GraphValidator.ValidateDocument(workspace.CreateSnapshot());
            }
            finally
            {
                window.Close();
            }

            GraphNodeViewModel Create(string kind, double x, double y) =>
                workspace.CreateNode(GraphNodeDefinition.Core.Single(item => item.TypeName == kind).Kind, x, y);
            GraphNodeViewModel Find(string kind) =>
                workspace.Nodes.Single(item => CpuParticleSchema.Name(item.Definition.Kind) == kind);
            void Enter(GraphNodeViewModel node)
            {
                if (!workspace.EnterGraph(node))
                {
                    throw new InvalidOperationException("The CPU documentation graph was not navigable.");
                }
            }
            void Save(string name)
            {
                Pump();
                editor.FrameAll();
                Pump();
                if (workspace.IsCpuParticleOverview)
                {
                    if (editor.GetVisualDescendants().OfType<Button>().Count(button =>
                        button.IsEffectivelyVisible && button.Tag is CpuParticleFlowStage) != 3)
                    {
                        throw new InvalidOperationException("The CPU documentation Flow has missing stage actions.");
                    }
                }
                else if (editor.GetVisualDescendants().OfType<GraphNodeView>().Count() < workspace.Nodes.Count)
                {
                    throw new InvalidOperationException("The CPU documentation graph has missing node views.");
                }
                using var bitmap = new RenderTargetBitmap(new PixelSize(
                    (int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, $"cpu-particles-{name}-{language}.png"),
                    PngBitmapEncoderOptions.Default);
            }
            void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
