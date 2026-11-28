using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using LibraGpu.Core;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Project;
using LiberaFx.Services.Samples;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Assets;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.Views.Assets;
using LiberaFx.Views.Graph;

namespace LiberaFx.Tests;

public static class AssetReferenceTests
{
    private static EngineAsset Model(string suffix = "Shared") => new($"/Game/Meshes/{suffix}.{suffix}", suffix,
        "model", "Unreal", Materials: [new(0, "/Game/Materials/M.M", "M",
            [new("Albedo", "texture", JsonSerializer.SerializeToElement("/Game/Textures/T.T"))])]);

    public static void Run()
    {
        VerifyIdentityAndOverrides();
        VerifyUiAndIndex();
        Console.WriteLine("Asset usage, scoped legacy candidates, Group/Spawnable traversal, source navigation, live history and folder index passed.");
    }

    private static void VerifyIdentityAndOverrides()
    {
        var fixture = new GraphWorkspaceViewModel(new UndoRedoManager());
        var render = RenderNodeAuthoring.CreateModel(fixture, Model());
        var descriptor = RenderNodeSchema.Read(render.Definition, render.Parameters).Single();
        var item = new GroupExposedItem(Guid.NewGuid(), "Texture", "", "", 0, "parameter",
            [new(render.Id, descriptor.Id)]);
        var child = fixture.CreateSnapshot() with { ExposedItems = [item] };
        var parent = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = parent.CreateNode(GraphNodeKind.GraphGroup);
        var original = parent.CreateSnapshot();
        original = original with { Nodes = [original.Nodes.Single() with { ChildGraph = child,
            GroupOverrides = [new(item.Id, [new(JsonSerializer.SerializeToElement("/Game/Textures/Override.Override"))])] }] };
        var first = new ReferenceSource(new("document:first", ReferenceResourceKind.Project, "First", "project"), original);
        var second = new ReferenceSource(new("document:second", ReferenceResourceKind.Project, "Second", "project"), original);
        var savedJson = JsonSerializer.Serialize(original);
        var index = new AssetReferenceIndex([first, second]);
        var models = index.Resources.Where(resource => resource.AssetClass == "model").ToArray();
        Require(models.Length == 2 && models[0].Key != models[1].Key &&
                index.Incoming(models[0]).Count == 1 && index.Candidates(models[0]).Single().Inferred,
            "Same legacy binding in different sources must be candidates, not a confirmed shared identity.");
        Require(index.Uses.Count(use => use.Target.Binding == "/Game/Textures/Override.Override") == 2 &&
                index.Uses.All(use => use.Target.Binding != "/Game/Textures/T.T") &&
                index.Uses.All(use => use.ParentPath.SequenceEqual(new[] { group.Id })) &&
                JsonSerializer.Serialize(first.Graph) == savedJson,
            "Index effective Group values without changing saved defaults, identities or parent paths.");
        var unknown = AssetReferenceIndex.FromBinding("scope", "addressable:future/asset", "texture");
        Require(unknown.Kind == ReferenceResourceKind.Unknown, "Unknown bindings cannot be declared missing or resolved.");
        var gpuGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var gpu = GpuParticleEditorService.Create(gpuGraph);
        var resource = GpuParticleEditorService.ReadState(gpu).Program.Resources.Single();
        GpuParticleEditorService.AssignTextureResource(gpuGraph, gpu,
            new("/Game/Textures/Flow.Flow", "Flow", "texture", "Unreal"), resource.Id);
        var flowAsset = new EngineAsset("/Game/Textures/Flow.Flow", "Flow", "texture", "Unreal");
        var flowMap = new ResourceDeclaration
        {
            Id = "flowMap",
            Kind = ResourceKind.FlowMap2D,
            FlowMap = new()
        };
        GpuParticleEditorService.Apply(gpuGraph, gpu.Id, [new() { Action = "resource", Resource = flowMap }]);
        var flowTarget = ResourceAssignment.Targets(gpu, flowAsset).Single(target =>
            target.Kind == ResourceTargetKind.GpuTexture && target.Id == flowMap.Id);
        ResourceAssignment.Assign(gpuGraph, gpu, flowAsset, flowTarget);
        var gpuIndex = new AssetReferenceIndex([new(first.Resource, gpuGraph.CreateSnapshot())]);
        Require(gpuIndex.Uses.Count(use => use.Target.Binding == "/Game/Textures/Flow.Flow") == 2 &&
                gpuIndex.Uses.Any(use => use.Parameter == "GPU/flowMap") &&
                gpuIndex.Uses.Any(use => use.Target.Binding == "builtin:unlit-alpha"),
            "GPU Texture2D/FlowMap declarations and actual renderer materials must be indexed separately.");
    }

