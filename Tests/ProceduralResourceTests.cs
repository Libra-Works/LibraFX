using System.Buffers.Binary;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Project;
using LiberaFx.Services.Samples;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Procedural;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

internal static class ProceduralResourceTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public static void Run()
    {
        _ = new ThemeSettingsService();
        UntouchedProducers();
        foreach (var name in new[] { "Slash", "Shockwave", "Beam" })
        {
            var sample = ProceduralSamples.Create(name);
            GraphValidator.ValidateDocument(sample);
            var package = SpawnableGroupPackage.Compile(sample);
            using var render = JsonDocument.Parse(package.RootRenderConfiguration);
            var root = render.RootElement;
            Require(root.GetProperty("proceduralVersion").GetInt32() == 1 &&
                root.GetProperty("nodes").GetArrayLength() == 1 &&
                root.GetProperty("nodes")[0].GetProperty("kind").GetString() == "proceduralModel", "Model Draw must consume the generated model.");
            var resources = root.GetProperty("proceduralResources");
            Require(resources.GetArrayLength() == 3, "Three scoped producer definitions are required.");
            foreach (var resource in resources.EnumerateArray())
            {
                var bytes = Convert.FromBase64String(resource.GetProperty("definition").GetString()!);
                Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x31584650, "Native definitions need the PFX1 header.");
            }
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager()); graph.InitializeFromSnapshot(sample);
            var model = graph.Nodes.Single(n => ProceduralResourceSchema.Name(n.Definition.Kind) == "ProceduralModel");
            var recipe = ProceduralResourceSchema.Read(model.BaseDefinition, model.Parameters);
            graph.ApplyProceduralRecipe(model, recipe with { PivotMode = 4, Pivot = [2, 3, 4] });
            Require(ProceduralResourceSchema.Read(model.BaseDefinition, model.Parameters).Pivot[0] == 2, "Pivot edits must persist.");
            Require(graph.History.Undo() && ProceduralResourceSchema.Read(model.BaseDefinition, graph.Nodes.Single(n => n.Id == model.Id).Parameters).PivotMode == recipe.PivotMode,
                "Pivot edits must undo atomically.");
            Require(graph.History.Redo(), "Pivot edits must redo.");
            model = graph.Nodes.Single(n => n.Id == model.Id);
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult(); stream.Position = 0;
            var loaded = new ProjectFileService().LoadAsync(stream).GetAwaiter().GetResult();
            Require(GraphSnapshot.ContentEquals(loaded.Graph, graph.CreateSnapshot()), "Procedural graphs must round-trip through project files.");
            var workspace = new ProceduralWorkspaceView();
            var window = new Window { Content = workspace, Width = 1100, Height = 760 }; window.Show(); workspace.Open(graph, model.Id); Dispatcher.UIThread.RunJobs();
            Require(workspace.Title.Contains(LocalizationService.Shared.Get("Procedural.ProceduralModel")), "Flow titles must use the active catalog.");
            Require(model.InlineParameters.Count == 0, "Internal procedural parameters must not appear inline.");
            Require(model.InputPorts.All(p => !p.Name.Contains("stage_")), "Ports must show friendly names instead of storage identities.");
            var flow = workspace.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "FlowPanel");
            Require(flow.Children.OfType<Button>().Count() == recipe.Modifiers.Length + 2, "Flow must show source, modifier cards and result.");
            var palette = workspace.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "Palette");
            var addTaper = palette.Children.OfType<Button>().Single(b => Equals(b.Content, LocalizationService.Shared.Get("Procedural.Modifier.Taper")));
            addTaper.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Require(flow.Children.OfType<Button>().Count() == recipe.Modifiers.Length + 3, "Palette actions must add real ordered stages.");
            Require(graph.History.Undo(), "Palette additions must support Undo."); Dispatcher.UIThread.RunJobs();
            Require(flow.Children.OfType<Button>().Count() == recipe.Modifiers.Length + 2, "Undo must refresh the Flow after restoring the producer.");
            var modifierCard = flow.Children.OfType<Button>().Skip(1).FirstOrDefault();
            if (modifierCard is not null) modifierCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var capture = Environment.GetEnvironmentVariable("LIBERAFX_PROCEDURAL_UI_PROOF");
            if (!string.IsNullOrEmpty(capture))
            {
                Directory.CreateDirectory(capture);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1100, 760));
                bitmap.Render(workspace); bitmap.Save(Path.Combine(capture, name + "-flow.png"), PngBitmapEncoderOptions.Default);
            }
            var text = LocalizationService.Shared; var originalLanguage = text.SelectedLanguage;
            try
            {
                text.SelectedLanguage = text.Languages.Single(l => l.Id == "ja"); Dispatcher.UIThread.RunJobs();
                Require(workspace.Title.Contains(text.Get("Procedural.ProceduralModel")) &&
                    workspace.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, text.Get("Procedural.Back"))),
                    "An open workspace must refresh its title and controls when the language changes.");
                if (!string.IsNullOrEmpty(capture))
                {
                    window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1100, 760));
                    bitmap.Render(workspace); bitmap.Save(Path.Combine(capture, name + "-flow-ja.png"), PngBitmapEncoderOptions.Default);
                }
            }
            finally { text.SelectedLanguage = originalLanguage; }
            workspace.CloseWorkspace(); window.Close();
            graph.PasteSnapshot(sample, 800, 0);
            Require(graph.Nodes.Count == 8, "Copy/paste must retain and remap procedural producers and draws.");
            Require(SpawnableGroupPackage.Compile(graph.CreateSnapshot()).RootRenderConfiguration.Length > 0,
                "Pasted resource edges must compile in their new identities.");
        }
        var large = ProceduralResourceSchema.Encode(new ProceduralRecipe { Operations = Enumerable.Range(0, 32)
            .Select(_ => ProceduralResourceSchema.CreatePixel(1, 0)).ToArray(), OutputRegister = 32 });
        Require(large.Length > 4096 && NodeParameter.IsValidValue("proceduralState", JsonSerializer.SerializeToElement(large)),
            "Internal procedural state must support a full stack beyond the ordinary string limit.");
        Console.WriteLine("Procedural authoring, lowering, persistence and history passed.");
    }
    private static void UntouchedProducers()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        GraphNodeViewModel Add(string kind) => graph.CreateNode(GraphNodeDefinition.All.Single(d => d.TypeName == kind).Kind, 0, 0);
        var model = Add("ProceduralModel"); var texture = Add("ProceduralTexture");
        var material = Add("ProceduralMaterial"); var draw = Add("ModelDraw");
        foreach (var producer in new[] { texture, material })
        {
            var first = ProceduralResourceSchema.Effective(producer.BaseDefinition, producer.Parameters);
            var second = ProceduralResourceSchema.Effective(producer.BaseDefinition, producer.Parameters);
            Require(first.InputPorts!.Select(p => p.Id).SequenceEqual(second.InputPorts!.Select(p => p.Id)),
                "Untouched producers must retain stable input identities before their first edit.");
            Require(producer.BaseDefinition.Parameters.Single(p => p.Id == "proceduralState").Hidden,
                "Hidden must be loaded from node definitions, rather than special-cased in one inspector.");
        }
        graph.ApplyProceduralRecipe(material, new ProceduralRecipe { Operations = [new() { Kind = 30 }], OutputRegister = 1 });
        void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string port)
        {
            Require(graph.TryConnectNodes(source, target, out var error, targetPortId: port, sourcePortId: "value"), error ?? "Typed procedural connection failed.");
        }
        Connect(model, draw, "proceduralModel"); Connect(texture, material, "texture0"); Connect(material, draw, "proceduralMaterial");
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        using var render = JsonDocument.Parse(package.RootRenderConfiguration);
        var resource = render.RootElement.GetProperty("proceduralResources").EnumerateArray()
            .Single(r => r.GetProperty("kind").GetString() == "ProceduralTexture");
        Require(resource.GetProperty("inputs").GetArrayLength() == 6,
            "A default texture must compile all six stable numeric input blocks without requiring an edit first.");
        var waveId = Guid.NewGuid();
        graph.ApplyProceduralRecipe(model, new ProceduralRecipe
        {
            Shape = 14,
            Modifiers = [new() { Id = waveId, Kind = 3, Values = [0.25f, 2f, 0.5f, 1.5f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], Flags = 1 }]
        });
        var amp = Add("Float"); var speed = Add("Float");
        Require(graph.TryConnectNodes(amp, model, out var ampErr, targetPortId: ProceduralResourceSchema.ScalarPort(waveId, 0), sourcePortId: "value"), ampErr ?? "Scalar Amplitude connection failed.");
        Require(graph.TryConnectNodes(speed, model, out var spdErr, targetPortId: ProceduralResourceSchema.ScalarPort(waveId, 3), sourcePortId: "value"), spdErr ?? "Scalar Speed connection failed.");
        var compiled = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        using var compiledRender = JsonDocument.Parse(compiled.RootRenderConfiguration);
        var modelRes = compiledRender.RootElement.GetProperty("proceduralResources").EnumerateArray()
            .Single(r => r.GetProperty("kind").GetString() == "ProceduralModel");
        Require(modelRes.GetProperty("inputs").GetArrayLength() == 4, "Scalar procedural inputs must lower into 4-component blocks.");
        Require(modelRes.GetProperty("inputs")[0].GetProperty("sourceNodeId").GetGuid() != Guid.Empty,
            "Connected scalar procedural inputs must lower to a valid vector4 compose source.");
    }
    public static void WriteFixtures(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
            throw new ArgumentException("Use a new isolated absolute fixture directory.");
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "Slash", "Shockwave", "Beam", "ModelOnly" })
        {
            var snapshot = ProceduralSamples.Create(name == "ModelOnly" ? "Slash" : name);
            if (name == "ModelOnly")
            {
                var model = snapshot.Nodes.Single(n => ProceduralResourceSchema.Name(n.Kind) == "ProceduralModel");
                var draw = snapshot.Nodes.Single(n => n.Kind.Value == "ModelDraw");
                model = model with { Parameters = [new("proceduralState", "string", JsonSerializer.SerializeToElement(
                    ProceduralResourceSchema.Encode(new ProceduralRecipe { Shape = 15, SegmentsU = 2, SegmentsV = 2 })))] };
                snapshot = new([model, draw], [new(model.Id, draw.Id, "proceduralModel", "value")]) { AutoDestroyWhenIdle = false };
            }
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager()); graph.InitializeFromSnapshot(snapshot);
            using (var project = new MemoryStream())
            {
                new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
                File.WriteAllBytes(Path.Combine(directory, "Procedural" + name + ".lfxproj"), project.ToArray());
            }
            var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
            File.WriteAllBytes(Path.Combine(directory, name + ".lfxp"), SpawnableGroupPackageBinary.Serialize(package));
            File.WriteAllBytes(Path.Combine(directory, name + ".render.json"), package.RootRenderConfiguration);
        }
    }
}
