using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;
using Thumbnail = LiberaFx.Services.EngineAssetThumbnail;

internal static class VisibleAssetThumbnailTests
{
    public static void Run(byte[] png)
    {
        var requests = new List<(string Path, TaskCompletionSource<Thumbnail> Completion)>();
        using (var cache = new VisibleAssetThumbnails((path, _) =>
        {
            var result = new TaskCompletionSource<Thumbnail>();
            requests.Add((path, result));
            return result.Task;
        }))
        {
            cache.Select("selected");
            using var selected = cache.Observe("selected", _ => { });
            using var first = cache.Observe("first", _ => { });
            var offscreen = cache.Observe("old-viewport", _ => throw new Exception("Offscreen callback ran"));
            PumpUntil(() => requests.Count == 1);
            Require(requests[0].Path == "first", "Visible fetch must leave selected requests to the priority channel.");
            offscreen.Dispose();
            using var next = cache.Observe("new-viewport", _ => { });
            requests[0].Completion.SetResult(new("first", "image/png", png, 1, 1));
            PumpUntil(() => requests.Count == 2);
            Require(requests[1].Path == "new-viewport", "Scroll must discard queued offscreen requests.");
            requests[1].Completion.SetResult(new("new-viewport", "image/png", png, 1, 1));
            Dispatcher.UIThread.RunJobs();
            var reused = false;
            using var again = cache.Observe("first", _ => reused = true);
            Require(reused && requests.Count == 2, "Visible thumbnails must reuse their bounded cache.");
            for (var index = 0; index < 65; index++) cache.Store(new("cached-" + index, "image/png", png, 1, 1));
            Require(!cache.TryGet("first", out _), "Thumbnail cache must evict old entries rather than grow with the project.");
        }
        VerifyViewport(png);
        var project = new LiberaFx.Models.Resources.EngineProjectIdentity("Unreal", Guid.NewGuid());
        var otherProject = project with { ProjectId = Guid.NewGuid() };
        EngineAssetScopeSettings.Save(project, "/Game/ThumbnailTests/");
        Require(EngineAssetScopeSettings.Load(project) == "/Game/ThumbnailTests" && EngineAssetScopeSettings.Load(otherProject) == "",
            "Content scopes must persist per engine project, without leaking to another project.");
        Require(!EngineAssetFolder.Contains("/Game/VFX", "/Game/VFX2/Smoke.Smoke"), "Folder masks must respect path boundaries.");
    }

    private static void VerifyViewport(byte[] png)
    {
        var main = new MainViewModel();
        var peer = Guid.NewGuid();
        var assets = Enumerable.Range(0,32).Select(index => new EngineAsset(
            $"/Game/ThumbnailTests/Texture{index}.Texture{index}", $"Texture {index}", "texture", "Unreal", 1, 1)).ToArray();
        var requested = new List<string>();
        main.PreviewTelemetry.Connect(peer, 3, bytes =>
        {
            using var command = JsonDocument.Parse(bytes.AsMemory(CompiledPreviewProtocol.HeaderSize));
            var root = command.RootElement;
            var request = root.GetProperty("requestId").GetGuid();
            var type = root.GetProperty("type").GetString();
            var path = root.TryGetProperty("path", out var pathValue) ? pathValue.GetString()! : "";
            if (type == "asset_thumbnail") requested.Add(path);
            var folder = root.TryGetProperty("folder", out var folderValue) ? folderValue.GetString()! : "";
            var foldersOnly = root.TryGetProperty("foldersOnly", out var folderFlag) && folderFlag.GetBoolean();
            object response = type switch
            {
                "asset_catalog" when foldersOnly => new { type = "asset_result", operation = "catalog", requestId = request,
                    folder, total = folder.Length == 0 ? 2 : 1, offset = 0,
                    folders = folder.Length == 0 ? new[] { new { path = "/Engine", name = "Engine" }, new { path = "/Game", name = "Game" } } :
                        new[] { new { path = "/Game/ThumbnailTests", name = "ThumbnailTests" } } },
                "asset_catalog" => new { type = "asset_result", operation = "catalog", requestId = request, folder, total = 32, offset = 0, assets },
                "asset_details" => new { type = "asset_result", operation = "details", requestId = request, asset = assets.Single(a => a.Path == path) },
                _ => new { type = "asset_result", operation = "thumbnail", requestId = request, path, mime = "image/png",
                    imageBase64 = Convert.ToBase64String(png), width = 1, height = 1 }
            };
            Require(main.PreviewAssets.Receive(peer, JsonSerializer.SerializeToUtf8Bytes(response)), "Thumbnail fixture response rejected.");
            return Task.CompletedTask;
        });
        Require(main.PreviewTelemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"hello","version":1,"engine":"Unreal","role":"editor","capabilities":["assets"],"viewerName":"Thumbnail viewport fixture"}""")), "Thumbnail fixture handshake rejected.");
        using var model = new EngineAssetsWindowViewModel(main);
        var view = new EngineAssetsWindow { DataContext = model };
        var window = new Window { Width = 1000, Height = 420, Content = view };
        window.Show();
        try
        {
            var load = model.SearchCommand.ExecuteAsync(null);
            PumpUntil(() => load.IsCompleted && requested.Count > 1);
            load.GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
            Require(requested[0] == assets[0].Path && !requested.Contains(assets[31].Path) && requested.Count < 20,
                "The selected row and visible rows must load without requesting the whole page.");
            Require(view.GetVisualDescendants().OfType<LiberaFx.Views.EngineAssetThumbnail>().Count(image => image.Source is not null) > 1,
                "Visible row thumbnails must appear on the real list controls.");
            var list = view.FindControl<ListBox>("EngineAssetList")!;
            list.ScrollIntoView(model.Assets[31]);
            PumpUntil(() => requested.Contains(assets[31].Path));
            Require(requested.Count(path => path == assets[0].Path) == 1, "The selected row must share its thumbnail with the list.");
            Console.WriteLine("Visible thumbnail priority passed: selected first, bounded initial viewport, scroll target loaded and image controls populated.");
            view.FindControl<Expander>("EngineFolderScopeSettings")!.IsExpanded = true;
            var folders = model.RefreshEngineFoldersCommand.ExecuteAsync(null);
            PumpUntil(() => folders.IsCompleted); folders.GetAwaiter().GetResult();
            model.SelectedEngineFolder = model.EngineFolders.Single(entry => entry.Path == "/Game");
            folders = model.OpenEngineFolderCommand.ExecuteAsync(null);
            PumpUntil(() => folders.IsCompleted); folders.GetAwaiter().GetResult();
            view.FindControl<ListBox>("EngineFolderList")!.SelectedItem = model.EngineFolders.Single();
            new ButtonAutomationPeer(view.FindControl<Button>("ApplyEngineFolderButton")!).GetProvider<IInvokeProvider>()!.Invoke();
            PumpUntil(() => model.EngineFolder == "/Game/ThumbnailTests" && !model.UseEngineFolderCommand.IsRunning);
            Require(model.Status.Contains("32", StringComparison.Ordinal), "Folder selection must reload matching asset results.");
            Console.WriteLine("Engine folder browser and scope selection passed through the visible controls.");
        }
        finally
        {
            window.Close();
            main.PreviewAssets.Disconnect(peer); main.PreviewTelemetry.Disconnect(peer);
            main.DisposeStorageFiles();
        }
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Require(done(), "Thumbnail viewport verification timed out.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
