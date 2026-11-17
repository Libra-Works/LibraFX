using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using LiberaFx;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--inspector-plugins"] or ["--inspector-plugins-reload"] or
            ["--inspector-plugins-tampered-check"] or ["--inspector-plugins-disabled-check"] or
            ["--inspector-plugins-missing-check"])
        {
            var inspectorDataRoot = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(inspectorDataRoot) || !Path.IsPathFullyQualified(inspectorDataRoot))
                throw new ArgumentException("Set an absolute isolated LIBERAFX_DATA_ROOT for inspector SDK regressions.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            if (args[0] == "--inspector-plugins") LiberaFx.Tests.InspectorPluginTests.Run();
            else if (args[0] == "--inspector-plugins-reload") LiberaFx.Tests.InspectorPluginTests.RunReloadChild();
            else if (args[0] == "--inspector-plugins-tampered-check") LiberaFx.Tests.InspectorPluginTests.RunTamperedChildCheck();
            else if (args[0] == "--inspector-plugins-missing-check") LiberaFx.Tests.InspectorPluginTests.RunMissingChildCheck();
            else LiberaFx.Tests.InspectorPluginTests.RunDisabledChildCheck();
            return;
        }
        if (args.Length is 1 or 2 && args[0] == "--sequence")
        {
            var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
            if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
                throw new ArgumentException("Set an absolute isolated LIBERAFX_DATA_ROOT for Sequence verification.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            if (args.Length == 2 && !Path.IsPathFullyQualified(args[1]))
                throw new ArgumentException("Use an absolute Sequence fixture output directory.");
            SequenceNodeTests.Run(args.Length == 2 ? args[1] : null);
            Console.WriteLine("Sequence dynamic pins, Signal cardinality, persistence, history, MCP and native execution passed.");
            return;
        }
        if (args is ["--group-exposed-schema"])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")))
                throw new ArgumentException("Set isolated LIBERAFX_DATA_ROOT for group exposed metadata regression.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            GroupExposedSchemaTests.Run();
            GroupExposedInterfaceTests.Run();
            Console.WriteLine("Group exposed metadata persistence, cloning and history checks passed.");
            return;
        }
        if (args is ["--custom-node-compilation"])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")))
                throw new ArgumentException("Set isolated LIBERAFX_DATA_ROOT for custom compiler regression.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            LiberaFx.Tests.CustomNodeCompilationTests.Run();
            Console.WriteLine("Custom node compilation regression passed (no windows).");
            return;
        }
        if (args is ["--game-io"])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")))
                throw new ArgumentException("Set isolated LIBERAFX_DATA_ROOT for Game I/O regression.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            LiberaFx.Tests.GameIoCompilationTests.Run();
            Console.WriteLine("Game I/O graph, metadata, signal lowering and package regressions passed.");
            return;
        }
        if (args is ["--compiled-preview-protocol"])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")))
                throw new ArgumentException("Set isolated LIBERAFX_DATA_ROOT for preview transport regression.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            CompiledPreviewProtocolTests.Run();
            Console.WriteLine("Compiled preview protocol regression passed (no windows).");
            return;
        }
        if (args is ["--fireworks-save-sample", var fireworksSamplePath])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")) ||
                !Path.IsPathFullyQualified(fireworksSamplePath))
                throw new ArgumentException("Use isolated LIBERAFX_DATA_ROOT and an absolute sample output path.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            using var memory = new MemoryStream();
            byte[]? previousPackage = null;
            if (File.Exists(fireworksSamplePath))
            {
                using var existing = new MemoryStream(File.ReadAllBytes(fireworksSamplePath));
                var project = new LiberaFx.Services.ProjectFileService().LoadAsync(existing).GetAwaiter().GetResult();
                if (!project.Graph.Nodes.Any(node => node.Kind == GraphNodeKind.SpawnableGroup && node.Name == "Launch") ||
                    !project.Graph.Nodes.Any(node => node.Kind == GraphNodeKind.SpawnableGroup && node.Name == "Burst"))
                    throw new InvalidOperationException("Refusing to overwrite an unrelated project with Fireworks instructions.");
                previousPackage = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    LiberaFx.Services.Compilation.SpawnableGroupPackage.Compile(project.Graph));
                var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
                workspace.InitializeFromSnapshot(project.Graph);
                if (project.History is not null) workspace.History.LoadSnapshot(project.History);
                var comment = workspace.Nodes.FirstOrDefault(node => node.Definition.Kind == GraphNodeKind.Comment &&
                    node.Name == LiberaFx.Services.FireworksSample.PreviewSetupTitle)
                    ?? workspace.CreateNode(GraphNodeKind.Comment, 0, 220);
                workspace.RenameNode(comment, LiberaFx.Services.FireworksSample.PreviewSetupTitle);
                workspace.SetParameter(comment, "text", System.Text.Json.JsonSerializer.SerializeToElement(LiberaFx.Services.FireworksSample.PreviewSetupText));
                workspace.SetParameter(comment, "width", System.Text.Json.JsonSerializer.SerializeToElement(LiberaFx.Services.FireworksSample.PreviewSetupWidth));
                workspace.SetParameter(comment, "height", System.Text.Json.JsonSerializer.SerializeToElement(LiberaFx.Services.FireworksSample.PreviewSetupHeight));
                workspace.MoveNodes(workspace.Nodes.Where(node => LiberaFx.Services.FireworksSample.RootPosition(node.Name) is not null)
                    .Select(node =>
                    {
                        var position = LiberaFx.Services.FireworksSample.RootPosition(node.Name)!.Value;
                        return (node, position.X, position.Y);
                    }));
                new LiberaFx.Services.ProjectFileService().SaveAsync(memory, workspace).GetAwaiter().GetResult();
            }
            else LiberaFx.Services.FireworksSample.SaveAsync(memory).GetAwaiter().GetResult();
            File.WriteAllBytes(fireworksSamplePath, memory.ToArray());
            memory.Position = 0;
            var saved = new LiberaFx.Services.ProjectFileService().LoadAsync(memory).GetAwaiter().GetResult();
            if (saved.History is null) throw new InvalidOperationException("Fireworks sample lacks project history.");
            if (previousPackage is not null && !previousPackage.AsSpan().SequenceEqual(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(LiberaFx.Services.Compilation.SpawnableGroupPackage.Compile(saved.Graph))))
                throw new InvalidOperationException("Layout update changed compiled Fireworks IDs, values or lifecycle metadata.");
            foreach (var node in saved.Graph.Nodes)
                if (LiberaFx.Services.FireworksSample.RootPosition(node.Name) is { } placement &&
                    (node.X != placement.X || node.Y != placement.Y))
                    throw new InvalidOperationException("Saved Fireworks root layout does not match the compact factory layout.");
            Console.WriteLine("History-bearing Fireworks sample saved with End=240 instructions (existing node UUIDs preserved): " + fireworksSamplePath);
            return;
        }
        if (args is ["--fireworks-native-preview", var fireworksPreviewOutput])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")))
                throw new ArgumentException("Set an isolated absolute LIBERAFX_DATA_ROOT for native preview verification.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            EditorNativePreviewTests.Run(fireworksPreviewOutput);
            return;
        }
        if (args is ["--spawnable-package-binary", var binaryPackagePath])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")) ||
                !Path.IsPathFullyQualified(binaryPackagePath))
                throw new ArgumentException("Use an isolated LIBERAFX_DATA_ROOT and an absolute package output path.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            SpawnablePackageBinaryTests.Run(binaryPackagePath);
            return;
        }
        if (args is ["--fireworks-lifecycle", var fireworksOutput])
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")))
                throw new ArgumentException("Set an isolated absolute LIBERAFX_DATA_ROOT for Fireworks verification.");
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            FireworksLifecycleTests.Run(fireworksOutput);
            return;
        }
        var dataRoot = args.Length > 0 && args[0] == "--solar-system" ||
            args is ["--window-layout"] or ["--gpu-editor"] or ["--runtime-integration"] or ["--spawnable-group"] or ["--graph-type-diagnostics"]
            ? Path.Combine(Environment.CurrentDirectory, "build", "window-layout-data-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Path.GetTempPath(), "liberafx-comment-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", dataRoot);
        try { Run(args); }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static void Run(string[] args)
    {
        string? languageFixture = null;
        if (args is ["--language-startup", var mode])
        {
            languageFixture = mode switch
            {
                "ja" => """{"Language":"ja"}""",
                "invalid" => "{invalid",
                "unsupported" => """{"Language":"unsupported"}""",
                "save-failure" => "",
                _ => throw new ArgumentException("Unknown language startup fixture.")
            };
            var directory = Path.Combine(LiberaFx.Services.AppDataDirectory.Root, "LiberaFx");
            Directory.CreateDirectory(directory);
            if (mode == "save-failure") Directory.CreateDirectory(Path.Combine(directory, "language.json"));
            else File.WriteAllText(Path.Combine(directory, "language.json"), languageFixture);
        }
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        if (args is ["--matrix-ui-review", var reviewDirectory])
        {
            MatrixInterfaceTests.Run(reviewDirectory);
            Console.WriteLine("Actual Matrix pin click, undo/redo and localized inspector UI checks passed.");
            return;
        }
        if (args is ["--matrix-save-sample", var samplePath])
        {
            using var stream = File.Create(samplePath);
            Task.Run(() => LiberaFx.Services.SolarSystemSample.SaveDedicatedAsync(stream)).GetAwaiter().GetResult();
            Console.WriteLine("Saved dedicated Matrix sample with its project history.");
            return;
        }
        if (args.Length is 1 or 2 && args[0] == "--matrix-nodes")
        {
            MatrixNodeTests.Run(args.Length == 2 ? args[1] : null);
            Console.WriteLine("Matrix node authoring, persistence, compilation and fixture export checks passed.");
            return;
        }
        if (args.Length is 1 or 2 && args[0] == "--sprite-editor")
        {
            SpriteEditorTests.Run(args.Length == 2 ? args[1] : null);
            Console.WriteLine("Sprite editor authoring, atlas preview and undo checks passed.");
            return;
        }
        if (args.Length is 1 or 2 && args[0] == "--solar-system")
        {
            SolarSystemSampleTests.Run(args.Length == 2 ? args[1] : null);
            Console.WriteLine("Solar-system matrix authoring, saved project and render binding checks passed.");
            return;
        }
        if (args is ["--unreal-gpu-preview"])
        {
            CompiledPreviewProtocolTests.RunUnrealGpuPreview();
            return;
        }
        if (args is ["--spawnable-group"])
        {
            SpawnableGroupTests.Run();
            ContainingSpawnableInspectorTests.Run();
            LiberaFx.Tests.LifecycleNodePresentationTests.Run();
            EditorInterfaceTests.VerifyContainerPresentation();
            Console.WriteLine("Spawnable Group checks passed.");
            return;
        }
        if (args is ["--gpu-editor"])
        {
            LiberaFx.Tests.GpuParticleEditorTests.Run();
            EditorInterfaceTests.VerifyParameterRows();
            return;
        }
        if (args is ["--window-layout"])
        {
            LiberaFx.Tests.DockLayoutTests.Run();
            Console.WriteLine("Window menu, dock reopening, layout topology and preset persistence checks passed.");
            return;
        }
        if (args is ["--graph-type-diagnostics"])
        {
            LiberaFx.Tests.GraphTypeDiagnosticsTests.Run();
            Console.WriteLine("Graph type diagnostics checks passed.");
            return;
        }
        if (args is ["--asset-render"])
        {
            LocalProjectCatalogTests.Run();
            LiberaFx.Tests.BuiltinMathCompilationTests.Run();
            AssetSceneProtocolTests.Run();
            AssetAuthoringTests.Run();
            SpriteEditorTests.Run();
            EffectSampleAuthoringTests.Run();
            Console.WriteLine("Portable math, asset protocol and render sample authoring checks passed.");
            return;
        }
        if (args is ["--runtime-integration"])
        {
            EditorInterfaceTests.VerifyGraphToolbars();
            CompiledPreviewProtocolTests.Run();
            PreviewTelemetryTests.Run();
            McpParameterTests.Run();
            EditorInterfaceTests.VerifyParameterRows();
            Console.WriteLine("Preview protocol, native telemetry UI and MCP parameter checks passed.");
            return;
        }
        if (languageFixture is not null)
        {
            var localization = LiberaFx.Services.LocalizationService.Shared;
            var valid = args[1] == "ja";
            if (args[1] == "save-failure")
            {
                var model = new MainViewModel();
                var settings = new SettingsWindow { DataContext = model };
                settings.Show();
                try
                {
                    var languageList = settings.FindControl<ComboBox>("LanguageList")!;
                    languageList.SelectedItem = localization.Languages.Single(language => language.Id == "ja");
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    if (localization.SelectedLanguage.Id != "en" || string.IsNullOrEmpty(localization.Status) ||
                        languageList.SelectedItem is not LiberaFx.Services.EditorLanguage selected ||
                        selected != localization.SelectedLanguage ||
                        Directory.EnumerateFiles(Path.Combine(LiberaFx.Services.AppDataDirectory.Root, "LiberaFx"), ".language-*.tmp").Any())
                        throw new InvalidOperationException("Failed language saves changed the UI or left temporary files.");
                }
                finally
                {
                    settings.Close();
                    model.DisposeStorageFiles();
                }
                Console.WriteLine("Language save failure checks passed.");
                return;
            }
            if (localization.SelectedLanguage.Id != (valid ? "ja" : "en"))
                throw new InvalidOperationException("Startup did not load the expected language.");
            localization.SelectedLanguage = localization.Languages.Single(language => language.Id == (valid ? "en" : "ja"));
            if (!valid && (localization.SelectedLanguage.Id != "en" || string.IsNullOrEmpty(localization.Status) ||
                          File.ReadAllText(Path.Combine(LiberaFx.Services.AppDataDirectory.Root, "LiberaFx", "language.json")) != languageFixture))
                throw new InvalidOperationException("Invalid language settings were overwritten or not safely rejected.");
            Console.WriteLine("Language startup and settings protection checks passed.");
            return;
        }
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = workspace.CreateNode(GraphNodeKind.Comment);
        var view = new GraphNodeView { DataContext = node };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            var file = DispatchProxy.Create<IStorageFile, TestStorageFile>();
            var fake = (TestStorageFile)file;
            var import = typeof(GraphNodeView).GetMethod("AddCommentImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Import(byte[] data)
            {
                fake.Data = data;
                ((Task)import.Invoke(view, [node, file])!).GetAwaiter().GetResult();
            }

            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVQIHWP8DwQMQMD4HwgYgAAAV9UH+14vURUAAAAASUVORK5CYII=");
            Import(png);
            if (view.FindControl<Image>("EmbeddedImage")!.Source is null)
                throw new InvalidOperationException("Valid comment image did not render.");
            var definition = node.Definition.Parameters.Single(item => item.Id == "image");
            var largePng = CreateLargeImage(PngBitmapEncoderOptions.Default);
            if (largePng.Length <= CommentImage.MaximumBytes)
                throw new InvalidOperationException("Large image fixture is too small.");
            Import(largePng);
            var original = node.GetParameterValue(definition).GetString();
            if (view.FindControl<Image>("EmbeddedImage")!.Source is null ||
                !view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("optimized") ||
                Convert.FromBase64String(original!).Length > CommentImage.MaximumBytes)
                throw new InvalidOperationException("A large PNG was not optimized and displayed.");
            var largeJpeg = CreateLargeImage(new JpegBitmapEncoderOptions { Quality = 100 });
            if (largeJpeg.Length <= CommentImage.MaximumBytes)
                throw new InvalidOperationException("Large JPEG fixture is too small.");
            Import(largeJpeg);
            original = node.GetParameterValue(definition).GetString();
            var embeddedJpeg = Convert.FromBase64String(original!);
            if (view.FindControl<Image>("EmbeddedImage")!.Source is null ||
                !view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("optimized") ||
                embeddedJpeg.Length > CommentImage.MaximumBytes ||
                embeddedJpeg[0] != 255 || embeddedJpeg[1] != 216)
                throw new InvalidOperationException("A large JPEG was not optimized and displayed.");
            Import(new byte[CommentImage.MaximumSourceBytes + 1]);
            if (!view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("16 MiB") ||
                node.GetParameterValue(definition).GetString() != original)
                throw new InvalidOperationException("Oversized image was not safely rejected.");
            Import([0, 1, 2, 3]);
            if (!view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("PNG or JPEG") ||
                node.GetParameterValue(definition).GetString() != original)
                throw new InvalidOperationException("Invalid image was not safely rejected.");
            Import(png[..24]);
            if (string.IsNullOrEmpty(view.FindControl<TextBlock>("ImageStatus")!.Text) ||
                node.GetParameterValue(definition).GetString() != original)
                throw new InvalidOperationException("Truncated image was not safely rejected.");
            Console.WriteLine("Comment image import checks passed.");
        }
        finally
        {
            window.Close();
        }
        GroupLibraryTests.Run();
        Console.WriteLine("Project profile and group library checks passed.");
        EditorInterfaceTests.Run();
        Console.WriteLine("Inspector actions and English/Japanese localization checks passed.");
        ProjectNodeCatalogTests.Run();
        Console.WriteLine("Layered project node catalog, quick search and retained history checks passed.");
        LogViewerTests.Run();
        Console.WriteLine("Plain log window, filter, search, clear and atomic export checks passed.");
        CommentMarkupTests.Run();
        Console.WriteLine("Comment text/Markdown/HTML and undoable task-list checks passed.");
        GroupCompilationTests.Run();
        GroupBoundaryTests.Run();
        Console.WriteLine("Latest/frozen group compilation and deterministic instance checks passed.");
        PreviewProtocolTests.Run();
        Console.WriteLine("Preview parameter deltas, slow-client baselines and TLS resynchronization checks passed.");
        CompiledPreviewProtocolTests.Run();
        PreviewTelemetryTests.Run();
        McpParameterTests.Run();
        Console.WriteLine("Compiled preview protocol, playback baselines and TLS transport checks passed.");
        TimelineTests.Run();
        Console.WriteLine("Timeline retiming, shared playhead and graph persistence checks passed.");
        LiberaFx.Tests.VectorArithmeticTests.Run();
        Console.WriteLine("Vector operations, explicit conversions and persistent multi-output Break pins passed.");
        LiberaFx.Tests.CustomNodeCompilationTests.Run();
        Console.WriteLine("Project-native parameter/signal node compilation and mixed port categories passed.");
        LiberaFx.Tests.GameIoCompilationTests.Run();
        Console.WriteLine("Root Game I/O compilation and metadata regressions passed.");
    }

    private static byte[] CreateLargeImage(BitmapEncoderOptions options)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(1024, 768), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var pixels = bitmap.Lock())
        {
            var data = new byte[pixels.RowBytes * pixels.Size.Height];
            new Random(42).NextBytes(data);
            for (var index = 3; index < data.Length; index += 4) data[index] = 255;
            System.Runtime.InteropServices.Marshal.Copy(data, 0, pixels.Address, data.Length);
        }
        using var output = new MemoryStream();
        bitmap.Save(output, options);
        return output.ToArray();
    }
}

public class TestStorageFile : DispatchProxy
{
    public byte[] Data { get; set; } = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name switch
        {
            nameof(IStorageFile.OpenReadAsync) => Task.FromResult<Stream>(new MemoryStream(Data, false)),
            nameof(IDisposable.Dispose) => null,
            _ => throw new NotSupportedException(method?.Name)
        };
}