    private static void VerifyUiAndIndex()
    {
        var root = Path.Combine(AppDataDirectory.Root, "references", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var main = new MainViewModel();
        try
        {
            Require(main.TeamProfiles.Save(null, "References " + Guid.NewGuid().ToString("N"), Path.Combine(root, "groups")),
                main.TeamProfiles.Status);
            var graph = main.Graph;
            RenderNodeAuthoring.CreateModel(graph, Model());
            var plain = graph.CreateNode(GraphNodeKind.GraphGroup);
            Require(graph.EnterGraph(plain), "Enter ordinary Group.");
            var nested = RenderNodeAuthoring.CreateModel(graph, Model(), 4800, 3200);
            var nestedId = nested.Id;
            graph.NavigateToGraph(0);
            var spawn = graph.CreateNode(GraphNodeKind.SpawnableGroup);
            Require(graph.EnterGraph(spawn), "Enter Spawnable Group.");
            RenderNodeAuthoring.CreateModel(graph, Model());
            graph.NavigateToGraph(0);
            var libraryGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
            RenderNodeAuthoring.CreateModel(libraryGraph, Model());
            var asset = main.GroupAssets.Library().Create("Referenced", libraryGraph.CreateSnapshot());
            main.GroupAssets.Refresh();
            graph.CreateGroupAssetNode(asset.Id, 80, 80);

            using var model = new AssetReferencesViewModel(main);
            var view = new AssetReferencesView { DataContext = model };
            var editor = new GraphEditorView { DataContext = graph };
            var host = new Grid { RowDefinitions = new RowDefinitions("*,*") };
            host.Children.Add(editor);
            Grid.SetRow(view, 1);
            host.Children.Add(view);
            var window = new Window { Content = host, Width = 1200, Height = 1100 };
            window.Show();
            main.SetStorageProvider(window.StorageProvider);
            try
            {
                Dispatcher.UIThread.RunJobs();
                AssetReferenceRow Shared() => model.Resources.Single(row => row.Resource.Binding == Model().Path);
                var row = Shared();
                Require(row.Count == 4 && row.Resource.Kind == ReferenceResourceKind.Engine &&
                        row.Resolution == LocalizationService.Shared.Get("References.Disconnected") &&
                        model.Resources.Any(resource => resource.Resource.GroupId == asset.Id),
                    "Current effect usage must aggregate repeated assets through ordinary, Spawnable and linked Groups.");
                var list = view.FindControl<ListBox>("UsedAssetList")!;
                list.SelectedItem = row;
                Dispatcher.UIThread.RunJobs();
                var locations = view.FindControl<ListBox>("ReferenceLocations")!;
                locations.SelectedItem = model.Incoming.Single(location => location.Use.NodeId == nestedId);
                Dispatcher.UIThread.RunJobs();
                var before = graph.CreateSnapshot();
                new ButtonAutomationPeer(view.FindControl<Button>("GoToAssetUseButton")!)
                    .GetProvider<IInvokeProvider>()!.Invoke();
                Dispatcher.UIThread.RunJobs();
                Require(graph.SelectedNode?.Id == nestedId && !graph.IsRootGraph &&
                        GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()),
                    "Actual use-navigation button must enter the right Group and select without editing history.");
                var viewport = editor.FindControl<ScrollViewer>("GraphScrollViewer")!;
                var zoom = editor.FindControl<Grid>("GraphCanvas")!.RenderTransform!.Value.M11;
                Require(new Rect(viewport.Offset.X / zoom - editor.OriginX, viewport.Offset.Y / zoom - editor.OriginY,
                        viewport.Viewport.Width / zoom, viewport.Viewport.Height / zoom).Contains(new Point(4800, 3200)),
                    "Use navigation must frame the selected node, not merely select an offscreen item.");
                RenderNodeAuthoring.AssignAsset(graph, graph.SelectedNode!, Model("Changed"));
                Dispatcher.UIThread.RunJobs();
                Require(Shared().Count == 3 && model.Resources.Any(resource => resource.Resource.Binding == Model("Changed").Path),
                    "Usage must follow assignment edits inside a Group.");
                Require(graph.History.Undo(), "Undo asset assignment.");
                Dispatcher.UIThread.RunJobs();
                Require(Shared().Count == 4, "Usage must follow Undo.");
                Require(graph.History.Redo(), "Redo asset assignment.");
                Dispatcher.UIThread.RunJobs();
                Require(Shared().Count == 3, "Usage must follow Redo.");
                Require(graph.History.Undo(), "Restore shared fixture.");
                graph.NavigateToGraph(0);

                var path = Path.Combine(root, "indexed.lfxproj");
                using (var stream = new MemoryStream())
                {
                    new ProjectFileService().SaveAsync(stream, libraryGraph).GetAwaiter().GetResult();
                    File.WriteAllBytes(path, stream.ToArray());
                }
                File.WriteAllText(Path.Combine(root, "invalid.lfxproj"), "{invalid");
                model.Folder = root;
                model.AddFolderCommand.Execute(null);
                Complete(model.RefreshCommand.ExecuteAsync(null));
                model.Mode = 1;
                model.SelectedResource = model.Resources.Single(resource => resource.Resource.Binding == path);
                Require(model.Outgoing.Any(use => use.Use.Target.Binding == Model().Path) &&
                        model.Diagnostics.Contains("invalid.lfxproj") && model.HasDiagnostics && model.Folders.Count == 1,
                    "Registered projects need outgoing references and visible partial-index diagnostics.");
                model.SelectedOutgoing = model.Outgoing.First(use => use.Use.Target.Binding == Model().Path);
                model.FollowOutgoingCommand.Execute(null);
                Require(model.Incoming.Any(use => use.Use.Inferred) && model.Incoming.Any(use => !use.Use.Inferred),
                    "Cross-source path matches must be visibly inferred alongside direct locations.");
                model.SelectedLocation = model.Incoming.Single(use => use.Use.Source.LocalPath == path);
                Complete(model.GoToCommand.ExecuteAsync(null));
                Dispatcher.UIThread.RunJobs();
                Require(main.ActiveDocument?.FileName == "indexed.lfxproj" && main.Graph.SelectedNode?.Name == "Shared",
                    "Indexed source navigation must open the correct document before selecting its node.");
                model.Mode = 0;
                Require(model.Resources.Single(resource => resource.Resource.Binding == Model().Path).Count == 1,
                    "The usage view must switch to the new active document, excluding other documents and library counts.");
                main.ActivateDocument(main.Documents.First());
                Dispatcher.UIThread.RunJobs();
                Require(Shared().Count == 4, "Returning to the first document must restore whole-effect counts.");
                main.NewDocumentCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Require(model.IsEmpty && model.Resources.Count == 0 &&
                        model.EmptyMessage == LocalizationService.Shared.Get("References.NoMatches"),
                    "A reference-free active document must not show references from other open documents.");
                main.ActivateDocument(main.Documents.First());
                Dispatcher.UIThread.RunJobs();
                var pendingScan = model.RefreshCommand.ExecuteAsync(null);
                model.ClearFoldersCommand.Execute(null);
                Complete(pendingScan);
                model.Mode = 1;
                Require(model.Folders.Count == 0 &&
                        model.Resources.All(resource => resource.Resource.Binding != Path.Combine(root, "invalid.lfxproj")),
                    "Cancelled scans must not restore cleared registrations or stale file rows.");
                model.Mode = 0;
                graph.CreateNode(GraphNodeDefinition.All.Single(definition => definition.TypeName == "ModelDraw").Kind);
                Dispatcher.UIThread.RunJobs();
                Require(model.Resources.Any(resource => resource.Resource.Kind == ReferenceResourceKind.Unassigned),
                    "An unassigned draw node must be shown as missing assignment, not a missing engine asset.");
                Require(graph.History.Undo(), "Remove unassigned fixture.");
                var oldId = main.TeamProfiles.Active.Id;
                Require(main.TeamProfiles.Save(null, "Other references " + Guid.NewGuid().ToString("N"), ""), main.TeamProfiles.Status);
                Dispatcher.UIThread.RunJobs();
                Require(model.Folders.Count == 0 &&
                        model.Resources.Single(resource => resource.Resource.GroupId == asset.Id).Resolution ==
                        LocalizationService.Shared.Get("References.GroupUnverified"),
                    "Profile changes must clear folder scope and identify unavailable library sources while retaining embedded usage.");
                model.SelectedResource = model.Resources.Single(resource => resource.Resource.GroupId == asset.Id);
                Require(model.Outgoing.Any(use => use.Use.Target.Binding == Model().Path),
                    "Linked Group outgoing references must include saved instance contents even without its source library.");
                Require(main.TeamProfiles.Select(oldId), "Restore synthetic profile.");
                var document = main.Documents.First();
                document.MarkSaved("same.lfxproj", new Uri(Path.Combine(root, "first", "same.lfxproj")).AbsoluteUri,
                    graph.CreateSnapshot(), null);
                Dispatcher.UIThread.RunJobs();
                var oldKey = Shared().Resource.Key;
                document.MarkSaved("same.lfxproj", new Uri(Path.Combine(root, "second", "same.lfxproj")).AbsoluteUri,
                    graph.CreateSnapshot(), null);
                Dispatcher.UIThread.RunJobs();
                Require(Shared().Resource.Key != oldKey && Shared().Count == 4,
                    "Save As to a different location with the same filename must refresh location-scoped identities.");
            }
            finally { window.Close(); }
        }
        finally { main.DisposeStorageFiles(); }
    }

    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new TimeoutException("Reference operation did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
