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
        var dataRoot = Path.Combine(Path.GetTempPath(), "liberafx-comment-test-" + Guid.NewGuid().ToString("N"));
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
        if (args is ["--runtime-integration"])
        {
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
