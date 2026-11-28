using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Controls;
using LiberaFx.Views.Panels;

internal static class LifecycleAuthoringInterfaceTests
{
    public static void Run()
    {
        var localization = LocalizationService.Shared;
        var originalLanguage = localization.SelectedLanguage;
        using var catalog = new GroupAssetCatalog(new TeamProfilesService(
            Path.Combine(AppDataDirectory.Root, "lifecycle-ui-profiles.json")));
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph));
        var view = new NodePropertiesView { DataContext = pane };
        var commit = new Button { Content = "Commit edit" };
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        panel.Children.Add(view);
        Grid.SetRow(commit, 1);
        panel.Children.Add(commit);
        var window = new Window { Width = 850, Height = 950, Content = panel };
        window.Show();
        try
        {
            foreach (var language in new[] { "en", "ja" })
            {
                localization.SelectedLanguage = localization.Languages.Single(item => item.Id == language);
                foreach (var kind in new[] { "SphericalBillboard", "TreeBillboard" })
                {
                    var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
                    ResetGraph(new GraphSnapshot(
                        [new(Guid.NewGuid(), definition.Kind, kind, 0, 0,
                            definition.PortType, definition.PortCategory,
                            ChildGraph: BillboardGraphLibrary.Create(kind))], []));
                    graph.SelectNode(graph.Nodes.Single());
                    VerifyGroupEdit(8);
                }

                ResetGraph(new GraphSnapshot([], []));
                graph.CreateGroupAssetNode(Guid.Parse("37000000-0000-4000-8000-000000000001"), 0, 0);
                VerifyGroupEdit(5);

                var quality = graph.CreateNode(new("QualityBranch"));
                Pump(window);
                var before = graph.CreateSnapshot();
                var choice = view.GetVisualDescendants().OfType<ComboBox>()
                    .First(control => control.IsEffectivelyVisible && control.Items.Count > 1);
                choice.SelectedIndex = (choice.SelectedIndex + 1) % choice.Items.Count;
                Pump(window);
                var after = graph.CreateSnapshot();
                Require(!GraphSnapshot.ContentEquals(before, after) && graph.History.Undo() &&
                    GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) && graph.History.Redo() &&
                    GraphSnapshot.ContentEquals(after, graph.CreateSnapshot()),
                    "The visible quality comparison selector must edit through Undo/Redo.");
                Require(graph.GetNode(quality.Id).OutputPorts.Select(port => port.Id)
                    .SequenceEqual(["condition", "whenTrue", "whenFalse"]),
                    "Language changes must preserve the quality branch output identities.");

                graph.CreateNode(new("ForceEffect"));
                Pump(window);
                Require(!string.IsNullOrWhiteSpace(pane.InspectorNodeType) &&
                    view.GetVisualDescendants().OfType<TextBlock>().Any(control =>
                        control.IsEffectivelyVisible && control.Text == pane.InspectorNodeType),
                    "The selected Force node must be discoverable in the real inspector in both languages.");
            }
        }
        finally
        {
            window.Close();
            localization.SelectedLanguage = originalLanguage;
        }
        Console.WriteLine("EN/JA Billboard/phase Group inspector input, quality selection and Force presentation passed.");

        void ResetGraph(GraphSnapshot snapshot)
        {
            graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            graph.InitializeFromSnapshot(snapshot);
            pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph));
            view.DataContext = pane;
        }

        void VerifyGroupEdit(int count)
        {
            Pump(window);
            Require(pane.Parameters.Count == count, "The selected Group must expose its authored parameters.");
            var parameter = pane.Parameters.First(item => item.Type == "float");
            var host = view.GetVisualDescendants().OfType<ParameterEditorHost>()
                .Single(control => ReferenceEquals(control.DataContext, parameter));
            var input = host.GetVisualDescendants().OfType<TextBox>().First();
            var before = graph.CreateSnapshot();
            input.BringIntoView();
            Pump(window);
            Require(input.Focus(), "The visible parameter textbox must receive keyboard focus.");
            new TextBoxAutomationPeer(input).GetProvider<IValueProvider>()!.SetValue("0.125");
            Require(commit.Focus(), "The commit control must receive focus to apply the parameter edit.");
            Pump(window);
            var after = graph.CreateSnapshot();
            Require(!GraphSnapshot.ContentEquals(before, after) &&
                pane.Parameters.Any(item => item.Type == "float" && item.JsonValue == "0.125"),
                $"Textbox automation must commit an exposed value (text={input.Text}, value={parameter.JsonValue}).");
            Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) &&
                graph.History.Redo() && GraphSnapshot.ContentEquals(after, graph.CreateSnapshot()),
                "A visible Group parameter edit must be one reversible history step.");
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            Require(GraphSnapshot.ContentEquals(after,
                ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph),
                "The value edited in the visible Group inspector must survive project storage.");
        }
    }

    private static void Pump(Window window)
    {
        var frame = new DispatcherFrame();
        using var timer = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(100));
        Dispatcher.UIThread.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

}
