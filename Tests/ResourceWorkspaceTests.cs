using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;
using LibraGpu.Core;

namespace LiberaFx.Tests;

public static class ResourceWorkspaceTests
{
    public static void Run()
    {
        VerifyLocalAndGroupResources();
        VerifyTypedTargets();
        VerifyEngineWindow();
        VisibleAssetThumbnailTests.Run(CreatePng());
        Console.WriteLine("Resource origins, local/Group previews, explicit GPU/material targets, UI commands and Undo passed.");
    }

    private static void VerifyLocalAndGroupResources()
    {
        var root = Path.Combine(AppDataDirectory.Root, "resource-tests", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var library = Path.Combine(root, "groups");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(library);
        var png = CreatePng();
        File.WriteAllBytes(Path.Combine(local, "texture.png"), png);
        File.WriteAllBytes(Path.Combine(local, "broken.jpg"), [0, 1, 2]);
        File.WriteAllText(Path.Combine(local, "ignore.txt"), "not a resource");
        Directory.CreateDirectory(Path.Combine(local, "nested"));
        File.WriteAllBytes(Path.Combine(local, "nested", "nested.PNG"), png);
        var fixture = new GraphWorkspaceViewModel(new UndoRedoManager());
        fixture.CreateNode(GraphNodeKind.Float, 4800, 3200);
        using (var stream = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(stream, fixture).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(local, "effect.lfxproj"), stream.ToArray());
        }
        Require(LocalResourceCatalog.Read(local, false).Assets.Count == 3 &&
                LocalResourceCatalog.Read(local, true).Assets.Count == 4,
            "The local catalog must list only supported images/effects and respect recursive selection.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { LocalResourceCatalog.Read(local, true, cancelled.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        }

        var main = new MainViewModel();
        try
        {
            var projectName = "Resource project " + Guid.NewGuid().ToString("N");
            Require(main.TeamProfiles.Save(null, projectName, library), main.TeamProfiles.Status);
            var group = main.GroupAssets.Library().Create("Reusable glow", fixture.CreateSnapshot());
            main.GroupAssets.Refresh();
            using var model = new ProjectResourcesViewModel(main);
            var entry = model.Entries.Single(resource => resource.Group?.Id == group.Id);
            Require(entry.Origin == projectName && !entry.PreviewOnly, "Group origin must name its project.");
            model.Query = "glow";
            Require(model.Entries.Count == 1, "Group resources must be searchable.");
            model.SelectedEntry = model.Entries[0];
            Complete(model.PreviewTask);
            Require(model.PreviewGraph?.Nodes.Count == 1, "Groups must have a separate read-only graph preview.");
            var view = new ProjectResourcesView { DataContext = model };
            var window = new Window { Content = view, Width = 1000, Height = 650 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Require(!view.FindControl<GraphEditorView>("ResourceGraphPreview")!.IsEffectivelyEnabled,
                    "Resource graph previews must not edit source data.");
                var preview = view.FindControl<GraphEditorView>("ResourceGraphPreview")!;
                var viewport = preview.FindControl<ScrollViewer>("GraphScrollViewer")!;
                var zoom = preview.FindControl<Grid>("GraphCanvas")!.RenderTransform!.Value.M11;
                var visible = new Rect(viewport.Offset.X / zoom - preview.OriginX,
                    viewport.Offset.Y / zoom - preview.OriginY,
                    viewport.Viewport.Width / zoom, viewport.Viewport.Height / zoom);
                Require(visible.Contains(new Point(4800, 3200)),
                    $"Read-only graph previews must frame off-origin content: visible {visible}, zoom {zoom}, viewport {viewport.Viewport}.");
                var before = main.Graph.Nodes.Count;
                var button = view.FindControl<Button>("UseResourceButton")!;
                new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
                Dispatcher.UIThread.RunJobs();
                Require(main.Graph.Nodes.Count == before + 1 &&
                        main.Graph.SelectedNode?.GroupAsset?.Id == group.Id &&
                        main.Graph.History.Undo() && main.Graph.Nodes.Count == before,
                    "The actual Group action must insert the selected asset through one undoable command.");

                model.SourceIndex = 1;
                model.Query = "";
                model.Folder = local;
                Complete(model.RefreshCommand.ExecuteAsync(null));
                model.SelectedEntry = model.Entries.Single(resource => resource.Name == "texture.png");
                Complete(model.PreviewTask);
                Dispatcher.UIThread.RunJobs();
                Require(model.PreviewOnly && model.Thumbnail is not null &&
                        !model.UseCommand.CanExecute(null) && !button.IsEffectivelyEnabled &&
                        ReferenceEquals(view.FindControl<Image>("LocalThumbnail")!.Source, model.Thumbnail),
                    "Local images must display real thumbnails but cannot be assigned/opened as engine resources.");
                var active = main.Graph.CreateSnapshot();
                model.SelectedEntry = model.Entries.Single(resource => resource.Name == "effect.lfxproj");
                Complete(model.PreviewTask);
                Require(model.PreviewGraph?.Nodes.Count == 1 &&
                        GraphSnapshot.ContentEquals(active, main.Graph.CreateSnapshot()),
                    "Previewing a local effect must not replace the active document.");
                main.SetStorageProvider(window.StorageProvider);
                var documents = main.Documents.Count;
                Complete(model.UseCommand.ExecuteAsync(null));
                Require(main.Documents.Count == documents + 1 && main.Graph.Nodes.Count == 1,
                    "Opening a local effect must create an actual document through the normal storage path.");
                model.SelectedEntry = model.Entries.Single(resource => resource.Name == "broken.jpg");
                Complete(model.PreviewTask);
                Require(model.Thumbnail is null && !string.IsNullOrEmpty(model.Status), "Malformed images must be diagnosed.");
                model.SelectedEntry = model.Entries.Single(resource => resource.Name == "texture.png");
                var pending = model.PreviewTask;
                model.SelectedEntry = null;
                Complete(pending);
                Require(model.Thumbnail is null && model.PreviewGraph is null, "Cancelled previews must not restore stale content.");
                model.SourceIndex = 0;
                Require(model.Entries.Any(resource => resource.Group?.Id == group.Id), "Returning to Groups must restore project resources.");
                model.SelectedEntry = model.Entries.Single(resource => resource.Group?.Id == group.Id);
                Require(main.TeamProfiles.Save(null, projectName + " other", Path.Combine(root, "other")), main.TeamProfiles.Status);
                Require(model.SelectedEntry is null &&
                        model.Entries.All(resource => resource.Group?.Origin == GroupAssetOrigin.Standard) && !model.CanUse,
                    "Switching projects must clear stale Group actions.");
            }
            finally { window.Close(); }
        }
        finally { main.DisposeStorageFiles(); }
    }

    private static void VerifyTypedTargets()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var texture = Texture();
        var modelAsset = new EngineAsset("/Game/Meshes/Model.Model", "Model", "model", "Unreal",
            Materials: [new(0, "/Game/Materials/M.M", "M", [
                new("First", "texture", JsonSerializer.SerializeToElement("/Game/Textures/Old.Old")),
                new("Second", "texture", JsonSerializer.SerializeToElement("/Game/Textures/Old.Old"))])]);
        var node = RenderNodeAuthoring.CreateModel(graph, modelAsset);
        var before = graph.CreateSnapshot();
        try { RenderNodeAuthoring.AssignAsset(graph, node, texture); throw new Exception("Wrong resource class accepted."); }
        catch (ArgumentException) { }
        Require(GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Invalid assignments must not edit history/data.");
        var targets = ResourceAssignment.Targets(node, texture);
        Require(targets.Count == 2 && targets.All(target => target.Kind == ResourceTargetKind.MaterialTexture),
            "Material texture parameters must be separate explicit targets, not model replacements.");
        ResourceAssignment.Assign(graph, node, texture, targets[1]);
        var parameter = node.Definition.Parameters.Single(parameter => parameter.Id == targets[1].Id);
        Require(node.GetParameterValue(parameter).GetString() == texture.Path &&
                node.GetParameterValue(node.Definition.Parameters.Single(parameter => parameter.Id == targets[0].Id)).GetString()
                    == "/Game/Textures/Old.Old" &&
                Encoding.UTF8.GetString(LiberaFx.Services.Compilation.RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 1))
                    .Contains(texture.Path),
            "Only the selected material texture must reach render delivery.");
        Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()),
            "Material texture assignment must be one reversible edit.");
        try
        {
            ResourceAssignment.Assign(graph, graph.GetNode(node.Id), texture, targets[0] with { Id = "missing" });
            throw new Exception("Stale target accepted.");
        }
        catch (ArgumentException) { }
    }

    private static void VerifyEngineWindow()
    {
        var main = new MainViewModel();
        var peer = Guid.NewGuid();
        var otherPeer = Guid.NewGuid();
        var png = CreatePng();
        void Connect(Guid id)
        {
            main.PreviewTelemetry.Connect(id, 3, bytes =>
            {
                using var command = JsonDocument.Parse(bytes.AsMemory(CompiledPreviewProtocol.HeaderSize));
                var request = command.RootElement.GetProperty("requestId").GetGuid();
                var type = command.RootElement.GetProperty("type").GetString();
                object response = type switch
                {
                    "asset_catalog" => new { type = "asset_result", operation = "catalog", requestId = request,
                        total = 1, offset = 0, assets = new[] { Texture() } },
                    "asset_details" => new { type = "asset_result", operation = "details", requestId = request, asset = Texture() },
                    _ => new { type = "asset_result", operation = "thumbnail", requestId = request,
                        path = Texture().Path, mime = "image/png", imageBase64 = Convert.ToBase64String(png), width = 1, height = 1 }
                };
                Require(main.PreviewAssets.Receive(id, JsonSerializer.SerializeToUtf8Bytes(response)), "Fixture response rejected.");
                return Task.CompletedTask;
            });
            Require(main.PreviewTelemetry.Receive(id, Encoding.UTF8.GetBytes(
                """{"type":"hello","version":1,"engine":"Unreal","role":"editor","capabilities":["assets"],"viewerName":"Resource fixture"}""")),
                "Fixture handshake rejected.");
        }
        try
        {
            VerifyOwnedEngineWindow(main);
            Connect(peer);
            var graph = main.Graph;
            var gpu = GpuParticleEditorService.Create(graph);
            var resource = GpuParticleEditorService.ReadState(gpu).Program.Resources.Single();
            GpuParticleEditorService.Apply(graph, gpu.Id, [new() { Action = "resource",
                Resource = resource with { Id = "otherTexture" }, AssetPath = "builtin:white" }]);
            graph.SelectNode(gpu);
            using var browser = new EngineAssetsWindowViewModel(main);
            var window = new EngineAssetsWindow { DataContext = browser };
            var host = new Window { Content = window, Width = 1200, Height = 800 };
            host.Show();
            try
            {
                Complete(browser.SearchCommand.ExecuteAsync(null));
                PumpUntil(() => browser.Thumbnail is not null);
                Dispatcher.UIThread.RunJobs();
                var button = window.FindControl<Button>("AssignResourceButton")!;
                var picker = window.FindControl<ComboBox>("AssignmentTargetPicker")!;
                Require(browser.AssignmentTargets.Count == 2 && browser.SelectedAssignmentTarget is null &&
                        picker.SelectedItem is null && !button.IsEffectivelyEnabled,
                    "Multiple GPU resources must not silently select their first entry.");
                picker.SelectedItem = browser.AssignmentTargets.Single(target => target.Id == "otherTexture");
                Dispatcher.UIThread.RunJobs();
                Require(button.IsEffectivelyEnabled, "An explicit resource choice must enable assignment.");
                new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
                var state = GpuParticleEditorService.ReadState(graph.GetNode(gpu.Id));
                Require(state.ResourceAssetPaths["otherTexture"] == Texture().Path &&
                        state.ResourceAssetPaths[resource.Id] == "builtin:white", "Only the explicitly selected GPU binding may change.");
                Require(graph.History.Undo() &&
                        GpuParticleEditorService.ReadState(graph.GetNode(gpu.Id)).ResourceAssetPaths["otherTexture"] == "builtin:white",
                    "GPU resource assignment must be undoable.");
                browser.SelectedAsset = null;
                Require(browser.Thumbnail is null && browser.AssignmentTargets.Count == 0 && !browser.CanAssign,
                    "Clearing an asset selection must clear stale previews and assignment actions.");
                Connect(otherPeer);
                browser.RefreshPeers();
                Require(browser.SelectedPeer is null && browser.Assets.Count == 0 && !browser.CanAssign,
                    "Multiple engine peers require explicit selection and must clear prior peer data.");
                browser.SelectedPeer = browser.Peers.Single(option => option.ClientId == peer);
                Complete(browser.SearchCommand.ExecuteAsync(null));
                PumpUntil(() => browser.Thumbnail is not null);
                browser.SelectedPeer = browser.Peers.Single(option => option.ClientId == otherPeer);
                Require(browser.SelectedAsset is null && browser.Thumbnail is null && browser.Assets.Count == 0,
                    "Switching engine peers must clear asset selection, details and thumbnails.");
            }
            finally { host.Close(); }
        }
        finally
        {
            main.PreviewAssets.Disconnect(peer);
            main.PreviewAssets.Disconnect(otherPeer);
            main.PreviewTelemetry.Disconnect(peer);
            main.PreviewTelemetry.Disconnect(otherPeer);
            main.DisposeStorageFiles();
        }
    }

    private static void VerifyOwnedEngineWindow(MainViewModel main)
    {
        var castFailed = false;
        void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is InvalidCastException error &&
                error.Message.Contains(nameof(EngineAssetsWindowViewModel), StringComparison.Ordinal) &&
                error.Message.Contains(nameof(ResourceIdentityPaneViewModel), StringComparison.Ordinal))
            {
                castFailed = true;
            }
        }

        var owner = new MainWindow { DataContext = main };
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            owner.Show();
            main.AssetBrowser.OpenForCurrentSelection();
            Dispatcher.UIThread.RunJobs();
            var hosts = main.Layout.Windows?.Select(window => window.Host).OfType<Window>() ?? [];
            var browser = hosts.Prepend(owner).SelectMany(window => window.GetVisualDescendants())
                .OfType<EngineAssetsWindow>().Single();
            Require(!castFailed &&
                    browser.FindControl<ResourceIdentityView>("ResourceIdentityPane")?.DataContext is ResourceIdentityPaneViewModel,
                "The resource identity tab inherited the asset browser's view model during startup.");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
            foreach (var window in main.Layout.Windows?.ToArray() ?? [])
            {
                window.Host?.Exit();
            }
            owner.Close();
        }
    }

    private static EngineAsset Texture() => new("/Game/Textures/New.New", "New", "texture", "Unreal", 1, 1);
    private static byte[] CreatePng()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var pixels = bitmap.Lock()) System.Runtime.InteropServices.Marshal.WriteInt32(pixels.Address, -1);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }
    private static void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        if (!done()) throw new TimeoutException("Resource UI request did not complete.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
